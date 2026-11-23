using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoMode.Api.Features.Analysis;
using PoMode.Api.Features.Push;
using PoMode.Api.Features.SongStatistics;
using PoMode.Api.Infrastructure;
using PoMode.Api.Pipeline;
using PoMode.Api.Platform;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;

namespace PoMode.Api.Features.Diagnostics;

/// <summary>
/// Builds the anonymous /api/diag report. Every dependency is summarised to Healthy / Degraded /
/// Unhealthy (or Off, when it is switched off on purpose on this server) with a fixed sentence of our own: health-check descriptions and exceptions are never
/// passed through (they can name a path, a host or an account), and where secrets come from is
/// reported only as the Key Vault check's status.
/// </summary>
public sealed class DiagnosticsService(
    IHostEnvironment environment,
    ExternalServiceProbes external,
    HardwareProbe hardwareProbe,
    HealthCheckService healthChecks,
    JobQueue queue,
    ExecutionPlanner planner,
    IConfiguration configuration,
    PushSettings push,
    SongInterpreterSelector interpreterSelector,
    InterpreterStats interpreters,
    DiagReportCache cache,
    TimeProvider time)
{
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(1.5);

    /// <summary>The build stamp (UTC yyMMdd) from the one BuildVersion assembly metadata key.</summary>
    public static string BuildVersion { get; } =
        typeof(DiagnosticsService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildVersion")?.Value ?? "dev";

    /// <summary>The report /health and /api/diag answer with: built at most once a minute
    /// (<see cref="DiagReportCache"/>), so the live probes run once however often the page is loaded.</summary>
    public Task<DiagReportDto> GetReportAsync(CancellationToken ct) =>
        cache.GetAsync(() => BuildReportAsync(CancellationToken.None)).WaitAsync(ct);

    public async Task<DiagReportDto> BuildReportAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        List<DiagCheckDto> checks =
        [
            .. await StoresAsync(ct),
            .. await ServicesAsync(ct),
        ];
        // The API row is measured: the time the API took to build this report, checks included.
        checks.Add(new("api", "api", "PoMode API", DiagnosticStatus.Healthy,
            "Answers every page, upload and analysis.", "Answering.",
            Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), null));
        // Data rows first, then services: the page groups by Group but keeps this order inside each.
        checks = [.. checks.OrderBy(check => check.Group switch { "api" => 0, "data" => 1, _ => 2 })];
        return new DiagReportDto(
            Status: DiagnosticStatus.Worst(checks.Select(check => check.Status)),
            Environment: environment.EnvironmentName,
            Version: BuildVersion,
            CheckedAt: time.GetUtcNow(),
            CheckedMs: Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
            Checks: [.. checks]);
    }

    /// <summary>The operator extras (queue, guards, interpreters, default plan) for /api/diag/activity.</summary>
    public async Task<DiagnosticsActivity> BuildActivityAsync(CancellationToken ct) => new(
        QueueDepth: queue.Depth,
        DefaultPlan: await DefaultPlanAsync(ct),
        Operational: Operational(),
        Interpreters: interpreters.Snapshot());

    private async Task<List<DiagCheckDto>> StoresAsync(CancellationToken ct)
    {
        var report = await healthChecks.CheckHealthAsync(ct);
        return
        [
            .. report.Entries.Select(entry =>
            {
                var status = entry.Value.Status switch
                {
                    HealthStatus.Healthy => DiagnosticStatus.Healthy,
                    HealthStatus.Degraded => DiagnosticStatus.Degraded,
                    _ => DiagnosticStatus.Unhealthy,
                };
                return new DiagCheckDto(
                    Name: entry.Key,
                    Group: "data",
                    Label: entry.Key == "job-storage" ? "Job storage" : entry.Key,
                    Status: status,
                    Purpose: entry.Key == "job-storage"
                        ? "Keeps uploads, stems and finished analyses on this server's disk."
                        : "A storage dependency of the app.",
                    Detail: Plain(status),
                    LatencyMs: Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                    Problem: status == DiagnosticStatus.Healthy ? null : "Storage did not answer as expected.");
            }),
        ];
    }

    private async Task<List<DiagCheckDto>> ServicesAsync(CancellationToken ct)
    {
        // Blob storage, Key Vault, Application Insights and the web services, probed live in parallel
        // while the local checks below run.
        var externalTask = external.ProbeAllAsync(ct);

        // Capped: an Ollama that is not running can hold a probe for seconds, and the page waits on it.
        var started = Stopwatch.GetTimestamp();
        var anyModel = false;
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            probe.CancelAfter(ProbeBudget);
            try
            {
                anyModel = (await interpreterSelector.ListAsync(probe.Token)).Any(writer => writer.UsesLlm && writer.Available);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Too slow to count as available.
            }
        }
        var writersLatency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // Off when no model is set up on purpose (no cloud deployment, no Ollama address): the template
        // writes the summary by design. Degraded only when a model is set up and did not answer.
        var llmConfigured = !string.IsNullOrWhiteSpace(configuration["PoMode:AzureOpenAI:Endpoint"])
            || !string.IsNullOrWhiteSpace(configuration["Llm:Ollama:Endpoint"]);
        const string aiPurpose = "Language models that write a song's summary and answer questions.";
        var ai = anyModel
            ? new DiagCheckDto("ai-interpreters", "service", "AI interpreters", DiagnosticStatus.Healthy, aiPurpose,
                "A language model is ready.", Math.Round(writersLatency, 1), null)
            : llmConfigured
                ? new DiagCheckDto("ai-interpreters", "service", "AI interpreters", DiagnosticStatus.Degraded, aiPurpose,
                    "No language model answered; a template writes the summary.", Math.Round(writersLatency, 1),
                    "The language model that is set up did not answer.")
                : new DiagCheckDto("ai-interpreters", "service", "AI interpreters", DiagnosticStatus.Off, aiPurpose,
                    "Switched off: no language model on this server; a template writes the summary.",
                    Math.Round(writersLatency, 1), null);

        var probeStarted = Stopwatch.GetTimestamp();
        var hardware = await hardwareProbe.ProbeAsync(ct);
        var ready = hardware.Models.Count(model => model.Available);
        var allReady = ready == hardware.Models.Count;
        // Model downloads are switched off on purpose on Azure (ModelRegistry refuses there) or by
        // Models:AutoDownload=false: missing models are then Off, not Degraded.
        var downloadsOff = hardware.IsAzureHosted || !configuration.GetValue("Models:AutoDownload", true);
        var models = new DiagCheckDto(
            "analysis-models", "service", "Analysis models",
            allReady ? DiagnosticStatus.Healthy : downloadsOff ? DiagnosticStatus.Off : DiagnosticStatus.Degraded,
            "Neural models for stems, pitch, chords and beats.",
            allReady || !downloadsOff
                ? $"{ready} of {hardware.Models.Count} ready."
                : $"{ready} of {hardware.Models.Count} ready; the rest are switched off on this server and simpler analysers stand in.",
            Math.Round(Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds, 1),
            allReady || downloadsOff ? null : "Some models are not downloaded; simpler analysers stand in.");

        var notifications = new DiagCheckDto(
            "push", "service", "Push notifications",
            push.Available ? DiagnosticStatus.Healthy : DiagnosticStatus.Off,
            "Tells a closed tab that its song has finished.",
            push.Available ? "Set up." : "Switched off: no push key pair on this server.",
            0,
            null);

        var externalRows = await externalTask;
        return [.. externalRows, ai, models, notifications];
    }

    private static string Plain(string status) => status switch
    {
        DiagnosticStatus.Healthy => "Working normally.",
        DiagnosticStatus.Degraded => "Answering, but not fully.",
        _ => "Not answering.",
    };

    /// <summary>
    /// The guards this instance is running with. Presence only — in particular the OTLP endpoint is
    /// reported as a boolean, because that URL can carry credentials in a header and this payload
    /// redacts everything that could.
    /// </summary>
    private OperationalReport Operational() => new(
        RateLimitsEnabled: PoRateLimits.IsEnabled(configuration),
        MaxQueueDepth: configuration.GetValue("Jobs:MaxQueueDepth", 24),
        PushAvailable: push.Available,
        TelemetryEnabled: Telemetry.IsEnabled(configuration));

    private async Task<List<StagePlan>?> DefaultPlanAsync(CancellationToken ct)
    {
        try
        {
            return await planner.PlanAsync(ct);
        }
        catch (InvalidOperationException)
        {
            return null; // no executor set available — the report shows the gap as null
        }
    }
}
