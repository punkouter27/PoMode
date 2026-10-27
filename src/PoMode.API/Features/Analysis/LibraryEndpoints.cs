using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Auth;
using PoMode.API.Features.Demo;
using PoMode.API.Features.SoundSearch;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibrary(this IEndpointRouteBuilder app)
    {
        // The caller's own jobs only. Unguessable ids are the only gate on the per-job read endpoints,
        // so a listing that enumerated anyone else's would defeat that. Jobs from before ownership
        // existed belong to nobody and are listed for nobody.
        app.MapGet("/api/library", async Task<Ok<List<LibraryEntryDto>>> (
            HttpContext context, JobStore store, DemoLibrary demo, SoundIndexer sound, CancellationToken ct) =>
        {
            var owner = PoUser.IdOf(context.User);
            var entries = new List<LibraryEntryDto>();
            foreach (var jobId in store.ListJobIds())
            {
                if (!JobId.IsValid(jobId))
                {
                    continue; // a stray folder (temp dir, manual copy) is not a job
                }
                var state = await store.LoadAsync(jobId, ct);
                if (state is null || owner is null || state.OwnerId != owner)
                {
                    continue;
                }
                // Listing the library is when a song not yet heard by the sound search gets queued, so
                // it is ready by the time anyone types a description into the box.
                sound.EnqueueIfMissing(state);
                entries.Add(await EntryOfAsync(state, store, ct));
            }

            // An empty library is the moment a new user gets the demo; DemoLibrary says why here.
            if (entries.Count == 0 && owner is not null && await demo.TrySeedAsync(owner, ct) is { } seeded)
            {
                sound.EnqueueIfMissing(seeded);
                entries.Add(await EntryOfAsync(seeded, store, ct));
            }
            return TypedResults.Ok(entries.OrderByDescending(e => e.CreatedAt).ToList());
        }).RequireAuthorization();

        // Removes one of the caller's songs, cancelling it first if it is still running. Someone else's
        // answers exactly like a missing one, as everywhere else. 409 when a file in it is still open
        // (a stem mid-stream); trying again a moment later works.
        app.MapDelete("/api/library/{jobId}", async Task<Results<NoContent, NotFound, Conflict>> (
            string jobId, HttpContext context, JobStore store, JobCancellationRegistry cancellations,
            CancellationToken ct) =>
        {
            var owner = PoUser.IdOf(context.User);
            if (owner is null || !JobId.IsValid(jobId)
                || await store.LoadAsync(jobId, ct) is not { } state || state.OwnerId != owner)
            {
                return TypedResults.NotFound();
            }
            cancellations.TryCancel(jobId);
            return store.TryDelete(jobId) ? TypedResults.NoContent() : TypedResults.Conflict();
        }).RequireAuthorization();

        // The caller's other analysed songs, most alike first (SongSimilarity). Only their own library
        // is searched, the same rule as the listing above.
        app.MapGet("/api/library/{jobId}/similar", async Task<Results<Ok<List<SimilarSongDto>>, NotFound>> (
            string jobId, HttpContext context, JobStore store, CancellationToken ct) =>
        {
            var owner = PoUser.IdOf(context.User);
            if (owner is null || !JobId.IsValid(jobId)
                || await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct) is not { } song)
            {
                return TypedResults.NotFound();
            }
            var alike = new List<SimilarSongDto>();
            foreach (var otherId in store.ListJobIds())
            {
                if (otherId == jobId || !JobId.IsValid(otherId)
                    || await store.LoadAsync(otherId, ct) is not { Stage: JobStage.Complete } other
                    || other.OwnerId != owner || other.Origin?.Kind == TakeOrigin.Beat
                    || await store.ReadArtifactAsync<ModalResult>(otherId, "result.json", ct) is not { } result)
                {
                    continue;
                }
                var score = SongSimilarity.Score(song, result);
                if (score >= SongSimilarity.MinScore)
                {
                    alike.Add(new SimilarSongDto(otherId, other.InputFileName, Math.Round(score, 3), SongSimilarity.Reason(song, result)));
                }
            }
            return TypedResults.Ok(alike.OrderByDescending(s => s.Score).Take(5).ToList());
        }).RequireAuthorization();

        return app;
    }

    private static async Task<LibraryEntryDto> EntryOfAsync(JobState state, JobStore store, CancellationToken ct)
    {
        // The pipeline stamps headline facts on completion; anything missing from the
        // job.json (legacy jobs, or completed runs where the engine found no mode) pays
        // the full result.json read as a fallback. PrimaryMode is the only field that
        // can legitimately be null after a successful run, so it is the most common
        // reason to fall back; we check all three rather than guessing.
        var tonicName = state.TonicName;
        var primaryMode = state.PrimaryMode;
        var tempoBpm = state.TempoBpm;
        if (state.Stage == JobStage.Complete
            && (tonicName is null || primaryMode is null || tempoBpm is null)
            && await store.ReadArtifactAsync<ModalResult>(state.JobId, "result.json", ct) is { } result)
        {
            tonicName ??= result.TonicName;
            primaryMode ??= result.PrimaryMode?.ToString();
            tempoBpm ??= result.TempoBpm;
        }
        return new LibraryEntryDto(
            state.JobId,
            state.InputFileName,
            state.CreatedAt,
            state.Stage,
            tonicName,
            primaryMode,
            tempoBpm,
            state.Origin);
    }
}
