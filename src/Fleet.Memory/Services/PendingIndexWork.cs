using System.Collections.Concurrent;

namespace Fleet.Memory.Services;

/// <summary>
/// The memory files whose search index is out of date because the index could not be reached, and
/// the retries that bring it back in line.
///
/// The files are the source of truth and the vector index is derived from them. A write, an edit or a
/// delete that lands while the index or the embedder is down is safe on disk, but before this class a
/// file got two indexing attempts (the watcher event and one polling pass) and after that nothing
/// asked again until the service restarted: the memory existed and no search or list could find it.
///
/// A path is held here until the index agrees with the disk for it. What "agrees" means is decided
/// when the work runs, never when the failure was recorded: a file that exists is indexed, a file
/// that is gone is removed from the index. So an edit or a delete that arrives while a path is
/// waiting needs no bookkeeping of its own.
///
/// Two things can work on the same path at once: a retry pass and the watcher's event for that file.
/// Indexing reads the file and writes the index some time later (the embedding sits between), so
/// without care a retry that read a file before it was deleted, or before it was edited, could write
/// the index after the delete or the edit had been handled, and the older state would win with
/// nothing left waiting to repair it. Two rules prevent that:
///
/// - One path is worked on by one caller at a time. The gate is per path: a slow embedding holds up
///   work on its own file and on no other.
/// - What the disk held is noted before the work and looked at again after it. The path stops
///   waiting only if the two agree. If the file changed or went away while it was being indexed, the
///   index may hold the older state, so the path keeps waiting and the next pass does it again.
///   This second rule does not depend on anyone else taking the gate, so it also covers a change
///   whose event never arrives and one made by a writer that does not come through here.
///
/// While the index is down the retries do not hammer it or the embedder. A pass stops at the first
/// failure, so one file is embedded per pass however many are waiting, and the wait before the next
/// pass doubles from <see cref="FirstDelay"/> up to <see cref="MaxDelay"/>. The first pass that gets
/// through works off everything that is waiting and resets the wait.
///
/// A pass takes the paths in the order they were last tried, longest ago first. So one file that can
/// never be indexed (an input the embedder rejects, say) ends up last and costs one attempt per
/// pass; it cannot keep the files behind it from being reached.
/// </summary>
/// <param name="observe">
/// What the disk holds for a path right now: null when there is no file, otherwise a value that
/// changes whenever the file does. The default is the file's last write time and length.
/// </param>
public sealed class PendingIndexWork(
    Func<string, CancellationToken, Task> index,
    Func<string, CancellationToken, Task> remove,
    TimeProvider time,
    ILogger logger,
    Func<string, object?>? observe = null)
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    private readonly Func<string, object?> _observe = observe ?? ObserveFile;
    // Path → when it was last tried, as a counter (0 = not yet). The counter orders a pass.
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.Ordinal);
    private long _attempts;
    private readonly object _gate = new();
    private TimeSpan _delay = FirstDelay;
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    // One entry per path that is being worked on or waited for, counted so it can be dropped when
    // the last user leaves. Guarded by itself.
    private readonly Dictionary<string, PathGate> _pathGates = new(StringComparer.Ordinal);

    private sealed class PathGate
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public int Users;
    }

    public enum Outcome
    {
        /// <summary>The file exists and is now indexed.</summary>
        Indexed,
        /// <summary>The file is gone and is no longer in the index.</summary>
        Removed,
        /// <summary>The file cannot be parsed. Asking again cannot help, so it is not kept.</summary>
        Corrupt,
        /// <summary>The index or the embedder failed. The path is waiting for a retry.</summary>
        Deferred,
        /// <summary>
        /// The work went through, and the file changed or went away while it ran, so the index may
        /// hold the older state. The path is waiting for another pass.
        /// </summary>
        Stale,
    }

    /// <summary>How many paths are waiting.</summary>
    public int Count => _pending.Count;

    /// <summary>When the next pass may run. For logs and tests.</summary>
    public DateTimeOffset NotBefore { get { lock (_gate) return _notBefore; } }

    /// <summary>
    /// Brings the index in line with the disk for one path, now. A failure of the index or the
    /// embedder is not thrown: the path is kept for <see cref="RetryDueAsync"/> and the answer is
    /// <see cref="Outcome.Deferred"/>. Only cancellation is thrown.
    /// </summary>
    public async Task<Outcome> ApplyAsync(string path, CancellationToken ct)
    {
        try
        {
            return await ReconcileAsync(path, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Defer(path);
            logger.LogWarning(ex, "Index not updated for {Path}; it will be retried ({Pending} waiting)", path, _pending.Count);
            return Outcome.Deferred;
        }
    }

    /// <summary>Keeps a path for a retry, for a caller that saw the failure itself.</summary>
    public void Defer(string path)
    {
        lock (_gate)
        {
            // The first path to wait starts the clock. Later ones join the wait already running.
            if (_pending.IsEmpty)
            {
                _delay = FirstDelay;
                _notBefore = time.GetUtcNow() + _delay;
            }
            _pending.TryAdd(path, 0);
        }
    }

    /// <summary>
    /// One retry pass, if one is due. Returns how many paths it brought in line. It stops at the
    /// first failure of the index or the embedder and waits longer before the next pass.
    /// </summary>
    public async Task<int> RetryDueAsync(CancellationToken ct)
    {
        if (_pending.IsEmpty) return 0;
        lock (_gate)
            if (time.GetUtcNow() < _notBefore) return 0;

        var done = 0;
        foreach (var path in _pending.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Indexed or removed: caught up. Stale: the index answered and the file moved on
                // meanwhile; it stays waiting and is no reason to stop the pass or to wait longer.
                if (await ReconcileAsync(path, ct) is Outcome.Indexed or Outcome.Removed) done++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Tried most recently now, so the next pass reaches the others first.
                _pending.TryUpdate(path, Interlocked.Increment(ref _attempts), _pending.GetValueOrDefault(path));
                TimeSpan next;
                lock (_gate)
                {
                    // Nothing got through: wait longer. Something did: the index is answering, so
                    // this is one file's trouble and the wait starts over.
                    next = _delay = done > 0 ? FirstDelay : Min(_delay + _delay, MaxDelay);
                    _notBefore = time.GetUtcNow() + next;
                }
                logger.LogWarning(ex,
                    "Index still not reachable: {Pending} memory file(s) waiting, {Done} caught up this pass, next attempt in {Seconds}s",
                    _pending.Count, done, (int)next.TotalSeconds);
                return done;
            }
        }

        lock (_gate)
        {
            _delay = FirstDelay;
            // A path deferred while this pass ran waits one first delay, like any new one.
            _notBefore = _pending.IsEmpty ? DateTimeOffset.MinValue : time.GetUtcNow() + _delay;
        }
        if (done > 0) logger.LogInformation("Index caught up: {Done} memory file(s) that were waiting are searchable again", done);
        return done;
    }

    /// <summary>
    /// One path, one caller at a time. Decides from what the disk holds once it has the path to
    /// itself, and stops the path waiting only if the disk still holds the same when the work is done.
    /// A failure of the index or the embedder is thrown, with the path's waiting state untouched.
    /// </summary>
    private async Task<Outcome> ReconcileAsync(string path, CancellationToken ct)
    {
        var gate = await EnterAsync(path, ct);
        try
        {
            var before = _observe(path);
            Outcome outcome;
            try
            {
                if (before is not null)
                {
                    await index(path, ct);
                    outcome = Outcome.Indexed;
                }
                else
                {
                    await remove(path, ct);
                    outcome = Outcome.Removed;
                }
            }
            catch (InvalidDataException ex)
            {
                logger.LogError(ex, "Corrupt memory file skipped: {Path}", path);
                outcome = Outcome.Corrupt;
            }

            if (!Equals(before, _observe(path)))
            {
                // The file changed or went away while this ran. What was written may be the older
                // state, and nothing says the change will be reported: keep the path.
                Defer(path);
                return Outcome.Stale;
            }
            _pending.TryRemove(path, out _);
            return outcome;
        }
        finally
        {
            Leave(path, gate, held: true);
        }
    }

    private async Task<PathGate> EnterAsync(string path, CancellationToken ct)
    {
        PathGate gate;
        lock (_pathGates)
        {
            if (!_pathGates.TryGetValue(path, out gate!)) _pathGates[path] = gate = new PathGate();
            gate.Users++;
        }
        try
        {
            await gate.Turn.WaitAsync(ct);
        }
        catch
        {
            Leave(path, gate, held: false);
            throw;
        }
        return gate;
    }

    private void Leave(string path, PathGate gate, bool held)
    {
        if (held) gate.Turn.Release();
        lock (_pathGates)
            if (--gate.Users == 0) _pathGates.Remove(path);
    }

    private static object? ObserveFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
