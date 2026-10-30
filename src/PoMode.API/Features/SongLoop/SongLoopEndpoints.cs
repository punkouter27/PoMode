using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongLoop;

public static class SongLoopEndpoints
{
    public static IEndpointRouteBuilder MapSongLoop(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis");
        group.AddEndpointFilter<JobIdEndpointFilter>();

        // Derived per request from the stored chords, same ruling as /structure: nothing to keep in
        // step with the chords if a rerun replaces them. 404 when the song never comes round twice.
        group.MapGet("/{jobId}/loop", async Task<Results<Ok<SongLoopDto>, NotFound>> (
            string jobId, JobStore store, HooktheoryClient hooktheory, CancellationToken ct) =>
            await FindAsync(jobId, store, ct) is { } loop
                ? TypedResults.Ok(new SongLoopDto(
                    SongLoops.Id(loop.Mode, loop.Bpm, loop.Found.Formulas),
                    [.. loop.Found.Chords.Select(chord => chord.Symbol)],
                    string.Join(" - ", loop.Found.Formulas.Select(formula => formula[..formula.IndexOf(':')])),
                    loop.Found.Repeats,
                    hooktheory.IsConfigured && HooktheoryClient.ChordPath(loop.Found.Formulas) is not null))
                : TypedResults.NotFound())
            .WithTags("Analysis")
            .WithName("GetSongLoop")
            .WithSummary("The chord cycle the song repeats most, as a progression the Mode Lab and Practice can play.");

        // Asked only when the person opens the list. 404 when Hooktheory is not configured, the loop
        // has no Hooktheory name, or nobody answered; an empty list when it knows no such song.
        group.MapGet("/{jobId}/loop/songs", async Task<Results<Ok<List<ProgressionSongDto>>, NotFound>> (
            string jobId, JobStore store, HooktheoryClient hooktheory, CancellationToken ct) =>
            await FindAsync(jobId, store, ct) is { } loop
            && HooktheoryClient.ChordPath(loop.Found.Formulas) is { } path
            && await hooktheory.SongsAsync(path, ct) is { } songs
                ? TypedResults.Ok(songs.ToList())
                : TypedResults.NotFound())
            .WithTags("Analysis")
            .WithName("GetSongsOnLoop")
            .WithSummary("Published songs (Hooktheory TheoryTab) whose chords match this song's loop.");

        return app;
    }

    private static async Task<(SongLoopFinder.Loop Found, ScaleMode Mode, double Bpm)?> FindAsync(
        string jobId, JobStore store, CancellationToken ct)
    {
        if (await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct) is not { } result)
        {
            return null;
        }
        var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);
        var tempoMap = await store.ReadArtifactAsync<TempoMapDto>(jobId, "tempo-map.json", ct);
        var beats = await store.ReadArtifactAsync<BeatGridDto>(jobId, "beats.json", ct);
        var bpm = Math.Clamp(result.TempoBpm > 0 ? result.TempoBpm : 100, 50, 180);
        return SongLoopFinder.Find(chords, result, tempoMap, beats) is { } found ? (found, found.Mode, bpm) : null;
    }
}
