using Fleet.Memory.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Memory.Tests;

/// <summary>
/// A memory written, edited or deleted while the vector index is down must become searchable (or
/// stop being searchable) once the index is back, without a restart and without re-embedding every
/// memory there is. The index here is a double that fails until it is told to recover.
/// </summary>
public sealed class PendingIndexWorkTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>One call into the index that the test holds open between its read and its write.</summary>
    private sealed class Hold
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// The index and the disk, as far as this class can see them. A file is a path and the version of
    /// its content; indexing reads the version first and writes it to the index last, as the real
    /// thing reads the file, embeds it, and only then upserts.
    /// </summary>
    private sealed class Index
    {
        public bool Down = true;
        public readonly Dictionary<string, int> OnDisk = new(StringComparer.Ordinal);
        public readonly HashSet<string> Corrupt = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Indexed = new(StringComparer.Ordinal);
        public readonly List<string> Attempts = [];
        /// <summary>Holds the next call open after its read. One call only; later ones run through.</summary>
        public Hold? HoldNext;

        public object? Observe(string path) { lock (OnDisk) return OnDisk.TryGetValue(path, out var version) ? version : null; }
        public void Write(string path, int version) { lock (OnDisk) OnDisk[path] = version; }
        public void Delete(string path) { lock (OnDisk) OnDisk.Remove(path); }
        public int? IndexedVersion(string path) { lock (Indexed) return Indexed.TryGetValue(path, out var version) ? version : null; }

        public async Task IndexAsync(string path, CancellationToken ct)
        {
            lock (Attempts) Attempts.Add("index " + path);
            if (Corrupt.Contains(path)) throw new InvalidDataException("synthetic corrupt file");
            if (Down) throw new HttpRequestException("synthetic index outage");
            int read;
            lock (OnDisk) read = OnDisk[path];
            await HeldAsync(ct);
            lock (Indexed) Indexed[path] = read;
        }

        public async Task RemoveAsync(string path, CancellationToken ct)
        {
            lock (Attempts) Attempts.Add("remove " + path);
            if (Down) throw new HttpRequestException("synthetic index outage");
            await HeldAsync(ct);
            lock (Indexed) Indexed.Remove(path);
        }

        private async Task HeldAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref HoldNext, null) is not { } hold) return;
            hold.Reached.TrySetResult();
            await hold.Release.Task.WaitAsync(ct);
        }
    }

    private readonly Clock _clock = new();
    private readonly Index _index = new();
    private readonly PendingIndexWork _work;

    public PendingIndexWorkTests() =>
        _work = new PendingIndexWork(_index.IndexAsync, _index.RemoveAsync, _clock, NullLogger.Instance, _index.Observe);

    private async Task<PendingIndexWork.Outcome> Write(string path, int version = 1)
    {
        _index.Write(path, version);
        return await _work.ApplyAsync(path, CancellationToken.None);
    }

    /// <summary>a.md is waiting, the index is back, and the retry pass is held inside its call into it.</summary>
    private async Task<(Task<int> Retry, Hold Hold)> ARetryHeldInsideTheIndexAsync()
    {
        _index.Down = false;
        var hold = _index.HoldNext = new Hold();
        _clock.Advance(PendingIndexWork.FirstDelay);
        var retry = _work.RetryDueAsync(CancellationToken.None);
        await hold.Reached.Task.WaitAsync(Patience);
        return (retry, hold);
    }

    [Fact]
    public async Task AWriteDuringAnOutage_IsKept_AndBecomesSearchableWhenTheIndexReturns()
    {
        Assert.Equal(PendingIndexWork.Outcome.Deferred, await Write("a.md"));
        Assert.Equal(1, _work.Count);

        // The outage lasts well past the two attempts the watcher used to make.
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(PendingIndexWork.MaxDelay);
            Assert.Equal(0, await _work.RetryDueAsync(CancellationToken.None));
            Assert.Equal(1, _work.Count);
        }
        Assert.Null(_index.IndexedVersion("a.md"));

        _index.Down = false;
        _clock.Advance(PendingIndexWork.MaxDelay);
        Assert.Equal(1, await _work.RetryDueAsync(CancellationToken.None));

        Assert.Equal(1, _index.IndexedVersion("a.md"));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task NothingIsAttempted_BeforeTheWaitIsOver()
    {
        await Write("a.md");
        _index.Attempts.Clear();

        _clock.Advance(PendingIndexWork.FirstDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, await _work.RetryDueAsync(CancellationToken.None));
        Assert.Empty(_index.Attempts);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await _work.RetryDueAsync(CancellationToken.None);
        Assert.Equal(["index a.md"], _index.Attempts);
    }

    [Fact]
    public async Task WhileTheIndexIsDown_APassEmbedsOneFile_HoweverManyAreWaiting()
    {
        foreach (var name in new[] { "a.md", "b.md", "c.md", "d.md" }) await Write(name);
        _index.Attempts.Clear();

        _clock.Advance(PendingIndexWork.FirstDelay);
        await _work.RetryDueAsync(CancellationToken.None);

        // One probe, not four: an outage must not turn every waiting memory into an embedding call
        // on every pass.
        Assert.Single(_index.Attempts);
        Assert.Equal(4, _work.Count);
    }

    [Fact]
    public async Task TheWaitDoubles_UpToItsCap_AndStartsOverAfterRecovery()
    {
        await Write("a.md");
        var waits = new List<TimeSpan>();
        for (var i = 0; i < 9; i++)
        {
            _clock.Now = _work.NotBefore;
            await _work.RetryDueAsync(CancellationToken.None);
            waits.Add(_work.NotBefore - _clock.Now);
        }

        Assert.Equal([10, 20, 40, 80, 160, 300, 300, 300, 300], waits.Select(w => (int)w.TotalSeconds));

        _index.Down = false;
        _clock.Now = _work.NotBefore;
        await _work.RetryDueAsync(CancellationToken.None);
        _index.Down = true;
        await Write("b.md");

        Assert.Equal(PendingIndexWork.FirstDelay, _work.NotBefore - _clock.Now);
    }

    [Fact]
    public async Task Recovery_WorksOffEverythingWaiting_EachFileOnce()
    {
        foreach (var name in new[] { "a.md", "b.md", "c.md" }) await Write(name);
        _index.Attempts.Clear();
        _index.Down = false;

        _clock.Advance(PendingIndexWork.FirstDelay);
        Assert.Equal(3, await _work.RetryDueAsync(CancellationToken.None));

        Assert.Equal(["index a.md", "index b.md", "index c.md"], _index.Attempts);
        Assert.Equal(0, _work.Count);

        // And nothing is embedded again afterwards.
        _clock.Advance(PendingIndexWork.MaxDelay);
        Assert.Equal(0, await _work.RetryDueAsync(CancellationToken.None));
        Assert.Equal(3, _index.Attempts.Count);
    }

    [Fact]
    public async Task ADeleteDuringAnOutage_IsKept_AndLeavesTheIndexWhenItReturns()
    {
        _index.Down = false;
        await Write("a.md");
        _index.Down = true;

        _index.Delete("a.md");
        Assert.Equal(PendingIndexWork.Outcome.Deferred, await _work.ApplyAsync("a.md", CancellationToken.None));
        Assert.Equal(1, _index.IndexedVersion("a.md"));

        _index.Down = false;
        _clock.Advance(PendingIndexWork.FirstDelay);
        await _work.RetryDueAsync(CancellationToken.None);

        Assert.Null(_index.IndexedVersion("a.md"));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task AFileDeletedWhileItWaits_IsRemovedFromTheIndex_NotIndexed()
    {
        await Write("a.md");
        _index.Delete("a.md");
        _index.Attempts.Clear();
        _index.Down = false;

        _clock.Advance(PendingIndexWork.FirstDelay);
        await _work.RetryDueAsync(CancellationToken.None);

        // The disk is the truth at the moment of the retry, not what failed earlier.
        Assert.Equal(["remove a.md"], _index.Attempts);
        Assert.Null(_index.IndexedVersion("a.md"));
    }

    [Fact]
    public async Task ACorruptFile_IsDropped_AndDoesNotHoldUpTheOthers()
    {
        await Write("a.md");
        await Write("b.md");
        _index.Corrupt.Add("a.md");
        _index.Down = false;

        _clock.Advance(PendingIndexWork.FirstDelay);
        Assert.Equal(1, await _work.RetryDueAsync(CancellationToken.None));

        Assert.Equal(1, _index.IndexedVersion("b.md"));
        Assert.Equal(0, _work.Count);
        Assert.Equal(PendingIndexWork.Outcome.Corrupt, await _work.ApplyAsync("a.md", CancellationToken.None));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task AFileIndexedByALaterEvent_StopsWaiting()
    {
        await Write("a.md");
        _index.Down = false;

        Assert.Equal(PendingIndexWork.Outcome.Indexed, await _work.ApplyAsync("a.md", CancellationToken.None));

        Assert.Equal(0, _work.Count);
        _index.Attempts.Clear();
        _clock.Advance(PendingIndexWork.MaxDelay);
        await _work.RetryDueAsync(CancellationToken.None);
        Assert.Empty(_index.Attempts);
    }

    [Fact]
    public async Task OneFileThatKeepsFailing_DoesNotKeepTheOthersWaiting()
    {
        // The index is up, one file's upsert fails (an embedding timeout on that text, say).
        var flaky = new Index { Down = false };
        foreach (var name in new[] { "a.md", "b.md", "c.md" }) flaky.Write(name, 1);
        var work = new PendingIndexWork(
            (path, ct) => path == "b.md" ? throw new TimeoutException("synthetic") : flaky.IndexAsync(path, ct),
            flaky.RemoveAsync, _clock, NullLogger.Instance, flaky.Observe);
        work.Defer("a.md");
        work.Defer("b.md");
        work.Defer("c.md");

        _clock.Advance(PendingIndexWork.FirstDelay);
        Assert.Equal(1, await work.RetryDueAsync(CancellationToken.None));

        // a.md got through, so the wait starts over instead of doubling.
        Assert.Equal(PendingIndexWork.FirstDelay, work.NotBefore - _clock.Now);
        Assert.Equal(1, flaky.IndexedVersion("a.md"));
        Assert.Equal(2, work.Count);

        // The next pass reaches c.md before it tries b.md again: the file that failed goes last.
        _clock.Advance(PendingIndexWork.FirstDelay);
        Assert.Equal(1, await work.RetryDueAsync(CancellationToken.None));
        Assert.Equal(1, flaky.IndexedVersion("c.md"));
        Assert.Equal(1, work.Count);

        // Alone, the failing file is asked less and less often, up to the cap.
        _clock.Now = work.NotBefore;
        Assert.Equal(0, await work.RetryDueAsync(CancellationToken.None));
        Assert.Equal(PendingIndexWork.FirstDelay + PendingIndexWork.FirstDelay, work.NotBefore - _clock.Now);
    }

    [Fact]
    public async Task Cancellation_IsThrown_AndKeepsThePath()
    {
        await Write("a.md");
        _clock.Advance(PendingIndexWork.FirstDelay);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _work.RetryDueAsync(cts.Token));

        Assert.Equal(1, _work.Count);
    }

    // ── A retry and an event for the same file ──────────────────────────────────────────────
    //
    // Indexing reads the file and writes the index later. Each test below holds a retry between
    // the two, lets something else happen to the same file, releases the retry, and looks at
    // where things ended: the disk, the index, and what is still waiting.

    [Fact]
    public async Task ADeleteHandledWhileARetryIsIndexingTheFile_DoesNotComeBackInTheIndex()
    {
        await Write("a.md");
        var (retry, hold) = await ARetryHeldInsideTheIndexAsync();

        // The file is deleted and the watcher's event for it is handled, while the retry still
        // holds the content it read.
        _index.Delete("a.md");
        var deleted = _work.ApplyAsync("a.md", CancellationToken.None);
        hold.Release.SetResult();
        await retry.WaitAsync(Patience);
        await deleted.WaitAsync(Patience);

        Assert.Null(_index.Observe("a.md"));
        Assert.Null(_index.IndexedVersion("a.md"));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task ADeleteNobodyReports_WhileARetryIsIndexingTheFile_KeepsThePathAndIsRepaired()
    {
        await Write("a.md");
        var (retry, hold) = await ARetryHeldInsideTheIndexAsync();

        // Deleted, and no event comes: the event queue was full, or the writer did not go through
        // the watcher at all.
        _index.Delete("a.md");
        hold.Release.SetResult();
        Assert.Equal(0, await retry.WaitAsync(Patience));

        // The retry wrote what it had read. It must know that is no longer true.
        Assert.Equal(1, _index.IndexedVersion("a.md"));
        Assert.Equal(1, _work.Count);

        _clock.Now = _work.NotBefore;
        Assert.Equal(1, await _work.RetryDueAsync(CancellationToken.None));
        Assert.Null(_index.IndexedVersion("a.md"));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task AnEditHandledWhileARetryIsIndexingTheFile_EndsWithTheNewContentIndexed()
    {
        await Write("a.md", version: 1);
        var (retry, hold) = await ARetryHeldInsideTheIndexAsync();

        // The file is edited and the watcher's event for it is handled. Another file's event is not
        // made to wait for this one: it goes through while the retry is still held.
        _index.Write("a.md", 2);
        var edited = _work.ApplyAsync("a.md", CancellationToken.None);
        Assert.Equal(PendingIndexWork.Outcome.Indexed, await Write("b.md").WaitAsync(Patience));
        hold.Release.SetResult();
        await retry.WaitAsync(Patience);
        await edited.WaitAsync(Patience);

        // The older content must not be the last thing written.
        Assert.Equal(2, _index.IndexedVersion("a.md"));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task AnEditNobodyReports_WhileARetryIsIndexingTheFile_KeepsThePathAndIsRepaired()
    {
        await Write("a.md", version: 1);
        var (retry, hold) = await ARetryHeldInsideTheIndexAsync();

        _index.Write("a.md", 2);
        hold.Release.SetResult();
        Assert.Equal(0, await retry.WaitAsync(Patience));

        Assert.Equal(1, _index.IndexedVersion("a.md"));
        Assert.Equal(1, _work.Count);

        _clock.Now = _work.NotBefore;
        Assert.Equal(1, await _work.RetryDueAsync(CancellationToken.None));
        Assert.Equal(2, _index.IndexedVersion("a.md"));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task AFileCreatedAgainWhileARetryIsRemovingIt_EndsIndexed()
    {
        // Indexed, then deleted during an outage: the removal is what waits.
        _index.Down = false;
        await Write("a.md", version: 1);
        _index.Down = true;
        _index.Delete("a.md");
        await _work.ApplyAsync("a.md", CancellationToken.None);
        var (retry, hold) = await ARetryHeldInsideTheIndexAsync();

        // The file is written again and its event is handled while the retry is about to remove it.
        _index.Write("a.md", 2);
        var created = _work.ApplyAsync("a.md", CancellationToken.None);
        hold.Release.SetResult();
        await retry.WaitAsync(Patience);
        await created.WaitAsync(Patience);

        // The removal must not be the last thing done to a file that exists.
        Assert.Equal(2, _index.IndexedVersion("a.md"));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task AnEventHeldInsideTheIndex_AndARetryForTheSameFile_EndWithTheNewContentIndexed()
    {
        // The other way round: the event is the one that read early, the retry the one that comes later.
        _index.Down = false;
        _index.Write("a.md", 1);
        var hold = _index.HoldNext = new Hold();
        var first = _work.ApplyAsync("a.md", CancellationToken.None);
        await hold.Reached.Task.WaitAsync(Patience);

        _index.Write("a.md", 2);
        _work.Defer("a.md");
        _clock.Advance(PendingIndexWork.FirstDelay);
        var retry = _work.RetryDueAsync(CancellationToken.None);
        hold.Release.SetResult();
        Assert.Equal(PendingIndexWork.Outcome.Stale, await first.WaitAsync(Patience));
        await retry.WaitAsync(Patience);

        Assert.Equal(2, _index.IndexedVersion("a.md"));
        Assert.Equal(0, _work.Count);
    }
}
