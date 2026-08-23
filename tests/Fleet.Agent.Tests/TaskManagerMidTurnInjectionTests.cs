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

public class TaskManagerMidTurnInjectionTests
{
    [Fact]
    public async Task StartTask_UserMessageDuringSession_InjectsIntoRunningTurn()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionCountAsync(1);

        Assert.Contains("[NEW MESSAGE", executor.InjectedTasks.Single());
        Assert.Contains("second", executor.InjectedTasks.Single());
        Assert.DoesNotContain("priority", executor.InjectedTasks.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.Injected));

        executor.ReleaseAllTurns();
        await idle;
    }

    [Fact]
    public async Task StartTask_MoreThanThreeMessagesInOneTurn_QueuesTheRemainder()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        for (var i = 1; i <= 5; i++)
            _ = manager.StartTask(123, $"message {i}", $"message {i}", isSessionTask: true);

        await executor.WaitForInjectionCountAsync(3);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 2);
        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(3, executor.InjectedTasks.Count);
        Assert.Equal(3, executor.ExecutedTasks.Count);
        for (var i = 1; i <= 5; i++)
        {
            var message = $"message {i}";
            Assert.True(
                executor.ExecutedTasks.Contains(message) || executor.InjectedTasks.Any(t => t.Contains(message, StringComparison.Ordinal)),
                $"{message} was neither injected nor delivered from the fallback inbox");
        }
        Assert.Equal(3, counter.GetCount("claude", InjectionOutcomeCounter.Injected));
        Assert.Equal(2, counter.GetCount("claude", InjectionOutcomeCounter.DegradedToQueue));
    }

    [Fact]
    public async Task StartTask_InjectionRacingTurnCompletion_IsDeterministicInjectedOrQueued()
    {
        var executor = new ControllableExecutor
        {
            InjectionResult = MidTurnInjectionResult.Injected,
            HoldInjection = true,
        };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionStartedAsync();
        executor.ReleaseAllTurns();
        await Task.Delay(50);

        Assert.True(manager.HasRunningTasks(123));

        executor.ReleaseInjection();
        await idle;

        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.Injected));
        Assert.Equal(0, counter.GetCount("claude", InjectionOutcomeCounter.DegradedToQueue));
        Assert.Single(executor.InjectedTasks);
        Assert.Equal(["first"], executor.ExecutedTasks);
    }

    [Theory]
    [InlineData(MidTurnInjectionStatus.Unsupported, InjectionOutcomeCounter.DegradedToQueue)]
    [InlineData(MidTurnInjectionStatus.NoActiveTurn, InjectionOutcomeCounter.DegradedToQueue)]
    [InlineData(MidTurnInjectionStatus.Failed, InjectionOutcomeCounter.FailedThenQueued)]
    public async Task StartTask_UnavailableInjection_QueuesForTurnEndDelivery(MidTurnInjectionStatus status, string expectedOutcome)
    {
        var result = status switch
        {
            MidTurnInjectionStatus.Unsupported => MidTurnInjectionResult.Unsupported,
            MidTurnInjectionStatus.NoActiveTurn => MidTurnInjectionResult.NoActiveTurn("turn ended"),
            MidTurnInjectionStatus.Failed => MidTurnInjectionResult.Failed("write failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
        var executor = new ControllableExecutor { InjectionResult = result };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask: true);
        await counter.WaitForCountAsync("claude", expectedOutcome, 1);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "second"], executor.ExecutedTasks);
        Assert.Equal(1, counter.GetCount("claude", expectedOutcome));
    }

    // Late-injection (final-answer gate) returns NoActiveTurn("already begun…") —
    // the same status as genuine no-active-turn so it maps to DegradedToQueue,
    // but the error string distinguishes it in logs from a cap-exhaustion degradation.
    [Fact]
    public async Task StartTask_LateInjection_FinalAnswerGate_DegradesToQueue_WithDistinctErrorText()
    {
        const string lateError = "Claude has already begun emitting its final answer for this turn.";
        var executor = new ControllableExecutor
        {
            InjectionResult = MidTurnInjectionResult.NoActiveTurn(lateError),
        };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "late message", "late message", isSessionTask: true);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 1);

        executor.ReleaseAllTurns();
        await idle;

        // Message was queued and delivered as its own turn — not injected.
        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "late message"], executor.ExecutedTasks);
        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.DegradedToQueue));
        // The distinct error text for the final-answer gate (vs. cap exhaustion) is verified in
        // ClaudeExecutorMidTurnInjectionTests.CommittedFlag_BlocksInjection_WithExpectedErrorText.
    }

    [Fact]
    public async Task StartTask_CheckInDuringRunningTurn_IsDeferredNotDropped()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "check-in", "check-in", isSessionTask: true, source: TaskSource.CheckIn);
        executor.ReleaseAllTurns();
        await executor.WaitForExecuteCountAsync(2);
        await WaitUntilIdleAsync(manager, 123);

        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "check-in"], executor.ExecutedTasks);
    }

    [Fact]
    public async Task HandleCancel_DrainsFallbackInboxToGlobalQueue()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Unsupported };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask: true);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 1);

        await manager.HandleCancel(123, "all");
        await executor.WaitForExecuteCountAsync(2);
        executor.ReleaseAllTurns();
        await WaitUntilIdleAsync(manager, 123);

        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "second"], executor.ExecutedTasks);
    }

    [Theory]
    [InlineData(TaskSource.Relay, true)]
    [InlineData(TaskSource.NewCommand, false)]
    public async Task StartTask_NonConversationalSourcesDuringSession_AreNotInjected(TaskSource source, bool isSessionTask)
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask, source: source);
        executor.ReleaseAllTurns();
        await idle;
        await executor.WaitForExecuteCountAsync(2);

        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "second"], executor.ExecutedTasks);
    }

    [Fact]
    public async Task StartTask_InjectedMessageAfterError_IsRedeliveredAsPossibleDuplicate()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        executor.ErrorResults.Enqueue(true);
        executor.ProcessExitResults.Enqueue(true);
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionCountAsync(1);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(["first", "second"], executor.ExecutedTasks);
        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.PossibleDuplicateAfterResume));
    }

    [Fact]
    public async Task StartTask_InjectedMessageAfterNonProcessError_IsNotRedelivered()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        executor.ErrorResults.Enqueue(true);
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionCountAsync(1);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(["first"], executor.ExecutedTasks);
        Assert.Equal(0, counter.GetCount("claude", InjectionOutcomeCounter.PossibleDuplicateAfterResume));
    }

    [Fact]
    public async Task StartTask_UserMessagesForWaitingChat_CoalescesIntoOneQueuedTurn()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "chat1 running", "chat1 running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(2, "[telegram_message_id: 10]\nmessage b", "message b", isSessionTask: true);
        _ = manager.StartTask(2, "[telegram_message_id: 11]\nmessage d", "message d", isSessionTask: true);

        var queued = Assert.Single(manager.GetQueueSnapshot());
        Assert.Equal(2, queued.PartCount);
        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.MergedIntoQueue));

        executor.ReleaseAllTurns();
        await idle;
        await executor.WaitForExecuteCountAsync(2);

        var combined = executor.ExecutedTasks[1];
        Assert.Contains("[This batch of 2 messages waited", combined);
        Assert.Contains("[ADDITIONAL MESSAGE", combined);
        Assert.Contains("[telegram_message_id: 10]", combined);
        Assert.Contains("[telegram_message_id: 11]", combined);
        Assert.True(combined.IndexOf("message b", StringComparison.Ordinal) < combined.IndexOf("message d", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartTask_RelayBridgeAndCheckIn_DoNotMergeIntoPendingUserEntry()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);
        var completed = new ConcurrentQueue<string?>();
        manager.OnTaskCompleted += (_, _, _, _, _, correlationId, _, _) => completed.Enqueue(correlationId);

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "chat1 running", "chat1 running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(2, "user b", "user b", isSessionTask: true);
        _ = manager.StartTask(2, "relay", "relay", isSessionTask: true, source: TaskSource.Relay, correlationId: "relay-1");
        _ = manager.StartTask(2, "bridge", "bridge", isSessionTask: true, source: TaskSource.Bridge, correlationId: "bridge-1", taskId: "bridge-task");
        _ = manager.StartTask(2, "check-in", "check-in", isSessionTask: true, source: TaskSource.CheckIn);
        _ = manager.StartTask(2, "user d", "user d", isSessionTask: true);

        var snapshot = manager.GetQueueSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal([2, 1, 1], snapshot.Select(q => q.PartCount).ToArray());
        Assert.Equal(TaskSource.Relay, snapshot[1].Source);
        Assert.Equal(TaskSource.Bridge, snapshot[2].Source);

        executor.ReleaseAllTurns();
        await idle;
        await executor.WaitForExecuteCountAsync(4);

        Assert.Contains("relay-1", completed);
        Assert.Contains("bridge-1", completed);
    }

    [Fact]
    public async Task StartTask_CheckInForPendingChat_IsDroppedRatherThanQueued()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "chat1 running", "chat1 running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(2, "user b", "user b", isSessionTask: true);
        _ = manager.StartTask(2, "check-in", "check-in", isSessionTask: true, source: TaskSource.CheckIn);

        var queued = Assert.Single(manager.GetQueueSnapshot());
        Assert.Equal(TaskSource.UserMessage, queued.Source);
        Assert.Equal(1, queued.PartCount);

        executor.ReleaseAllTurns();
        await idle;
    }

    [Fact]
    public async Task StartTask_CoalescedQueue_PreservesAttachmentsInPartOrder()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);
        var image1 = new MessageImage([1], "image/png");
        var image2 = new MessageImage([2], "image/jpeg");
        var doc1 = new MessageDocument("doc1", "application/pdf", 1, "a.pdf");
        var doc2 = new MessageDocument("doc2", "application/pdf", 1, "b.pdf");

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "chat1 running", "chat1 running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(2, "user b", "user b", isSessionTask: true, images: [image1], documents: [doc1]);
        _ = manager.StartTask(2, "user d", "user d", isSessionTask: true, images: [image2], documents: [doc2]);

        executor.ReleaseAllTurns();
        await idle;
        await executor.WaitForExecuteCountAsync(2);

        Assert.Equal([image1, image2], executor.ExecutedImages[1]);
        Assert.Equal([doc1, doc2], executor.ExecutedDocuments[1]);
    }

    [Fact]
    public async Task StartTask_EleventhQueuedUserMessage_SpillsIntoNewEntry()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "chat1 running", "chat1 running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        for (var i = 1; i <= 12; i++)
            _ = manager.StartTask(2, $"user {i}", $"user {i}", isSessionTask: true);

        var snapshot = manager.GetQueueSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(10, snapshot[0].PartCount);
        Assert.Equal(2, snapshot[1].PartCount);
        Assert.Equal(10, counter.GetCount("claude", InjectionOutcomeCounter.MergedIntoQueue));

        executor.ReleaseAllTurns();
        await idle;
    }

    [Fact]
    public async Task StartTask_MessageDuringClaimedQueueDrain_QueuesBehindClaimedEntry()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);
        using var claimed = new ManualResetEventSlim();
        using var releaseClaim = new ManualResetEventSlim();
        manager.QueueEntryClaimedForTest = () =>
        {
            claimed.Set();
            releaseClaim.Wait(TimeSpan.FromSeconds(5));
        };

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "chat1 running", "chat1 running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);
        _ = manager.StartTask(2, "queued first", "queued first", isSessionTask: true);

        executor.ReleaseAllTurns();
        Assert.True(claimed.Wait(TimeSpan.FromSeconds(5)));

        _ = manager.StartTask(2, "queued second", "queued second", isSessionTask: true);
        Assert.Single(executor.ExecutedTasks);
        Assert.Single(manager.GetQueueSnapshot());

        releaseClaim.Set();
        await idle;
        await executor.WaitForExecuteCountAsync(3);

        Assert.Equal("queued first", executor.ExecutedTasks[1]);
        Assert.Equal("queued second", executor.ExecutedTasks[2]);
    }

    [Fact]
    public async Task CancelByBridgeTaskId_MergeRacingQueueDrain_LeavesQueuedEntryIntact()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);
        using var dequeuedForCancel = new ManualResetEventSlim();
        using var releaseCancel = new ManualResetEventSlim();
        manager.QueueEntryDequeuedForBridgeCancelForTest = () =>
        {
            dequeuedForCancel.Set();
            releaseCancel.Wait(TimeSpan.FromSeconds(5));
        };

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "chat1 running", "chat1 running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);
        _ = manager.StartTask(2, "queued first", "queued first", isSessionTask: true);

        var cancelTask = Task.Run(() => manager.CancelByBridgeTaskIdAsync("missing-bridge-task"));
        Assert.True(dequeuedForCancel.Wait(TimeSpan.FromSeconds(5)));

        _ = manager.StartTask(2, "queued second", "queued second", isSessionTask: true);
        releaseCancel.Set();

        Assert.False(await cancelTask.WaitAsync(TimeSpan.FromSeconds(5)));
        var queued = Assert.Single(manager.GetQueueSnapshot());
        Assert.Equal(2, queued.PartCount);

        executor.ReleaseAllTurns();
        await idle;
    }

    [Fact]
    public async Task StartTask_QueueMergeCounter_IncrementsOnlyOnSuccessfulMerge()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 1);
        _ = manager.StartTask(1, "chat1 running", "chat1 running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(2, "fresh user", "fresh user", isSessionTask: true);
        _ = manager.StartTask(3, "other fresh user", "other fresh user", isSessionTask: true);
        Assert.Equal(0, counter.GetCount("claude", InjectionOutcomeCounter.MergedIntoQueue));

        _ = manager.StartTask(2, "merged user", "merged user", isSessionTask: true);
        _ = manager.StartTask(2, "relay", "relay", isSessionTask: true, source: TaskSource.Relay);
        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.MergedIntoQueue));

        executor.ReleaseAllTurns();
        await idle;
    }


    [Fact]
    public async Task ProcessTask_RecoveredAnswerEvent_IsDeliveredToSinkImmediately()
    {
        // When the executor emits a "recovered_answer" event (a stale answer preserved
        // during DrainStaleTurnEvents), ProcessTask must send it directly to the sink
        // so the user receives the prior turn's response that would otherwise be lost.
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        executor.PreambleEvents.Enqueue(new AgentProgress
        {
            EventType = "recovered_answer",
            Summary = "stale text from prior turn",
            IsSignificant = true,
        });

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);
        executor.ReleaseAllTurns();
        await idle;

        // The stale answer must have been sent to the sink immediately —
        // not swallowed into lastResult where it would be overwritten by the real answer.
        await sink.Received(1).SendTextAsync(123, "stale text from prior turn");
    }

    [Fact]
    public async Task DeliverMidTurnMessage_FailedInjection_SendsBusyNotice()
    {
        // A Failed injection means the write to the process stdin broke. The user sent
        // a message they expect a reply to, so they must be told the agent is busy.
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Failed("write error") };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask: true);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.FailedThenQueued, 1);

        executor.ReleaseAllTurns();
        await idle;

        await sink.Received(1).SendTextAsync(123, Arg.Is<string>(s => s.Contains("busy")));
    }

    [Fact]
    public async Task DeliverMidTurnMessage_NoActiveTurnInjection_DoesNotSendBusyNotice()
    {
        // NoActiveTurn means the turn already ended (race condition). The message was
        // queued silently — no notice needed because the race is transparent to the user.
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.NoActiveTurn("turn ended") };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(123, "second", "second", isSessionTask: true);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 1);

        executor.ReleaseAllTurns();
        await idle;

        await sink.DidNotReceive().SendTextAsync(123, Arg.Is<string>(s => s.Contains("busy")));
    }

    [Fact]
    public async Task DeliverMidTurnMessage_CapExhausted_DoesNotSendBusyNotice()
    {
        // When the cap (MaxMidTurnInjectionsPerTurn=3) is reached the message is silently
        // queued for turn-end. No "busy" notice must be sent — the wait time is similar to
        // the NoActiveTurn gate path, so a notice would be misleading and noisy.
        // The DegradedToQueue counter must still increment so metrics are not suppressed.
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        // Three messages inject successfully (InjectionCount 1→3).
        _ = manager.StartTask(123, "inject1", "inject1", isSessionTask: true);
        _ = manager.StartTask(123, "inject2", "inject2", isSessionTask: true);
        _ = manager.StartTask(123, "inject3", "inject3", isSessionTask: true);
        await executor.WaitForInjectionCountAsync(3);

        // This one hits the cap.
        _ = manager.StartTask(123, "capped", "capped", isSessionTask: true);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 1);

        executor.ReleaseAllTurns();
        await idle;

        // Counter must increment — suppressing the notice must never suppress the metric.
        Assert.True(counter.GetCount("claude", InjectionOutcomeCounter.DegradedToQueue) >= 1);
        // No busy notice for the cap path.
        await sink.DidNotReceive().SendTextAsync(123, Arg.Is<string>(s => s.Contains("busy")));
    }

    [Fact]
    public async Task ProcessTask_DrainedQueuedMessages_EachAnswerDeliveredToSink()
    {
        // When the fallback-inbox drain runs after a turn (inboxReader.TryRead succeeds),
        // the completed turn's lastResult must be sent to the chat sink before the next
        // turn starts — otherwise the first answer is silently lost when lastResult is
        // overwritten by the second turn's result.
        var executor = new ControllableExecutor
        {
            InjectionResult = MidTurnInjectionResult.NoActiveTurn("turn ended"),
        };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        _ = manager.StartTask(123, "first question", "first question", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        // Second message arrives while first is running; NoActiveTurn routes it to the inbox.
        _ = manager.StartTask(123, "second question", "second question", isSessionTask: true);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 1);

        executor.ReleaseAllTurns();
        await idle;

        // Both answers must reach the sink — not just the last one.
        await sink.Received(1).SendTextAsync(123, Arg.Is<string>(s => s.Contains("first question")));
        await sink.Received(1).SendTextAsync(123, Arg.Is<string>(s => s.Contains("second question")));
    }

    private static TaskManager BuildManager(IAgentExecutor executor, IMessageSink sink, InjectionOutcomeCounter counter)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
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
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilIdleAsync(TaskManager manager, long chatId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.HasRunningTasks(chatId))
            await Task.Delay(10, cts.Token);
    }

    private sealed class ControllableExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _releaseTurns = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _injectionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseInjection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _executedTasks = new();
        private readonly ConcurrentQueue<string> _injectedTasks = new();
        private readonly ConcurrentQueue<IReadOnlyList<MessageImage>> _executedImages = new();
        private readonly ConcurrentQueue<IReadOnlyList<MessageDocument>> _executedDocuments = new();

        public ConcurrentQueue<bool> ErrorResults { get; } = new();
        public ConcurrentQueue<bool> ProcessExitResults { get; } = new();
        public ConcurrentQueue<AgentProgress> PreambleEvents { get; } = new();
        public MidTurnInjectionResult InjectionResult { get; init; } = MidTurnInjectionResult.Injected;
        public bool HoldInjection { get; init; }
        public IReadOnlyList<string> ExecutedTasks => _executedTasks.ToList();
        public IReadOnlyList<string> InjectedTasks => _injectedTasks.ToList();
        public IReadOnlyList<IReadOnlyList<MessageImage>> ExecutedImages => _executedImages.ToList();
        public IReadOnlyList<IReadOnlyList<MessageDocument>> ExecutedDocuments => _executedDocuments.ToList();
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
            _executedImages.Enqueue(images ?? []);
            _executedDocuments.Enqueue(documents ?? []);
            await _releaseTurns.Task.WaitAsync(ct);
            while (PreambleEvents.TryDequeue(out var preamble))
                yield return preamble;
            var isError = ErrorResults.TryDequeue(out var error) && error;
            var isProcessExit = ProcessExitResults.TryDequeue(out var processExit) && processExit;
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = isError ? "executor failed" : task,
                FinalResult = isError ? "executor failed" : task,
                IsErrorResult = isError,
                IsProcessExit = isProcessExit,
            };
        }

        public async Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default)
        {
            _injectionStarted.TrySetResult();
            if (HoldInjection)
                await _releaseInjection.Task.WaitAsync(ct);
            if (InjectionResult.Status == MidTurnInjectionStatus.Injected)
                _injectedTasks.Enqueue(task);
            return InjectionResult;
        }

        public void ReleaseAllTurns() => _releaseTurns.TrySetResult();
        public void ReleaseInjection() => _releaseInjection.TrySetResult();
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForExecuteCountAsync(int expected)
        {
            await WaitUntilAsync(() => _executedTasks.Count >= expected);
        }

        public async Task WaitForInjectionCountAsync(int expected)
        {
            await WaitUntilAsync(() => _injectedTasks.Count >= expected);
        }

        public Task WaitForInjectionStartedAsync() =>
            _injectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
                await Task.Delay(10, cts.Token);
        }
    }
}

file static class InjectionOutcomeCounterTestExtensions
{
    public static async Task WaitForCountAsync(this InjectionOutcomeCounter counter, string provider, string outcome, long expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (counter.GetCount(provider, outcome) < expected)
            await Task.Delay(10, cts.Token);
    }
}
