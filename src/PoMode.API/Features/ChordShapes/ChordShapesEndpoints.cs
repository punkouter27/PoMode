using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordShapes;

/// <summary>
/// <c>GET /api/analysis/{jobId}/shapes</c>: the song's chords as a player would finger them, with the
/// capo that makes them easiest (<see cref="CapoAdvisor"/>). The fingerings themselves are chords-db's,
/// drawn by the browser from <c>/web-runtime/guitar.json</c> and <c>ukulele.json</c>.
/// </summary>
public static class ChordShapesEndpoints
{
    public static IEndpointRouteBuilder MapChordShapes(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/analysis").MapGet("/{jobId}/shapes", async Task<Results<Ok<ChordShapesDto>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);
            var shapes = CapoAdvisor.Advise(chords);
            return shapes.Chords.Count == 0 ? TypedResults.NotFound() : TypedResults.Ok(shapes);
        }).AddEndpointFilter<JobIdEndpointFilter>();

        // The Practice page's open-chord jams, made fresh each time. The seed names a jam, so the same
        // seed always gives the same one back; without one the server picks.
        app.MapGet("/api/chord-jams/random", (int? seed) => TypedResults.Ok(OpenChordJams.Generate(seed ?? Random.Shared.Next())));

        return app;
    }
}
