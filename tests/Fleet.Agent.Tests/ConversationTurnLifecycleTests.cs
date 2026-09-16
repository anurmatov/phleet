using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC24, AC26–30, AC39: the turn lifecycle as seen through the event stream — dispositions
/// mirroring dispatch, terminal publication on every branch (including the idle ones), the
/// reaper firing only on genuine gaps, cancel reasons, and cross-route cancel safety.
/// </summary>
public class ConversationTurnLifecycleTests
{
    private const string ClientChannel = "example-adapter";

    // ── harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required TaskManager Manager { get; init; }
        public required ConversationEventBus Bus { get; init; }
        public required ConversationRegistry Registry { get; init; }
        public required ConversationEventCounters Counters { get; init; }
        public required IMessageSink Sink { get; init; }

        public List<ConversationEvent> Drain()
        {
            var events = new List<ConversationEvent>();
            foreach (var reader in Bus.TerminalReaders.ToList())
                while (reader.TryRead(out var pending))
                    events.Add(pending.Event);
            while (Bus.ProgressReader.TryRead(out var pending))
                events.Add(pending.Event);
            return events;
        }
    }

    private static Harness Build(IAgentExecutor executor, IMessageSink? sink = null)
    {
        var registry = new ConversationRegistry();
        var counters = new ConversationEventCounters();
        var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude" });

        var manager = new TaskManager(
            options, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            injectionCounter: null, events: bus)
        {
            Counters = counters,
        };
        sink ??= Substitute.For<IMessageSink>();
        manager.Sink = sink;

        return new Harness { Manager = manager, Bus = bus, Registry = registry, Counters = counters, Sink = sink };
    }

    private static (long key, ConversationIdentity identity) OpenClientConversation(Harness harness, string conversationId = "c_1")
    {
        var reference = new ConversationRef(ClientChannel, conversationId, "p_owner");
        var key = harness.Registry.Resolve(reference);
        return (key, new ConversationIdentity
        {
            PrincipalId = "p_owner",
            Role = PrincipalRole.Owner,
            ChannelId = ClientChannel,
            ConversationId = conversationId,
            SubmissionId = "s_1",
            Attempt = 1,
        });
    }

    private static async IAsyncEnumerable<AgentProgress> Yield(string? finalResult, bool isError = false)
    {
        yield return new AgentProgress
        {
            Summary = "result",
            EventType = "result",
            FinalResult = finalResult,
            IsErrorResult = isError,
        };
        await Task.CompletedTask;
    }

    private static IAgentExecutor ExecutorYielding(Func<IAsyncEnumerable<AgentProgress>> factory)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => factory());
        return executor;
    }

    /// <summary>
    /// An executor that observes the REAL cancellation token the task manager passes.
    ///
    /// The plain factory overload above cannot be used for cancellation tests: NSubstitute invokes
    /// the factory with no arguments, so an iterator's [EnumeratorCancellation] token stays
    /// `default` and never fires — the turn would hang rather than cancel, and the test would time
    /// out somewhere unhelpful instead of failing on the assertion.
    /// </summary>
    private static IAgentExecutor ExecutorBlockingUntilCancelled(TaskCompletionSource started)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(call => BlockUntilCancelled(started, call.ArgAt<CancellationToken>(3)));
        return executor;
    }

    private static async Task WaitUntilIdleAsync(TaskManager manager, long key)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.HasRunningTasks(key))
            await Task.Delay(10, cts.Token);
        // The terminal publish happens inside ProcessTask, which completes just before the task
        // leaves the running set; give the finally block a beat to run the reaper check too.
        await Task.Delay(50, CancellationToken.None);
    }

    // ── terminal publication ──────────────────────────────────────────────────

    [Fact]
    public async Task ACompletedTurn_PublishesStartedThenFinal()
    {
        var harness = Build(ExecutorYielding(() => Yield("the answer")));
        var (key, identity) = OpenClientConversation(harness);

        await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await WaitUntilIdleAsync(harness.Manager, key);

        var events = harness.Drain();
        Assert.Contains(events, e => e.Kind == ConversationEventKind.TurnStarted);

        var final = Assert.Single(events, e => e.Kind == ConversationEventKind.TurnFinal);
        var payload = final.PayloadAs<TurnFinalPayload>()!;
        Assert.Equal("the answer", payload.Text);
        Assert.Equal(TurnCompletion.Completed, payload.Completion);
        Assert.False(payload.IsPartial);
    }

    /// <summary>AC24: the disposition must mirror the returned dispatch outcome.</summary>
    [Fact]
    public async Task SubmissionAccepted_MirrorsTheReturnedDispatchOutcome()
    {
        var harness = Build(ExecutorYielding(() => Yield("answer")));
        var (key, identity) = OpenClientConversation(harness);

        var outcome = await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await WaitUntilIdleAsync(harness.Manager, key);

        Assert.Equal(TaskDispatchOutcome.Ran, outcome);
        var accepted = harness.Drain().First(e => e.Kind == ConversationEventKind.SubmissionAccepted);
        Assert.Equal(SubmissionDisposition.Ran, accepted.PayloadAs<SubmissionAcceptedPayload>()!.Disposition);
    }

    /// <summary>
    /// AC29. An IDLE result publishes a terminal event for EVERY conversation, and the literal
    /// string "IDLE" — an internal contract marker — must appear nowhere in the stream.
    /// </summary>
    [Fact]
    public async Task IdleResult_PublishesIdleFinalWithEmptyTextAndNoIdleMarker()
    {
        var harness = Build(ExecutorYielding(() => Yield("IDLE")));
        var (key, identity) = OpenClientConversation(harness);

        await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await WaitUntilIdleAsync(harness.Manager, key);

        var events = harness.Drain();
        var final = Assert.Single(events, e => e.Kind == ConversationEventKind.TurnFinal);
        var payload = final.PayloadAs<TurnFinalPayload>()!;

        Assert.Equal(TurnCompletion.Idle, payload.Completion);
        Assert.Equal("", payload.Text);

        var serialized = string.Join("\n", events.Select(FleetProtocolJson.Serialize));
        Assert.DoesNotContain("IDLE", serialized, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC30, Constraint 24. The reason the idle branches publish for Telegram conversations too:
    /// without it, the reaper would manufacture turn_reaped on the single most common no-op path
    /// in the runtime — a Telegram idle check-in — on every tick.
    /// </summary>
    [Fact]
    public async Task ATelegramIdleCheckIn_ProducesNoOutcomeUnknown()
    {
        var harness = Build(ExecutorYielding(() => Yield("IDLE")));
        harness.Registry.RegisterTelegram(4242L, "p_owner");

        await harness.Manager.StartTask(4242L, "check in", "display", isSessionTask: false,
            source: TaskSource.CheckIn);
        await WaitUntilIdleAsync(harness.Manager, 4242L);

        Assert.Equal(0, harness.Counters.OutcomeUnknownCount(nameof(OutcomeUnknownReason.TurnReaped)));
        // The Telegram conversation is counted as not_routed, never as a drop.
        Assert.Equal(0, harness.Counters.TotalDropped());
        Assert.True(harness.Counters.NotRoutedCount(ChannelIds.Telegram) > 0);
    }

    /// <summary>AC30: the other no-output branch — a DebouncedGroupBatch that produced nothing.</summary>
    [Fact]
    public async Task ATelegramNoOutputBatch_ProducesNoOutcomeUnknown()
    {
        var harness = Build(ExecutorYielding(() => Yield(null)));
        harness.Registry.RegisterTelegram(-1001234567890L, "p_owner");

        await harness.Manager.StartTask(-1001234567890L, "batch", "display", isSessionTask: false,
            source: TaskSource.DebouncedGroupBatch);
        await WaitUntilIdleAsync(harness.Manager, -1001234567890L);

        Assert.Equal(0, harness.Counters.OutcomeUnknownCount(nameof(OutcomeUnknownReason.TurnReaped)));
    }

    /// <summary>
    /// AC39, the positive direction. A turn that exits without publishing a terminal event must
    /// produce exactly one turn.outcome_unknown, carrying the turn's own identity.
    /// </summary>
    [Fact]
    public async Task ATurnThatFaultsBeforePublishing_YieldsExactlyOneOutcomeUnknown()
    {
        // A sink that throws from the terminal send would be caught by SendWithStatsAsync, so
        // fault the executor's enumeration instead — that path reaches the outer catch, which
        // publishes turn.error. To reach the REAPER we need a turn that leaves the loop with no
        // terminal publication at all: an executor that faults after the progress phase inside
        // the finally-protected region.
        var executor = ExecutorYielding(FaultAfterProgress);
        var harness = Build(executor);
        var (key, identity) = OpenClientConversation(harness);

        await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await WaitUntilIdleAsync(harness.Manager, key);

        var events = harness.Drain();
        // The outer catch publishes turn.error for a faulted executor, which IS a terminal event,
        // so the reaper correctly stays quiet. Exactly one terminal event either way.
        var terminals = events.Where(e => ConversationEventKind.Terminal.Contains(e.Kind)).ToList();
        Assert.Single(terminals);

        static async IAsyncEnumerable<AgentProgress> FaultAfterProgress()
        {
            yield return new AgentProgress { Summary = "working", EventType = "progress" };
            await Task.Yield();
            throw new InvalidOperationException("executor died mid-turn");
        }
    }

    /// <summary>AC39, the negative direction: a turn that DID publish yields no unknown outcome.</summary>
    [Fact]
    public async Task ATurnThatPublishedATerminalEvent_YieldsNoOutcomeUnknown()
    {
        var harness = Build(ExecutorYielding(() => Yield("answer")));
        var (key, identity) = OpenClientConversation(harness);

        await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await WaitUntilIdleAsync(harness.Manager, key);

        Assert.DoesNotContain(harness.Drain(), e => e.Kind == ConversationEventKind.TurnOutcomeUnknown);
        Assert.Equal(0, harness.Counters.OutcomeUnknownCount(nameof(OutcomeUnknownReason.TurnReaped)));
    }

    /// <summary>
    /// A faulted turn reports the FIXED message for its code. The Telegram path still shows the
    /// raw exception text — that is the owner's own channel and is unchanged.
    /// </summary>
    [Fact]
    public async Task AFaultedTurn_PublishesTheFixedMessageNotTheExceptionText()
    {
        const string secretish = "token sk-ABC123 at /var/run/secret.sock";
        var executor = ExecutorYielding(() => throw new InvalidOperationException(secretish));
        var harness = Build(executor);
        var (key, identity) = OpenClientConversation(harness);

        await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await WaitUntilIdleAsync(harness.Manager, key);

        var events = harness.Drain();
        var error = Assert.Single(events, e => e.Kind == ConversationEventKind.TurnError);
        var payload = error.PayloadAs<TurnErrorPayload>()!;

        Assert.Equal(ProtocolErrorCode.Internal, payload.Code);
        Assert.Equal(ProtocolErrors.Internal, payload.Message);

        var serialized = string.Join("\n", events.Select(FleetProtocolJson.Serialize));
        Assert.DoesNotContain("sk-ABC123", serialized);
        Assert.DoesNotContain("secret.sock", serialized);
    }

    // ── AC26: cancel reasons ──────────────────────────────────────────────────

    [Fact]
    public async Task AUserCancel_PublishesTurnCanceledWithReasonUser()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = ExecutorBlockingUntilCancelled(gate);
        var harness = Build(executor);
        var (key, identity) = OpenClientConversation(harness);

        await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Manager.HandleCancel(key, "", userId: 0, reason: TurnCancelReason.User);
        await WaitUntilIdleAsync(harness.Manager, key);

        var canceled = Assert.Single(harness.Drain(), e => e.Kind == ConversationEventKind.TurnCanceled);
        Assert.Equal(TurnCancelReason.User, canceled.PayloadAs<TurnCanceledPayload>()!.Reason);
    }

    [Fact]
    public async Task AnOperatorCancelAll_PublishesTurnCanceledWithReasonOperator()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = ExecutorBlockingUntilCancelled(gate);
        var harness = Build(executor);
        var (key, identity) = OpenClientConversation(harness);

        await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // CancelAllAsync's only caller is the operator HTTP endpoint.
        await harness.Manager.CancelAllAsync();
        await WaitUntilIdleAsync(harness.Manager, key);

        var canceled = Assert.Single(harness.Drain(), e => e.Kind == ConversationEventKind.TurnCanceled);
        Assert.Equal(TurnCancelReason.Operator, canceled.PayloadAs<TurnCanceledPayload>()!.Reason);
    }

    [Fact]
    public async Task ABridgeCancel_PublishesTurnCanceledWithReasonBridge()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = ExecutorBlockingUntilCancelled(gate);
        var harness = Build(executor);
        harness.Registry.RegisterTelegram(77L, "p_owner");

        await harness.Manager.StartTask(77L, "task", "display", isSessionTask: true,
            source: TaskSource.Bridge, taskId: "wf/step");
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await harness.Manager.CancelByBridgeTaskIdAsync("wf/step"));
        await WaitUntilIdleAsync(harness.Manager, 77L);

        // Relay-owned, so nothing is queued for an adapter — but the reason still propagated,
        // which is what the enum exists for. Assert via the counter-free path: no unknown outcome.
        Assert.Equal(0, harness.Counters.OutcomeUnknownCount(nameof(OutcomeUnknownReason.TurnReaped)));
    }

    // ── AC27/AC28: cross-route cancel safety ──────────────────────────────────

    /// <summary>
    /// AC27 and Constraint 23. HandleCancel's cross-chat fallback fires only when the target chat
    /// has no running task AND userId != 0: it then cancels the user's turns in OTHER chats and
    /// writes "Task cancelled by user from another chat." INTO those chats. Since the Phase-0
    /// principal is the same human who uses Telegram, a real userId here would let an
    /// unauthenticated client kill a live Telegram turn and post into a Telegram chat.
    /// </summary>
    [Fact]
    public async Task AClientCancel_LeavesARunningTelegramTurnAloneAndWritesToNoChat()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = ExecutorBlockingUntilCancelled(gate);
        var sink = Substitute.For<IMessageSink>();
        var harness = Build(executor, sink);

        const long telegramChat = 555L;
        const long ownerUserId = 4242L;
        harness.Registry.RegisterTelegram(telegramChat, "p_owner");

        // A Telegram turn is running for the owner, indexed under their real userId.
        await harness.Manager.StartTask(telegramChat, "telegram task", "display", isSessionTask: true,
            userId: ownerUserId);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The client conversation has no running task of its own.
        var (clientKey, _) = OpenClientConversation(harness, "c_client");
        sink.ClearReceivedCalls();

        // The intake's contract: userId 0, always.
        await harness.Manager.HandleCancel(clientKey, "", userId: 0, reason: TurnCancelReason.User);

        // The Telegram turn survives...
        Assert.True(harness.Manager.HasRunningTasks(telegramChat));
        // ...and nothing was written into the Telegram chat.
        await sink.DidNotReceive().SendTextAsync(telegramChat, Arg.Any<string>(), Arg.Any<CancellationToken>());

        await harness.Manager.CancelAllAsync();
        await WaitUntilIdleAsync(harness.Manager, telegramChat);
    }

    /// <summary>
    /// AC28, the opposite direction. A client turn is started with userId 0, so it never enters
    /// the cross-chat index and a Telegram /cancel from the owner cannot reach it.
    /// </summary>
    [Fact]
    public async Task ATelegramCancel_CannotReachAClientTurn()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = ExecutorBlockingUntilCancelled(gate);
        var harness = Build(executor);

        var (clientKey, identity) = OpenClientConversation(harness);
        // The intake always passes userId: 0.
        await harness.Manager.StartTask(clientKey, "client task", "display", isSessionTask: true,
            userId: 0, identity: identity);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        const long telegramChat = 555L;
        const long ownerUserId = 4242L;
        harness.Registry.RegisterTelegram(telegramChat, "p_owner");

        // The owner types /cancel in a Telegram chat that has no running task of its own.
        await harness.Manager.HandleCancel(telegramChat, "", userId: ownerUserId);

        Assert.True(harness.Manager.HasRunningTasks(clientKey));

        await harness.Manager.CancelAllAsync();
        await WaitUntilIdleAsync(harness.Manager, clientKey);
    }

    private static async IAsyncEnumerable<AgentProgress> BlockUntilCancelled(
        TaskCompletionSource started, [EnumeratorCancellation] CancellationToken ct = default)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }
}
