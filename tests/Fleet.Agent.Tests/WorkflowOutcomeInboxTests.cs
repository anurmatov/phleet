using System.Net;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using NSubstitute;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;
using static Fleet.Agent.Tests.WorkflowOutcomes.WorkflowOutcomeFixture;

namespace Fleet.Agent.Tests.WorkflowOutcomes;

/// <summary>
/// #476 coordinator runtime, end to end through the real pull loop, local store, registry,
/// completion handler and TaskManager against an in-memory server (AC10, AC11, AC12). No real
/// orchestrator, broker or provider is involved.
/// </summary>
public sealed class WorkflowOutcomeInboxTests : IAsyncLifetime
{
    private const string Suite = "WorkflowOutcomeSuite";
    private readonly List<CoordinatorRuntime> _runtimes = [];

    private CoordinatorRuntime Runtime(FakeOutcomeServer? server = null, ScriptedExecutor? executor = null)
    {
        var rt = new CoordinatorRuntime(server, executor);
        _runtimes.Add(rt);
        return rt;
    }

    private CoordinatorRuntime Restarted(CoordinatorRuntime previous, ScriptedExecutor? executor = null, string token = Token)
    {
        var rt = previous.Restart(executor, token);
        _runtimes.Add(rt);
        return rt;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rt in _runtimes)
        {
            await rt.DisposeAsync();
            await rt.Manager.CancelAllAsync();
        }
        foreach (var rt in _runtimes) rt.DeleteDir();
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private static void AssertNoExternalOutput(CoordinatorRuntime rt)
    {
        Assert.Empty(rt.Sink.ReceivedCalls());
        Assert.Empty(rt.Events.Published);
        Assert.Equal(0, rt.TaskCompletedCount);
    }

    // ── delivery and the happy path ───────────────────────────────────────────────────────

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task RunCycle_FinishDecision_SendsItsArtifactRefAsArtifactRef()
    {
        var a = Item();
        var batch = Batch(a);
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Executor.Enqueue([ScriptedExecutor.Final(Output(batch, [a], [a], operation: "finish",
            evidence: ["event:7", "artifact:owner/repo#pr/7"]))]);

        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.UntilAsync(() => rt.Local(batch.BatchId).Completed, "the batch to complete");

        var decision = Assert.Single(rt.Server.Decisions);
        Assert.Equal(("finish", "artifact:owner/repo#pr/7"), (decision.Request.Operation, decision.Request.ArtifactRef));
        AssertNoExternalOutput(rt);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_FailureBatch_FsyncsBeforeDeliveredThenOneSilentTurnAcksAndDecides()
    {
        var a = Item();
        var b = Item();
        var batch = Batch(a, b);
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Executor.Enqueue([ScriptedExecutor.Final(Output(batch, [a, b], [a]))]);

        int? deliveredCallsAtFirstCommit = null;
        rt.Store.FaultBeforeCommitForTest = () =>
        {
            deliveredCallsAtFirstCommit ??= rt.Server.Calls("delivered").Count();
            return null;
        };

        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.UntilAsync(() => rt.Local(batch.BatchId).Completed, "the batch to complete");

        Assert.Equal(0, deliveredCallsAtFirstCommit);
        var local = rt.Local(batch.BatchId);
        Assert.Equal(WorkflowOutcomeLocalStates.DeliveryDelivered, local.Delivery);
        Assert.Equal("parsed", local.TurnResult);
        Assert.Equal(WorkflowOutcomeLocalStates.AckAcked, local.Ack);
        Assert.All(local.Decisions, d => Assert.Equal(WorkflowOutcomeLocalStates.DecisionAccepted, d.State));

        var task = Assert.Single(rt.Executor.Tasks);
        Assert.Contains(batch.BatchId.ToString("D"), task, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, task, StringComparison.Ordinal);
        Assert.DoesNotContain(batch.Nonce, task, StringComparison.Ordinal);

        var receipt = rt.Server.Receipts[batch.BatchId];
        Assert.Equal(new[] { new WorkflowOutcomeItemRef(a.Id, 1), new WorkflowOutcomeItemRef(b.Id, 1) }.OrderBy(r => r.Id),
            receipt.AckedVersions.OrderBy(r => r.Id));
        var decision = Assert.Single(rt.Server.Decisions);
        Assert.Equal(a.Id, decision.OutcomeId);
        Assert.Equal(1, decision.Request.ExpectedVersion);
        Assert.Equal(batch.BatchId, decision.Request.BatchId);
        Assert.Equal(1L, decision.Request.Generation);
        Assert.All(rt.Server.Requests, r => Assert.Equal("Bearer " + Token, r.Authorization));
        Assert.All(rt.Server.Requests, r => Assert.DoesNotContain("agent=", r.PathAndQuery, StringComparison.Ordinal));

        AssertNoExternalOutput(rt);
        await rt.UntilAsync(() => rt.Handler.KeyFor(batch.BatchId) is null, "the conversation to be released");
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_FetchedButStoreFailsBeforeCommit_PostsNoDeliveredAndStartsNoTask()
    {
        var batch = Batch();
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Store.FaultBeforeCommitForTest = () => new IOException("disk full");

        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.CycleAsync();

        Assert.NotEmpty(rt.Server.Calls("inbox"));
        Assert.Empty(rt.Server.Calls("delivered"));
        Assert.Empty(rt.Server.Receipts);
        Assert.Empty(rt.Executor.Tasks);
        Assert.Null(rt.Store.Get(batch.BatchId));
        Assert.False(File.Exists(rt.InboxPath));
        Assert.Equal("store_error", rt.Pull.LastState);

        // The process dies here (pre-fsync kill). A restart finds nothing, fetches again, and only
        // then delivers — fetched is never delivered.
        var restarted = Restarted(rt, new ScriptedExecutor([ScriptedExecutor.Final(Output(batch, batch.Items))]));
        Assert.True(restarted.Start());
        await restarted.CycleAsync();
        await restarted.UntilAsync(() => restarted.Local(batch.BatchId).Completed, "the batch to complete");
        Assert.Single(restarted.Server.Calls("delivered"));
        Assert.Single(restarted.Executor.Tasks);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_LostDeliveredResponse_ReconcilesByReceiptGetAfterRestartWithoutResend()
    {
        var batch = Batch();
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Server.LoseResponseAfterApply.Add("delivered");

        Assert.True(rt.Start());
        await rt.CycleAsync();
        Assert.Equal(WorkflowOutcomeLocalStates.DeliveryPending, rt.Local(batch.BatchId).Delivery);
        Assert.Equal(1, rt.Local(batch.BatchId).DeliveryAttempts);
        Assert.Empty(rt.Executor.Tasks);
        Assert.Equal("delivered_no_response", rt.Pull.LastState);
        await rt.DisposeAsync();

        var restarted = Restarted(rt, new ScriptedExecutor([ScriptedExecutor.Final(Output(batch, batch.Items))]));
        Assert.True(restarted.Start());
        await restarted.CycleAsync();
        await restarted.UntilAsync(() => restarted.Local(batch.BatchId).Completed, "the batch to complete");

        Assert.Single(restarted.Server.Calls("delivered"));
        var routes = restarted.Server.Requests.Select(r => r.Route).ToList();
        Assert.True(routes.IndexOf("receipt") > routes.IndexOf("delivered"), "the readback follows the lost POST");
        Assert.Equal(WorkflowOutcomeLocalStates.DeliveryDelivered, restarted.Local(batch.BatchId).Delivery);
        Assert.Single(restarted.Executor.Tasks);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_DeliveredPostNeverLanded_ReadsBackThenResendsTheSameReceiptRequest()
    {
        var batch = Batch();
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Server.FailBeforeApply.Add("delivered");

        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.CycleAsync(fetch: false);

        var delivered = rt.Server.Calls("delivered").ToList();
        Assert.Equal(2, delivered.Count);
        Assert.Equal(delivered[0].Body, delivered[1].Body);
        var routes = rt.Server.Requests.Select(r => r.Route).ToList();
        Assert.Equal("receipt", routes[routes.LastIndexOf("delivered") - 1]);
        Assert.Equal(WorkflowOutcomeLocalStates.DeliveryDelivered, rt.Local(batch.BatchId).Delivery);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_LostAckResponse_ReconcilesByReceiptGetAndNeverRerunsTheModel()
    {
        var a = Item();
        var batch = Batch(a);
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Server.LoseResponseAfterApply.Add("ack");
        rt.Executor.Enqueue([ScriptedExecutor.Final(Output(batch, [a]))]);

        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.UntilAsync(() => rt.Local(batch.BatchId).AckAttempts == 1, "the ack attempt");
        Assert.Equal(WorkflowOutcomeLocalStates.AckPending, rt.Local(batch.BatchId).Ack);
        // No further cycle here: the readback must happen in the restarted process.
        await WaitAsync(() => !rt.Manager.IsTaskIdActive(WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId)));
        await rt.DisposeAsync();

        var restarted = Restarted(rt);
        Assert.True(restarted.Start());
        await restarted.CycleAsync();

        Assert.Equal(WorkflowOutcomeLocalStates.AckAcked, restarted.Local(batch.BatchId).Ack);
        Assert.True(restarted.Local(batch.BatchId).Completed);
        Assert.Single(restarted.Server.Calls("ack"));
        Assert.Single(rt.Executor.Tasks);
        Assert.Empty(restarted.Executor.Tasks);
    }

    // ── credentials, generations, outages ─────────────────────────────────────────────────

    [Theory]
    [Trait(Suite, "receipts")]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    [InlineData(HttpStatusCode.Conflict, "generation_conflict")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task RunCycle_InboxRefused_StoresNothingAndNamesTheState(HttpStatusCode status, string state)
    {
        var rt = Runtime();
        rt.Server.Batches.Add(Batch());
        rt.Server.ForcedStatus["inbox"] = status;

        Assert.True(rt.Start());
        await rt.CycleAsync();

        Assert.Equal(state, rt.Pull.LastState);
        Assert.Empty(rt.Store.Snapshot());
        Assert.Empty(rt.Server.Calls("delivered"));
        Assert.Empty(rt.Executor.Tasks);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_StaleGeneration_IsRefusedBy409AndRunsNothing()
    {
        var server = new FakeOutcomeServer { Generation = 2 };
        server.Batches.Add(BatchWith(Guid.NewGuid(), 2, Item()));
        var rt = Runtime(server);

        Assert.True(rt.Start());
        await rt.CycleAsync();

        Assert.Equal("generation_conflict", rt.Pull.LastState);
        Assert.Contains(rt.Server.Calls("inbox"), c => c.PathAndQuery.Contains("generation=1", StringComparison.Ordinal));
        Assert.Empty(rt.Store.Snapshot());
        Assert.Empty(rt.Executor.Tasks);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_RotatedTokenBeforeAck_OldToken401ThenNewTokenAcksWithoutRerun()
    {
        var a = Item();
        var batch = Batch(a);
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Executor.Enqueue([ScriptedExecutor.Final(Output(batch, [a]))]);
        Assert.True(rt.Start());

        // Rotate as soon as the turn has produced its parsed result, before the ack is posted.
        rt.Handler.Changed += () => rt.Server.Token = "rotated-token-placeholder";
        await rt.CycleAsync();
        await rt.UntilAsync(() => rt.Pull.LastState == "unauthorized", "the old token to be refused");
        Assert.Equal(WorkflowOutcomeLocalStates.AckPending, rt.Local(batch.BatchId).Ack);
        await rt.DisposeAsync();

        var restarted = Restarted(rt, token: "rotated-token-placeholder");
        Assert.True(restarted.Start());
        await restarted.CycleAsync();

        Assert.Equal(WorkflowOutcomeLocalStates.AckAcked, restarted.Local(batch.BatchId).Ack);
        Assert.Single(rt.Executor.Tasks);
        Assert.Empty(restarted.Executor.Tasks);
        Assert.Contains(new WorkflowOutcomeItemRef(a.Id, 1), restarted.Server.Receipts[batch.BatchId].AckedVersions);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_DeliveredRefusedWith409_SupersedesTheBatchWithNoTurn()
    {
        var batch = Batch();
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Server.ForcedStatus["delivered"] = HttpStatusCode.Conflict;

        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.CycleAsync();

        Assert.Equal(WorkflowOutcomeLocalStates.DeliverySuperseded, rt.Local(batch.BatchId).Delivery);
        Assert.Single(rt.Server.Calls("delivered"));
        Assert.Empty(rt.Executor.Tasks);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_BatchWithForgedMembershipHash_IsNeitherStoredNorDelivered()
    {
        var batch = Batch(Item()) with { ItemVersionsHash = new string('0', 64) };
        var rt = Runtime();
        rt.Server.Batches.Add(batch);

        Assert.True(rt.Start());
        await rt.CycleAsync();

        Assert.Null(rt.Store.Get(batch.BatchId));
        Assert.Empty(rt.Server.Calls("delivered"));
        Assert.Empty(rt.Executor.Tasks);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task RunCycle_BacklogAcrossPages_IsReadInFullAndDeliveredBatchByBatch()
    {
        var rt = Runtime(new FakeOutcomeServer { PageSize = 2 });
        var batches = Enumerable.Range(0, 5).Select(_ => Batch()).ToList();
        rt.Server.Batches.AddRange(batches);

        Assert.True(rt.Start());
        await rt.CycleAsync();

        Assert.Equal(3, rt.Server.Calls("inbox").Count());
        Assert.All(batches, b => Assert.Equal(WorkflowOutcomeLocalStates.DeliveryDelivered, rt.Local(b.BatchId).Delivery));
        Assert.Equal(5, rt.Server.Calls("delivered").Count());
    }

    // ── output validation: nothing but an exact raw object acknowledges ───────────────────

    public static TheoryData<string> RejectedOutputs => new()
    {
        "plain",
        "fenced",
        "prose-before",
        "trailing",
        "foreign-id",
        "stale-version",
        "unknown-key",
        "duplicate-key",
        "wrong-batch",
        "decision-not-acked",
        "wait-without-due",
        "partial",
        "failed",
        "no-output",
    };

    [Theory]
    [Trait(Suite, "receipts")]
    [MemberData(nameof(RejectedOutputs))]
    public async Task Turn_MalformedFencedForeignOrPartialOutput_AcknowledgesAndDecidesNothing(string shape)
    {
        var a = Item();
        var batch = Batch(a);
        var valid = Output(batch, [a]);
        var foreign = Item();
        AgentProgress[] script = shape switch
        {
            "plain" => [ScriptedExecutor.Final("Reviewed: all good, acknowledged.")],
            "fenced" => [ScriptedExecutor.Final("```json\n" + valid + "\n```")],
            "prose-before" => [ScriptedExecutor.Final("Here is the result: " + valid)],
            "trailing" => [ScriptedExecutor.Final(valid + " done")],
            "foreign-id" => [ScriptedExecutor.Final(Output(batch, [foreign]))],
            "stale-version" => [ScriptedExecutor.Final(Output(batch, [a with { Version = 2 }]))],
            "unknown-key" => [ScriptedExecutor.Final(valid.Insert(1, "\"extra\":1,"))],
            "duplicate-key" => [ScriptedExecutor.Final(valid.Insert(1, "\"schemaVersion\":1,"))],
            "wrong-batch" => [ScriptedExecutor.Final(Output(Batch(a), [a]))],
            "decision-not-acked" => [ScriptedExecutor.Final(Output(batch, [], [a]))],
            "wait-without-due" => [ScriptedExecutor.Final(Output(batch, [a], [a], operation: "wait"))],
            "partial" => [ScriptedExecutor.ErrorFinal(valid)],
            "failed" => [new AgentProgress { EventType = "error", Summary = "executor crashed" }],
            "no-output" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Executor.Enqueue(script);
        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.UntilAsync(() => rt.Local(batch.BatchId).Turn == WorkflowOutcomeLocalStates.TurnCompleted, "the turn to end");
        await rt.CycleAsync();

        var local = rt.Local(batch.BatchId);
        Assert.True(local.Completed);
        Assert.Null(local.Output);
        Assert.Equal(WorkflowOutcomeLocalStates.AckNone, local.Ack);
        Assert.Matches("^(rejected|failed):", local.TurnResult);
        Assert.Empty(rt.Server.Calls("ack"));
        Assert.Empty(rt.Server.Decisions);
        Assert.Empty(rt.Server.Receipts[batch.BatchId].AckedVersions);
        Assert.Single(rt.Executor.Tasks);
        AssertNoExternalOutput(rt);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task Turn_ExplicitSubset_AcknowledgesOnlyTheListedMemberVersions()
    {
        var a = Item();
        var b = Item(version: 3);
        var c = Item();
        var batch = Batch(a, b, c);
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Executor.Enqueue([ScriptedExecutor.Final(Output(batch, [b], [b], operation: "reassign"))]);

        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.UntilAsync(() => rt.Local(batch.BatchId).Completed, "the batch to complete");

        Assert.Equal(new[] { new WorkflowOutcomeItemRef(b.Id, 3) }, rt.Server.Receipts[batch.BatchId].AckedVersions);
        var ack = Assert.Single(rt.Server.Calls("ack"));
        Assert.Contains(b.Id.ToString("D"), ack.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(a.Id.ToString("D"), ack.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(c.Id.ToString("D"), ack.Body, StringComparison.Ordinal);
        Assert.Equal(b.Id, Assert.Single(rt.Server.Decisions).OutcomeId);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public async Task Decisions_StaleUnsafeAndUnavailable_AreRecordedOrRetriedWithoutRerun()
    {
        var a = Item();
        var b = Item();
        var c = Item();
        var batch = Batch(a, b, c);
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        rt.Server.DecisionStatus[a.Id] = HttpStatusCode.Conflict;
        rt.Server.DecisionStatus[b.Id] = HttpStatusCode.UnprocessableEntity;
        rt.Server.DecisionStatus[c.Id] = HttpStatusCode.ServiceUnavailable;
        rt.Executor.Enqueue([ScriptedExecutor.Final(Output(batch, [a, b, c], [a, b, c]))]);

        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.UntilAsync(() => rt.Local(batch.BatchId).Ack == WorkflowOutcomeLocalStates.AckAcked
            && rt.Local(batch.BatchId).Decisions.Count(d => d.State != WorkflowOutcomeLocalStates.DecisionPending) == 2, "decisions");

        var local = rt.Local(batch.BatchId);
        Assert.False(local.Completed);
        Assert.Equal(WorkflowOutcomeLocalStates.DecisionStale, local.Decisions.Single(d => d.Decision.Id == a.Id).State);
        Assert.Equal(WorkflowOutcomeLocalStates.DecisionRejected, local.Decisions.Single(d => d.Decision.Id == b.Id).State);
        Assert.Equal(WorkflowOutcomeLocalStates.DecisionPending, local.Decisions.Single(d => d.Decision.Id == c.Id).State);

        rt.Server.DecisionStatus.Remove(c.Id);
        await rt.CycleAsync(fetch: false);

        local = rt.Local(batch.BatchId);
        Assert.True(local.Completed);
        Assert.Equal(WorkflowOutcomeLocalStates.DecisionAccepted, local.Decisions.Single(d => d.Decision.Id == c.Id).State);
        Assert.Single(rt.Executor.Tasks);
    }

    // ── scheduling, busy, restart ─────────────────────────────────────────────────────────

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task Scheduling_BusyAgent_LeavesTheBatchPendingUntilHumanWorkEnds()
    {
        var a = Item();
        var batch = Batch(a);
        var executor = new ScriptedExecutor([ScriptedExecutor.Final("human answer")]) { BlockFirstTurnUntilReleased = true };
        executor.Enqueue([ScriptedExecutor.Final(Output(batch, [a]))]);
        var rt = Runtime(executor: executor);
        rt.Server.Batches.Add(batch);
        Assert.True(rt.Start());

        await rt.Manager.StartTask(4242, "hello", "hello", isSessionTask: true, source: TaskSource.UserMessage, userId: 4242);
        await executor.FirstTurnStarted;
        await rt.CycleAsync();
        await rt.CycleAsync();

        Assert.Equal(WorkflowOutcomeLocalStates.DeliveryDelivered, rt.Local(batch.BatchId).Delivery);
        Assert.Equal(WorkflowOutcomeLocalStates.TurnPending, rt.Local(batch.BatchId).Turn);
        Assert.Single(executor.Tasks);

        executor.Release();
        await rt.UntilAsync(() => rt.Local(batch.BatchId).Completed, "the review after the human turn");
        Assert.Equal(2, executor.Tasks.Count);
        Assert.Equal("hello", executor.Tasks[0]);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task Scheduling_TwoBatches_NeverMoreThanOneOutstandingTurn()
    {
        var first = Batch();
        var second = Batch() with { CreatedAt = first.CreatedAt.AddSeconds(1) };
        var executor = new ScriptedExecutor([ScriptedExecutor.Final(Output(first, first.Items))]) { BlockFirstTurnUntilReleased = true };
        executor.Enqueue([ScriptedExecutor.Final(Output(second, second.Items))]);
        var rt = Runtime(executor: executor);
        rt.Server.Batches.AddRange([first, second]);
        Assert.True(rt.Start());

        await rt.CycleAsync();
        await executor.FirstTurnStarted;
        await rt.CycleAsync();
        await rt.CycleAsync(fetch: false);

        Assert.Single(executor.Tasks);
        Assert.Single(rt.Store.Snapshot(), b => b.Turn == WorkflowOutcomeLocalStates.TurnAdmitted);
        Assert.Equal(WorkflowOutcomeLocalStates.TurnPending, rt.Local(second.BatchId).Turn);

        executor.Release();
        await rt.UntilAsync(() => rt.Local(second.BatchId).Completed, "both reviews");
        Assert.Equal(2, executor.Tasks.Count);
        Assert.Contains(first.BatchId.ToString("D"), executor.Tasks[0], StringComparison.Ordinal);
        Assert.Contains(second.BatchId.ToString("D"), executor.Tasks[1], StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task Restart_AfterAdmission_IsTurnUnknownAndNeverReruns()
    {
        var batch = Batch();
        var rt = Runtime(executor: new ScriptedExecutor { BlockUntilCancelled = true });
        rt.Server.Batches.Add(batch);
        Assert.True(rt.Start());
        await rt.CycleAsync();
        await rt.UntilAsync(() => rt.Executor.Tasks.Count == 1, "the review to start");
        Assert.Equal(WorkflowOutcomeLocalStates.TurnAdmitted, rt.Local(batch.BatchId).Turn);

        // The process dies mid-turn: the old handler is gone before its task can report.
        await rt.DisposeAsync();

        var restarted = Restarted(rt);
        Assert.True(restarted.Start());
        for (var i = 0; i < 3; i++) await restarted.CycleAsync();

        var local = restarted.Local(batch.BatchId);
        Assert.Equal(WorkflowOutcomeLocalStates.TurnUnknown, local.Turn);
        Assert.False(local.Completed);
        Assert.Empty(restarted.Executor.Tasks);
        Assert.Empty(restarted.Server.Calls("ack"));
        Assert.Empty(restarted.Server.Receipts[batch.BatchId].AckedVersions);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task Reconcile_AdmittedTurnWithNoLiveTask_IsTurnUnknownNotRestarted()
    {
        var batch = Batch();
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        Assert.True(rt.Start());
        Assert.True(rt.Store.TryAdd(new WorkflowOutcomeLocalBatch
        {
            Batch = batch,
            Conversation = WorkflowOutcomeCompletionHandler.ConversationFor(batch.BatchId, Coordinator, 1),
            TaskId = WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId),
            Delivery = WorkflowOutcomeLocalStates.DeliveryDelivered,
            Turn = WorkflowOutcomeLocalStates.TurnAdmitted,
        }));

        await rt.CycleAsync(fetch: false);

        Assert.Equal(WorkflowOutcomeLocalStates.TurnUnknown, rt.Local(batch.BatchId).Turn);
        Assert.Equal("task_lost", rt.Local(batch.BatchId).TurnResult);
        Assert.Empty(rt.Executor.Tasks);
    }

    // ── no success path, no recursion ─────────────────────────────────────────────────────

    [Fact]
    [Trait(Suite, "recursion")]
    [Trait(Suite, "coordinator")]
    public async Task NoFailureBatch_ManyCycles_RunZeroModelTurnsAndWriteNothing()
    {
        var rt = Runtime();
        Assert.True(rt.Start());

        for (var i = 0; i < 200; i++) await rt.CycleAsync();

        Assert.Empty(rt.Executor.Tasks);
        Assert.All(rt.Server.Requests, r => Assert.Equal("inbox", r.Route));
        Assert.Empty(rt.Store.Snapshot());
        AssertNoExternalOutput(rt);
    }

    [Fact]
    [Trait(Suite, "recursion")]
    public async Task BatchWithNoConfirmedFailureMember_IsNeverAModelTurn()
    {
        var batch = Batch(Item(classification: WorkflowOutcomeClassifications.ExcludedNonfailure),
            Item(classification: WorkflowOutcomeClassifications.InsufficientEvidence));
        var rt = Runtime();
        rt.Server.Batches.Add(batch);
        Assert.True(rt.Start());

        for (var i = 0; i < 5; i++) await rt.CycleAsync();

        Assert.Equal("no_failure_members", rt.Local(batch.BatchId).TurnResult);
        Assert.Empty(rt.Executor.Tasks);
        Assert.Empty(rt.Server.Calls("ack"));
    }

    [Fact]
    [Trait(Suite, "recursion")]
    public async Task InfrastructureFailure_Repeated_UsesOnlyTheNamedHealthStateAndNeverATurnOrMessage()
    {
        var rt = Runtime();
        rt.Server.Batches.Add(Batch());
        rt.Server.ForcedStatus["inbox"] = HttpStatusCode.ServiceUnavailable;
        Assert.True(rt.Start());

        for (var i = 0; i < 200; i++) await rt.CycleAsync();

        Assert.Equal("unavailable", rt.Pull.LastState);
        Assert.Equal(200, rt.Server.Calls("inbox").Count());
        Assert.All(rt.Server.Requests, r => Assert.Equal("inbox", r.Route));
        Assert.Empty(rt.Executor.Tasks);
        AssertNoExternalOutput(rt);
    }

    [Fact]
    [Trait(Suite, "receipts")]
    public void Start_CorruptLocalInbox_DisablesIntakeAndLeavesTheFileUntouched()
    {
        var rt = Runtime();
        Directory.CreateDirectory(Path.GetDirectoryName(rt.InboxPath)!);
        File.WriteAllText(rt.InboxPath, "{ not json");

        Assert.False(rt.Start());

        Assert.Equal("{ not json", File.ReadAllText(rt.InboxPath));
        Assert.Equal("store_unreadable", rt.Pull.LastState);
        Assert.False(rt.Pull.Ready);
        Assert.Empty(rt.Server.Requests);
    }
}
