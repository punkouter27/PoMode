using PoMode.Shared.Diagnostics;

namespace PoMode.Api.Features.Diagnostics;

/// <summary>
/// One /diag report shared by <c>/health</c> and <c>/api/diag</c> for <see cref="Lifetime"/>. Every
/// report probes Key Vault, Application Insights and five web services; without this, each page load,
/// availability ping or refresh click would repeat all of them (F1 CPU quota, other people's APIs).
/// Callers arriving while a report is being built wait for that same build. A failed build is not kept.
/// </summary>
public sealed class DiagReportCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private Task<DiagReportDto>? _current;
    private DateTimeOffset _startedAt;

    public Task<DiagReportDto> GetAsync(Func<Task<DiagReportDto>> build)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_current is null || _current.IsFaulted || _current.IsCanceled || now - _startedAt >= Lifetime)
            {
                // Not on the caller's token: one visitor closing the tab must not cancel everyone's report.
                _current = Task.Run(build);
                _startedAt = now;
            }
            return _current;
        }
    }
}
