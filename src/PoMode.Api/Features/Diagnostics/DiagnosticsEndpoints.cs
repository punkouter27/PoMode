using PoMode.Shared.Diagnostics;

namespace PoMode.Api.Features.Diagnostics;

public static class DiagnosticsEndpoints
{
    /// <summary>
    /// <c>GET /api/diag</c>: the standard DiagReportDto behind the /diag "System status" page. Anonymous by
    /// design, so the payload is status words only (DiagnosticsService). <c>GET /api/diag/activity</c> carries
    /// the operator extras (queue, guards, interpreters, default plan), presence and counts only.
    /// <c>GET /health</c> answers the same cached report's overall status (plain text, 503 when Unhealthy),
    /// so a health ping never runs the outside probes a second time within the cache's minute.
    /// </summary>
    public static IEndpointRouteBuilder MapDiagnostics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diag", async (DiagnosticsService diagnostics, CancellationToken ct)
            => TypedResults.Ok(await diagnostics.GetReportAsync(ct)));
        app.MapGet("/health", async (DiagnosticsService diagnostics, CancellationToken ct) =>
        {
            var report = await diagnostics.GetReportAsync(ct);
            return Results.Text(report.Status,
                statusCode: report.Status == DiagnosticStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        });
        app.MapGet("/api/diag/activity", async (DiagnosticsService diagnostics, CancellationToken ct)
            => TypedResults.Ok(await diagnostics.BuildActivityAsync(ct)));
        return app;
    }
}
