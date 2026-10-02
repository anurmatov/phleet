using NSubstitute;
using Fleet.Agent.Models;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

public sealed partial class PrimaryHumanQueueTests
{
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Q1_Q3_Q4_RuntimeDispatchWaitsForRelayAndAppliesStarvationGuard(int primaryCount)
    {
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var manager = MidTurnIsolationTests.Manager("claude", executor);
        try
        {
            await manager.StartTask(1, "relay", "relay", true, TaskSource.Relay);
            await executor.WaitStarted(1);
            await manager.StartTask(2, "R1", "R1", true);
            for (var i = 1; i <= primaryCount; i++)
                await manager.StartTask(100 + i, $"P{i}", $"P{i}", true, priority: TaskPriority.PrimaryHuman, telegramMessageId: i);
            Assert.Equal(0, executor.InjectionAttempts);
            Assert.Single(executor.Tasks);
            Assert.Equal(primaryCount, manager.PriorityQueuedCount);
            var order = primaryCount == 1 ? new[] { "P1", "R1" } : ["P1", "P2", "P3", "R1", "P4", "P5"];
            for (var i = 0; i < order.Length; i++)
            {
                executor.Release(); await executor.WaitStarted(i + 2);
                Assert.Equal(order[i], executor.Tasks[i + 1]);
            }
            executor.Release(); await Until(() => manager.GetOrchestratorStatus().Status == "idle");
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Theory]
    [InlineData(TaskSource.UserMessage)]
    [InlineData(TaskSource.NewCommand)]
    public async Task Q13_Q19_DuplicatePrimaryPartCreatesOneEntry(TaskSource source)
    {
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var counter = new QueueLaneCounter();
        var manager = new TaskManager(Microsoft.Extensions.Options.Options.Create(new Fleet.Agent.Configuration.AgentOptions
        { Name = "agent1", Role = "test", WorkDir = "/tmp" }), executor, new SessionManager(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskManager>.Instance, queueCounter: counter);
        try
        {
            await manager.StartTask(1, "relay", "relay", true, TaskSource.Relay); await executor.WaitStarted(1);
            Assert.Equal(TaskDispatchOutcome.Queued, await manager.StartTask(2, "first", "first", true, source,
                priority: TaskPriority.PrimaryHuman, telegramMessageId: 7));
            Assert.Equal(TaskDispatchOutcome.Dropped, await manager.StartTask(2, "duplicate", "duplicate", true, source,
                priority: TaskPriority.PrimaryHuman, telegramMessageId: 7));
            Assert.Equal(1, counter.Count("primary_duplicate_dropped"));
            Assert.Single(manager.GetQueueSnapshot()); Assert.Equal(1, manager.GetQueueSnapshot()[0].PartCount);
            await manager.CancelAllAsync(); await Until(() => !manager.HasRunningTasks(1));
            // Clearing a never-dispatched entry releases its key.
            Assert.Equal(TaskDispatchOutcome.Ran, await manager.StartTask(2, "retry", "retry", true, source,
                priority: TaskPriority.PrimaryHuman, telegramMessageId: 7));
            await executor.WaitStarted(2); executor.Release(); await Until(() => !manager.HasRunningTasks(2));
            Assert.Equal(TaskDispatchOutcome.Dropped, await manager.StartTask(2, "recent duplicate", "duplicate", true, source,
                priority: TaskPriority.PrimaryHuman, telegramMessageId: 7));
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Theory]
    [InlineData("claude", MidTurnInjectionStatus.Injected)]
    [InlineData("claude", MidTurnInjectionStatus.NoActiveTurn)]
    [InlineData("codex", MidTurnInjectionStatus.NoActiveTurn)]
    [InlineData("gemini", MidTurnInjectionStatus.Unsupported)]
    public Task Q14_RuntimeSameChatInjectionOrContinuationPrecedesGlobalQueue(string provider, MidTurnInjectionStatus status) =>
        new MidTurnIsolationTests().X5_SameGroupUsesInjectionOrInboxBeforeDifferentGroupPriority(provider, status);

    [Fact]
    public void Q15_DedupEvictsDispatchedKeysButNeverQueuedOrRunningKeys()
    {
        var clock = new KeyClock(); var dedup = new PrimaryHumanDedup(clock);
        for (var i = 1; i <= 212; i++)
        { Assert.True(dedup.TryReserve(1, i)); dedup.Dispatched(1, i); dedup.Complete(1, i); clock.Advance(1); }
        for (var i = 1; i <= 300; i++) Assert.True(dedup.TryReserve(2, i));
        Assert.Equal(512, dedup.Count); Assert.True(dedup.TryReserve(3, 1));
        for (var i = 1; i <= 300; i++) Assert.False(dedup.TryReserve(2, i));
        Assert.True(dedup.TryReserve(1, 1)); // Oldest dispatched key was evicted, not a protected key.
        clock.Advance(600);
        Assert.True(dedup.TryReserve(1, 212));
        Assert.False(dedup.TryReserve(2, 1));
    }

    [Theory]
    [InlineData(TaskSource.UserMessage, TaskSource.UserMessage, 1)]
    [InlineData(TaskSource.UserMessage, TaskSource.UserMessage, 10)]
    [InlineData(TaskSource.UserMessage, TaskSource.NewCommand, 1)]
    [InlineData(TaskSource.NewCommand, TaskSource.UserMessage, 1)]
    public async Task Q5_Q6_Q16_Q17_RuntimePromotionPreservesHumanChatOrder(TaskSource earlier, TaskSource incoming, int parts)
    {
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var manager = MidTurnIsolationTests.Manager("claude", executor);
        try
        {
            await manager.StartTask(1, "relay", "relay", true, TaskSource.Relay); await executor.WaitStarted(1);
            await manager.StartTask(2, "R1", "R1", true);
            for (var i = 0; i < parts; i++) await manager.StartTask(-101, $"U{i + 1}", $"U{i + 1}", true, earlier);
            await manager.StartTask(-101, "primary", "primary", true, incoming,
                priority: TaskPriority.PrimaryHuman, telegramMessageId: 7);
            var promoted = manager.GetQueueSnapshot().Where(e => e.ChatId == -101).ToArray();
            Assert.All(promoted, e => Assert.Equal(TaskPriority.PrimaryHuman, e.Priority));
            var merged = earlier == TaskSource.UserMessage && incoming == TaskSource.UserMessage && parts < 10;
            Assert.Equal(merged ? 1 : 2, promoted.Length);
            executor.Release(); await executor.WaitStarted(2);
            Assert.Contains("U1", executor.Tasks[1]);
            Assert.Equal(merged, executor.Tasks[1].Contains("primary", StringComparison.Ordinal));
            executor.Release(); await executor.WaitStarted(3);
            Assert.Equal(merged ? "R1" : "primary", executor.Tasks[2]);
            if (!merged) { executor.Release(); await executor.WaitStarted(4); Assert.Equal("R1", executor.Tasks[3]); }
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Fact]
    public async Task Q11_BridgeCancelAndCancelAllPreserveThenClearBothLanes()
    {
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var manager = MidTurnIsolationTests.Manager("claude", executor);
        try
        {
            await manager.StartTask(1, "relay", "relay", true, TaskSource.Relay); await executor.WaitStarted(1);
            await manager.StartTask(2, "R1", "R1", true);
            await manager.StartTask(3, "bridge", "bridge", false, TaskSource.Bridge, taskId: "synthetic/step");
            await manager.StartTask(4, "P1", "P1", true, priority: TaskPriority.PrimaryHuman);
            await manager.StartTask(5, "R2", "R2", true);
            var retained = manager.GetQueueSnapshot().Where(e => e.Source != TaskSource.Bridge).Select(e => (e.Task, e.Priority, e.Seq)).ToArray();
            await manager.CancelByBridgeTaskIdAsync("synthetic/step");
            Assert.Equal(retained, manager.GetQueueSnapshot().Select(e => (e.Task, e.Priority, e.Seq)));
            await manager.CancelAllAsync();
            Assert.Empty(manager.GetQueueSnapshot()); Assert.Equal(0, manager.PriorityQueuedCount);
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Theory]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.Bridge)]
    public async Task Q10_Q12_BothLanesFullKeepFailureCallbackAndNotice(TaskSource source)
    {
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var sink = NSubstitute.Substitute.For<Fleet.Agent.Abstractions.IMessageSink>();
        var manager = MidTurnIsolationTests.Manager("claude", executor, sink: sink);
        var failed = 0;
        manager.OnTaskCompleted += (_, result, _, completedSource, success, _, _, _) =>
        { if (completedSource == source && !success && result == "agent queue full") failed++; };
        try
        {
            await manager.StartTask(1, "relay", "relay", true, TaskSource.Relay); await executor.WaitStarted(1);
            for (var i = 0; i < 10; i++) await manager.StartTask(100 + i, "P", "P", true, priority: TaskPriority.PrimaryHuman);
            for (var i = 0; i < 20; i++) await manager.StartTask(200 + i, "R", "R", true);
            Assert.Equal(TaskDispatchOutcome.QueueFull, await manager.StartTask(300, "full", "full", false, source,
                relaySender: "agent2", correlationId: "synthetic", taskId: "synthetic/step", priority: TaskPriority.PrimaryHuman));
            Assert.Equal(1, failed); Assert.Equal(30, manager.GetQueueSnapshot().Count);
            Assert.Equal(10, manager.PriorityQueuedCount);
            Assert.Contains(sink.ReceivedCalls(), call => call.GetArguments().OfType<string>().Any(v => v.StartsWith("Queue is full", StringComparison.Ordinal)));
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Fact]
    public async Task Q13_TeardownDrainRetainsPrimaryMessageAheadOfRoutine()
    {
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Unsupported);
        var manager = MidTurnIsolationTests.Manager("gemini", executor);
        try
        {
            await manager.StartTask(1, "human", "human", true); await executor.WaitStarted(1);
            await manager.StartTask(2, "routine", "routine", true);
            await manager.StartTask(1, "primary", "primary", true, priority: TaskPriority.PrimaryHuman, telegramMessageId: 7);
            await manager.HandleCancel(1, "all");
            await executor.WaitStarted(2); Assert.Equal("primary", executor.Tasks[1]);
            executor.Release(); await executor.WaitStarted(3); Assert.Equal("routine", executor.Tasks[2]);
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Fact]
    public void Q15_AllProtectedCacheEntriesRejectNewReservationsWithoutCallingThemDuplicates()
    {
        var cache = new PrimaryHumanDedup();
        for (var i = 1; i <= PrimaryHumanDedup.Capacity; i++) Assert.True(cache.TryReserve(1, i));
        Assert.False(cache.TryReserve(2, 1, out var duplicate)); Assert.False(duplicate);
        Assert.False(cache.TryReserve(1, 1, out duplicate)); Assert.True(duplicate);
        Assert.Equal(PrimaryHumanDedup.Capacity, cache.Count);
        cache.Complete(1, 1); Assert.True(cache.TryReserve(2, 1));
    }

    private sealed class KeyClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
