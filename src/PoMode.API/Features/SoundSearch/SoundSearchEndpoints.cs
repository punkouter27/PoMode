using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Auth;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SoundSearch;

/// <summary>
/// <c>GET /api/library/sound?q=dark acoustic</c>: the caller's songs whose sound is nearest the words,
/// best first. The library box asks this only for words that are not a mode, key, tempo or kind and
/// match no file name, so "dorian 120" never reaches it.
/// </summary>
public static class SoundSearchEndpoints
{
    /// <summary>CLAP's text-to-audio cosines sit low (0.1 to 0.5 for a real match), so an absolute
    /// floor only drops songs nothing describes; the cut that matters is distance from the best.</summary>
    internal const double MinScore = 0.08;
    internal const double Spread = 0.1;
    internal const int MaxResults = 8;

    public static IEndpointRouteBuilder MapSoundSearch(this IEndpointRouteBuilder app)
    {
        // 404 rather than an empty list when the server cannot listen at all (Azure, or the models not
        // yet downloaded), so the page can tell "nothing sounds like that" from "can't search by sound".
        app.MapGet("/api/library/sound", async Task<Results<Ok<List<SoundMatchDto>>, NotFound, BadRequest>> (
            string? q, HttpContext context, JobStore store, ClapEncoder clap, SoundIndexer indexer, CancellationToken ct) =>
        {
            var owner = PoUser.IdOf(context.User);
            if (owner is null || !clap.IsAvailable)
            {
                return TypedResults.NotFound();
            }
            if (string.IsNullOrWhiteSpace(q) || q.Length > 200)
            {
                return TypedResults.BadRequest();
            }

            var scored = new List<(string JobId, double Score)>();
            float[]? words = null;
            foreach (var jobId in store.ListJobIds())
            {
                if (!JobId.IsValid(jobId)
                    || await store.LoadAsync(jobId, ct) is not { } state
                    || state.OwnerId != owner || !SoundIndexer.IsIndexable(state))
                {
                    continue;
                }
                if (await store.ReadArtifactAsync<SoundVector>(jobId, SoundIndexer.ArtifactName, ct) is not { } sound
                    || sound.Model != SoundIndexer.Model)
                {
                    indexer.Enqueue(state); // older than the index: it joins the next search
                    continue;
                }
                words ??= await clap.EmbedTextAsync(q, ct);
                scored.Add((jobId, ClapEncoder.Cosine(words, sound.Vector)));
            }
            return TypedResults.Ok(Rank(scored));
        }).RequireAuthorization();

        return app;
    }

    /// <summary>Best first, kept while within <see cref="Spread"/> of the best and above
    /// <see cref="MinScore"/>, at most <see cref="MaxResults"/>.</summary>
    internal static List<SoundMatchDto> Rank(IEnumerable<(string JobId, double Score)> scored)
    {
        var ordered = scored.OrderByDescending(s => s.Score).ToList();
        if (ordered.Count == 0)
        {
            return [];
        }
        var floor = Math.Max(MinScore, ordered[0].Score - Spread);
        return [.. ordered.TakeWhile(s => s.Score >= floor).Take(MaxResults)
            .Select(s => new SoundMatchDto(s.JobId, Math.Round(s.Score, 3)))];
    }
}
