using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Requests.Abstractions;
using TGFile = Telegram.Bot.Types.TGFile;

namespace Fleet.Agent.Tests;

/// <summary>
/// #277 §10 "Shared-session honesty" (T16–T23) and T14.
///
/// <para>
/// Phase 0 gives one agent one executor process and one global FIFO. The client channel is a new
/// front door onto that same session, and the whole risk of this change is that it LOOKS like a
/// second, isolated one. Each test below pins one place where the two channels are deliberately
/// not isolated (admission, the queue cap, the global kill switch) or deliberately are (cancel in
/// either direction), so that a later change which quietly converts one into the other fails here
/// rather than in production.
/// </para>
///
/// <para>
/// These are component-level tests on purpose: the claims are about runtime behaviour, not about
/// registration. The registration claims — that these components exist and are wired at all
/// without a Telegram transport — are covered against the real graph in
/// <see cref="ProgramRegistrationGraphTests"/>.
/// </para>
/// </summary>
public sealed class SharedSessionHonestyTests : IDisposable
{
    private const string ClientChannel = "example-adapter";
    private const string OwnerToken = "operator-set-token";
    private const long OwnerUserId = 4242L;
    private const long TelegramChat = 555L;

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"fleet-session-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    // ── T14: Telegram unreachable ─────────────────────────────────────────────────────────

    /// <summary>
    /// T14 / S3. With a bot client that never returns — the shape of an unreachable Telegram — a
    /// first-party turn completes anyway, because no first-party path awaits a bot call.
    ///
    /// The control matters more than the headline here: it fires a Telegram-keyed send at the SAME
    /// bot and waits for the call to arrive and not return. Without it, a test asserting "the bot
    /// was never called" would also pass against a bot that was simply unreachable from the
    /// transport for an unrelated reason, and the isolation claim would be unevidenced.
    /// </summary>
    [Fact]
    public async Task T14_WithTelegramHung_AFirstPartyTurnCompletesAndNeverTouchesTheBot()
    {
        var executor = new ScriptedExecutor();
        executor.Script.Enqueue(TurnScript.Answer("the answer"));

        var h = Build(executor);
        executor.ReleaseAllTurns();
        var bot = new HangingBot();
        var transport = BuildTransport(h, bot);
        h.Holder.Attach(transport);

        // Control: a Telegram-keyed send reaches the bot and then hangs, so the bot is genuinely
        // in the path and genuinely never returns.
        var telegramSend = transport.SendTextAsync(TelegramChat, "into a hung Telegram");
        await bot.WaitForFirstCallAsync();
        Assert.False(telegramSend.IsCompleted);

        var (key, _) = OpenClientConversation(h);
        await h.Intake.SubmitAsync(key, "what is the answer?");

        // The bound is the assertion: a first-party path that awaited the bot would sit here until
        // the test's own timeout rather than finishing in milliseconds.
        await WaitUntilIdleAsync(h.Manager, key, TimeSpan.FromSeconds(5));

        var final = Assert.Single(h.Drain(), e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.Equal("the answer", final.PayloadAs<TurnFinalPayload>()!.Text);
        Assert.Equal(1, bot.CallCount);   // still only the control's call

        bot.Release();
        await telegramSend;
    }

    // ── T16/T17: admission and the queue cap are GLOBAL ───────────────────────────────────

    /// <summary>
    /// T16 / R2. A running Telegram turn makes a first-party submission <c>queued</c>, never
    /// <c>ran</c>. There is one executor process and its send lock is held for a whole turn, so a
    /// per-conversation admission rule would report <c>ran</c> for a turn that is in fact blocked —
    /// the client would be told its work started when nothing is happening.
    /// </summary>
    [Fact]
    public async Task T16_ATelegramTurnInFlight_MakesAFirstPartySubmissionQueuedNotRan()
    {
        var executor = new ScriptedExecutor();
        var h = Build(executor);

        await h.Manager.StartTask(TelegramChat, "telegram task", "display", isSessionTask: true,
            userId: OwnerUserId);
        await executor.WaitForExecuteCountAsync(1);

        var (key, _) = OpenClientConversation(h);
        var outcome = await h.Intake.SubmitAsync(key, "client work");

        Assert.Equal(TaskDispatchOutcome.Queued, outcome);
        var accepted = Assert.Single(h.Drain(), e => e.Kind == ConversationEventKind.SubmissionAccepted);
        Assert.Equal(SubmissionDisposition.Queued, accepted.PayloadAs<SubmissionAcceptedPayload>()!.Disposition);

        await h.Manager.CancelAllAsync();
    }

    /// <summary>
    /// T17 / R3. The 20-entry FIFO is shared. Twenty queued Telegram chats leave no room, and the
    /// next first-party submission is <c>queue_full</c> — reported, not silently dropped.
    ///
    /// Distinct chat ids are deliberate: consecutive same-chat user messages coalesce into ONE
    /// entry (cap 10 parts), so twenty messages in one chat would fill two entries, not twenty.
    /// </summary>
    [Fact]
    public async Task T17_AFullGlobalQueue_MakesTheNextFirstPartySubmissionQueueFull()
    {
        var executor = new ScriptedExecutor();
        var h = Build(executor);

        await h.Manager.StartTask(TelegramChat, "telegram task", "display", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        for (var i = 0; i < 20; i++)
        {
            var outcome = await h.Manager.StartTask(1000L + i, $"queued {i}", "display", isSessionTask: true);
            Assert.Equal(TaskDispatchOutcome.Queued, outcome);
        }

        var (key, _) = OpenClientConversation(h);
        var clientOutcome = await h.Intake.SubmitAsync(key, "client work");

        Assert.Equal(TaskDispatchOutcome.QueueFull, clientOutcome);
        var accepted = Assert.Single(h.Drain(), e => e.Kind == ConversationEventKind.SubmissionAccepted);
        Assert.Equal(SubmissionDisposition.QueueFull, accepted.PayloadAs<SubmissionAcceptedPayload>()!.Disposition);

        await h.Manager.CancelAllAsync();
    }

    // ── T18/T19: cancel does NOT cross the channel boundary ───────────────────────────────

    /// <summary>
    /// T18 / R5. <c>submission.cancel</c> during a Telegram turn leaves it running and writes
    /// nothing into any Telegram chat.
    ///
    /// Driven through <see cref="ConversationIntake.CancelAsync"/> rather than the task manager,
    /// because the safety property belongs to the intake's <c>userId: 0</c> contract: a real user
    /// id there would reach the cross-chat index, kill the Telegram turn and post into that chat.
    /// </summary>
    [Fact]
    public async Task T18_AClientCancel_LeavesARunningTelegramTurnAloneAndWritesNothingToTelegram()
    {
        var executor = new ScriptedExecutor();
        var sink = Substitute.For<IMessageSink>();
        var h = Build(executor, sink);

        await h.Manager.StartTask(TelegramChat, "telegram task", "display", isSessionTask: true,
            userId: OwnerUserId);
        await executor.WaitForExecuteCountAsync(1);

        var (key, _) = OpenClientConversation(h);
        sink.ClearReceivedCalls();

        var hadRunning = await h.Intake.CancelAsync(key, CancelScope.All);

        Assert.False(hadRunning);
        Assert.True(h.Manager.HasRunningTasks(TelegramChat));
        await sink.DidNotReceive().SendTextAsync(TelegramChat, Arg.Any<string>(), Arg.Any<CancellationToken>());

        var ack = Assert.Single(h.Drain(), e => e.Kind == ConversationEventKind.ControlAck);
        Assert.False(ack.PayloadAs<ControlAckPayload>()!.HadRunningTask);

        await h.Manager.CancelAllAsync();
    }

    /// <summary>
    /// T19 / R4a. The other direction, through the real command surface: <c>/cancel</c> and
    /// <c>/cancel all</c> typed into a Telegram chat leave a first-party turn running, emit no
    /// client event, and answer the Telegram chat with "No active tasks to cancel."
    ///
    /// A client turn is started with <c>userId: 0</c> and so never enters the cross-chat index —
    /// that zero is the entire mechanism, which is why this asserts the chat's reply text too: the
    /// day the fallback reaches a client turn, this string is what changes first.
    /// </summary>
    [Theory]
    [InlineData("/cancel")]
    [InlineData("/cancel all")]
    public async Task T19_TelegramCancel_CannotReachAFirstPartyTurn(string command)
    {
        var executor = new ScriptedExecutor();
        var sink = Substitute.For<IMessageSink>();
        var h = Build(executor, sink);

        var (key, _) = OpenClientConversation(h);
        await h.Intake.SubmitAsync(key, "client work");
        await executor.WaitForExecuteCountAsync(1);
        h.Drain();
        sink.ClearReceivedCalls();

        Assert.True(await h.Commands.TryHandleAsync(TelegramChat, command));

        Assert.True(h.Manager.HasRunningTasks(key));
        await sink.Received().SendTextAsync(TelegramChat, "No active tasks to cancel.", Arg.Any<CancellationToken>());
        Assert.DoesNotContain(h.Drain(), e => ConversationEventKind.Terminal.Contains(e.Kind));

        await h.Manager.CancelAllAsync();
    }

    // ── T4: the command surface stays off the client's event stream ───────────────────────

    /// <summary>
    /// T4, behaviourally. <c>ConversationIntakeTests.Intake_HasNoCommandDispatcherDependency</c>
    /// proves a client cannot REACH the command surface; this proves the other direction — a
    /// command handled on the Telegram side emits nothing onto the client's event stream.
    ///
    /// They are different failures. A client event synthesised for a <c>/status</c> or <c>/run</c>
    /// reply would leak the operator's own command output — process state, executor output, chat
    /// ids — onto a channel that never asked for it, without any client path being wired at all.
    /// </summary>
    [Theory]
    [InlineData("/status")]
    [InlineData("/cancel")]
    [InlineData("/reset")]
    [InlineData("/stop")]
    public async Task T4_ACommandHandledOnTelegram_ProducesNoClientEvent(string command)
    {
        var executor = new ScriptedExecutor();
        var sink = Substitute.For<IMessageSink>();
        var h = Build(executor, sink);

        // A client conversation exists and is idle: the command has somewhere to leak TO.
        var (clientKey, _) = OpenClientConversation(h);
        h.Drain();

        // The ordinary case — the command arrives on a Telegram chat.
        Assert.True(await h.Commands.TryHandleAsync(TelegramChat, command));
        Assert.Empty(h.Drain());

        // And the shape a leak actually takes: the dispatcher reached with the CLIENT's own
        // runtime key. Nothing routes a client submission here today — the intake has no
        // dispatcher dependency at all — but "no client event on any channel" is a claim about
        // the command surface, not about who happens to call it, and this is the half that fails
        // when a command handler gains a publish. Driving only the Telegram chat is not enough:
        // an event synthesised for a Telegram conversation is dropped by the bus before routing,
        // so that half of the test is satisfied by the bus rather than by the command surface.
        Assert.True(await h.Commands.TryHandleAsync(clientKey, command));
        Assert.Empty(h.Drain());

        // ...and the command did answer, on its own channel. Without this the test would pass
        // against a dispatcher that silently did nothing at all.
        await sink.Received().SendTextAsync(TelegramChat, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── T20: /halt is global, and says so honestly ────────────────────────────────────────

    /// <summary>
    /// T20 / R4b. The global kill switch DOES reach a first-party turn — it is an operator action
    /// on the whole process, and pretending otherwise would leave the client waiting on a turn
    /// whose executor is gone.
    ///
    /// The reason must be <c>unknown</c>, not <c>user</c> and not <c>operator</c>.
    /// <see cref="TaskManager.HandleStop"/> records no reason before cancelling, so
    /// <c>unknown</c> is the truthful reading: the client cannot tell who halted the process.
    /// Mapping it to <c>user</c> would blame the client for someone else's action, and
    /// <c>operator</c> would assert an attribution the runtime does not have — this test is the
    /// mutation guard for both.
    /// </summary>
    [Fact]
    public async Task T20_HaltDuringAFirstPartyTurn_PublishesTurnCanceledWithReasonUnknown()
    {
        var executor = new ScriptedExecutor();
        var sink = Substitute.For<IMessageSink>();
        var h = Build(executor, sink);

        var (key, identity) = OpenClientConversation(h);
        await h.Intake.SubmitAsync(key, "client work");
        await executor.WaitForExecuteCountAsync(1);

        // The command surface itself, not HandleStop directly — /halt reaching the task manager at
        // all is half the claim.
        Assert.True(await h.Commands.TryHandleAsync(TelegramChat, "/stop"));
        await WaitUntilIdleAsync(h.Manager, key, TimeSpan.FromSeconds(5));

        var canceled = Assert.Single(h.Drain(), e => e.Kind == ConversationEventKind.TurnCanceled);
        Assert.Equal(TurnCancelReason.Unknown, canceled.PayloadAs<TurnCanceledPayload>()!.Reason);
        Assert.Equal(identity.ConversationId, canceled.Identity.ConversationId);
    }

    // ── T21: /reset cannot strand a turn ──────────────────────────────────────────────────

    /// <summary>
    /// T21 / R4c. <c>/reset</c> in an IDLE Telegram chat still stops the shared executor process,
    /// so it reaches a first-party turn running in another conversation. The turn must terminate —
    /// the failure this guards is the turn hanging forever with the client holding an open
    /// submission.
    ///
    /// The kind is pinned deliberately: <c>turn.error</c> carrying the fixed <c>internal</c>
    /// message. A stopped provider process ends the in-flight stream abnormally, which reaches
    /// <c>ProcessTask</c>'s outer catch. Pinning it means a future change that converts this into a
    /// silent <c>turn.final</c> — reporting success for a turn that was killed — fails here.
    /// </summary>
    [Fact]
    public async Task T21_ResetInAnIdleTelegramChat_TerminatesAFirstPartyTurnRatherThanStrandingIt()
    {
        var executor = new ScriptedExecutor { FaultInFlightOnStop = true };
        var sink = Substitute.For<IMessageSink>();
        var h = Build(executor, sink);

        var (key, _) = OpenClientConversation(h);
        await h.Intake.SubmitAsync(key, "client work");
        await executor.WaitForExecuteCountAsync(1);

        // The Telegram chat is idle, so HandleReset proceeds instead of refusing.
        Assert.False(h.Manager.HasRunningTasks(TelegramChat));
        Assert.True(await h.Commands.TryHandleAsync(TelegramChat, "/reset"));

        await WaitUntilIdleAsync(h.Manager, key, TimeSpan.FromSeconds(5));

        var events = h.Drain();
        var terminal = Assert.Single(events, e => ConversationEventKind.Terminal.Contains(e.Kind));
        Assert.Equal(ConversationEventKind.TurnError, terminal.Kind);
        var payload = terminal.PayloadAs<TurnErrorPayload>()!;
        Assert.Equal(ProtocolErrorCode.Internal, payload.Code);
        Assert.Equal(ProtocolErrors.Internal, payload.Message);
    }

    // ── T22/T23: continuation identity ────────────────────────────────────────────────────

    /// <summary>
    /// T22. An injected message plus a provider process exit produces ONE redelivery turn that
    /// answers both submissions. Its identity must keep the ORIGINAL <c>submissionId</c>,
    /// increment <c>attempt</c> exactly once, and list the injected submission exactly once.
    ///
    /// Re-pointing the identity at the injected submission would drop the original from the turn
    /// entirely — it would never terminate — while duplicating the injected id, so all three
    /// assertions are one claim looked at from three sides.
    /// </summary>
    [Fact]
    public async Task T22_AnInjectionThenAResume_KeepsTheSubmissionIdAndIncrementsAttemptOnce()
    {
        var executor = new ScriptedExecutor();
        // Turn 1 dies with the process; the redelivered turn answers normally.
        executor.Script.Enqueue(TurnScript.ProcessExit());
        executor.Script.Enqueue(TurnScript.Answer("answer for both"));

        var h = Build(executor);
        var (key, _) = OpenClientConversation(h);

        await h.Intake.SubmitAsync(key, "first");
        await executor.WaitForExecuteCountAsync(1);
        var injectedOutcome = await h.Intake.SubmitAsync(key, "second");
        Assert.Equal(TaskDispatchOutcome.Injected, injectedOutcome);
        await executor.WaitForInjectionCountAsync(1);

        executor.ReleaseAllTurns();
        await WaitUntilIdleAsync(h.Manager, key, TimeSpan.FromSeconds(5));

        var events = h.Drain();
        var accepted = events.Where(e => e.Kind == ConversationEventKind.SubmissionAccepted).ToList();
        var originalSubmissionId = accepted[0].Identity.SubmissionId;
        var injectedSubmissionId = accepted[1].Identity.SubmissionId;
        Assert.NotEqual(originalSubmissionId, injectedSubmissionId);

        var started = events.Where(e => e.Kind == ConversationEventKind.TurnStarted).ToList();
        Assert.Equal(2, started.Count);
        Assert.All(started, e => Assert.Equal(originalSubmissionId, e.Identity.SubmissionId));
        Assert.Equal([1, 2], started.Select(e => e.Identity.Attempt).ToArray());
        // A new turn id per attempt: the attempt is a retry of the submission, not a resumption of
        // the same turn.
        Assert.Equal(2, started.Select(e => e.Identity.TurnId).Distinct().Count());

        var final = Assert.Single(events, e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.Equal(originalSubmissionId, final.Identity.SubmissionId);
        Assert.Equal(2, final.Identity.Attempt);

        // mergedSubmissionIds is "every submission this turn answers", its own included and
        // deduplicated, so the resumed turn must list BOTH exactly once. Re-pointing the identity
        // at the injected submission — the mutation this guards — would make the list collapse to
        // the injected id alone and leave the original submission with no terminal event at all.
        Assert.Equal(
            [originalSubmissionId, injectedSubmissionId],
            final.PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds);
    }

    /// <summary>
    /// T23. The same provider process exit with NOTHING to redeliver is identity-invisible: one
    /// <c>turn.started</c>, attempt stays 1, and the turn terminates once.
    ///
    /// Paired with T22 this is what pins the attempt increment to the redelivery path. Re-pointing
    /// it at the provider resume itself turns this test red — an internal restart would start
    /// announcing itself to the client as a new attempt on work the client never re-sent.
    /// </summary>
    [Fact]
    public async Task T23_AProviderResumeWithNothingToRedeliver_IsIdentityInvisible()
    {
        var executor = new ScriptedExecutor();
        executor.Script.Enqueue(TurnScript.ProcessExit());

        var h = Build(executor);
        var (key, _) = OpenClientConversation(h);

        await h.Intake.SubmitAsync(key, "only message");
        await executor.WaitForExecuteCountAsync(1);
        executor.ReleaseAllTurns();
        await WaitUntilIdleAsync(h.Manager, key, TimeSpan.FromSeconds(5));

        var events = h.Drain();
        var started = Assert.Single(events, e => e.Kind == ConversationEventKind.TurnStarted);
        Assert.Equal(1, started.Identity.Attempt);

        var terminal = Assert.Single(events, e => ConversationEventKind.Terminal.Contains(e.Kind));
        Assert.Equal(1, terminal.Identity.Attempt);
        Assert.Equal(started.Identity.SubmissionId, terminal.Identity.SubmissionId);
        Assert.Equal(1, executor.ExecuteCount);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required TaskManager Manager { get; init; }
        public required ConversationIntake Intake { get; init; }
        public required CommandDispatcher Commands { get; init; }
        public required ConversationEventBus Bus { get; init; }
        public required ConversationRegistry Registry { get; init; }
        public required MessageSinkHolder Holder { get; init; }
        public required GroupBehavior GroupBehavior { get; init; }
        public required GroupRelayService Relay { get; init; }
        public required IOptions<AgentOptions> AgentOptions { get; init; }
        public required IOptions<TelegramOptions> TelegramOptions { get; init; }
        public required AllowlistHolder Allowlist { get; init; }
        public required IAgentExecutor Executor { get; init; }
        public required PromptAssembler Prompts { get; init; }

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

    private Harness Build(IAgentExecutor executor, IMessageSink? sink = null)
    {
        Directory.CreateDirectory(_workDir);

        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1", Role = "generic-role", WorkDir = _workDir, Provider = "claude",
            FormattingMode = Fleet.Shared.FormattingMode.PlainText, ShortName = "agent1",
        });
        var telegramOpts = Options.Create(new TelegramOptions { AllowedUserIds = [OwnerUserId] });
        var clientOpts = Options.Create(new ClientChannelOptions
        {
            OwnerPrincipalToken = OwnerToken, OwnerUserId = OwnerUserId,
        });
        var rabbitOpts = Options.Create(new RabbitMqOptions());

        var holder = new MessageSinkHolder();
        if (sink is not null)
            holder.Attach(sink);

        var registry = new ConversationRegistry();
        var counters = new ConversationEventCounters();
        var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var manager = new TaskManager(agentOpts, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, injectionCounter: null, events: bus,
            telegramConfig: null, counters: counters, sink: holder);
        var prompts = new PromptAssembler(executor);
        var commands = new CommandDispatcher(manager, executor, agentOpts,
            NullLogger<CommandDispatcher>.Instance, sink: holder);
        var groupBehavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, prompts, NullLogger<GroupBehavior>.Instance, sink: holder);
        var binder = new PrincipalBinder(clientOpts, agentOpts, allowlist);
        var intake = new ConversationIntake(registry, binder, manager, groupBehavior, bus,
            NullLogger<ConversationIntake>.Instance);

        return new Harness
        {
            Manager = manager, Intake = intake, Commands = commands, Bus = bus, Registry = registry,
            Holder = holder, GroupBehavior = groupBehavior, Relay = relay, AgentOptions = agentOpts,
            TelegramOptions = telegramOpts, Allowlist = allowlist, Executor = executor,
            Prompts = prompts,
        };
    }

    /// <summary>A real transport over the harness's own components — only the bot client is fake.</summary>
    private static AgentTransport BuildTransport(Harness h, HangingBot bot)
    {
        var router = new MessageRouter(h.AgentOptions, h.TelegramOptions, h.Allowlist, h.Manager,
            h.GroupBehavior, h.Relay, h.Commands, NullLogger<MessageRouter>.Instance, sink: h.Holder);
        var httpFact = Substitute.For<IHttpClientFactory>();
        var voice = new VoiceTranscriptionService(httpFact, Options.Create(new WhisperOptions()),
            NullLogger<VoiceTranscriptionService>.Instance);
        var tts = new TtsService(httpFact, Options.Create(new TtsOptions()), NullLogger<TtsService>.Instance);

        var transport = new AgentTransport(
            h.AgentOptions, h.TelegramOptions, h.Allowlist, h.Relay, h.Manager, h.GroupBehavior,
            router, h.Commands, voice, tts, Substitute.For<IFleetConnectionState>(),
            NullLogger<AgentTransport>.Instance, h.Holder, null)
        {
            BotForTesting = bot,
        };
        return transport;
    }

    private static (long key, ConversationIdentity identity) OpenClientConversation(Harness h)
    {
        var result = h.Intake.Open(new ConversationOpenPayload
        {
            ChannelId = ClientChannel,
            PrincipalBinding = new PrincipalBinding
            {
                Scheme = PrincipalBinding.LegacyOwnerScheme,
                Value = OwnerToken,
            },
            Role = PrincipalRole.Owner,
        });
        Assert.True(result.Success);
        return (result.RuntimeKey, new ConversationIdentity
        {
            PrincipalId = result.PrincipalId!,
            Role = PrincipalRole.Owner,
            ChannelId = ClientChannel,
            ConversationId = result.ConversationId!,
            SubmissionId = "s_probe",
            Attempt = 1,
        });
    }

    private static async Task WaitUntilIdleAsync(TaskManager manager, long key, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        while (manager.HasRunningTasks(key))
            await Task.Delay(10, cts.Token);
        // The terminal publish happens inside ProcessTask, just before the task leaves the running
        // set; give the finally block a beat so the reaper check has run too.
        await Task.Delay(50, CancellationToken.None);
    }

    // ── executor ──────────────────────────────────────────────────────────────────────────

    /// <summary>One scripted turn. Absent from the script means "block until released".</summary>
    private sealed record TurnScript(string? Text, bool IsError, bool IsProcessExit)
    {
        public static TurnScript Answer(string text) => new(text, false, false);

        /// <summary>The provider process died mid-turn — an error result carrying the exit flag.</summary>
        public static TurnScript ProcessExit() => new("executor failed", true, true);
    }

    private sealed class ScriptedExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _releaseTurns = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _executed = new();
        private readonly ConcurrentQueue<string> _injected = new();

        public ConcurrentQueue<TurnScript> Script { get; } = new();

        /// <summary>
        /// Models a provider whose process is killed while a turn is streaming: the in-flight
        /// enumeration ends abnormally. This is what <c>/reset</c> does to a turn in another
        /// conversation.
        /// </summary>
        public bool FaultInFlightOnStop { get; init; }

        public int ExecuteCount => _executed.Count;
        public IReadOnlyList<string> Executed => _executed.ToList();
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executed.Enqueue(task);

            if (FaultInFlightOnStop)
            {
                await _stopped.Task.WaitAsync(ct);
                throw new InvalidOperationException("provider process stopped mid-turn");
            }

            // EVERY turn blocks until released. A turn that finishes on its own would race every
            // test that needs one in flight — the injection in T22 would arrive after the turn it
            // is supposed to interrupt, and report `ran` rather than `injected`.
            await _releaseTurns.Task.WaitAsync(ct);

            if (!Script.TryDequeue(out var script))
                script = TurnScript.Answer("released");

            yield return new AgentProgress
            {
                EventType = "result",
                Summary = script.Text ?? "",
                FinalResult = script.Text,
                IsErrorResult = script.IsError,
                IsProcessExit = script.IsProcessExit,
            };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
        {
            _injected.Enqueue(task);
            return Task.FromResult(MidTurnInjectionResult.Injected);
        }

        public Task StopProcessAsync()
        {
            _stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public void ReleaseAllTurns() => _releaseTurns.TrySetResult();
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
            ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task WaitForExecuteCountAsync(int expected) => WaitUntilAsync(() => _executed.Count >= expected);
        public Task WaitForInjectionCountAsync(int expected) => WaitUntilAsync(() => _injected.Count >= expected);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
                await Task.Delay(10, cts.Token);
        }
    }
}

/// <summary>
/// A bot client that accepts a call and never returns — the shape an unreachable Telegram takes
/// from inside the process (T14 / S3). Distinct from a THROWING client: a throw returns control,
/// and the failure this models is the one that does not.
/// </summary>
internal sealed class HangingBot : ITelegramBotClient
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public bool LocalBotServer => false;
    public long BotId => 1;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

#pragma warning disable CS0067
    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
#pragma warning restore CS0067

    public async Task<TResponse> MakeRequestAsync<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        _firstCall.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);

        // Released at the end of the test so the pending send completes rather than faulting into
        // an unobserved task — the hang is the subject, the unwind is just cleanup.
        return request is SendMessageRequest m
            ? (TResponse)(object)new Message
            {
                Id = _callCount,
                Chat = new Chat { Id = m.ChatId.Identifier ?? 0 },
            }
            : default!;
    }

    public Task<TResponse> SendRequest<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<TResponse> MakeRequest<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task WaitForFirstCallAsync() => _firstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
    public void Release() => _release.TrySetResult();

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> TestApiAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task DownloadFileAsync(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
