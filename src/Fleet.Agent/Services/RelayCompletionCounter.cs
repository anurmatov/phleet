using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// <c>relay_completions_published_total{type}</c> (#277 §9).
///
/// Proves that moving relay publication off the Telegram transport (#277 D-2) did not quietly stop
/// it. The regression this measures is a flat line after deploy, which is otherwise invisible:
/// relay answers failing to publish looks exactly like nobody having delegated anything.
/// </summary>
public sealed class RelayCompletionCounter
{
    private readonly ConcurrentDictionary<string, long> _published = new();

    public void Published(string type) =>
        _published.AddOrUpdate(type, 1L, (_, c) => c + 1L);

    public long GetPublished(string type) =>
        _published.TryGetValue(type, out var v) ? v : 0L;
}
