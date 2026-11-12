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
/// Chat is a first-class channel (#530 D11): the deployment's primary human gets the priority lane
/// and steering eligibility a Telegram message from them gets, and a cancel stops the turn.
/// </summary>
public sealed class ConversationChatParityTests : IDisposable
{
    private const string Channel = "client";
    private const string Owner = "owner";

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"fleet-chat-parity-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── the primary human's message is first-class ───────────────────────────

    [Fact]
    public void Only_the_named_principal_is_the_primary_human_and_exactly()
    {
        var intake = Build(primaryHuman: Owner).Intake;

        Assert.True(intake.IsPrimaryHuman(Owner));
        Assert.False(intake.IsPrimaryHuman("OWNER"));
        Assert.False(intake.IsPrimaryHuman("someone-else"));
        Assert.False(intake.IsPrimaryHuman(null));
        Assert.False(intake.IsPrimaryHuman(""));
    }

    [Fact]
    public void With_no_primary_human_named_nobody_is_one()
    {
        var intake = Build(primaryHuman: "").Intake;

        Assert.False(intake.IsPrimaryHuman(Owner));
        Assert.False(intake.IsPrimaryHuman(""));
    }

    [Theory]
    [InlineData(Owner, TaskPriority.PrimaryHuman)]
    [InlineData("someone-else", TaskPriority.Routine)]
    public async Task A_queued_chat_message_takes_the_lane_its_principal_earns(string principal, TaskPriority expected)
    {
        var harness = Build(primaryHuman: Owner);

        // One turn holds the runtime, so the next chat message — in another conversation — queues.
        var busy = harness.Intake.OpenAuthorized(Channel, "01JBUSY0000000000000000001", Owner);
        Assert.Equal(TaskDispatchOutcome.Ran, await harness.Intake.SubmitAsync(busy.RuntimeKey, "first"));
        await WaitUntilAsync(() => harness.Manager.HasRunningTasks(busy.RuntimeKey));

        var other = harness.Intake.OpenAuthorized(Channel, "01JQUEUED00000000000000001", principal);
        Assert.Equal(TaskDispatchOutcome.Queued, await harness.Intake.SubmitAsync(other.RuntimeKey, "second"));

        var queued = Assert.Single(harness.Manager.GetQueueSnapshot(), q => q.ChatId == other.RuntimeKey);
        Assert.Equal(expected, queued.Priority);

        await harness.Intake.CancelAsync(busy.RuntimeKey, CancelScope.All);
        await harness.Intake.CancelAsync(other.RuntimeKey, CancelScope.All);
    }

    [Fact]
    public async Task With_no_primary_human_named_the_owners_message_is_routine()
    {
        var harness = Build(primaryHuman: "");

        var busy = harness.Intake.OpenAuthorized(Channel, "01JBUSY0000000000000000002", Owner);
        await harness.Intake.SubmitAsync(busy.RuntimeKey, "first");
        await WaitUntilAsync(() => harness.Manager.HasRunningTasks(busy.RuntimeKey));

        var other = harness.Intake.OpenAuthorized(Channel, "01JQUEUED00000000000000002", Owner);
        await harness.Intake.SubmitAsync(other.RuntimeKey, "second");

        var queued = Assert.Single(harness.Manager.GetQueueSnapshot(), q => q.ChatId == other.RuntimeKey);
        Assert.Equal(TaskPriority.Routine, queued.Priority);

        await harness.Intake.CancelAsync(busy.RuntimeKey, CancelScope.All);
        await harness.Intake.CancelAsync(other.RuntimeKey, CancelScope.All);
    }

    // ── a cancel stops the turn ──────────────────────────────────────────────

    [Fact]
    public async Task A_cancel_stops_the_running_turn_and_says_so()
    {
        var harness = Build(primaryHuman: Owner);
        var open = harness.Intake.OpenAuthorized(Channel, "01JCANCEL00000000000000001", Owner);

        await harness.Intake.SubmitAsync(open.RuntimeKey, "a long task");
        await WaitUntilAsync(() => harness.Manager.HasRunningTasks(open.RuntimeKey));

        Assert.True(await harness.Intake.CancelAsync(open.RuntimeKey, CancelScope.Current));
        await WaitUntilAsync(() => !harness.Manager.HasRunningTasks(open.RuntimeKey));

        var events = await DrainUntilAsync(harness, e => e.Kind == ConversationEventKind.TurnCanceled);

        var ack = Assert.Single(events, e => e.Kind == ConversationEventKind.ControlAck).PayloadAs<ControlAckPayload>()!;
        Assert.True(ack.Accepted);
        Assert.True(ack.HadRunningTask);
        Assert.Contains(events, e => e.Kind == ConversationEventKind.TurnCanceled);
    }

    [Fact]
    public async Task A_cancel_in_one_conversation_never_stops_another()
    {
        var harness = Build(primaryHuman: Owner);
        var mine = harness.Intake.OpenAuthorized(Channel, "01JMINE000000000000000001", Owner);
        var theirs = harness.Intake.OpenAuthorized(Channel, "01JTHEIRS0000000000000001", Owner);

        await harness.Intake.SubmitAsync(theirs.RuntimeKey, "their task");
        await WaitUntilAsync(() => harness.Manager.HasRunningTasks(theirs.RuntimeKey));

        Assert.False(await harness.Intake.CancelAsync(mine.RuntimeKey, CancelScope.All));

        // Still running a moment later: the cancel was scoped to its own conversation.
        await Task.Delay(200);
        Assert.True(harness.Manager.HasRunningTasks(theirs.RuntimeKey));

        await harness.Intake.CancelAsync(theirs.RuntimeKey, CancelScope.All);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private sealed record Harness(ConversationIntake Intake, TaskManager Manager, ConversationEventBus Bus);

    private Harness Build(string primaryHuman)
    {
        Directory.CreateDirectory(_workDir);

        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "example-agent", Role = "r", WorkDir = _workDir, Provider = "claude",
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var clientOpts = Options.Create(new ClientChannelOptions { OwnerPrincipalToken = "t", OwnerUserId = 1 });
        var conversationsOpts = Options.Create(new ConversationsOptions { PrimaryHumanPrincipalId = primaryHuman });

        // Every turn runs until it is cancelled, so a test controls exactly what is running.
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(call => UntilCancelled(call.ArgAt<CancellationToken>(3)));

        var registry = new ConversationRegistry();
        var counters = new ConversationEventCounters();
        var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        var manager = new TaskManager(agentOpts, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, injectionCounter: null, events: bus,
            telegramConfig: null, counters: counters, sink: Substitute.For<IMessageSink>());
        var commands = new CommandDispatcher(manager, executor, agentOpts, NullLogger<CommandDispatcher>.Instance,
            sink: Substitute.For<IMessageSink>());
        var groupBehavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, new PromptAssembler(executor), NullLogger<GroupBehavior>.Instance);
        var binder = new PrincipalBinder(clientOpts, agentOpts, allowlist);

        var intake = new ConversationIntake(registry, binder, manager, groupBehavior, bus,
            NullLogger<ConversationIntake>.Instance, conversationsOpts);

        return new Harness(intake, manager, bus);
    }

    private static async IAsyncEnumerable<AgentProgress> UntilCancelled([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, deadline.Token);
    }

    private static async Task<List<ConversationEvent>> DrainUntilAsync(Harness harness, Func<ConversationEvent, bool> until)
    {
        var events = new List<ConversationEvent>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!events.Any(until))
        {
            foreach (var reader in harness.Bus.TerminalReaders.ToList())
                while (reader.TryRead(out var pending)) events.Add(pending.Event);
            while (harness.Bus.ProgressReader.TryRead(out var pending)) events.Add(pending.Event);

            if (!events.Any(until)) await Task.Delay(10, deadline.Token);
        }

        return events;
    }
}
