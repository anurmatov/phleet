using Fleet.Agent.Services;
using Fleet.Shared;
using static Fleet.Agent.Tests.WorkflowOutcomes.WorkflowOutcomeFixture;

namespace Fleet.Agent.Tests.WorkflowOutcomes;

/// <summary>
/// #476 durable local inbox: temp + fsync + atomic rename, directory 0700 / file 0600, full
/// restart round trip, and a failed or corrupt write never turning into a delivered receipt.
/// </summary>
public sealed class LocalStoreTests : IDisposable
{
    private const string Suite = "WorkflowOutcomeSuite";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wo-store-{Guid.NewGuid():N}");
    private string InboxPath => Path.Combine(_dir, ".fleet", "workflow-outcome-inbox.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static WorkflowOutcomeLocalBatch Local(WorkflowOutcomeInboxBatch batch) => new()
    {
        Batch = batch,
        Conversation = WorkflowOutcomeCompletionHandler.ConversationFor(batch.BatchId, Coordinator, batch.Generation),
        TaskId = WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId),
        StoredAt = DateTimeOffset.UtcNow,
        LastListedAt = DateTimeOffset.UtcNow,
    };

    private WorkflowOutcomeLocalStore Open()
    {
        var store = new WorkflowOutcomeLocalStore(InboxPath);
        store.Load();
        return store;
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public void Commit_WritesAPrivateFileInAPrivateDirectoryAndLeavesNoTempFile()
    {
        var store = Open();

        Assert.True(store.TryAdd(Local(Batch())));

        Assert.True(File.Exists(InboxPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(InboxPath)!));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(InboxPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(InboxPath)!));
        }
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public void Commit_TightensAnExistingLooseDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.GetDirectoryName(InboxPath)!;
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, (UnixFileMode)0b111_101_101);
        var store = Open();

        Assert.True(store.TryAdd(Local(Batch())));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public void Restart_RoundTripsBatchRefTaskReceiptAckAndTurnState()
    {
        var a = Item();
        var batch = Batch(a);
        var store = Open();
        var refs = new[] { new WorkflowOutcomeItemRef(a.Id, 1) };
        var receipt = new WorkflowOutcomeReceipt(batch.BatchId, 1, batch.Nonce, batch.ItemVersionsHash, refs, refs, refs,
            "receipt-hash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var decision = new WorkflowOutcomeTurnDecision(a.Id, 1, "hold", "agent1", "waiting on proof", ["event:7"],
            DateTimeOffset.UtcNow.AddHours(1));
        var output = new WorkflowOutcomeTurnOutput(1, batch.BatchId, 1, refs, [decision]);
        Assert.True(store.TryAdd(Local(batch)));
        Assert.True(store.TryUpdate(batch.BatchId, b => b with
        {
            Delivery = WorkflowOutcomeLocalStates.DeliveryDelivered,
            DeliveryAttempts = 2,
            Receipt = receipt,
            Turn = WorkflowOutcomeLocalStates.TurnCompleted,
            TurnResult = "parsed",
            Output = output,
            Ack = WorkflowOutcomeLocalStates.AckAcked,
            AckAttempts = 1,
            Decisions = [new WorkflowOutcomeLocalDecision(decision, WorkflowOutcomeLocalStates.DecisionPending, 1)],
        }));

        var reopened = Open().Get(batch.BatchId)!;

        Assert.Equal(batch.BatchId, reopened.Batch.BatchId);
        Assert.Equal(batch.ItemVersionsHash, reopened.Batch.ItemVersionsHash);
        Assert.Equal(Local(batch).Conversation, reopened.Conversation);
        Assert.Equal(WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId), reopened.TaskId);
        Assert.Equal(WorkflowOutcomeLocalStates.DeliveryDelivered, reopened.Delivery);
        Assert.Equal(2, reopened.DeliveryAttempts);
        Assert.Equal("receipt-hash", reopened.Receipt!.ReceiptHash);
        Assert.Equal(refs, reopened.Receipt.AckedVersions);
        Assert.Equal(WorkflowOutcomeLocalStates.TurnCompleted, reopened.Turn);
        Assert.Equal(WorkflowOutcomeLocalStates.AckAcked, reopened.Ack);
        Assert.Equal(refs, reopened.Output!.Ack);
        var d = Assert.Single(reopened.Decisions);
        Assert.Equal("hold", d.Decision.Operation);
        Assert.Equal(decision.DueAt, d.Decision.DueAt);
        Assert.Equal(WorkflowOutcomeLocalStates.DecisionPending, d.State);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public void FailedCommit_LeavesTheOldFileAndTheOldStateExactlyAsTheyWere()
    {
        var first = Batch();
        var store = Open();
        Assert.True(store.TryAdd(Local(first)));
        var before = File.ReadAllBytes(InboxPath);

        store.FaultBeforeCommitForTest = () => new IOException("no space left on device");
        Assert.False(store.TryAdd(Local(Batch())));
        Assert.False(store.TryUpdate(first.BatchId, b => b with { Delivery = WorkflowOutcomeLocalStates.DeliveryDelivered }));

        Assert.Equal(before, File.ReadAllBytes(InboxPath));
        Assert.Single(store.Snapshot());
        Assert.Equal(WorkflowOutcomeLocalStates.DeliveryPending, store.Get(first.BatchId)!.Delivery);
        Assert.Equal(nameof(IOException), store.LastError);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(InboxPath)!));
    }

    [Theory]
    [Trait(Suite, "receipts")]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\":2,\"batches\":[]}")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("")]
    public void Load_CorruptOrUnknownFile_ThrowsAndNeverRewritesIt(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InboxPath)!);
        File.WriteAllText(InboxPath, content);
        var store = new WorkflowOutcomeLocalStore(InboxPath);

        Assert.Throws<WorkflowOutcomeStoreException>(store.Load);
        Assert.Throws<InvalidOperationException>(() => store.TryAdd(Local(Batch())));
        Assert.Equal(content, File.ReadAllText(InboxPath));
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public void Load_MissingFile_StartsEmptyWithoutCreatingIt()
    {
        var store = Open();

        Assert.Empty(store.Snapshot());
        Assert.False(File.Exists(InboxPath));
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public void TryAdd_SameBatchTwice_KeepsTheFirstCopy()
    {
        var batch = Batch();
        var store = Open();

        Assert.True(store.TryAdd(Local(batch)));
        Assert.False(store.TryAdd(Local(batch) with { TaskId = "outcome/other" }));

        Assert.Equal(WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId), Open().Get(batch.BatchId)!.TaskId);
    }
}
