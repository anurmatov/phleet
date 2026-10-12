using Fleet.Orchestrator.Models;

namespace Fleet.Orchestrator.Services;

public sealed record EpicVisibilityResult(IReadOnlyList<WorkflowSummary> Runs, bool Complete, string? Error);

/// <summary>Temporal visibility and exact-run wake transport, not repository visibility.</summary>
public interface IEpicDriverTemporal
{
    bool IsConfigured { get; }
    Task<EpicVisibilityResult> ListAsync(string ns, string query, int? limit, CancellationToken ct);
    Task<string> SignalWorkflowAsync(string ns, string workflowId, string runId, string signalName, object payload, CancellationToken ct);
}

/// <summary>Bounded, thread-safe per-run LRU; reads never extend a reply's lifetime.</summary>
internal sealed class EpicRunCache<T>(int capacity = 4096)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> _items = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, T Value)> _lru = new();
    public bool TryGet(string key, out T value)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _lru.AddLast(node); value = node.Value.Value; return true;
            }
            value = default!; return false;
        }
    }
    public void Put(string key, T value)
    {
        lock (_lock)
        {
            if (_items.Remove(key, out var old)) _lru.Remove(old);
            _items[key] = _lru.AddLast((key, value));
            while (_items.Count > capacity)
            {
                var first = _lru.First!; _items.Remove(first.Value.Key); _lru.RemoveFirst();
            }
        }
    }
}
