using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Serialization;
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
        // Every outside service the app calls has its own row (AGENTS.md, Health + /diag contract).
        foreach (var name in new[] { "job-storage", "blob-storage", "key-vault", "app-insights", "acoustid", "lrclib", "hooktheory", "model-hosts", "ai-interpreters", "analysis-models", "push" })
        {
            Assert.Contains($"\"name\":\"{name}\"", json, StringComparison.Ordinal);
        }
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

    [Fact]
    public async Task Services_switched_off_on_purpose_read_Off_and_never_lower_the_overall_status()
    {
        // No vault, no blob mirror, no AcoustID/Hooktheory key, no model downloads: all off on purpose.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Jobs:Storage:Mode", "FileSystem")
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false")
            .UseSetting("Demo:Enabled", "false"));
        using var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync("/api/diag", PoModeJsonContext.Default.DiagReportDto);

        Assert.NotNull(report);
        var byName = report.Checks.ToDictionary(check => check.Name);
        foreach (var name in new[] { "blob-storage", "key-vault", "acoustid", "hooktheory", "analysis-models" })
        {
            Assert.Equal(DiagnosticStatus.Off, byName[name].Status);
            Assert.Null(byName[name].Problem);
        }
        Assert.True(byName["api"].LatencyMs > 0, "the API row is measured, not a constant");
        Assert.Equal(DiagnosticStatus.Worst(report.Checks.Where(c => c.Status != DiagnosticStatus.Off).Select(c => c.Status)), report.Status);
        Assert.Equal(DiagnosticStatus.Healthy, DiagnosticStatus.Worst([DiagnosticStatus.Off, DiagnosticStatus.Healthy]));
    }
}
