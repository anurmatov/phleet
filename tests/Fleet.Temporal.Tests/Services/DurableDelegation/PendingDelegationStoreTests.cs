using Fleet.Temporal.Configuration;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// The state file is all a restarted bridge has, so what these pin is what survives: a write that
/// is whole or absent, a reload that returns what was written, and a file that cannot be read
/// being kept for a person to look at instead of being overwritten.
/// </summary>
public sealed class PendingDelegationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pending-store-" + Guid.NewGuid().ToString("N"));
    private string StateFile => Path.Combine(_dir, "nested", "pending-delegations.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a temp directory */ }
    }

    private PendingDelegationStore Store(string? path = null) => new(path ?? StateFile, NullLogger.Instance);

    private static PendingDelegation Record(string taskId = "wf-1/review") => new()
    {
        TaskId = taskId,
        CurrentTaskId = taskId + "/incomplete-retry-1",
        Namespace = "fleet",
        WorkflowId = "wf-1",
        RunId = "run-1",
        WorkflowType = "example-workflow",
        ActivityId = "7",
        Attempt = 1,
        TaskToken = Convert.ToBase64String([9, 8, 7]),
        Agent = "reviewer-one",
        CurrentInstruction = "line one\nline two — with “quotes”",
        Repo = "owner/name",
        BudgetSeconds = 900,
        RetryOnIncomplete = true,
        MaxIncompleteRetries = 3,
        IncompleteAttempt = 1,
        AccumulatedText = "partial answer",
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        WaitStartedAt = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero),
        LastPublishedAt = new DateTimeOffset(2026, 1, 1, 0, 5, 1, TimeSpan.Zero),
        ResultStatus = "completed",
        ResultText = "the answer",
        TimeoutMessage = null,
        CancelSent = true,
        Publishing = true,
    };

    [Fact]
    public void WhatIsWritten_IsWhatAReloadReturns()
    {
        var store = Store();
        store.Put(Record());

        var reloaded = Store();
        reloaded.Load();

        Assert.True(reloaded.TryGet("wf-1/review", out var record));
        var expected = Record();
        Assert.Equal(expected.CurrentTaskId, record.CurrentTaskId);
        Assert.Equal(expected.Namespace, record.Namespace);
        Assert.Equal(expected.RunId, record.RunId);
        Assert.Equal(expected.WorkflowType, record.WorkflowType);
        Assert.Equal(expected.ActivityId, record.ActivityId);
        Assert.Equal(expected.TaskToken, record.TaskToken);
        Assert.Equal(expected.CurrentInstruction, record.CurrentInstruction);
        Assert.Equal(expected.Repo, record.Repo);
        Assert.Equal(expected.BudgetSeconds, record.BudgetSeconds);
        Assert.Equal(expected.MaxIncompleteRetries, record.MaxIncompleteRetries);
        Assert.Equal(expected.IncompleteAttempt, record.IncompleteAttempt);
        Assert.Equal(expected.AccumulatedText, record.AccumulatedText);
        Assert.Equal(expected.WaitStartedAt, record.WaitStartedAt);
        Assert.Equal(expected.LastPublishedAt, record.LastPublishedAt);
        Assert.Equal(expected.Deadline, record.Deadline);
        Assert.Equal(expected.ResultStatus, record.ResultStatus);
        Assert.Equal(expected.ResultText, record.ResultText);
        Assert.True(record.CancelSent);
        // In memory only: nobody is publishing it after a restart.
        Assert.False(record.Publishing);
    }

    [Fact]
    public void AMissingFile_IsAnEmptyStore()
    {
        var store = Store();
        store.Load();

        Assert.Equal(0, store.Count);
        Assert.True(store.Durable);
    }

    [Fact]
    public void ARemovedRecord_IsGoneFromTheFileToo()
    {
        var store = Store();
        store.Put(Record("wf-1/a"));
        store.Put(Record("wf-1/b"));

        Assert.True(store.Remove("wf-1/a"));
        Assert.False(store.Remove("wf-1/a"));

        var reloaded = Store();
        reloaded.Load();
        Assert.Equal(["wf-1/b"], reloaded.TaskIds);
    }

    /// <summary>
    /// A crash in the middle of a write leaves a half-written temp file beside a whole state file.
    /// The state file is what is read; the leftover is overwritten by the next write.
    /// </summary>
    [Fact]
    public void AHalfWrittenTempFile_NeverReplacesTheStateFile()
    {
        var store = Store();
        store.Put(Record("wf-1/a"));
        File.WriteAllText(StateFile + ".tmp", "[{\"TaskId\": \"wf-1/tr");

        var reloaded = Store();
        reloaded.Load();
        Assert.Equal(["wf-1/a"], reloaded.TaskIds);

        reloaded.Put(Record("wf-1/b"));
        Assert.False(File.Exists(StateFile + ".tmp"));
        var again = Store();
        again.Load();
        Assert.Equal(["wf-1/a", "wf-1/b"], again.TaskIds);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"TaskId\": \"an object, not a list\"}")]
    [InlineData("null")]
    public void AFileThatCannotBeRead_IsMovedAside_NotOverwritten(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, content);

        var store = Store();
        store.Load();

        Assert.Equal(0, store.Count);
        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StateFile)!, "pending-delegations.json.unreadable-*"));
        Assert.Equal(content, File.ReadAllText(aside));

        store.Put(Record());
        Assert.Equal(content, File.ReadAllText(aside));
        Assert.True(File.Exists(StateFile));
    }

    /// <summary>A record nothing could be done with (no token to complete it by) is skipped; the rest load.</summary>
    [Fact]
    public void ARecordThatCannotBeActedOn_IsSkipped_AndTheOthersLoad()
    {
        var store = Store();
        store.Put(Record("wf-1/good"));
        var json = File.ReadAllText(StateFile);
        var bad = json.TrimEnd().TrimEnd(']') + ", { \"TaskId\": \"wf-1/no-token\", \"Namespace\": \"fleet\", \"Agent\": \"a\" }, " +
                  "{ \"TaskId\": \"wf-1/bad-token\", \"Namespace\": \"fleet\", \"Agent\": \"a\", \"TaskToken\": \"***\" } ]";
        File.WriteAllText(StateFile, bad);

        var reloaded = Store();
        reloaded.Load();

        Assert.Equal(["wf-1/good"], reloaded.TaskIds);
    }

    [Fact]
    public void ADirectoryThatCannotBeWritten_KeepsTheRecordsInMemory_AndSaysSo()
    {
        // A regular file where the directory should be: creating the directory fails.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "");
        var store = Store(Path.Combine(blocker, "pending-delegations.json"));

        store.Put(Record());

        Assert.False(store.Durable);
        Assert.True(store.TryGet("wf-1/review", out _));
    }

    [Fact]
    public void NoPath_IsMemoryOnly()
    {
        var store = new PendingDelegationStore("  ", NullLogger.Instance);
        store.Put(Record());

        Assert.False(store.Durable);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void TheFile_IsReadableByItsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;

        Store().Put(Record());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StateFile));
    }

    [Fact]
    public void TheDefaultStateFile_IsInTheBridgesMountedStateDirectory() =>
        Assert.Equal("/var/lib/fleet-temporal-bridge/pending-delegations.json", new DurableDelegationOptions().StateFile);
}
