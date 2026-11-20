namespace PoMode.Api.Features.Diagnostics;

public static class DiagnosticsEndpoints
{
    /// <summary>
    /// <c>GET /api/diag</c>: the standard DiagReportDto behind the /diag "System status" page. Anonymous by
    /// design, so the payload is status words only (DiagnosticsService). <c>GET /api/diag/activity</c> carries
    /// the operator extras (queue, guards, interpreters, default plan), presence and counts only.
    /// </summary>
    public static IEndpointRouteBuilder MapDiagnostics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diag", async (DiagnosticsService diagnostics, CancellationToken ct)
            => TypedResults.Ok(await diagnostics.BuildReportAsync(ct)));
        app.MapGet("/api/diag/activity", async (DiagnosticsService diagnostics, CancellationToken ct)
            => TypedResults.Ok(await diagnostics.BuildActivityAsync(ct)));
        return app;
    }
}
