using Microsoft.Extensions.Time.Testing;
using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.PitchTracking;

public class ClientWorkRegistryTests
{
    private static readonly IReadOnlyList<NoteEvent> Notes = [new NoteEvent(60, 0.0, 0.5, 90)];

    [Fact]
    public async Task A_waiter_completes_once_and_timeouts_or_cancels_leave_none_behind()
    {
        var registry = new ClientWorkRegistry<IReadOnlyList<NoteEvent>>(new FakeTimeProvider());
        var wait = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(registry.TryComplete("job1", Notes));
        Assert.False(registry.TryComplete("job1", [new NoteEvent(72, 1.0, 0.5, 90)]));
        Assert.Equal(Notes, await wait);

        // A waiter that times out or is cancelled is not left behind.
        var time = new FakeTimeProvider();
        var timed = new ClientWorkRegistry<IReadOnlyList<NoteEvent>>(time);
        var expiring = timed.WaitAsync("job1", TimeSpan.FromSeconds(300), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(301));
        await Assert.ThrowsAsync<TimeoutException>(() => expiring);
        Assert.False(timed.IsWaiting("job1"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelledWait = timed.WaitAsync("job2", TimeSpan.FromMinutes(5), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        Assert.False(timed.IsWaiting("job2"));
    }
}
