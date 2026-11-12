using PoMode.Api.Features.Push;
using PoMode.Api.Features.SoundSearch;

namespace PoMode.Api.Features.Analysis;

/// <summary>Everything that wants to hear a job finished: the owner's browsers (Web Push), the work
/// done ahead of the owner opening it (<see cref="JobAftercare"/>), and the library's sound index.</summary>
public sealed class OutcomeFanOut(WebPushOutcomeNotifier push, JobAftercare aftercare, SoundIndexer sound) : IJobOutcomeNotifier
{
    public async Task NotifyAsync(JobState state, CancellationToken ct)
    {
        aftercare.Enqueue(state);
        sound.Enqueue(state);
        await push.NotifyAsync(state, ct);
    }
}
