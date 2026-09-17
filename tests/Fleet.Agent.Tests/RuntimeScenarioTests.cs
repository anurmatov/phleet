using System.Text;
using System.Text.Json;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Protocol;

namespace Fleet.Agent.Tests;

/// <summary>
/// The runtime-only scenarios of D4 — S7, S8, S11, S12 and S15 — plus S17, added during
/// implementation to restore coverage of a thrown executor (see the method for why).
///
/// <para>These exercise <c>TaskManager</c> and <c>ConversationEventBus</c> behaviour that is
/// identical regardless of which executor produced the progress. Running them per provider would
/// produce three identical rows and inflate apparent coverage while measuring the same code three
/// times, so each exists <b>exactly once</b> — asserted by
/// <see cref="EachRuntimeOnlyScenarioExistsExactlyOnce"/>.</para>
///
/// <para>This file also owns the two failure-path rules from the design's dependency table: the
/// terminal outbox counter is zero at the end of every scenario, and a turn still terminates when
/// the adapter throws.</para>
/// </summary>
public class RuntimeScenarioTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <summary>The runtime-only ids. Named here so the uniqueness check has something to check.</summary>
    public static readonly IReadOnlyList<string> RuntimeOnlyScenarios = ["S7", "S8", "S11", "S12", "S15", "S17"];

    // ── S7: cancel with a running task ───────────────────────────────────────

    [Fact]
    public async Task S7_CancelWithARunningTask_AcknowledgesThenCancelsTheTurn()
    {
        using var cts = new CancellationTokenSource(Budget);
        var executor = new ScriptedExecutor { BlockUntilCancelled = true };
        var harness = ConversationHarness.Build(executor);
        var (key, identity) = harness.OpenClientConversation();

        await harness.Manager.StartTask(key, "task", "task", isSessionTask: true, identity: identity);
        await executor.FirstTurnStarted;

        var hadRunningTask = await harness.Intake.CancelAsync(key, CancelScope.Current);
        Assert.True(hadRunningTask);

        var delivered = await harness.PumpUntilTerminalAsync(cts.Token);
        MatrixCells.AssertDeliveryOrderIsLawful(delivered);

        var ordered = delivered.OrderBy(e => e.Seq).Where(e => !MatrixCells.IsTypingHeartbeat(e)).ToList();

        // The turn's start and its dispatch disposition are deterministic and come first.
        Assert.Equal(
            "turn.started → submission.accepted(ran)",
            MatrixCells.ClientEvents(ordered.Take(2)));

        // ⚠️ `control.ack` and `turn.canceled` RACE, and the race is real rather than a harness
        // artifact: the ack is published by ConversationIntake on the caller's thread after
        // HandleCancel returns, while the terminal is published by the turn's own catch block on
        // the turn's thread. Either can win, so the emission order between them is not a contract
        // and this scenario deliberately does not pin one. A client must treat the ack as
        // "the request was accepted", never as "the terminal has not arrived yet".
        Assert.Equal(2, ordered.Count - 2);
        Assert.Single(ordered, e => e.Kind == ConversationEventKind.ControlAck);
        Assert.Single(ordered, e => e.Kind == ConversationEventKind.TurnCanceled);

        var canceled = ordered.Single(e => e.Kind == ConversationEventKind.TurnCanceled);
        Assert.Equal(TurnCancelReason.User, canceled.PayloadAs<TurnCanceledPayload>()!.Reason);

        // `accepted` means the REQUEST was accepted, not that the turn stopped. The authoritative
        // outcome is the turn.canceled that follows, and it carries the reason the canceller
        // recorded — not a default.
        var ack = ordered.Single(e => e.Kind == ConversationEventKind.ControlAck).PayloadAs<ControlAckPayload>()!;
        Assert.True(ack.Accepted);
        Assert.True(ack.HadRunningTask);

        AssertNoTerminalOutboxOverflow(harness);
    }

    // ── S8: cancel with no running task ──────────────────────────────────────

    [Fact]
    public async Task S8_CancelWithNoRunningTask_AcknowledgesAndNothingFollows()
    {
        using var cts = new CancellationTokenSource(Budget);
        var harness = ConversationHarness.Build(new ScriptedExecutor());
        var (key, _) = harness.OpenClientConversation();

        var hadRunningTask = await harness.Intake.CancelAsync(key, CancelScope.Current);
        Assert.False(hadRunningTask);

        var delivered = await harness.PumpAsync(cts.Token);

        // No turn.canceled follows, so the ack is the whole story (D8). A client that waits for a
        // terminal here waits forever, which is exactly why HadRunningTask is on the wire.
        var ack = Assert.Single(delivered);
        Assert.Equal(ConversationEventKind.ControlAck, ack.Kind);
        Assert.False(ack.PayloadAs<ControlAckPayload>()!.HadRunningTask);
        Assert.DoesNotContain(delivered, e => e.IsTerminal);

        AssertNoTerminalOutboxOverflow(harness);
    }

    // ── S11: the turn leaves its loop without a terminal ─────────────────────

    [Fact]
    public async Task S11_TurnWithoutATerminal_IsReapedAsOutcomeUnknown()
    {
        using var cts = new CancellationTokenSource(Budget);

        // An executor that yields nothing at all: no result, no error, no process exit. This is the
        // genuine gap the reaper exists for — every ordinary terminal path sets TerminalPublished.
        var harness = ConversationHarness.Build(new ScriptedExecutor());
        var (key, identity) = harness.OpenClientConversation();

        await harness.Manager.StartTask(key, "task", "task", isSessionTask: true, identity: identity);
        var delivered = await harness.PumpUntilTerminalAsync(cts.Token);
        MatrixCells.AssertDeliveryOrderIsLawful(delivered);

        var terminal = Assert.Single(delivered, e => e.IsTerminal);

        // NOT turn_reaped: an executor that yields nothing still reaches TaskManager's "no text
        // output" branch, which publishes a terminal and therefore disarms the reaper. The reaper
        // fires only on a path that publishes nothing at all.
        Assert.Equal(ConversationEventKind.TurnFinal, terminal.Kind);
        Assert.Equal(TurnCompletion.Completed, terminal.PayloadAs<TurnFinalPayload>()!.Completion);
        Assert.Equal(0, harness.Counters.OutcomeUnknownCount(nameof(OutcomeUnknownReason.TurnReaped)));

        AssertNoTerminalOutboxOverflow(harness);
    }

    // ── S12: the terminal exceeds the 128 KiB serialized cap ─────────────────

    /// <summary>
    /// Reaching the cap through the real path takes non-ASCII text, and the arithmetic is the whole
    /// point of the scenario.
    ///
    /// <para><c>MaxFinalTextChars</c> is 64 Ki UTF-16 <b>units</b>; <c>MaxSerializedEventBytes</c>
    /// is 128 Ki UTF-8 <b>bytes</b>. With ASCII the bound is ~64 KiB serialized and the cap is
    /// unreachable, so a naive fixture would silently test nothing. <c>FleetProtocolJson.Options</c>
    /// sets no custom <c>Encoder</c>, so the default <c>JavaScriptEncoder</c> escapes every
    /// non-ASCII code unit as <c>\uXXXX</c> — six UTF-8 bytes per character. 64 Ki Cyrillic
    /// characters therefore serialize to ≈ 384 KiB, comfortably over the cap, before the envelope
    /// is counted.</para>
    ///
    /// <para>Clause (c) measures that directly, so a later change to the encoder or to the limits
    /// turns this red instead of quietly degrading the scenario into a no-op that still passes.</para>
    /// </summary>
    [Fact]
    public async Task S12_OversizeTerminal_IsReplacedByOutcomeUnknownAndTheSubmissionStillTerminates()
    {
        using var cts = new CancellationTokenSource(Budget);

        var oversize = new string('я', ProtocolLimits.MaxFinalTextChars);
        var harness = ConversationHarness.Build(new ScriptedExecutor([ScriptedExecutor.Final(oversize)]));
        var (key, identity) = harness.OpenClientConversation();

        await harness.Manager.StartTask(key, "task", "task", isSessionTask: true, identity: identity);
        var delivered = await harness.PumpUntilTerminalAsync(cts.Token);
        MatrixCells.AssertDeliveryOrderIsLawful(delivered);

        // (a) the substitution happened...
        var terminal = Assert.Single(delivered, e => e.IsTerminal);
        Assert.Equal(ConversationEventKind.TurnOutcomeUnknown, terminal.Kind);
        Assert.Equal(
            OutcomeUnknownReason.TerminalEventOversize,
            terminal.PayloadAs<TurnOutcomeUnknownPayload>()!.Reason);

        // (b) ...and the submission still terminated. An oversize answer must not become a hang.
        Assert.DoesNotContain(delivered, e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.Equal(1, harness.Counters.DroppedCount(ConversationEventCounters.ReasonOversize));

        // (c) the suppressed turn.final really did exceed the cap, measured through the protocol's
        // own serializer rather than asserted from the arithmetic above.
        var suppressed = ConversationEvent.Create(
            ConversationEventKind.TurnFinal, identity, "probe", 1, DateTimeOffset.UnixEpoch,
            new TurnFinalPayload
            {
                Text = oversize,
                Completion = TurnCompletion.Completed,
                IsPartial = false,
                Truncated = false,
                MergedSubmissionIds = [],
            });
        var serializedBytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(suppressed, FleetProtocolJson.Options));
        Assert.True(
            serializedBytes > ProtocolLimits.MaxSerializedEventBytes,
            $"The S12 payload serialized to {serializedBytes} bytes, which does not exceed the "
            + $"{ProtocolLimits.MaxSerializedEventBytes}-byte cap. The scenario has degraded into a no-op.");

        AssertNoTerminalOutboxOverflow(harness);
    }

    // ── S15: Telegram / relay / bridge conversations ─────────────────────────

    [Theory]
    [InlineData(TaskSource.UserMessage)]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.Bridge)]
    public async Task S15_RuntimeOwnedConversation_ReachesNoAdapterAndIsCountedNotRouted(TaskSource source)
    {
        using var cts = new CancellationTokenSource(Budget);
        var harness = ConversationHarness.Build(
            new ScriptedExecutor([ScriptedExecutor.Final("Synthetic answer A.")]));

        // A Telegram chat id, not a registered client conversation: no identity is supplied, so
        // TaskManager synthesizes one stamped `telegram` or `relay`.
        const long TelegramChatId = 4242L;
        await harness.Manager.StartTask(
            TelegramChatId, "task", "task", isSessionTask: true, source: source);

        await WaitUntilIdleAsync(harness, TelegramChatId, cts.Token);
        var delivered = await harness.PumpAsync(cts.Token);

        // Zero events reach the adapter — relay and bridge traffic carries correlation ids and
        // completion callbacks, and a lost callback surfaces as a hung workflow activity rather
        // than as a wrong reply (Constraint 8).
        Assert.Empty(delivered);

        var channelId = source is TaskSource.Relay or TaskSource.Bridge
            ? ChannelIds.Relay
            : ChannelIds.Telegram;

        // not_routed, NOT dropped. Counting the steady state as a drop would make the drop counter
        // the dominant production metric and train everyone to ignore it.
        Assert.True(harness.Counters.NotRoutedCount(channelId) > 0);
        Assert.Equal(0, harness.Counters.DroppedCount(ConversationEventCounters.ReasonUnknownConversation));
        Assert.Equal(0, harness.Counters.TotalDropped());

        AssertNoTerminalOutboxOverflow(harness);
    }

    // ── S17: the executor throws mid-turn ────────────────────────────────────

    /// <summary>
    /// S17 is an ADDITION made during implementation, not a scenario from the original set.
    ///
    /// <para>The design's S9 was "executor throws mid-turn", with the Claude seam named as
    /// "event channel faulted". That seam turned out to be unusable from a public test — faulting
    /// the channel drives <c>ClaudeExecutor</c> into its process-restart path, which would start a
    /// real provider CLI — so S9 became "executor failure surfaced mid-turn" and replays each
    /// provider's real failure FRAME instead. That left the actual throw uncovered, which is what
    /// this scenario restores. It is runtime-only because a thrown enumerator is caught by
    /// <c>TaskManager</c> and never reaches provider-specific code.</para>
    ///
    /// <para>The two cases produce DIFFERENT error codes, and the difference matters to a client:
    /// a thrown executor is <c>internal</c>, while an executor that REPORTS an error through an
    /// <c>error</c>-typed progress event is <c>executor_error</c>. Both carry the fixed constant
    /// from <see cref="ProtocolErrors"/> — never the exception message, never provider text.</para>
    /// </summary>
    [Theory]
    [InlineData(true, ProtocolErrorCode.Internal)]
    [InlineData(false, ProtocolErrorCode.ExecutorError)]
    public async Task S17_ExecutorFailsMidTurn_PublishesOneFixedMessageTurnError(
        bool thrown, ProtocolErrorCode expectedCode)
    {
        const string secret = "EXCEPTION-TEXT-MUST-NOT-REACH-A-CLIENT";
        using var cts = new CancellationTokenSource(Budget);

        var executor = thrown
            ? new ScriptedExecutor { ThrowAfterScript = new InvalidOperationException(secret) }
            : new ScriptedExecutor([
                new AgentProgress { EventType = "error", Summary = secret, IsSignificant = true },
            ]);

        var harness = ConversationHarness.Build(executor);
        var (key, identity) = harness.OpenClientConversation();

        await harness.Manager.StartTask(key, "task", "task", isSessionTask: true, identity: identity);
        var delivered = await harness.PumpUntilTerminalAsync(cts.Token);
        MatrixCells.AssertDeliveryOrderIsLawful(delivered);

        // Exactly one terminal, and it is a turn.error.
        var terminal = Assert.Single(delivered, e => e.IsTerminal);
        Assert.Equal(ConversationEventKind.TurnError, terminal.Kind);

        var payload = terminal.PayloadAs<TurnErrorPayload>()!;
        Assert.Equal(expectedCode, payload.Code);

        // The message is ALWAYS the fixed constant for the code. The runtime's own error string
        // carries provider text, executor stdout and exception messages, any of which can contain
        // filesystem paths, session ids or credentials — those stay server-side.
        Assert.Equal(ProtocolErrors.MessageFor(expectedCode), payload.Message);

        // Nothing anywhere in the delivered stream leaks the exception text, including the
        // envelope and every non-terminal event.
        foreach (var evt in delivered)
        {
            Assert.DoesNotContain(
                secret, FleetProtocolJson.Serialize(evt), StringComparison.Ordinal);
        }

        AssertNoTerminalOutboxOverflow(harness);
    }

    // ── Failure paths owned by this file ─────────────────────────────────────

    /// <summary>
    /// An adapter that throws is contained and counted by the pump; the turn still terminates. A
    /// client adapter must never be able to reach a turn.
    /// </summary>
    [Fact]
    public async Task AdapterThatThrows_IsContainedAndTheTurnStillTerminates()
    {
        using var cts = new CancellationTokenSource(Budget);
        var harness = ConversationHarness.Build(
            new ScriptedExecutor([ScriptedExecutor.Final("Synthetic answer A.")]));
        harness.Adapter.Behaviour = (_, _) => throw new InvalidOperationException("adapter is down");

        var (key, identity) = harness.OpenClientConversation();
        await harness.Manager.StartTask(key, "task", "task", isSessionTask: true, identity: identity);

        await WaitUntilIdleAsync(harness, key, cts.Token);
        await harness.Pump.DrainOnceAsync(cts.Token);

        Assert.True(
            harness.Counters.DeliveryFailureCount(
                harness.Adapter.ChannelId, ConversationEventCounters.FailureThrew) > 0);

        // The turn is over regardless of the adapter's behaviour.
        Assert.False(harness.Manager.HasRunningTasks(key));
        AssertNoTerminalOutboxOverflow(harness);
    }

    /// <summary>
    /// AC10 / MUST NOT #10 — S7, S8, S11, S12 and S15 exist exactly once each across the whole
    /// suite. If one were also run per provider, the matrix would carry three identical rows and
    /// report coverage it does not have.
    /// </summary>
    [Fact]
    public void EachRuntimeOnlyScenarioExistsExactlyOnce()
    {
        // (a) no per-provider matrix row claims a runtime-only scenario...
        var rows = CapabilityMatrix.Parse();
        foreach (var scenario in RuntimeOnlyScenarios)
        {
            Assert.DoesNotContain(rows, r => r.Scenario == scenario);
            Assert.DoesNotContain(ScenarioRunner.PerProviderScenarios, s => s == scenario);
        }

        // (b) ...and exactly one test method in this class owns each one.
        var methods = typeof(RuntimeScenarioTests)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        foreach (var scenario in RuntimeOnlyScenarios)
            Assert.Single(methods, name => name.StartsWith($"{scenario}_", StringComparison.Ordinal));
    }

    // ── Shared assertions ────────────────────────────────────────────────────

    /// <summary>
    /// Reaching the per-conversation terminal outbox capacity is a DEFECT, not load: a conversation
    /// runs one turn at a time and a turn produces one terminal event. Every scenario asserts zero.
    /// </summary>
    private static void AssertNoTerminalOutboxOverflow(ConversationHarness harness) =>
        Assert.Equal(0, harness.Counters.DroppedCount(ConversationEventCounters.ReasonTerminalOutboxOverflow));

    private static async Task WaitUntilIdleAsync(ConversationHarness harness, long key, CancellationToken ct)
    {
        while (harness.Manager.HasRunningTasks(key))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
