namespace Fleet.Orchestrator.Services;

/// <summary>
/// One holder at a time per key; different keys do not wait for each other. Not reentrant: a
/// holder that asks for its own key again waits for itself.
/// </summary>
/// <remarks>
/// A key's semaphore exists only while someone holds or waits for it, so the table does not grow
/// with every name ever used. Keys compare without regard to case, as agent names do in the
/// database.
/// </remarks>
internal sealed class KeyedAsyncLock
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waits for the key. Dispose the result to release it.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting; nothing is held.</exception>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries[key] = entry = new Entry();
            entry.Interested++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Forget(key, entry);
            throw;
        }

        return new Holder(this, key, entry);
    }

    /// <summary>Keys held or waited for right now. For tests.</summary>
    internal int ActiveKeyCount
    {
        get { lock (_entries) return _entries.Count; }
    }

    private void Forget(string key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.Interested == 0)
                _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Interested;
    }

    private sealed class Holder(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            entry.Gate.Release();
            owner.Forget(key, entry);
        }
    }
}
