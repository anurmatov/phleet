namespace Fleet.Orchestrator.Services.Push;

/// <summary>
/// Chat replies the phone should hear about (#530 step 9): queued by the chat service when an agent
/// answered and no client was attached, and sent by <see cref="PushNotifierService"/>, which they
/// also wake so a reply is not held back for a whole interval.
/// </summary>
/// <remarks>
/// In memory, like every other push: a restart owes nothing. A reply is still worth retrying for
/// <see cref="Lifetime"/>; after that the person has very likely opened the app anyway.
/// </remarks>
public sealed class ChatReplyPushes(TimeProvider time)
{
    /// <summary>How long a reply notification is retried to a device that did not accept it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Replies waiting for the next pass. More is a flood, not a conversation.</summary>
    public const int MaxQueued = 100;

    private readonly object _gate = new();
    private readonly List<PushMessage> _queued = [];
    private readonly Dictionary<string, DateTimeOffset> _current = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Queue one reply. False when the queue is full.</summary>
    public bool Enqueue(PushMessage message)
    {
        lock (_gate)
        {
            if (_queued.Count >= MaxQueued) return false;
            _queued.Add(message);
            _current[message.Key] = time.GetUtcNow();
        }

        // One pending wake is enough; the pass takes everything queued.
        try { _signal.Release(); } catch (SemaphoreFullException) { }
        return true;
    }

    /// <summary>Everything queued since the last pass.</summary>
    public IReadOnlyList<PushMessage> Take()
    {
        lock (_gate)
        {
            var taken = _queued.ToList();
            _queued.Clear();

            var now = time.GetUtcNow();
            foreach (var stale in _current.Where(c => now - c.Value >= Lifetime).Select(c => c.Key).ToList())
                _current.Remove(stale);

            return taken;
        }
    }

    /// <summary>Whether a reply notification is still worth sending again.</summary>
    public bool IsCurrent(string key)
    {
        lock (_gate)
            return _current.TryGetValue(key, out var at) && time.GetUtcNow() - at < Lifetime;
    }

    /// <summary>Wait until a reply is queued or <paramref name="timeout"/> passes.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _signal.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
