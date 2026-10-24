using Fleet.Shared.Security;

namespace Fleet.Memory.Services;

/// <summary>Receiver-local MCP session binding. Restart invalidates all sessions; never evicts into acceptance.</summary>
public sealed class MemoryIdentitySessions
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (VerifiedAgentIdentity Identity, DateTimeOffset Expires)> _sessions = [];
    private readonly Dictionary<Guid, int> _requests = [];
    public IDisposable? TryEnter(Guid launch)
    {
        lock (_gate)
        {
            var count = _requests.GetValueOrDefault(launch);
            if (count >= 64) return null;
            _requests[launch] = count + 1;
            return new RequestLease(() => { lock (_gate) { if (_requests[launch] == 1) _requests.Remove(launch); else _requests[launch]--; } });
        }
    }
    private sealed class RequestLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    public bool Matches(string hash, VerifiedAgentIdentity identity)
    {
        lock (_gate) return _sessions.TryGetValue(hash, out var value) && value.Expires > DateTimeOffset.UtcNow && value.Identity == identity;
    }
    public bool Bind(string hash, VerifiedAgentIdentity identity)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _sessions.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _sessions.Remove(expired);
            if (_sessions.TryGetValue(hash, out var existing)) return existing.Identity == identity;
            if (_sessions.Values.Count(x => x.Identity.LaunchId == identity.LaunchId) >= 100) return false;
            _sessions.Add(hash, (identity, now.AddMinutes(15)));
            return true;
        }
    }
}
