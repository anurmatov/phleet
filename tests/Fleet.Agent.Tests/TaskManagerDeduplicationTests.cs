using System.Collections.Concurrent;
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
/// Tests for issue #233: _activeTaskIds reservation lifecycle and CompletionKind.Idle.
///
/// The reservation must live as long as the task ID is conceptually "in-flight" —
/// both while the task is running and while it is waiting in the queue. If the
/// reservation is released the moment a task enters the queue, a re-delivered
/// directive passes the dedup check and the delegate step executes twice.
/// </summary>
public class TaskManagerDeduplicationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TaskManager BuildManager(IAgentExecutor executor)
    {
        var sink = Substitute.For<IMessageSink>();
        var options = Options.Create(new AgentOptions
        {
            Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude"
        });
        var tm = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        tm.Sink = sink;
        return tm;
    }

    private static async Task WaitForIdleAsync(TaskManager manager, long chatId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnStatusChanged += () =>
        {
            if (!manager.HasRunningTasks(chatId)) tcs.TrySetResult();
        };
        // Guard against the task having already completed before we subscribed.
        if (!manager.HasRunningTasks(chatId)) tcs.TrySetResult();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Polling-based idle wait; safe when the completion event may have fired
    /// before subscription (i.e., drain happens before we subscribe).</summary>
    private static async Task PollUntilIdleAsync(TaskManager manager, long chatId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.HasRunningTasks(chatId))
            await Task.Delay(10, cts.Token);
    }

    // ── Issue #233 Defect A — reservation lifecycle ───────────────────────

    [Fact]
    public async Task SameTaskId_WhileQueued_IsDeduped()
    {
        // Task with taskId="x" is queued (not yet running). A re-delivery of the
        // same taskId must be rejected as a duplicate — the reservation must still
        // be held even though the task hasn't started yet.
        var exec = new BlockingExecutor();
        var manager = BuildManager(exec);

        // Chat 1 occupies the executor (the only running slot).
        var idle1 = WaitForIdleAsync(manager, 1);
        _ = manager.StartTask(1, "blocker", "blocker", isSessionTask: false);
        await exec.WaitForExecuteCountAsync(1);

        // Queue a relay task for chat 2 with a known taskId.
        var outcome = await manager.StartTask(2, "relay", "relay", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-task-1", relaySender: "agent-a", correlationId: "c1");
        Assert.Equal(TaskDispatchOutcome.Queued, outcome);

        // Re-delivery of the same taskId must be dropped.
        var redelivery = await manager.StartTask(2, "relay-dup", "relay-dup", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-task-1", relaySender: "agent-a", correlationId: "c1-dup");
        Assert.Equal(TaskDispatchOutcome.Dropped, redelivery);

        exec.ReleaseAllTurns();
        await idle1;
        // Let the queued task drain and finish.
        await WaitForIdleAsync(manager, 2);
    }

    [Fact]
    public async Task SameTaskId_AfterCancelByBridgeTaskId_IsAccepted()
    {
        // After CancelByBridgeTaskIdAsync removes a task from the queue, its
        // reservation must be released so a future re-delivery is accepted.
        var exec = new BlockingExecutor();
        var manager = BuildManager(exec);

        _ = manager.StartTask(1, "blocker", "blocker", isSessionTask: false);
        await exec.WaitForExecuteCountAsync(1);

        // Queue a bridge task with a known taskId.
        var outcome = await manager.StartTask(2, "bridge", "bridge", isSessionTask: false,
            source: TaskSource.Bridge, taskId: "bridge-task-1", relaySender: "bridge", correlationId: "c2");
        Assert.Equal(TaskDispatchOutcome.Queued, outcome);

        // Verify it is deduped while in queue.
        var before = await manager.StartTask(2, "bridge-dup", "bridge-dup", isSessionTask: false,
            source: TaskSource.Bridge, taskId: "bridge-task-1", relaySender: "bridge", correlationId: "c2");
        Assert.Equal(TaskDispatchOutcome.Dropped, before);

        // Cancel the queued task — this must release its reservation.
        var found = await manager.CancelByBridgeTaskIdAsync("bridge-task-1");
        Assert.True(found, "CancelByBridgeTaskIdAsync should find the queued task");

        // The same taskId should now be accepted (re-delivery after cancellation).
        // It may queue (blocker still running) or run (if blocker finished) — never Dropped.
        var after = await manager.StartTask(2, "bridge-retry", "bridge-retry", isSessionTask: false,
            source: TaskSource.Bridge, taskId: "bridge-task-1", relaySender: "bridge", correlationId: "c2");
        Assert.NotEqual(TaskDispatchOutcome.Dropped, after);

        // Release the executor and poll until everything drains.
        exec.ReleaseAllTurns();
        await PollUntilIdleAsync(manager, 1);
        await PollUntilIdleAsync(manager, 2);
    }

    [Fact]
    public async Task SameTaskId_AfterCancelAll_IsAccepted()
    {
        // CancelAllAsync must release reservations for all queued tasks.
        var exec = new BlockingExecutor();
        var manager = BuildManager(exec);

        var idle1 = WaitForIdleAsync(manager, 1);
        _ = manager.StartTask(1, "blocker", "blocker", isSessionTask: false);
        await exec.WaitForExecuteCountAsync(1);

        // Queue two relay tasks.
        await manager.StartTask(2, "relay-a", "relay-a", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-a", relaySender: "ag", correlationId: "ca");
        await manager.StartTask(3, "relay-b", "relay-b", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-b", relaySender: "ag", correlationId: "cb");

        // Both are deduped while queued.
        Assert.Equal(TaskDispatchOutcome.Dropped, await manager.StartTask(2, "x", "x", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-a", relaySender: "ag", correlationId: "ca"));
        Assert.Equal(TaskDispatchOutcome.Dropped, await manager.StartTask(3, "x", "x", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-b", relaySender: "ag", correlationId: "cb"));

        // CancelAll must release all queued reservations.
        await manager.CancelAllAsync();

        exec.ReleaseAllTurns();
        // Wait for blocker and any drained tasks to finish before probing the reservation.
        await idle1;
        await PollUntilIdleAsync(manager, 2);
        await PollUntilIdleAsync(manager, 3);

        var afterA = await manager.StartTask(2, "relay-a-retry", "relay-a-retry", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-a", relaySender: "ag", correlationId: "ca");
        Assert.NotEqual(TaskDispatchOutcome.Dropped, afterA);

        await PollUntilIdleAsync(manager, 2);
    }

    [Fact]
    public async Task QueueFull_Relay_FiresOnTaskCompletedWithFailedKind()
    {
        // When the queue is full and a Relay task is dropped, OnTaskCompleted must fire
        // with CompletionKind.Failed so the workflow delegate step can advance.
        var exec = new BlockingExecutor();
        var manager = BuildManager(exec);

        _ = manager.StartTask(1, "blocker", "blocker", isSessionTask: false);
        await exec.WaitForExecuteCountAsync(1);

        // Fill the queue to capacity (MaxQueueDepth = 20).
        for (var i = 0; i < 20; i++)
            await manager.StartTask(i + 10, $"filler-{i}", $"filler-{i}", isSessionTask: false);

        // Subscribe before the drop so we don't miss it.
        CompletionKind? capturedKind = null;
        string? capturedResult = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnTaskCompleted += (_, result, _, _, _, _, _, kind) =>
        {
            capturedResult = result;
            capturedKind = kind;
            completed.TrySetResult();
        };

        // No correlationId — matches production relay shape (correlationId is not set).
        // The gate now checks CorrelationId is not null || TaskId is not null, so taskId alone is enough.
        var outcome = await manager.StartTask(99, "relay-drop", "relay-drop", isSessionTask: false,
            source: TaskSource.Relay, taskId: "dropped-relay", relaySender: "agent-x");

        Assert.Equal(TaskDispatchOutcome.QueueFull, outcome);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CompletionKind.Failed, capturedKind);
        Assert.NotNull(capturedResult);
        Assert.Contains("queue full", capturedResult, StringComparison.OrdinalIgnoreCase);

        exec.ReleaseAllTurns();
    }

    [Fact]
    public async Task QueueFull_DoesNotLeakReservation_TaskIdReusableAfterDrop()
    {
        // When queue is full and the task is dropped, its reservation must be released
        // so the same taskId can be accepted once capacity frees up.
        var exec = new BlockingExecutor();
        var manager = BuildManager(exec);

        _ = manager.StartTask(1, "blocker", "blocker", isSessionTask: false);
        await exec.WaitForExecuteCountAsync(1);

        for (var i = 0; i < 20; i++)
            await manager.StartTask(i + 10, $"filler-{i}", $"filler-{i}", isSessionTask: false);

        // Drop a relay task (queue full). No correlationId — production relay shape.
        var dropped = await manager.StartTask(99, "relay", "relay", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-tid", relaySender: "ag");
        Assert.Equal(TaskDispatchOutcome.QueueFull, dropped);

        // Release the blocker — the queue drains, capacity returns.
        exec.ReleaseAllTurns();
        // Wait for at least one task to finish (making room).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.GetQueueSnapshot().Count >= 20)
            await Task.Delay(20, cts.Token);

        // The same taskId should now be accepted (not deduped as "still in-flight").
        var retry = await manager.StartTask(99, "relay-retry", "relay-retry", isSessionTask: false,
            source: TaskSource.Relay, taskId: "relay-tid", relaySender: "ag", correlationId: "c");
        Assert.NotEqual(TaskDispatchOutcome.Dropped, retry);
    }

    // ── Issue #233 Defect B — CompletionKind.Idle ────────────────────────

    [Fact]
    public async Task IdleResult_FromRelaySource_FiresOnTaskCompletedWithIdleKind()
    {
        // A Relay-sourced task whose executor returns "IDLE" must notify the caller
        // via OnTaskCompleted with CompletionKind.Idle, not hang the delegate step.
        var exec = new ImmediateResultExecutor("IDLE");
        var manager = BuildManager(exec);

        CompletionKind? capturedKind = null;
        string? capturedResult = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnTaskCompleted += (_, result, _, _, _, _, _, kind) =>
        {
            capturedResult = result;
            capturedKind = kind;
            completed.TrySetResult();
        };

        _ = manager.StartTask(1, "relay task", "relay task", isSessionTask: false,
            source: TaskSource.Relay, relaySender: "orchestrator", correlationId: "corr-1", taskId: "tid-1");

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CompletionKind.Idle, capturedKind);
    }

    [Fact]
    public async Task IdleResult_FromBridgeSource_FiresOnTaskCompletedWithIdleKind()
    {
        // A Bridge-sourced task whose executor returns "IDLE" must notify the bridge
        // via OnTaskCompleted with CompletionKind.Idle.
        var exec = new ImmediateResultExecutor("IDLE");
        var manager = BuildManager(exec);

        CompletionKind? capturedKind = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, kind) =>
        {
            capturedKind = kind;
            completed.TrySetResult();
        };

        _ = manager.StartTask(1, "bridge task", "bridge task", isSessionTask: false,
            source: TaskSource.Bridge, relaySender: "bridge", correlationId: "corr-2", taskId: "tid-2");

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CompletionKind.Idle, capturedKind);
    }

    [Fact]
    public async Task IdleResult_FromUserMessageSource_DoesNotFireOnTaskCompleted()
    {
        // For non-relay sources, IDLE should suppress chat output and NOT fire
        // OnTaskCompleted — there is no caller waiting for a response.
        var exec = new ImmediateResultExecutor("IDLE");
        var manager = BuildManager(exec);

        var callbackFired = false;
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => { callbackFired = true; };

        var idle = WaitForIdleAsync(manager, 1);
        _ = manager.StartTask(1, "check task", "check task", isSessionTask: false,
            source: TaskSource.UserMessage);
        await idle;

        Assert.False(callbackFired, "OnTaskCompleted must not fire for IDLE from UserMessage source");
    }

    [Fact]
    public async Task IdleResult_FromCheckInSource_DoesNotFireOnTaskCompleted()
    {
        // CheckIn tasks are fully silent — no chat output, no callback.
        var exec = new ImmediateResultExecutor("IDLE");
        var manager = BuildManager(exec);

        var callbackFired = false;
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => { callbackFired = true; };

        var idle = WaitForIdleAsync(manager, 1);
        _ = manager.StartTask(1, "check-in task", "check-in task", isSessionTask: false,
            source: TaskSource.CheckIn);
        await idle;

        Assert.False(callbackFired, "OnTaskCompleted must not fire for IDLE from CheckIn source");
    }

    [Fact]
    public async Task NonIdleResult_FromRelaySource_FiresOnTaskCompletedWithCompletedKind()
    {
        // Baseline: a normal (non-IDLE) relay result fires OnTaskCompleted with Completed kind.
        var exec = new ImmediateResultExecutor("some actual result");
        var manager = BuildManager(exec);

        CompletionKind? capturedKind = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, kind) =>
        {
            capturedKind = kind;
            completed.TrySetResult();
        };

        _ = manager.StartTask(1, "relay task", "relay task", isSessionTask: false,
            source: TaskSource.Relay, relaySender: "orchestrator", correlationId: "corr-3", taskId: "tid-3");

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CompletionKind.Completed, capturedKind);
    }

    [Fact]
    public async Task TruncatedResult_FromRelaySource_FiresOnTaskCompletedWithIncompleteKind()
    {
        // When the executor signals IsErrorResult=true (max-turns exhaustion / truncation),
        // OnTaskCompleted must fire with CompletionKind.Incomplete so the workflow
        // continuation loop can retry — NOT CompletionKind.Failed which abandons the work.
        var exec = new TruncatingExecutor("partial output");
        var manager = BuildManager(exec);

        CompletionKind? capturedKind = null;
        string? capturedResult = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnTaskCompleted += (_, result, _, _, _, _, _, kind) =>
        {
            capturedResult = result;
            capturedKind = kind;
            completed.TrySetResult();
        };

        _ = manager.StartTask(1, "relay task", "relay task", isSessionTask: false,
            source: TaskSource.Relay, relaySender: "orchestrator", correlationId: "corr-trunc", taskId: "tid-trunc");

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CompletionKind.Incomplete, capturedKind);
        Assert.NotNull(capturedResult);
        Assert.Contains("partial output", capturedResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueFull_Bridge_FiresOnTaskCompletedWithFailedKind()
    {
        // Bridge tasks dropped at queue capacity must fire OnTaskCompleted with
        // relaySender="bridge" and CompletionKind.Failed so the calling workflow
        // can surface the error rather than hanging indefinitely.
        var exec = new BlockingExecutor();
        var manager = BuildManager(exec);

        _ = manager.StartTask(1, "blocker", "blocker", isSessionTask: false);
        await exec.WaitForExecuteCountAsync(1);

        // Fill the queue to capacity.
        for (var i = 0; i < 20; i++)
            await manager.StartTask(i + 10, $"filler-{i}", $"filler-{i}", isSessionTask: false);

        string? capturedRelaySender = null;
        CompletionKind? capturedKind = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnTaskCompleted += (_, _, sender, _, _, _, _, kind) =>
        {
            capturedRelaySender = sender;
            capturedKind = kind;
            completed.TrySetResult();
        };

        // Drop a bridge task — correlationId identifies it as bridge-originating.
        var outcome = await manager.StartTask(99, "bridge-drop", "bridge-drop", isSessionTask: false,
            source: TaskSource.Bridge, correlationId: "corr-bridge-drop");

        Assert.Equal(TaskDispatchOutcome.QueueFull, outcome);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CompletionKind.Failed, capturedKind);
        Assert.Equal("bridge", capturedRelaySender);

        exec.ReleaseAllTurns();
    }

    // ── Exception safety ─────────────────────────────────────────────────────

    [Fact]
    public async Task StartTask_WhenExecutorThrows_ReleasesReservation()
    {
        // If the executor throws during task execution, the Task.Run finally block
        // must still release the taskId reservation so the same id can be retried.
        var exec = new ThrowingExecutor();
        var manager = BuildManager(exec);

        var outcome = await manager.StartTask(1, "task", "task", isSessionTask: false, taskId: "exc-tid");
        Assert.Equal(TaskDispatchOutcome.Ran, outcome);

        // Wait for the task to finish (exception is caught inside Task.Run's try/catch)
        await PollUntilIdleAsync(manager, 1);

        // Reservation must be released — the same taskId is accepted again, not Dropped
        var retry = await manager.StartTask(1, "retry", "retry", isSessionTask: false, taskId: "exc-tid");
        Assert.NotEqual(TaskDispatchOutcome.Dropped, retry);

        await PollUntilIdleAsync(manager, 1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Yields one result event immediately and returns.</summary>
    private sealed class ImmediateResultExecutor(string result) : IAgentExecutor
    {
        public string? LastSessionId => null;
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = result,
                FinalResult = result,
                IsErrorResult = false,
            };
        }
#pragma warning restore CS1998

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.NoActiveTurn());
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Blocks until <see cref="ReleaseAllTurns"/> is called.</summary>
    private sealed class BlockingExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executeCount;

        public string? LastSessionId => null;
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executeCount);
            try { await _release.Task.WaitAsync(ct); } catch (OperationCanceledException) { }
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = task,
                FinalResult = task,
                IsErrorResult = false,
            };
        }

        public void ReleaseAllTurns() => _release.TrySetResult();

        public async Task WaitForExecuteCountAsync(int expected)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref _executeCount) < expected)
                await Task.Delay(10, cts.Token);
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.NoActiveTurn());
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Yields one result event with IsErrorResult=true, simulating max-turns exhaustion.</summary>
    private sealed class TruncatingExecutor(string result) : IAgentExecutor
    {
        public string? LastSessionId => null;
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = result,
                FinalResult = result,
                IsErrorResult = true, // signals truncation / max-turns exhaustion
            };
        }
#pragma warning restore CS1998

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.NoActiveTurn());
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Throws immediately in ExecuteAsync to test exception-safety of reservation release.</summary>
    private sealed class ThrowingExecutor : IAgentExecutor
    {
        public string? LastSessionId => null;
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

#pragma warning disable CS1998, CS0162
        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            throw new InvalidOperationException("executor failure (test)");
            yield break; // required: makes this an iterator; unreachable by design
        }
#pragma warning restore CS1998, CS0162

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.NoActiveTurn());
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
