using PoMode.Shared.Analysis;

namespace PoMode.Shared.Diagnostics;

/// <summary>The four words every check on /diag is summarised to.</summary>
public static class DiagnosticStatus
{
    public const string Healthy = "Healthy";
    public const string Degraded = "Degraded";
    public const string Unhealthy = "Unhealthy";

    /// <summary>
    /// Switched off on purpose on this server (no key, not used in this environment): neutral, never
    /// counted against the overall status.
    /// </summary>
    public const string Off = "Off";

    /// <summary>The worst of <paramref name="statuses"/>, ignoring <see cref="Off"/>; Healthy when there are none.</summary>
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
/// What <c>GET /api/diag</c> answers (Po web standard, AGENTS.md) and the <c>/diag</c> "System status" page shows.
/// Anonymous, so plain words only: never a connection string, key, URL, path, host, id, email or exception message.
/// </summary>
/// <param name="Status">"Healthy", "Degraded" or "Unhealthy": the worst of <paramref name="Checks"/> ("Off" rows don't count).</param>
/// <param name="Environment">The host environment name (Production, Development, …).</param>
/// <param name="Version">The build's <c>BuildVersion</c> stamp, UTC <c>yyMMdd</c>.</param>
/// <param name="CheckedAt">When the checks ran (UTC).</param>
/// <param name="CheckedMs">How long all the checks took together, in milliseconds.</param>
public sealed record DiagReportDto(
    string Status,
    string Environment,
    string Version,
    DateTimeOffset CheckedAt,
    double CheckedMs,
    DiagCheckDto[] Checks);

/// <summary>One row on <c>/diag</c>: one dependency the app really calls.</summary>
/// <param name="Name">Stable machine id (e.g. "api", "job-storage").</param>
/// <param name="Group">"api", "data" or "service": the section it is shown in.</param>
/// <param name="Label">What the page calls it.</param>
/// <param name="Status">"Healthy", "Degraded", "Unhealthy", or "Off" (switched off on purpose).</param>
/// <param name="Purpose">What the app uses it for, in one line.</param>
/// <param name="Detail">How it is, in one line.</param>
/// <param name="LatencyMs">How long the check took.</param>
/// <param name="Problem">What is wrong, in plain words, when it is not Healthy.</param>
public sealed record DiagCheckDto(
    string Name,
    string Group,
    string Label,
    string Status,
    string Purpose,
    string Detail,
    double LatencyMs,
    string? Problem);

/// <summary>
/// The operator extras that used to sit beside the checks on /diag, now at <c>GET /api/diag/activity</c> so the
/// /diag contract stays the standard shape. Anonymous as well, so presence and counts only.
/// </summary>
public sealed record DiagnosticsActivity(
    /// <summary>Jobs waiting for the single-concurrency worker right now.</summary>
    int QueueDepth,
    /// <summary>What the planner would pick for an upload arriving now (no browser tier declared);
    /// null when no executor set is available at all.</summary>
    IReadOnlyList<StagePlan>? DefaultPlan,
    /// <summary>The guards and exporters that are switched on. Presence only.</summary>
    OperationalReport Operational,
    /// <summary>How each song interpreter has done since start-up (InterpreterStats).</summary>
    IReadOnlyList<InterpreterStatsDto> Interpreters);

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
