using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.Api.Features.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace PoMode.Integration;

/// <summary>
/// The blob mirror is the only job storage Azure keeps across a restart, so the round trip it exists
/// for is tested against the emulator: a mirrored file comes back byte for byte, and a purge removes
/// every blob under the job. Without Azurite (<c>docker compose up -d</c>) there is nothing to test
/// against, and the test says so and passes.
/// </summary>
public sealed class JobBlobStorageTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_mirrored_file_downloads_unchanged_and_a_purge_removes_it()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:Storage:Mode"] = "Auto",
            ["Jobs:Storage:Container"] = $"pomode-test-{Guid.NewGuid():N}",
        }).Build();
        var storage = new JobBlobStorage(configuration, new DevelopmentHost(), NullLogger<JobBlobStorage>.Instance);
        if (await storage.ProbeAsync(CancellationToken.None) != BlobMirrorStatus.Connected)
        {
            output.WriteLine("Azurite is not running (docker compose up -d); the blob round trip was not tested.");
            return;
        }

        var jobId = Guid.NewGuid().ToString("N");
        var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
        var local = Path.Combine(Path.GetTempPath(), $"{jobId}.bin");
        await File.WriteAllBytesAsync(local, bytes);
        try
        {
            await storage.MirrorFileAsync(jobId, "result.json", local, CancellationToken.None);
            Assert.Equal(bytes, await storage.TryDownloadAsync(jobId, "result.json", CancellationToken.None));

            storage.DeleteJobBlobs(jobId);
            Assert.Null(await storage.TryDownloadAsync(jobId, "result.json", CancellationToken.None));
        }
        finally
        {
            File.Delete(local);
        }
    }

    private sealed class DevelopmentHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "PoMode.Integration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
