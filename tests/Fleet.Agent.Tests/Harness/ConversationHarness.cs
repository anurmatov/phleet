using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// L2 (D2): the real runtime projection path, terminating at a
/// <see cref="LoopbackChannelAdapter"/>.
///
/// <para>Every class on this path is the production class —
/// <see cref="TaskManager"/>, <see cref="ConversationIntake"/>, <see cref="ConversationEventBus"/>,
/// <see cref="ConversationEventPump"/>, <see cref="ConversationRegistry"/>. Only the OS process is
/// replaced, by a <see cref="ScriptedExecutor"/> or by the <see cref="AgentProgress"/> sequence a
/// provider replay captured at L1.</para>
///
/// <para>Extracted from the private <c>Harness</c> in <c>ConversationTurnLifecycleTests</c>. That
/// file now uses this one; a second copy would be a maintenance defect.</para>
///
/// <para><b>The pump is never started as a hosted service.</b> <see cref="PumpAsync"/> calls the
/// pump's <c>DrainOnceAsync</c> explicitly, so delivery is deterministic and no background loop
/// competes with <see cref="Drain"/> for the same readers. A test picks one: <see cref="Drain"/>
/// to read the bus directly, <see cref="PumpAsync"/> to read what an adapter observed.</para>
/// </summary>
internal sealed class ConversationHarness
{
    private readonly Lazy<ConversationIntake> _intake;

    private ConversationHarness(
        TaskManager manager,
        ConversationEventBus bus,
        ConversationRegistry registry,
        ConversationEventCounters counters,
        IMessageSink sink,
        LoopbackChannelAdapter adapter,
        ConversationEventPump pump,
        Lazy<ConversationIntake> intake)
    {
        Manager = manager;
        Bus = bus;
        Registry = registry;
        Counters = counters;
        Sink = sink;
        Adapter = adapter;
        Pump = pump;
        _intake = intake;
    }

    public TaskManager Manager { get; }
    public ConversationEventBus Bus { get; }
    public ConversationRegistry Registry { get; }
    public ConversationEventCounters Counters { get; }
    public IMessageSink Sink { get; }
    public LoopbackChannelAdapter Adapter { get; }
    public ConversationEventPump Pump { get; }

    /// <summary>
    /// Built lazily. <see cref="ConversationIntake"/> pulls in <see cref="GroupBehavior"/>,
    /// <see cref="CommandDispatcher"/> and a relay service; constructing those for a test that
    /// only drives <see cref="TaskManager"/> would change what the refactored
    /// <c>ConversationTurnLifecycleTests</c> exercises.
    /// </summary>
    public ConversationIntake Intake => _intake.Value;

    /// <summary>
    /// Build the L2 path. <paramref name="workDir"/> is explicit rather than defaulted to a temp
    /// directory so a refactored caller can keep its original value byte-for-byte.
    /// </summary>
    public static ConversationHarness Build(
        IAgentExecutor executor,
        IMessageSink? sink = null,
        string workDir = "/tmp", // hygiene-ok: OS temp root, not fixture content
        string provider = "claude",
        string channelId = LoopbackChannelAdapter.DefaultChannelId,
        long ownerUserId = 4242L,
        string ownerToken = "harness-owner-token")
    {
        var registry = new ConversationRegistry();
        var counters = new ConversationEventCounters();
        var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
        var agentOptions = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = workDir,
            Provider = provider,
        });

        sink ??= Substitute.For<IMessageSink>();
        var manager = new TaskManager(
            agentOptions, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            injectionCounter: null, events: bus, telegramConfig: null, counters: counters,
            sink: sink);

        var adapter = new LoopbackChannelAdapter(channelId);
        var pump = new ConversationEventPump(
            bus, registry, counters, [adapter], NullLogger<ConversationEventPump>.Instance);

        var intake = new Lazy<ConversationIntake>(() =>
        {
            var telegramOptions = Options.Create(new TelegramOptions { AllowedUserIds = [ownerUserId] });
            var clientOptions = Options.Create(new ClientChannelOptions
            {
                OwnerPrincipalToken = ownerToken,
                OwnerUserId = ownerUserId,
            });
            var allowlist = new AllowlistHolder(telegramOptions);
            var relay = new GroupRelayService(
                agentOptions, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
            var prompts = new PromptAssembler(executor);
            var commands = new CommandDispatcher(
                manager, executor, agentOptions, NullLogger<CommandDispatcher>.Instance, sink: sink);
            var groupBehavior = new GroupBehavior(
                agentOptions, telegramOptions, allowlist, executor, relay, manager, commands, prompts,
                NullLogger<GroupBehavior>.Instance);
            var binder = new PrincipalBinder(clientOptions, agentOptions, allowlist);
            return new ConversationIntake(
                registry, binder, manager, groupBehavior, bus, NullLogger<ConversationIntake>.Instance);
        });

        return new ConversationHarness(manager, bus, registry, counters, sink, adapter, pump, intake);
    }

    /// <summary>Register a client conversation and mint the identity its submissions carry.</summary>
    public (long Key, ConversationIdentity Identity) OpenClientConversation(
        string conversationId = "c_1", string submissionId = "s_1", string principalId = "p_owner")
    {
        var reference = new ConversationRef(Adapter.ChannelId, conversationId, principalId);
        var key = Registry.Resolve(reference);
        return (key, new ConversationIdentity
        {
            PrincipalId = principalId,
            Role = PrincipalRole.Owner,
            ChannelId = Adapter.ChannelId,
            ConversationId = conversationId,
            SubmissionId = submissionId,
            Attempt = 1,
        });
    }

    /// <summary>Read the bus directly, terminal outboxes first. Does not involve the pump.</summary>
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

    /// <summary>
    /// Drive one pump pass and return what the adapter observed, in delivery order. Repeated calls
    /// accumulate into <see cref="LoopbackChannelAdapter.Delivered"/>, which is what makes the
    /// ordered assertion meaningful across a multi-turn scenario.
    /// </summary>
    public async Task<IReadOnlyList<ConversationEvent>> PumpAsync(CancellationToken ct = default)
    {
        await Pump.DrainOnceAsync(ct);
        return Adapter.Delivered;
    }

    /// <summary>
    /// Wait until the adapter has observed a terminal event for the conversation, pumping as it
    /// goes. <see cref="TaskManager.StartTask"/> returns as soon as dispatch decides; the turn
    /// itself runs fire-and-forget, so a single pump pass would race it.
    ///
    /// <para>Deliberately signal-driven rather than a polled sleep: this waits on the same
    /// <c>ConversationEventBus</c> semaphore the production pump waits on, so there is no fixed
    /// delay to tune, no busy spin, and no <c>Task.Delay</c> or wall-clock assertion anywhere in
    /// the suite (AC15). The caller's token supplies the only timeout.</para>
    /// </summary>
    public async Task<IReadOnlyList<ConversationEvent>> PumpUntilTerminalAsync(
        CancellationToken ct, int expectedTerminals = 1)
    {
        while (true)
        {
            await Pump.DrainOnceAsync(ct);
            var delivered = Adapter.Delivered;
            if (delivered.Count(e => e.IsTerminal) >= expectedTerminals)
                return delivered;
            await Bus.Signal.WaitAsync(ct);
        }
    }
}
