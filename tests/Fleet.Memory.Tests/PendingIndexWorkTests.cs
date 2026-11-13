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
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>The index and the disk, as far as this class can see them.</summary>
    private sealed class Index
    {
        public bool Down = true;
        public readonly HashSet<string> OnDisk = new(StringComparer.Ordinal);
        public readonly HashSet<string> Corrupt = new(StringComparer.Ordinal);
        public readonly HashSet<string> Indexed = new(StringComparer.Ordinal);
        public readonly List<string> Attempts = [];

        public Task IndexAsync(string path, CancellationToken ct)
        {
            Attempts.Add("index " + path);
            if (Corrupt.Contains(path)) throw new InvalidDataException("synthetic corrupt file");
            if (Down) throw new HttpRequestException("synthetic index outage");
            Indexed.Add(path);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string path, CancellationToken ct)
        {
            Attempts.Add("remove " + path);
            if (Down) throw new HttpRequestException("synthetic index outage");
            Indexed.Remove(path);
            return Task.CompletedTask;
        }
    }

    private readonly Clock _clock = new();
    private readonly Index _index = new();
    private readonly PendingIndexWork _work;

    public PendingIndexWorkTests() =>
        _work = new PendingIndexWork(_index.IndexAsync, _index.RemoveAsync, _clock, NullLogger.Instance, _index.OnDisk.Contains);

    private async Task<PendingIndexWork.Outcome> Write(string path)
    {
        _index.OnDisk.Add(path);
        return await _work.ApplyAsync(path, CancellationToken.None);
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
        Assert.DoesNotContain("a.md", _index.Indexed);

        _index.Down = false;
        _clock.Advance(PendingIndexWork.MaxDelay);
        Assert.Equal(1, await _work.RetryDueAsync(CancellationToken.None));

        Assert.Contains("a.md", _index.Indexed);
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

        _index.OnDisk.Remove("a.md");
        Assert.Equal(PendingIndexWork.Outcome.Deferred, await _work.ApplyAsync("a.md", CancellationToken.None));
        Assert.Contains("a.md", _index.Indexed);

        _index.Down = false;
        _clock.Advance(PendingIndexWork.FirstDelay);
        await _work.RetryDueAsync(CancellationToken.None);

        Assert.DoesNotContain("a.md", _index.Indexed);
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task AFileDeletedWhileItWaits_IsRemovedFromTheIndex_NotIndexed()
    {
        await Write("a.md");
        _index.OnDisk.Remove("a.md");
        _index.Attempts.Clear();
        _index.Down = false;

        _clock.Advance(PendingIndexWork.FirstDelay);
        await _work.RetryDueAsync(CancellationToken.None);

        // The disk is the truth at the moment of the retry, not what failed earlier.
        Assert.Equal(["remove a.md"], _index.Attempts);
        Assert.DoesNotContain("a.md", _index.Indexed);
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

        Assert.Contains("b.md", _index.Indexed);
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
        var work = new PendingIndexWork(
            (path, ct) => path == "b.md" ? throw new TimeoutException("synthetic") : flaky.IndexAsync(path, ct),
            flaky.RemoveAsync, _clock, NullLogger.Instance, _ => true);
        work.Defer("a.md");
        work.Defer("b.md");
        work.Defer("c.md");

        _clock.Advance(PendingIndexWork.FirstDelay);
        Assert.Equal(1, await work.RetryDueAsync(CancellationToken.None));

        // a.md got through, so the wait starts over instead of doubling.
        Assert.Equal(PendingIndexWork.FirstDelay, work.NotBefore - _clock.Now);
        Assert.Contains("a.md", flaky.Indexed);
        Assert.Equal(2, work.Count);

        // The next pass reaches c.md before it tries b.md again: the file that failed goes last.
        _clock.Advance(PendingIndexWork.FirstDelay);
        Assert.Equal(1, await work.RetryDueAsync(CancellationToken.None));
        Assert.Contains("c.md", flaky.Indexed);
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
}
