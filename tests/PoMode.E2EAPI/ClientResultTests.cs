using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PoMode.Api.Features.Analysis;
using PoMode.Api.Features.PitchTracking;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class ClientResultTests : IDisposable
{
    private const string JobId = "abcdef0123456789abcdef0123456789";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-clientres-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-clientres-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new AuthedFactory()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

    /// <summary>Parks the pitch stage of a job that belongs to the AuthedFactory's "e2e" user: only
    /// the browser that owns a job may hand it results (JobAccessFilter).</summary>
    private static async Task<Task<IReadOnlyList<NoteEvent>>> ParkAsync(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<JobStore>();
        Directory.CreateDirectory(store.JobDir(JobId));
        await store.SaveAsync(new JobState
        {
            JobId = JobId,
            InputFileName = "take.wav",
            CreatedAt = DateTimeOffset.UtcNow,
            OwnerId = "test:e2e",
        }, CancellationToken.None);
        var registry = factory.Services.GetRequiredService<ClientWorkRegistry<IReadOnlyList<NoteEvent>>>();
        return registry.WaitAsync(JobId, TimeSpan.FromMinutes(5), CancellationToken.None);
    }

    [Fact]
    public async Task A_valid_payload_satisfies_the_parked_stage_and_bad_ones_are_refused()
    {
        // A valid payload is accepted and satisfies the parked stage.
        {
            await using var factory = Factory();
            using var client = factory.CreateClient();
            using var stranger = factory.CreateClient();
            stranger.DefaultRequestHeaders.Remove("X-Fake-User");
            stranger.DefaultRequestHeaders.Add("X-Fake-User", "someone-else");
            var parked = await ParkAsync(factory);

            NoteEvent[] notes = [new(60, 0.0, 0.5, 90), new(64, 0.5, 0.5, 88)];
            // Anyone may read a job by its link; only its own collection may hand it results. Someone
            // else's post answers like a missing job, and the stage stays parked.
            Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync($"/api/analysis/{JobId}")).StatusCode);
            var hijack = await stranger.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", notes);
            Assert.Equal(HttpStatusCode.NotFound, hijack.StatusCode);
            Assert.False(parked.IsCompleted);

            var response = await client.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", notes);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var delivered = await parked;
            Assert.Equal(2, delivered.Count);
            Assert.Equal(60, delivered[0].MidiPitch);

        }

        // Invalid payload and unparked job are rejected.
        {
            await using var factory = Factory();
            using var client = factory.CreateClient();
            _ = await ParkAsync(factory);

            var badResponse = await client.PostAsJsonAsync(
                $"/api/analysis/{JobId}/client-result",
                new[] { new NoteEvent(109, 0.0, 0.5, 90) });
            Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

            var unparkedResponse = await client.PostAsJsonAsync(
                $"/api/analysis/00000000000000000000000000000000/client-result",
                new[] { new NoteEvent(60, 0.0, 0.5, 90) });
            Assert.Equal(HttpStatusCode.NotFound, unparkedResponse.StatusCode);

        }
    }

}
