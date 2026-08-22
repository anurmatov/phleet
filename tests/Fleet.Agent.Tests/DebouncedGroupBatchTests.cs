using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// Covers issue #232: DebouncedGroupBatch dispatch routing, peek/commit pattern,
/// watermark boundary overload, and output suppression.
/// </summary>
public class DebouncedGroupBatchTests
{
    // --- Helpers shared by all tests in this file ---

    private static async IAsyncEnumerable<AgentProgress> YieldResult(string? result, bool isError = false)
    {
        yield return new AgentProgress
        {
            EventType = "result",
            Summary = result ?? "result",
            FinalResult = result,
            IsErrorResult = isError,
        };
        await Task.CompletedTask;
    }

    private static TaskManager BuildSimpleManager(IAgentExecutor executor, IMessageSink sink)
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude" });
        var manager = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        manager.Sink = sink;
        return manager;
    }

    private static TaskManager BuildManager(ControllableExecutor executor, IMessageSink sink, InjectionOutcomeCounter? counter = null)
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude" });
        var manager = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance, counter ?? new InjectionOutcomeCounter());
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
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilIdleAsync(TaskManager manager, long chatId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.HasRunningTasks(chatId))
            await Task.Delay(10, cts.Token);
    }

    // ─── Dispatch outcome: no running session ───────────────────────────────

    [Fact]
    public async Task DebouncedGroupBatch_WhenIdle_ReturnsRan()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink);

        var outcome = await manager.StartTask(1, "batch", "batch", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);

        Assert.Equal(TaskDispatchOutcome.Ran, outcome);
        executor.ReleaseAllTurns();
    }

    // ─── Same-chat mid-turn injection ────────────────────────────────────────

    [Fact]
    public async Task DebouncedGroupBatch_SameChatDuringSession_IsInjected()
    {
        // Key fix from issue #232: DebouncedGroupBatch was going through the CheckIn
        // path (which defers), not the UserMessage path (which injects mid-turn).
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink);

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        var outcome = await manager.StartTask(1, "debounced", "debounced", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);

        Assert.Equal(TaskDispatchOutcome.Injected, outcome);

        executor.ReleaseAllTurns();
        await idle;
    }

    [Fact]
    public async Task DebouncedGroupBatch_SameChatDuringSession_InjectsChatContent()
    {
        // Verify the injected payload contains the batch text (mid-turn delivery works).
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink);

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = await manager.StartTask(1, "group activity summary", "group activity summary", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);
        await executor.WaitForInjectionCountAsync(1);

        Assert.Single(executor.InjectedTasks);
        Assert.Contains("group activity summary", executor.InjectedTasks[0]);

        executor.ReleaseAllTurns();
        await idle;
    }

    // ─── Different-chat: global FIFO, NOT DeferUntilTurnEndAsync ─────────────

    [Fact]
    public async Task DebouncedGroupBatch_DifferentChatDuringSession_IsQueued()
    {
        // DebouncedGroupBatch on a different chat than the running one goes to
        // the global FIFO queue (EnqueueFreshMessage), not DeferUntilTurnEndAsync.
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink);

        // Start a task on chat 1
        _ = manager.StartTask(1, "running on chat 1", "running on chat 1", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        // DebouncedGroupBatch for chat 2 while chat 1 is running → should queue
        var outcome = await manager.StartTask(2, "batch for chat 2", "batch for chat 2",
            isSessionTask: true, source: TaskSource.DebouncedGroupBatch);

        Assert.Equal(TaskDispatchOutcome.Queued, outcome);

        executor.ReleaseAllTurns();
        // Both chats should eventually execute
        await executor.WaitForExecuteCountAsync(2);
    }

    [Fact]
    public async Task CheckIn_DifferentChat_DeferredToTurnEnd_NotGlobalQueue()
    {
        // Contrast: CheckIn on a different chat uses DeferUntilTurnEndAsync (stored
        // in the running session's inbox). DebouncedGroupBatch must NOT do this.
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink);

        _ = manager.StartTask(1, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        // CheckIn on a different chat — goes to turn-end inbox, not global queue
        var checkInOutcome = await manager.StartTask(2, "check-in", "check-in",
            isSessionTask: true, source: TaskSource.CheckIn);

        // CheckIn is either Queued (deferred to turn end) or Dropped (when at capacity);
        // it must NOT be Ran because the agent is already busy.
        Assert.True(checkInOutcome == TaskDispatchOutcome.Queued || checkInOutcome == TaskDispatchOutcome.Dropped,
            $"CheckIn different-chat expected Queued or Dropped, got {checkInOutcome}");

        executor.ReleaseAllTurns();
    }

    // ─── GroupChatBuffer.MarkChecked(boundary) ───────────────────────────────

    [Fact]
    public void MarkChecked_Boundary_AdvancesWatermarkToExactBoundary()
    {
        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "hello", null, DateTimeOffset.UtcNow, telegramMessageId: 1);

        var boundary = DateTimeOffset.UtcNow.AddMinutes(-1);
        buffer.MarkChecked(boundary);

        // HasMessagesSinceLastCheck: messages added at "now" are all newer than boundary-1min
        Assert.True(buffer.HasMessagesSinceLastCheck());
    }

    [Fact]
    public void MarkChecked_Boundary_MarksAllMessagesBelowBoundaryAsRead()
    {
        var buffer = new GroupChatBuffer();
        var past = DateTimeOffset.UtcNow.AddSeconds(-5);
        buffer.Add("alice", "old message", null, past, telegramMessageId: 1);

        // Boundary set to now → the message at `past` is below the watermark
        var boundary = DateTimeOffset.UtcNow.AddSeconds(1);
        buffer.MarkChecked(boundary);

        Assert.False(buffer.HasMessagesSinceLastCheck());
    }

    [Fact]
    public void MarkChecked_Boundary_DoesNotRegressWatermark()
    {
        var buffer = new GroupChatBuffer();
        var now = DateTimeOffset.UtcNow;

        // Advance to a high boundary
        buffer.MarkChecked(now.AddMinutes(10));

        // Try to move it backward — should be ignored
        buffer.MarkChecked(now.AddMinutes(1));

        // A message at now+5min should still be seen as "already checked"
        buffer.Add("alice", "msg", null, now.AddMinutes(5), telegramMessageId: 1);
        Assert.False(buffer.HasMessagesSinceLastCheck());
    }

    [Fact]
    public void MarkChecked_Boundary_AdvancesOnlyWhenBoundaryIsNewer()
    {
        var buffer = new GroupChatBuffer();
        var t0 = DateTimeOffset.UtcNow;

        // No-arg overload advances to wall clock
        buffer.MarkChecked(t0.AddSeconds(2));

        // A later boundary should still advance
        buffer.MarkChecked(t0.AddSeconds(10));
        buffer.Add("alice", "future", null, t0.AddSeconds(5), telegramMessageId: 1);
        Assert.False(buffer.HasMessagesSinceLastCheck());
    }

    // ─── IDLE output suppression ─────────────────────────────────────────────

    [Fact]
    public async Task DebouncedGroupBatch_IdleResult_IsSuppressed()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldResult("IDLE"));

        var sink = Substitute.For<IMessageSink>();
        var manager = BuildSimpleManager(executor, sink);
        var idle = WaitForIdle(manager, chatId: 1);

        _ = manager.StartTask(1, "batch", "batch", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);

        await idle;

        await sink.DidNotReceive().SendTextAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("IDLE")]
    [InlineData("  IDLE  ")]
    public async Task DebouncedGroupBatch_IdleResultVariants_AreAllSuppressed(string idleVariant)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldResult(idleVariant));

        var sink = Substitute.For<IMessageSink>();
        var manager = BuildSimpleManager(executor, sink);
        var idle = WaitForIdle(manager, chatId: 1);

        _ = manager.StartTask(1, "batch", "batch", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);

        await idle;

        await sink.DidNotReceive().SendTextAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── No-output suppression ───────────────────────────────────────────────

    [Fact]
    public async Task DebouncedGroupBatch_NullResult_IsSuppressed()
    {
        // When the executor produces no text output (FinalResult=null), DebouncedGroupBatch
        // must not send a "Done!" or empty reply to chat (same as CheckIn).
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldResult(null));

        var sink = Substitute.For<IMessageSink>();
        var manager = BuildSimpleManager(executor, sink);
        var idle = WaitForIdle(manager, chatId: 1);

        _ = manager.StartTask(1, "batch", "batch", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);

        await idle;

        await sink.DidNotReceive().SendTextAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Dispatch outcome conveys Ran/Injected/Queued vs Dropped/QueueFull ──

    [Fact]
    public async Task DebouncedGroupBatch_DispatchOutcomeRan_WhenNoTaskRunning()
    {
        // Caller uses Ran/Injected/Queued to decide whether to commit the watermark.
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink);

        var outcome = await manager.StartTask(1, "batch", "batch", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);

        Assert.Equal(TaskDispatchOutcome.Ran, outcome);
        executor.ReleaseAllTurns();
        await WaitUntilIdleAsync(manager, 1);
    }

    [Fact]
    public async Task DebouncedGroupBatch_DispatchOutcomeInjected_WhenSameChatRunning()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink);

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        var outcome = await manager.StartTask(1, "batch", "batch", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);

        Assert.Equal(TaskDispatchOutcome.Injected, outcome);

        executor.ReleaseAllTurns();
        await idle;
    }

    [Fact]
    public async Task DebouncedGroupBatch_DispatchOutcomeQueued_WhenDifferentChatRunning()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink);

        _ = manager.StartTask(1, "running", "running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        var outcome = await manager.StartTask(2, "batch", "batch", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch);

        Assert.Equal(TaskDispatchOutcome.Queued, outcome);

        executor.ReleaseAllTurns();
        await executor.WaitForExecuteCountAsync(2);
    }

    // ─── ControllableExecutor (local copy mirrors the one in TaskManagerMidTurnInjectionTests) ─

    private sealed class ControllableExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _releaseTurns = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _executedTasks = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _injectedTasks = new();

        public MidTurnInjectionResult InjectionResult { get; init; } = MidTurnInjectionResult.Injected;
        public IReadOnlyList<string> ExecutedTasks => _executedTasks.ToList();
        public IReadOnlyList<string> InjectedTasks => _injectedTasks.ToList();
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
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = task,
                FinalResult = task,
            };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default)
        {
            if (InjectionResult.Status == MidTurnInjectionStatus.Injected)
                _injectedTasks.Enqueue(task);
            return Task.FromResult(InjectionResult);
        }

        public void ReleaseAllTurns() => _releaseTurns.TrySetResult();
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForExecuteCountAsync(int expected)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (_executedTasks.Count < expected)
                await Task.Delay(10, cts.Token);
        }

        public async Task WaitForInjectionCountAsync(int expected)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (_injectedTasks.Count < expected)
                await Task.Delay(10, cts.Token);
        }
    }
}
