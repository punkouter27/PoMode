using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// /api/diag is anonymous, so what it says is what anyone on the internet learns. It has to describe
/// the app's health without naming anything that identifies or unlocks it.
/// </summary>
public sealed class DiagnosticsReportTests : IDisposable
{
    private const string FakeSecret = "sk-super-secret-value-9000";
    private const string FakeEndpoint = "https://secret-openai-host.openai.azure.com/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-diag-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-diag-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    [Fact]
    public async Task Diag_json_is_anonymous_and_carries_no_secret_path_host_or_exception()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Jobs:Storage:Mode", "FileSystem")
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false")
            .UseSetting("Demo:Enabled", "false")
            .UseSetting("PoMode:AzureOpenAI:Endpoint", FakeEndpoint)
            .UseSetting("PoMode:AzureOpenAI:Deployment", "gpt-secret-deployment")
            .UseSetting("PoMode:AzureOpenAI:ApiKey", FakeSecret));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/diag");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":", json, StringComparison.Ordinal);
        Assert.Contains("\"checks\":", json, StringComparison.Ordinal);
        Assert.Contains("\"group\":\"api\"", json, StringComparison.Ordinal);
        Assert.Contains("\"version\":", json, StringComparison.Ordinal);
        string[] forbidden =
        [
            FakeSecret, "secret-openai-host", "gpt-secret-deployment", _root, _modelsRoot,
            Path.GetTempPath(), Environment.MachineName, Environment.UserName,
            "://", "vault.azure", "blob.core", "AccountKey", "Exception", "secretSource", "fellBack",
        ];
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);
        }
        // Escaped backslashes would be a Windows path, whatever its root.
        Assert.DoesNotContain("\\\\", json, StringComparison.Ordinal);
    }
}
