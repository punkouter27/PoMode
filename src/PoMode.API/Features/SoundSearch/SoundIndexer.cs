using System.Collections.Concurrent;
using System.Threading.Channels;
using PoMode.API.Audio;
using PoMode.API.Features.Analysis;
using PoMode.API.Infrastructure;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SoundSearch;

/// <summary>A song's CLAP embedding, kept beside its analysis as <c>sound.json</c>. The model key is
/// stored so a change of checkpoint is seen as "not indexed" rather than compared across spaces.</summary>
public sealed record SoundVector(string Model, float[] Vector);

/// <summary>
/// Writes each finished song's <see cref="SoundVector"/> in the background: after a job completes
/// (through <see cref="OutcomeFanOut"/>), and for older songs the first time their owner searches by
/// sound. One at a time, like the rest of the aftercare, because the audio tower is a few seconds of
/// CPU a song and a search should never wait on it.
/// </summary>
public sealed class SoundIndexer(JobStore store, ClapEncoder clap, ILogger<SoundIndexer> logger) : BackgroundService
{
    public const string ArtifactName = "sound.json";

    private readonly Channel<string> _jobs = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _pending = new();

    /// <summary>The same songs the aftercare looks after: finished, the caller's own, not a kept beat.</summary>
    public static bool IsIndexable(JobState state)
        => state.Stage == JobStage.Complete && !state.IsServerOwned && state.Origin?.Kind is not TakeOrigin.Beat;

    /// <summary>For the library listing: queues a song only when its folder has no index yet, so a
    /// listing of an indexed library costs one file check a song and no reads.</summary>
    public void EnqueueIfMissing(JobState state)
    {
        if (!File.Exists(Path.Combine(store.JobDir(state.JobId), ArtifactName)))
        {
            Enqueue(state);
        }
    }

    public void Enqueue(JobState state)
    {
        if (clap.IsAvailable && IsIndexable(state) && _pending.TryAdd(state.JobId, 0))
        {
            _jobs.Writer.TryWrite(state.JobId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var indexed = await store.ReadArtifactAsync<SoundVector>(jobId, ArtifactName, stoppingToken);
                if (indexed?.Model != Model
                    && await store.LoadAsync(jobId, stoppingToken) is { } state
                    && store.TryFindInputPath(jobId) is { } input)
                {
                    var audio = await Task.Run(() => AudioDecoder.Decode(input), stoppingToken);
                    var vector = await clap.EmbedAudioAsync(audio, stoppingToken);
                    await store.WriteArtifactAsync(state.JobId, ArtifactName, new SoundVector(Model, vector), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Indexing the sound of job {JobId} failed.", jobId);
            }
            finally
            {
                _pending.TryRemove(jobId, out _);
            }
        }
    }

    public static string Model => ModelCatalog.ClapAudio.Key;
}
