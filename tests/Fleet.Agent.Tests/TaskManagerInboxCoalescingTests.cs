using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for the inbox-drain coalescing fix (issue #243): when a burst of messages lands in
/// the fallback Inbox (refused by the final-answer gate), the turn-completion loop must drain
/// all of them, merge compatible consecutive ones into a single turn, and write overflow back to
/// the Inbox so DrainInboxToGlobalQueue can recover them on cancellation.
/// </summary>
public class TaskManagerInboxCoalescingTests
{
    // ── end-to-end coalescing scenarios ──────────────────────────────────────────

    [Fact]
    public async Task InboxCoalescing_ThreeUserMessages_MergedIntoSingleTurn()
    {
        // A burst of 3 same-chat UserMessage turns all land in the Inbox while the
        // initial turn is running.  After the initial turn, the drain must merge them
        // into ONE continuation turn instead of running three separate turns.
        var executor = new CoalescingTestExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123L);
        _ = manager.StartTask(123L, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123L, "second", "second", isSessionTask: true);
        _ = manager.StartTask(123L, "third",  "third",  isSessionTask: true);
        _ = manager.StartTask(123L, "fourth", "fourth", isSessionTask: true);
        await counter.WaitForCoalescingCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 3);

        executor.ReleaseAllTurns();
        await idle;

        // Only two executor invocations: the original turn and one merged turn.
        Assert.Equal(2, executor.ExecutedTasks.Count);
        Assert.Contains("second", executor.ExecutedTasks[1], StringComparison.Ordinal);
        Assert.Contains("third",  executor.ExecutedTasks[1], StringComparison.Ordinal);
        Assert.Contains("fourth", executor.ExecutedTasks[1], StringComparison.Ordinal);
        // third and fourth were merged (2 MergedIntoQueue increments, not 3).
        Assert.Equal(2, counter.GetCount("claude", InjectionOutcomeCounter.MergedIntoQueue));
    }

    [Fact]
    public async Task InboxCoalescing_FiveMessages_MergedIntoQueueCounterIsFour()
    {
        // N messages in the Inbox → N-1 MergedIntoQueue increments (the first message
        // is the "base" of the merged entry, so only N-1 are actually appended).
        var executor = new CoalescingTestExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123L);
        _ = manager.StartTask(123L, "turn0", "turn0", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        for (var i = 1; i <= 5; i++)
            _ = manager.StartTask(123L, $"msg{i}", $"msg{i}", isSessionTask: true);
        await counter.WaitForCoalescingCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 5);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(2, executor.ExecutedTasks.Count);
        Assert.Equal(4, counter.GetCount("claude", InjectionOutcomeCounter.MergedIntoQueue));
    }

    [Fact]
    public async Task InboxCoalescing_ElevenMessages_OverflowRemainInInboxAndFormSecondTurn()
    {
        // QueuedMessage.MaxParts == 10, so the 11th inbox message cannot be appended to the
        // first merged entry.  It must be written back to the Inbox, where it forms a second
        // continuation turn (i.e. 3 executor invocations total, not 12).
        var executor = new CoalescingTestExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123L);
        _ = manager.StartTask(123L, "turn0", "turn0", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        for (var i = 1; i <= 11; i++)
            _ = manager.StartTask(123L, $"msg{i}", $"msg{i}", isSessionTask: true);
        await counter.WaitForCoalescingCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 11);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(3, executor.ExecutedTasks.Count);
        for (var i = 1; i <= 10; i++)
            Assert.Contains($"msg{i}", executor.ExecutedTasks[1], StringComparison.Ordinal);
        Assert.Contains("msg11", executor.ExecutedTasks[2], StringComparison.Ordinal);
        // msg2..msg10 merged into the first group → 9 increments.
        Assert.Equal(9, counter.GetCount("claude", InjectionOutcomeCounter.MergedIntoQueue));
    }

    [Fact]
    public async Task InboxCoalescing_MixedSources_UserMessageThenDebouncedGroupBatch_NotMergedTogether()
    {
        // QueuedMessage.CanMerge only accepts consecutive UserMessage parts.  A
        // DebouncedGroupBatch part must not be merged with the preceding UserMessage.
        // Each ends up as its own continuation turn → 3 executor invocations total.
        var executor = new CoalescingTestExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123L);
        _ = manager.StartTask(123L, "turn0", "turn0", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123L, "user-msg",     "user-msg",     isSessionTask: true);
        _ = manager.StartTask(123L, "debounced-msg", "debounced-msg", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);
        await counter.WaitForCoalescingCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 2);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(3, executor.ExecutedTasks.Count);
        Assert.Contains("user-msg",      executor.ExecutedTasks[1], StringComparison.Ordinal);
        Assert.Contains("debounced-msg", executor.ExecutedTasks[2], StringComparison.Ordinal);
        // Nothing was merged (DebouncedGroupBatch cannot be appended to a UserMessage base).
        Assert.Equal(0, counter.GetCount("claude", InjectionOutcomeCounter.MergedIntoQueue));
    }

    [Fact]
    public async Task InboxCoalescing_LastResultSentToSinkBeforeNextMergedTurnStarts()
    {
        // The completed turn's lastResult must be delivered to the sink before the
        // next merged turn starts — otherwise the first answer is silently overwritten.
        var executor = new CoalescingTestExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123L);
        _ = manager.StartTask(123L, "question-one", "question-one", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123L, "question-two", "question-two", isSessionTask: true);
        await counter.WaitForCoalescingCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 1);

        executor.ReleaseAllTurns();
        await idle;

        // Both answers must reach the sink — not just the last one.
        await sink.Received(1).SendTextAsync(123L, Arg.Is<string>(s => s.Contains("question-one")));
        await sink.Received(1).SendTextAsync(123L, Arg.Is<string>(s => s.Contains("question-two")));
    }

    [Fact]
    public async Task InboxCoalescing_CancellationBetweenMergedTurns_OverflowRecoveredByDrainInbox()
    {
        // When the task is cancelled while a merged turn is executing, the overflow
        // that was written back to the Inbox must be recovered by DrainInboxToGlobalQueue
        // and form a new turn — so all 11 messages are eventually delivered.
        var executor = new PerTurnReleasableExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        _ = manager.StartTask(123L, "turn0", "turn0", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        for (var i = 1; i <= 11; i++)
            _ = manager.StartTask(123L, $"msg{i}", $"msg{i}", isSessionTask: true);
        await counter.WaitForCoalescingCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 11);

        // Release turn0 → drain fires → merged turn (msg1-10) starts; msg11 overflows to Inbox.
        executor.ReleaseNextTurn();
        await executor.WaitForExecuteCountAsync(2);

        // Cancel the merged turn mid-run → DrainInboxToGlobalQueue rescues msg11.
        await manager.HandleCancel(123L, "all");

        // DrainQueue starts turn2 with the recovered msg11.
        await executor.WaitForExecuteCountAsync(3);
        executor.ReleaseNextTurn();
        await WaitUntilIdleAsync(manager, 123L);

        var allText = string.Join(" ", executor.ExecutedTasks);
        for (var i = 1; i <= 11; i++)
            Assert.Contains($"msg{i}", allText, StringComparison.Ordinal);
    }

    // ── EnqueueForTurnEndAsync routing guard ──────────────────────────────────────

    [Theory]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.Bridge)]
    public async Task EnqueueForTurnEndAsync_RelayOrBridgeSource_RoutedToGlobalQueueNotInbox(TaskSource source)
    {
        // Relay/Bridge messages carry completion callbacks.  The coalescing path would
        // lose those callbacks, so EnqueueForTurnEndAsync must divert them to the global
        // queue (via EnqueueFreshMessage) rather than writing them to the Inbox.
        var executor = new CoalescingTestExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var running = new RunningTask
        {
            Id = 1,
            Description = "test",
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource(),
            IsSessionTask = true,
        };
        var msg = new MidTurnMessage(
            Task: "relay-task", DisplayText: "relay-display", IsSessionTask: false, Source: source,
            RelaySender: "sender", CorrelationId: null, TaskId: "task-x",
            Images: null, Documents: null, UserId: 0L, ArrivedAt: DateTimeOffset.UtcNow);

        var outcome = await manager.EnqueueForTurnEndAsync(123L, running, msg, notifyUser: false);

        // Message must land in the global queue, NOT the Inbox.
        Assert.Equal(TaskDispatchOutcome.Queued, outcome);
        Assert.False(running.Inbox.Reader.TryRead(out _));
        var snapshot = manager.GetQueueSnapshot();
        Assert.Single(snapshot);
        Assert.Equal(source, snapshot[0].Source);
    }

    [Fact]
    public async Task EnqueueForTurnEndAsync_DebouncedGroupBatch_GoesToInboxNotQueue()
    {
        // DebouncedGroupBatch is NOT Relay or Bridge, so it must bypass the routing guard
        // and be written to the Inbox for coalescing at turn-end.
        var executor = new CoalescingTestExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var running = new RunningTask
        {
            Id = 1, Description = "test", StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource(), IsSessionTask = true,
        };
        var msg = new MidTurnMessage(
            Task: "debounced-task", DisplayText: "debounced-display", IsSessionTask: true,
            Source: TaskSource.DebouncedGroupBatch, RelaySender: null, CorrelationId: null, TaskId: null,
            Images: null, Documents: null, UserId: 0L, ArrivedAt: DateTimeOffset.UtcNow);

        var outcome = await manager.EnqueueForTurnEndAsync(123L, running, msg, notifyUser: false);

        Assert.Equal(TaskDispatchOutcome.Queued, outcome);
        Assert.True(running.Inbox.Reader.TryRead(out var drained));
        Assert.Equal("debounced-task", drained!.Task);
        Assert.Empty(manager.GetQueueSnapshot());
    }

    [Fact]
    public async Task EnqueueForTurnEndAsync_RelaySource_QueueFull_CallbackFires()
    {
        // When EnqueueFreshMessage drops a Relay message because the queue is full,
        // the OnTaskCompleted callback must fire (completeBridgeOnDrop=true) so the
        // workflow can record a failure rather than hanging indefinitely.
        var executor = new CoalescingTestExecutor();
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        // One blocking task to make hasRunningTask=true for all subsequent StartTask calls.
        _ = manager.StartTask(123L, "blocker", "blocker", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        // Fill the global queue to MaxQueueDepth (20) with distinct chats so no merging occurs.
        for (var i = 0; i < 20; i++)
            _ = manager.StartTask(456L + i, $"filler{i}", $"filler{i}", isSessionTask: false);
        Assert.Equal(20, manager.GetQueueSnapshot().Count);

        var running = new RunningTask
        {
            Id = 999, Description = "relay-test", StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource(), IsSessionTask = false,
        };
        var relayMsg = new MidTurnMessage(
            Task: "relay-task", DisplayText: "relay-display", IsSessionTask: false,
            Source: TaskSource.Relay, RelaySender: "relay-sender", CorrelationId: null, TaskId: "relay-task-id",
            Images: null, Documents: null, UserId: 0L, ArrivedAt: DateTimeOffset.UtcNow);

        string? firedText = null;
        CompletionKind? firedKind = null;
        manager.OnTaskCompleted += (_, text, _, _, _, _, _, kind) =>
        {
            firedText = text;
            firedKind = kind;
        };

        var outcome = await manager.EnqueueForTurnEndAsync(789L, running, relayMsg, notifyUser: false);

        // Queue is full → Relay message dropped → callback fires.
        Assert.Equal(TaskDispatchOutcome.QueueFull, outcome);
        Assert.False(running.Inbox.Reader.TryRead(out _));
        Assert.Equal("agent queue full", firedText);
        Assert.Equal(CompletionKind.Failed, firedKind);

        executor.ReleaseAllTurns();
        await WaitUntilIdleAsync(manager, 123L);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static TaskManager BuildManager(IAgentExecutor executor, IMessageSink sink, InjectionOutcomeCounter counter)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude",
        });
        var manager = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance, counter);
        manager.Sink = sink;
        return manager;
    }

    private static async Task WaitForIdle(TaskManager manager, long chatId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnStatusChanged += () =>
        {
            if (!manager.HasRunningTasks(chatId))
                tcs.TrySetResult();
        };
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitUntilIdleAsync(TaskManager manager, long chatId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (manager.HasRunningTasks(chatId))
            await Task.Delay(10, cts.Token);
    }

    // ── test executors ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Releases ALL turns at once when ReleaseAllTurns() is called.
    /// Suitable for tests where all turns can be released together.
    /// </summary>
    private sealed class CoalescingTestExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _releaseTurns = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _executedTasks = new();

        public IReadOnlyList<string> ExecutedTasks => _executedTasks.ToList();
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executedTasks.Enqueue(task);
            await _releaseTurns.Task.WaitAsync(ct);
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.Unsupported);

        public void ReleaseAllTurns() => _releaseTurns.TrySetResult();

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default)
            => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForExecuteCountAsync(int expected)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (_executedTasks.Count < expected)
                await Task.Delay(10, cts.Token);
        }
    }

    /// <summary>
    /// Releases turns one-at-a-time via ReleaseNextTurn().
    /// Suitable for cancellation tests that need precise control over inter-turn timing.
    /// </summary>
    private sealed class PerTurnReleasableExecutor : IAgentExecutor
    {
        private readonly Channel<bool> _releases = Channel.CreateUnbounded<bool>();
        private readonly ConcurrentQueue<string> _executedTasks = new();

        public IReadOnlyList<string> ExecutedTasks => _executedTasks.ToList();
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executedTasks.Enqueue(task);
            await _releases.Reader.ReadAsync(ct);
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.Unsupported);

        public void ReleaseNextTurn() => _releases.Writer.TryWrite(true);

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default)
            => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForExecuteCountAsync(int expected)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (_executedTasks.Count < expected)
                await Task.Delay(10, cts.Token);
        }
    }
}

file static class CoalescingInjectionCounterExtensions
{
    public static async Task WaitForCoalescingCountAsync(
        this InjectionOutcomeCounter counter, string provider, string outcome, long expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (counter.GetCount(provider, outcome) < expected)
            await Task.Delay(10, cts.Token);
    }
}
