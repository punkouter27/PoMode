namespace PoMode.Api.Features.Diagnostics;

public static class DiagnosticsEndpoints
{
    /// <summary>
    /// The JSON behind the /diag page. Under /api since 2026-11-17: at /diag itself it answered a
    /// browser's navigation with raw JSON instead of the page. Anonymous by design, so the payload is
    /// status words only (DiagnosticsService).
    /// </summary>
    public static IEndpointRouteBuilder MapDiagnostics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diag", async (DiagnosticsService diagnostics, CancellationToken ct)
            => TypedResults.Ok(await diagnostics.BuildReportAsync(ct)));
        return app;
    }
}
