using System.Diagnostics;
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
    SecretSourceInfo secretSource,
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

    public async Task<DiagnosticsReport> BuildReportAsync(CancellationToken ct)
    {
        var stores = await StoresAsync(ct);
        var services = await ServicesAsync(ct);
        return new DiagnosticsReport(
            Status: DiagnosticStatus.Worst(stores.Concat(services).Select(check => check.Status)),
            EnvironmentName: environment.EnvironmentName,
            CheckedAt: time.GetUtcNow(),
            Stores: stores,
            Services: services,
            QueueDepth: queue.Depth,
            DefaultPlan: await DefaultPlanAsync(ct),
            Operational: Operational(),
            Interpreters: interpreters.Snapshot());
    }

    private async Task<List<DiagnosticCheck>> StoresAsync(CancellationToken ct)
    {
        var report = await healthChecks.CheckHealthAsync(ct);
        var now = time.GetUtcNow();
        return
        [
            .. report.Entries.Select(entry => new DiagnosticCheck(
                Name: entry.Key == "job-storage" ? "Job storage" : entry.Key,
                Status: entry.Value.Status switch
                {
                    HealthStatus.Healthy => DiagnosticStatus.Healthy,
                    HealthStatus.Degraded => DiagnosticStatus.Degraded,
                    _ => DiagnosticStatus.Unhealthy,
                },
                Description: entry.Key == "job-storage"
                    ? "Keeps uploads, stems and finished analyses, mirrored to blob storage where configured."
                    : "A storage dependency of the app.",
                LatencyMs: Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                CheckedAt: now)),
        ];
    }

    private async Task<List<DiagnosticCheck>> ServicesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Where the secrets came from is not reported, only whether the vault was reachable when it
        // was meant to be: falling back to environment variables is the degraded case.
        var vault = new DiagnosticCheck(
            "Key Vault",
            secretSource.FellBack ? DiagnosticStatus.Degraded : DiagnosticStatus.Healthy,
            "Holds the app's secrets; read once at start-up.",
            null,
            now);

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
        var ai = new DiagnosticCheck(
            "AI interpreters",
            anyModel ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded,
            "Language models that write a song's summary and answer questions; a template stands in without one.",
            Math.Round(writersLatency, 1),
            now);

        var hardware = await hardwareProbe.ProbeAsync(ct);
        var models = new DiagnosticCheck(
            "Analysis models",
            hardware.Models.All(model => model.Available) ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded,
            $"Neural models for stems, pitch, chords and beats: {hardware.Models.Count(model => model.Available)} of {hardware.Models.Count} ready.",
            null,
            now);

        var notifications = new DiagnosticCheck(
            "Push notifications",
            push.Available ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded,
            "Tells a closed tab that its song has finished.",
            null,
            now);

        return [vault, ai, models, notifications];
    }

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
