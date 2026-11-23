using Microsoft.Extensions.Time.Testing;
using PoMode.Api.Features.Diagnostics;
using PoMode.Shared.Diagnostics;
using Xunit;

namespace PoMode.Unit.Infrastructure;

public class DiagReportCacheTests
{
    [Fact]
    public async Task One_report_serves_every_caller_for_a_minute_then_is_rebuilt_and_a_failure_is_not_kept()
    {
        var time = new FakeTimeProvider();
        var cache = new DiagReportCache(time);
        var builds = 0;
        Task<DiagReportDto> Build()
        {
            builds++;
            return Task.FromResult(new DiagReportDto(DiagnosticStatus.Healthy, "Test", "v1", time.GetUtcNow(), 1, []));
        }

        // /health and /api/diag (and many visitors) within the minute: one build, the same report.
        var first = await cache.GetAsync(Build);
        time.Advance(TimeSpan.FromSeconds(59));
        var again = await cache.GetAsync(Build);
        Assert.Equal(1, builds);
        Assert.Same(first, again);

        // A minute on, the probes run again.
        time.Advance(TimeSpan.FromSeconds(1));
        var fresh = await cache.GetAsync(Build);
        Assert.Equal(2, builds);
        Assert.NotSame(first, fresh);

        // A build that throws is not cached: the next caller tries again instead of a minute of errors.
        time.Advance(DiagReportCache.Lifetime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(() => throw new InvalidOperationException("probe")));
        await cache.GetAsync(Build);
        Assert.Equal(3, builds);
    }
}
