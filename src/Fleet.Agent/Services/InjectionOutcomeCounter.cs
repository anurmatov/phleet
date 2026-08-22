using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// Tracks mid-turn injection outcomes by provider so tests and status surfaces can
/// distinguish true live delivery from fallback queueing.
/// </summary>
public sealed class InjectionOutcomeCounter
{
    public const string Injected = "injected";
    public const string DegradedToQueue = "degraded_to_queue";
    public const string FailedThenQueued = "failed_then_queued";
    public const string DroppedAtQueueCap = "dropped_at_queue_cap";
    public const string PossibleDuplicateAfterResume = "possible_duplicate_after_resume";

    private readonly ConcurrentDictionary<(string provider, string outcome), long> _counts = new();

    public void Increment(string provider, string outcome) =>
        _counts.AddOrUpdate((provider, outcome), 1L, (_, c) => c + 1L);

    public long GetCount(string provider, string outcome) =>
        _counts.TryGetValue((provider, outcome), out var value) ? value : 0L;
}
