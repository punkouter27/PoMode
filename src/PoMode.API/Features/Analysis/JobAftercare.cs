using System.Threading.Channels;
using PoMode.API.Audio;
using PoMode.API.Features.Lyrics;
using PoMode.API.Features.SongIdentity;
using PoMode.API.Features.SongStatistics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// Work worth doing for a finished song before anyone asks for it, so opening it is instant: the
/// written summary (the default interpreter's, which then sits in its cache), what the song is (its tags,
/// else AcoustID) and the lyrics (LRCLIB's published ones for a named song, else Whisper's). One at a
/// time, in the background, after the analysis worker has moved on, because both are seconds to a
/// minute of model time that a person opening the song would otherwise wait through.
/// </summary>
/// <remarks>
/// Best-effort throughout: a failure is logged and the song simply does these on demand (the summary)
/// or not at all (the lyrics), as it did before. Beats, the server's own demo template and cancelled
/// or failed jobs are skipped; a sung take gets the summary but no lyrics, since humming has no words.
/// </remarks>
public sealed class JobAftercare(
    JobStore store,
    SongInterpreterSelector interpreters,
    LyricsTranscriber lyrics,
    SongIdentifier identifier,
    LrclibClient lrclib,
    ILogger<JobAftercare> logger) : BackgroundService
{
    private readonly Channel<string> _jobs = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(JobState state)
    {
        if (state.Stage == JobStage.Complete && !state.IsServerOwned && state.Origin?.Kind is not TakeOrigin.Beat)
        {
            _jobs.Writer.TryWrite(state.JobId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            if (await store.LoadAsync(jobId, stoppingToken) is not { } state)
            {
                continue;
            }
            await TryAsync("summary", jobId, () => WriteSummaryAsync(jobId, stoppingToken), stoppingToken);
            if (TakeOrigin.IsSung(state.Origin?.Kind))
            {
                continue;
            }
            SongIdentityDto? identity = null;
            await TryAsync("identity", jobId, async () => identity = await identifier.IdentifyAsync(state, stoppingToken), stoppingToken);
            var published = false;
            if (identity is not null)
            {
                await TryAsync("published lyrics", jobId,
                    async () => published = await WritePublishedLyricsAsync(state, identity, stoppingToken), stoppingToken);
            }
            if (!published)
            {
                await TryAsync("lyrics", jobId, () => lyrics.TranscribeAsync(state, stoppingToken), stoppingToken);
            }
        }
    }

    /// <summary>Runs the summary exactly as the page would ask for it, which writes it to the cache.</summary>
    private async Task WriteSummaryAsync(string jobId, CancellationToken ct)
    {
        if (await SongStatsEndpoints.BuildAsync(jobId, store, ct) is not { } stats)
        {
            return;
        }
        await foreach (var _ in interpreters.StreamAsync(jobId, stats, question: null, history: null, requested: null, ct))
        {
        }
    }

    /// <summary>LRCLIB's timed lyrics as the song's lyrics, when it has them for this song at this
    /// length; false leaves the song to Whisper. An instrumental is written as no lines, not misheard.</summary>
    private async Task<bool> WritePublishedLyricsAsync(JobState state, SongIdentityDto identity, CancellationToken ct)
    {
        if (AudioDecoder.TryReadDurationSeconds(store.InputPath(state)) is not { } duration
            || await lrclib.SyncedAsync(identity, duration, ct) is not { } lines)
        {
            return false;
        }
        await store.WriteArtifactAsync(state.JobId, LyricsTranscriber.ArtifactName, lines.ToList(), ct);
        if (lines.Count > 0)
        {
            await identifier.StampAsync(state.JobId, identity with { LyricsSource = "LRCLIB" }, ct);
        }
        return true;
    }

    private async Task TryAsync(string what, string jobId, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Writing the {What} ahead of time failed for job {JobId}.", what, jobId);
        }
    }
}
