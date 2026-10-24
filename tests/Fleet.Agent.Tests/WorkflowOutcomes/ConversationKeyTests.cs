using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;
using static Fleet.Agent.Tests.WorkflowOutcomes.WorkflowOutcomeFixture;

namespace Fleet.Agent.Tests.WorkflowOutcomes;

/// <summary>
/// #476 AC11: the internal conversation key comes from the actual registry, in the positive reserved
/// band; it is never hard-coded, persisted as a number or replaced by a fallback.
/// </summary>
public sealed class ConversationKeyTests
{
    private const string Suite = "WorkflowOutcomeSuite";

    [Fact]
    [Trait(Suite, "coordinator")]
    public void Resolve_InternalRef_AllocatesAPositiveKeyInTheReservedBand()
    {
        var registry = new ConversationRegistry();
        var reference = WorkflowOutcomeCompletionHandler.ConversationFor(Guid.NewGuid(), Coordinator, 1);

        var key = registry.Resolve(reference);

        Assert.True(key > 0);
        Assert.InRange(key, ConversationRegistry.ReservedBandStart, ConversationRegistry.ReservedBandEnd - 1);
        Assert.True(registry.IsReserved(key));
        Assert.Equal(reference, registry.Lookup(key));
        Assert.Equal(WorkflowOutcomeTurnIdentity.ChannelId, reference.ChannelId);
        Assert.Equal(Coordinator + "/1", reference.PrincipalId);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public void Resolve_SameRefIsIdempotent_DifferentBatchOrPrincipalIsDistinct()
    {
        var registry = new ConversationRegistry();
        var batchId = Guid.NewGuid();
        var reference = WorkflowOutcomeCompletionHandler.ConversationFor(batchId, Coordinator, 1);

        var key = registry.Resolve(reference);

        Assert.Equal(key, registry.Resolve(WorkflowOutcomeCompletionHandler.ConversationFor(batchId, Coordinator, 1)));
        var otherBatch = registry.Resolve(WorkflowOutcomeCompletionHandler.ConversationFor(Guid.NewGuid(), Coordinator, 1));
        var otherGeneration = registry.Resolve(WorkflowOutcomeCompletionHandler.ConversationFor(batchId, Coordinator, 2));
        var otherCoordinator = registry.Resolve(WorkflowOutcomeCompletionHandler.ConversationFor(batchId, "agent1", 1));
        Assert.Equal(4, new[] { key, otherBatch, otherGeneration, otherCoordinator }.Distinct().Count());
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public void InternalChannel_IsRuntimeOwnedSoNoClientOrAdapterCanClaimIt()
    {
        Assert.Equal(WorkflowOutcomeTurnIdentity.ChannelId, ChannelIds.WorkflowOutcome);
        Assert.True(ChannelIds.IsRuntimeOwned(ChannelIds.WorkflowOutcome));
        Assert.True(ChannelIds.IsRuntimeOwned(ChannelIds.Telegram));
        Assert.True(ChannelIds.IsRuntimeOwned(ChannelIds.Relay));
        Assert.False(ChannelIds.IsRuntimeOwned("example-adapter"));
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task Restart_ReResolvesThePersistedRefIntoAFreshProcessLocalKey()
    {
        var a = Item();
        var batch = Batch(a);
        // The agent stays busy with human work, so no turn is admitted in the first process.
        await using var first = new CoordinatorRuntime(executor: new ScriptedExecutor { BlockUntilCancelled = true });
        try
        {
            first.Server.Batches.Add(batch);
            Assert.True(first.Start());
            await first.Manager.StartTask(4242, "human", "human", true, Models.TaskSource.UserMessage, userId: 4242);
            await first.CycleAsync();
            var persisted = first.Local(batch.BatchId).Conversation;
            Assert.Equal(WorkflowOutcomeCompletionHandler.ConversationFor(batch.BatchId, Coordinator, 1), persisted);
            Assert.DoesNotContain("\"key\"", File.ReadAllText(first.InboxPath), StringComparison.OrdinalIgnoreCase);
            await first.DisposeAsync();

            await using var second = first.Restart(new ScriptedExecutor([ScriptedExecutor.Final(Output(batch, [a]))]));
            Assert.True(second.Start());
            await second.CycleAsync();

            var key = second.Handler.KeyFor(batch.BatchId) ?? second.Registry.Resolve(persisted);
            Assert.True(second.Registry.IsReserved(key));
            Assert.Equal(persisted, second.Registry.Lookup(key));
            await second.UntilAsync(() => second.Local(batch.BatchId).Completed, "the review in the new process");
            Assert.Single(second.Executor.Tasks);
            await second.Manager.CancelAllAsync();
        }
        finally
        {
            await first.Manager.CancelAllAsync();
            first.DeleteDir();
        }
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task Exhaustion_LeavesTheBatchPendingWithNoFallbackKeyAndNoTurn()
    {
        var registry = new ExhaustedRegistry();
        await using var rt = new CoordinatorRuntime(registry: registry);
        try
        {
            var batch = Batch();
            rt.Server.Batches.Add(batch);
            Assert.True(rt.Start());

            for (var i = 0; i < 3; i++) await rt.CycleAsync();

            Assert.Equal(WorkflowOutcomeLocalStates.DeliveryDelivered, rt.Local(batch.BatchId).Delivery);
            Assert.Equal(WorkflowOutcomeLocalStates.TurnPending, rt.Local(batch.BatchId).Turn);
            Assert.Empty(rt.Executor.Tasks);
            Assert.Equal(3, registry.ResolveCalls);
            Assert.Null(rt.Handler.KeyFor(batch.BatchId));
        }
        finally
        {
            rt.DeleteDir();
        }
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task Completion_UnregistersTheKeyOnlyAfterTheTaskEndedAndTheBatchCompleted()
    {
        var a = Item();
        var batch = Batch(a);
        await using var rt = new CoordinatorRuntime(executor: new ScriptedExecutor([ScriptedExecutor.Final(Output(batch, [a]))]));
        try
        {
            rt.Server.Batches.Add(batch);
            rt.Server.ForcedStatus["ack"] = System.Net.HttpStatusCode.ServiceUnavailable;
            Assert.True(rt.Start());
            await rt.CycleAsync();
            await rt.UntilAsync(() => rt.Local(batch.BatchId).Turn == WorkflowOutcomeLocalStates.TurnCompleted, "the turn");

            // Ack still pending: the batch is not complete, so the conversation stays registered.
            var key = rt.Handler.KeyFor(batch.BatchId);
            Assert.NotNull(key);
            await rt.CycleAsync(fetch: false);
            Assert.Equal(WorkflowOutcomeCompletionHandler.ConversationFor(batch.BatchId, Coordinator, 1), rt.Registry.Lookup(key!.Value));

            rt.Server.ForcedStatus.Remove("ack");
            await rt.CycleAsync(fetch: false);
            await rt.CycleAsync(fetch: false);

            Assert.True(rt.Local(batch.BatchId).Completed);
            Assert.Null(rt.Registry.Lookup(key.Value));
            Assert.Null(rt.Handler.KeyFor(batch.BatchId));
        }
        finally
        {
            rt.DeleteDir();
        }
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task TurnEnd_WithAForeignKeySourceOrTaskId_IsRejectedAndChangesNothing()
    {
        var batch = Batch();
        await using var rt = new CoordinatorRuntime(executor: new ScriptedExecutor { BlockUntilCancelled = true });
        try
        {
            rt.Server.Batches.Add(batch);
            Assert.True(rt.Start());
            await rt.CycleAsync();
            await rt.UntilAsync(() => rt.Executor.Tasks.Count == 1, "the review to start");
            var key = rt.Handler.KeyFor(batch.BatchId)!.Value;
            var valid = Output(batch, batch.Items);
            var taskId = WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId);

            Assert.Null(rt.Handler.MatchAdmitted(new(key, taskId, Models.TaskSource.Relay, valid, Models.CompletionKind.Completed, false)));
            Assert.Null(rt.Handler.MatchAdmitted(new(4242, taskId, Models.TaskSource.WorkflowOutcome, valid, Models.CompletionKind.Completed, false)));
            Assert.Null(rt.Handler.MatchAdmitted(new(key + 1, taskId, Models.TaskSource.WorkflowOutcome, valid, Models.CompletionKind.Completed, false)));
            Assert.Null(rt.Handler.MatchAdmitted(new(key, WorkflowOutcomeTurnIdentity.TaskId(Guid.NewGuid()),
                Models.TaskSource.WorkflowOutcome, valid, Models.CompletionKind.Completed, false)));
            Assert.NotNull(rt.Handler.MatchAdmitted(new(key, taskId, Models.TaskSource.WorkflowOutcome, valid, Models.CompletionKind.Completed, false)));
            Assert.Equal(WorkflowOutcomeLocalStates.TurnAdmitted, rt.Local(batch.BatchId).Turn);
            await rt.Manager.CancelAllAsync();
        }
        finally
        {
            rt.DeleteDir();
        }
    }

    private sealed class ExhaustedRegistry : IConversationRegistry
    {
        public int ResolveCalls;

        public long Resolve(ConversationRef reference)
        {
            Interlocked.Increment(ref ResolveCalls);
            throw new InvalidOperationException("Reserved conversation key band exhausted");
        }

        public ConversationRef? Lookup(long runtimeKey) => null;
        public bool IsReserved(long runtimeKey) => ConversationRegistry.IsReservedKey(runtimeKey);
        public bool Unregister(long runtimeKey) => false;
    }
}
