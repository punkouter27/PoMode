using PoMode.Shared.Analysis;

namespace PoMode.Shared.Diagnostics;

/// <summary>The three words every check on /diag is summarised to.</summary>
public static class DiagnosticStatus
{
    public const string Healthy = "Healthy";
    public const string Degraded = "Degraded";
    public const string Unhealthy = "Unhealthy";

    /// <summary>The worst of <paramref name="statuses"/>; Healthy when there are none.</summary>
    public static string Worst(IEnumerable<string> statuses)
    {
        var worst = Healthy;
        foreach (var status in statuses)
        {
            if (status == Unhealthy)
            {
                return Unhealthy;
            }
            if (status == Degraded)
            {
                worst = Degraded;
            }
        }
        return worst;
    }
}

/// <summary>
/// The anonymous <c>GET /api/diag</c> payload behind the /diag page (2026-11-17: moved off /diag so a
/// browser lands on the page, not the JSON). Every check is reduced to a status word, a fixed
/// sentence saying what the dependency is for and a latency. Nothing identifying goes out: no
/// secret source, vault or storage address, path, host, key or exception text.
/// </summary>
public sealed record DiagnosticsReport(
    string Status,
    string EnvironmentName,
    DateTimeOffset CheckedAt,
    IReadOnlyList<DiagnosticCheck> Stores,
    IReadOnlyList<DiagnosticCheck> Services,
    /// <summary>Jobs waiting for the single-concurrency worker right now.</summary>
    int QueueDepth = 0,
    /// <summary>What the planner would pick for an upload arriving now (no browser tier declared);
    /// null when no executor set is available at all.</summary>
    IReadOnlyList<StagePlan>? DefaultPlan = null,
    /// <summary>The guards and exporters that are switched on. Presence only.</summary>
    OperationalReport? Operational = null,
    /// <summary>How each song interpreter has done since start-up (InterpreterStats).</summary>
    IReadOnlyList<InterpreterStatsDto>? Interpreters = null);

/// <summary>One store or external service: what it is for, how it is, and how long asking took
/// (null when the status is known without a round trip).</summary>
public sealed record DiagnosticCheck(
    string Name,
    string Status,
    string Description,
    double? LatencyMs,
    DateTimeOffset CheckedAt);

/// <summary>One interpreter's record since start-up: replies that passed the grounding check, replies
/// sent back for inventing a figure, and failures the next interpreter answered in its place.</summary>
public sealed record InterpreterStatsDto(string Name, int Replies, int GroundingRetries, int FellThrough);

/// <summary>
/// Which operational guards this instance is running with.
///
/// <para>Worth reporting because every one of them is invisible from the outside when it is working
/// and indistinguishable from a bug when it is not: a 429 from a rate limiter and a 429 from a
/// misconfigured proxy look identical to a user, and an app exporting no telemetry looks exactly
/// like an app whose collector is down.</para>
/// </summary>
public sealed record OperationalReport(
    bool RateLimitsEnabled,
    int MaxQueueDepth,
    /// <summary>Whether this instance has a VAPID key pair, i.e. can notify a closed tab at all.</summary>
    bool PushAvailable = false,
    /// <summary>Whether traces and metrics are exported to Application Insights. Presence only.</summary>
    bool TelemetryEnabled = false);
