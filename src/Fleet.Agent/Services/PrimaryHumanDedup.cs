namespace Fleet.Agent.Services;

/// <summary>Protects queued/running keys; only dispatched, inactive keys may be evicted.</summary>
public sealed class PrimaryHumanDedup(TimeProvider? time = null)
{
    public const int Capacity = 512;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<(long Chat, long Message), State> _keys = [];
    private sealed record State(bool Active, DateTimeOffset? DispatchedAt);
    public int Count { get { lock (_gate) return _keys.Count; } }
    public bool TryReserve(long chat, long message) => TryReserve(chat, message, out _);

    public bool TryReserve(long chat, long message, out bool duplicate)
    {
        lock (_gate)
        {
            duplicate = false;
            var now = _time.GetUtcNow();
            foreach (var expired in _keys.Where(e => !e.Value.Active && e.Value.DispatchedAt <= now - TimeSpan.FromMinutes(10)).Select(e => e.Key).ToArray()) _keys.Remove(expired);
            var key = (chat, message);
            if (_keys.ContainsKey(key)) { duplicate = true; return false; }
            if (_keys.Count == Capacity)
            {
                var evict = _keys.Where(e => !e.Value.Active).OrderBy(e => e.Value.DispatchedAt).FirstOrDefault();
                if (evict.Value is null) return false;
                _keys.Remove(evict.Key);
            }
            _keys.Add(key, new State(true, null));
            return true;
        }
    }
    public void Dispatched(long chat, long message)
    {
        lock (_gate) if (_keys.ContainsKey((chat, message))) _keys[(chat, message)] = new(true, _time.GetUtcNow());
    }
    public void Complete(long chat, long message)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue((chat, message), out var state)) return;
            if (state.DispatchedAt is null) _keys.Remove((chat, message));
            else _keys[(chat, message)] = state with { Active = false };
        }
    }
}
