using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PoMode.Api.Features.Analysis;
using PoMode.Api.Features.Auth;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class AuthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2e-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    /// <summary>
    /// Production boots with guest sessions and no header auth: the FakeAuth scheme is not registered
    /// there, so <c>X-Fake-User</c> is an ordinary header, while a guest session is a real one whose
    /// cookie opens the caller's own library, and three initials open their collection from any
    /// device. The cookie is <c>__Host-</c>-prefixed and Secure, so the
    /// client talks https, as App Service's front end does.
    /// </summary>
    [Fact]
    public async Task Production_ignores_fake_headers_but_accepts_a_guest_session()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Jobs:Storage:Mode", "FileSystem")
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });

        using var faked = new HttpRequestMessage(HttpMethod.Get, "/api/library");
        faked.Headers.Add("X-Fake-User", "mallory");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(faked)).StatusCode);

        using var minted = await client.PostAsync("/auth/guest", null);
        var cookie = Assert.Single(minted.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-PoMode.Auth=", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        var guest = await minted.Content.ReadFromJsonAsync<SessionDto>();
        Assert.Equal(SessionKind.Guest, guest?.Kind);
        Assert.Null(guest?.DisplayName); // no initials until the guest picks some

        Assert.NotNull(await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library"));
        // Initials are validated the same way the form validates them.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/auth/initials", new InitialsRequest { Initials = "A1" })).StatusCode);
        (await client.PutAsJsonAsync("/auth/initials", new InitialsRequest { Initials = "mpk" })).EnsureSuccessStatusCode();
        Assert.Equal("MPK", (await client.GetFromJsonAsync<SessionDto>("/api/auth/session"))?.DisplayName);

        // A song made before there were initials moves into their collection (the same call the
        // endpoint makes with the browser's guest id), and the initials open it on another device.
        var store = factory.Services.GetRequiredService<JobStore>();
        const string early = "0123456789abcdef0123456789abcdef";
        Directory.CreateDirectory(store.JobDir(early));
        await store.SaveAsync(new JobState
        {
            JobId = early,
            InputFileName = "early.wav",
            CreatedAt = DateTimeOffset.UtcNow,
            Stage = JobStage.Complete,
            OwnerId = "guest:before-initials",
        }, CancellationToken.None);
        Assert.Equal(1, await AuthEndpoints.MoveCollectionAsync(store, "guest:before-initials", PoUser.CollectionOf("MPK"), CancellationToken.None));

        using var otherDevice = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        (await otherDevice.PostAsync("/auth/guest", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await otherDevice.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library") ?? [], e => e.JobId == early);
        (await otherDevice.PutAsJsonAsync("/auth/initials", new InitialsRequest { Initials = "MPK" })).EnsureSuccessStatusCode();
        Assert.Contains(await otherDevice.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library") ?? [], e => e.JobId == early);
    }
}
