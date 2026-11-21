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
/// Unhealthy with a fixed sentence of our own: health-check descriptions and exceptions are never
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
    TimeProvider time)
{
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(1.5);

    /// <summary>The build stamp (UTC yyMMdd) from the one BuildVersion assembly metadata key.</summary>
    public static string BuildVersion { get; } =
        typeof(DiagnosticsService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildVersion")?.Value ?? "dev";

    public async Task<DiagReportDto> BuildReportAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        List<DiagCheckDto> checks =
        [
            new("api", "api", "PoMode API", DiagnosticStatus.Healthy,
                "Answers every page, upload and analysis.", "Answering.", 0, null),
            .. await StoresAsync(ct),
            .. await ServicesAsync(ct),
        ];
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
        var ai = new DiagCheckDto(
            "ai-interpreters", "service", "AI interpreters",
            anyModel ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded,
            "Language models that write a song's summary and answer questions.",
            anyModel ? "A language model is ready." : "No language model answered; a template writes the summary.",
            Math.Round(writersLatency, 1),
            anyModel ? null : "No language model is available.");

        var probeStarted = Stopwatch.GetTimestamp();
        var hardware = await hardwareProbe.ProbeAsync(ct);
        var ready = hardware.Models.Count(model => model.Available);
        var allReady = ready == hardware.Models.Count;
        var models = new DiagCheckDto(
            "analysis-models", "service", "Analysis models",
            allReady ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded,
            "Neural models for stems, pitch, chords and beats.",
            $"{ready} of {hardware.Models.Count} ready.",
            Math.Round(Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds, 1),
            allReady ? null : "Some models are not downloaded; simpler analysers stand in.");

        var notifications = new DiagCheckDto(
            "push", "service", "Push notifications",
            push.Available ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded,
            "Tells a closed tab that its song has finished.",
            push.Available ? "Set up." : "Not set up on this server.",
            0,
            push.Available ? null : "No push key pair is configured.");

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
