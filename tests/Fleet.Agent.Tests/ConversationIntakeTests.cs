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
/// AC31, AC42, AC46–48 and Constraints 8/9/25: the inbound seam — prompt assembly, the command
/// surface staying unreachable, attachment rejection, cancel acknowledgement, and the guarantee
/// that client text never reaches disk.
/// </summary>
public class ConversationIntakeTests : IDisposable
{
    private const string ClientChannel = "example-adapter";
    private const string Token = "operator-set-token";
    private const long OwnerUserId = 4242L;

    private readonly string _historyDir = Path.Combine(Path.GetTempPath(), $"fleet-intake-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_historyDir))
            Directory.Delete(_historyDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class Harness
    {
        public required ConversationIntake Intake { get; init; }
        public required ConversationEventBus Bus { get; init; }
        public required ConversationRegistry Registry { get; init; }
        public required GroupBehavior GroupBehavior { get; init; }
        public required CommandDispatcher Commands { get; init; }
        public required IAgentExecutor Executor { get; init; }
        public required string HistoryPath { get; init; }

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

    private Harness Build(string token = Token, long ownerUserId = OwnerUserId, bool allowlisted = true)
    {
        Directory.CreateDirectory(_historyDir);

        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1", Role = "r", WorkDir = _historyDir, Provider = "claude",
        });
        var telegramOpts = Options.Create(new TelegramOptions
        {
            AllowedUserIds = allowlisted ? [ownerUserId] : [],
        });
        var clientOpts = Options.Create(new ClientChannelOptions
        {
            OwnerPrincipalToken = token, OwnerUserId = ownerUserId,
        });
        var rabbitOpts = Options.Create(new RabbitMqOptions());

        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Yield("answer"));

        var registry = new ConversationRegistry();
        var counters = new ConversationEventCounters();
        var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var manager = new TaskManager(agentOpts, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, injectionCounter: null, events: bus,
            telegramConfig: null, counters: counters, sink: Substitute.For<IMessageSink>());
        var prompts = new PromptAssembler(executor);
        var commands = new CommandDispatcher(manager, executor, agentOpts, NullLogger<CommandDispatcher>.Instance, sink: Substitute.For<IMessageSink>());
        var groupBehavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, prompts, NullLogger<GroupBehavior>.Instance);
        var binder = new PrincipalBinder(clientOpts, agentOpts, allowlist);

        var intake = new ConversationIntake(registry, binder, manager, groupBehavior, bus,
            NullLogger<ConversationIntake>.Instance);

        return new Harness
        {
            Intake = intake, Bus = bus, Registry = registry, GroupBehavior = groupBehavior,
            Commands = commands, Executor = executor,
            HistoryPath = Path.Combine(_historyDir, ".fleet", "chat-history.json"),
        };
    }

    /// <summary>
    /// StartTask returns as soon as dispatch decides; ProcessTask runs fire-and-forget, so the
    /// executor has not necessarily been called yet when SubmitAsync returns.
    /// </summary>
    private static async Task<string> WaitForPromptAsync(Harness harness)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var call = harness.Executor.ReceivedCalls()
                .FirstOrDefault(c => c.GetMethodInfo().Name == nameof(IAgentExecutor.ExecuteAsync));
            if (call is not null)
                return (string)call.GetArguments()[0]!;
            await Task.Delay(10, cts.Token);
        }
    }

    private static async IAsyncEnumerable<AgentProgress> Yield(string result)
    {
        yield return new AgentProgress { Summary = "r", EventType = "result", FinalResult = result };
        await Task.CompletedTask;
    }

    private static ConversationOpenPayload OpenPayload(
        string value = Token, string scheme = PrincipalBinding.LegacyOwnerScheme,
        PrincipalRole role = PrincipalRole.Owner, string channelId = ClientChannel) => new()
    {
        ChannelId = channelId,
        PrincipalBinding = new PrincipalBinding { Scheme = scheme, Value = value },
        Role = role,
    };

    // ── AC42: open and its rejections ─────────────────────────────────────────

    [Fact]
    public void Open_SucceedsWithAValidBinding()
    {
        var harness = Build();

        var result = harness.Intake.Open(OpenPayload());

        Assert.True(result.Success);
        Assert.True(ConversationRegistry.IsReservedKey(result.RuntimeKey));
        Assert.StartsWith("p_", result.PrincipalId);
    }

    /// <summary>AC42: a rejected open must create NO registry entry.</summary>
    [Theory]
    [InlineData("wrong-token", PrincipalBinding.LegacyOwnerScheme, PrincipalRole.Owner)]
    [InlineData(Token, "device", PrincipalRole.Owner)]
    public void Open_RejectedBinding_CreatesNoRegistryEntry(string value, string scheme, PrincipalRole role)
    {
        var harness = Build();

        var result = harness.Intake.Open(OpenPayload(value, scheme, role));

        Assert.False(result.Success);
        Assert.Equal(ProtocolErrorCode.Unauthorized, result.Error);
        Assert.Equal(0, result.RuntimeKey);
        Assert.Null(harness.Registry.Lookup(ConversationRegistry.ReservedBandStart));
    }

    [Fact]
    public void Open_WithNonOwnerRole_IsRejectedAsUnsupportedRole()
    {
        var harness = Build();

        var result = harness.Intake.Open(OpenPayload(role: PrincipalRole.Member));

        Assert.False(result.Success);
        Assert.Equal(ProtocolErrorCode.UnsupportedRole, result.Error);
    }

    [Fact]
    public void Open_WithAnEmptyToken_IsRejected()
    {
        var harness = Build(token: "");

        Assert.False(harness.Intake.Open(OpenPayload()).Success);
    }

    [Fact]
    public void Open_WithADeAllowlistedOwner_IsRejected()
    {
        var harness = Build(allowlisted: false);

        Assert.False(harness.Intake.Open(OpenPayload()).Success);
    }

    /// <summary>
    /// A client must not be able to claim a runtime-owned channel — that would be asking to be
    /// routed through the bot client.
    /// </summary>
    [Theory]
    [InlineData("telegram")]
    [InlineData("relay")]
    public void Open_ClaimingARuntimeOwnedChannel_IsRejected(string channelId)
    {
        var harness = Build();

        Assert.False(harness.Intake.Open(OpenPayload(channelId: channelId)).Success);
    }

    // ── AC31: prompt assembly ─────────────────────────────────────────────────

    /// <summary>
    /// AC31. A client prompt must carry the channel anchor, never a Telegram DM anchor, and must
    /// not fabricate a [telegram_message_id:] tag or a reply block.
    /// </summary>
    [Fact]
    public async Task ClientSubmission_ProducesAChannelAnchorAndNoTelegramTags()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        await harness.Intake.SubmitAsync(open.RuntimeKey, "hello there", replyToEventId: "e_123");

        var prompt = await WaitForPromptAsync(harness);

        Assert.Contains($"[channel: {ClientChannel} conversation={open.ConversationId}]", prompt);
        Assert.DoesNotContain("[channel: dm", prompt);
        Assert.DoesNotContain("telegram_message_id", prompt);
        // replyToEventId is an EVENT id, not text — Phase 0 carries it in the identity for the
        // client's own threading and does not resolve it into the prompt.
        Assert.DoesNotContain("Replying to", prompt);
        Assert.Contains("hello there", prompt);
    }

    // ── AC47 / Constraint 8: the command surface stays unreachable ─────────────

    /// <summary>
    /// AC47. The command surface includes a global kill switch (/stop) and a raw executor
    /// passthrough (/run). Neither may become reachable from a channel that cannot authenticate
    /// its caller, so slash-prefixed text is conversation content, verbatim.
    /// </summary>
    [Theory]
    [InlineData("/stop")]
    [InlineData("/run rm -rf /")]
    [InlineData("/reset")]
    [InlineData("/new")]
    [InlineData("/status")]
    public async Task SlashPrefixedText_IsPassedThroughAsContentAndNeverDispatched(string text)
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        var outcome = await harness.Intake.SubmitAsync(open.RuntimeKey, text);

        // The text reached the EXECUTOR as ordinary content rather than being interpreted.
        Assert.Equal(TaskDispatchOutcome.Ran, outcome);
        Assert.Contains(text, await WaitForPromptAsync(harness));
    }

    /// <summary>
    /// The structural half of Constraint 8, and the stronger one: the intake has no
    /// CommandDispatcher dependency at all, so no client path CAN reach the command surface.
    /// A behavioural "was never called" assertion would only prove it did not happen this time;
    /// this proves it cannot be wired up without changing the constructor.
    /// </summary>
    [Fact]
    public void Intake_HasNoCommandDispatcherDependency()
    {
        var parameterTypes = typeof(ConversationIntake)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(CommandDispatcher), parameterTypes);
        Assert.DoesNotContain(typeof(MessageRouter), parameterTypes);
    }

    // ── AC46 / Constraint 9: attachments ──────────────────────────────────────

    /// <summary>
    /// AC46, as amended by #308. A submission carrying attachments for which NOTHING was fetched is
    /// rejected outright — it must not silently proceed as text-only, because a client that attached
    /// a photo and got a text-only answer has been lied to.
    /// </summary>
    /// <remarks>
    /// Before #308 this was every attachment array. Now the caller fetches the bytes and hands them
    /// in as <c>images</c>; an array with nothing behind it is the case that remains a refusal. A
    /// PARTIAL fetch is a different thing and is reported as a notice, because only the caller knows
    /// how many were asked for.
    /// </remarks>
    [Fact]
    public async Task SubmissionWithAttachmentsAndNoBytes_IsRejectedAndCreatesNoTurn()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        var outcome = await harness.Intake.SubmitAsync(open.RuntimeKey, "see attached", attachments:
        [
            new AttachmentDescriptor { AttachmentId = "a_1", Kind = AttachmentKind.Image },
        ]);

        Assert.Null(outcome);
        var rejected = Assert.Single(harness.Drain(), e => e.Kind == ConversationEventKind.ProtocolRejected);
        Assert.Equal(ProtocolErrorCode.UnsupportedAttachments,
            rejected.PayloadAs<ProtocolRejectedPayload>()!.Code);
        Assert.Empty(harness.Executor.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAgentExecutor.ExecuteAsync)));
    }

    /// <summary>An empty attachments array is not an attachment, so it proceeds normally.</summary>
    [Fact]
    public async Task SubmissionWithAnEmptyAttachmentsArray_Proceeds()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        var outcome = await harness.Intake.SubmitAsync(open.RuntimeKey, "hello", attachments: []);

        Assert.Equal(TaskDispatchOutcome.Ran, outcome);
    }

    /// <summary>
    /// #308: a submission whose attachments WERE fetched reaches the executor, and the bytes travel
    /// through the <c>images</c> parameter every provider is already wired for.
    /// </summary>
    /// <remarks>
    /// This is the assertion that makes "no executor changes" true rather than asserted: the image
    /// arrives on the same argument a Telegram photo does, so the Claude content-block path, the
    /// Codex <c>local_image</c> path and the Gemini <c>@path</c> resolver all pick it up unmodified.
    /// </remarks>
    [Fact]
    public async Task SubmissionWithFetchedBytes_ReachesTheExecutorAsAnImage()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        var image = new MessageImage([1, 2, 3], "image/png") { FilePath = "/workspace/attachments/x.png" };

        var outcome = await harness.Intake.SubmitAsync(
            open.RuntimeKey, "what is this?",
            attachments: [new AttachmentDescriptor { AttachmentId = "a_1", Kind = AttachmentKind.Image }],
            images: [image]);

        Assert.Equal(TaskDispatchOutcome.Ran, outcome);

        await WaitForPromptAsync(harness);

        var call = harness.Executor.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(IAgentExecutor.ExecuteAsync));

        var images = call.GetArguments()
            .OfType<IReadOnlyList<MessageImage>>()
            .SingleOrDefault();

        Assert.NotNull(images);
        Assert.Same(image, Assert.Single(images!));
    }

    /// <summary>
    /// #308 AC-29: an image that could not be fetched becomes a notice naming a COUNT — never a
    /// path, never an id, never a transport reason.
    /// </summary>
    /// <remarks>
    /// The turn is not the subject here; the notice is. An image must never abandon a turn, and it
    /// must equally never be dropped in silence, because a silent drop produces an answer that reads
    /// as though no image was sent.
    /// </remarks>
    [Fact]
    public void AnUnavailableAttachment_PublishesANoticeCarryingNoIdentifier()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        harness.Intake.PublishAttachmentNotice(open.RuntimeKey, unavailable: 2);

        var notice = Assert.Single(harness.Drain(), e => e.Kind == ConversationEventKind.TurnNotice);
        var text = notice.PayloadAs<TurnNoticePayload>()!.Text;

        Assert.Contains("2 images", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("attachment_", text, StringComparison.Ordinal);
    }

    /// <summary>Nothing unavailable is nothing to say.</summary>
    [Fact]
    public void AnAttachmentNoticeForZeroUnavailable_PublishesNothing()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        harness.Intake.PublishAttachmentNotice(open.RuntimeKey, unavailable: 0);

        Assert.Empty(harness.Drain());
    }

    // ── AC54: inbound bound ───────────────────────────────────────────────────

    [Fact]
    public async Task OversizeInboundText_IsRejectedAsPayloadTooLarge()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        var outcome = await harness.Intake.SubmitAsync(
            open.RuntimeKey, new string('a', ProtocolLimits.MaxInboundTextBytes + 1));

        Assert.Null(outcome);
        var rejected = Assert.Single(harness.Drain(), e => e.Kind == ConversationEventKind.ProtocolRejected);
        Assert.Equal(ProtocolErrorCode.PayloadTooLarge, rejected.PayloadAs<ProtocolRejectedPayload>()!.Code);
    }

    [Fact]
    public async Task InboundTextExactlyAtTheBound_IsAccepted()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        var outcome = await harness.Intake.SubmitAsync(
            open.RuntimeKey, new string('a', ProtocolLimits.MaxInboundTextBytes));

        Assert.Equal(TaskDispatchOutcome.Ran, outcome);
    }

    /// <summary>
    /// A submission against a key that was never opened creates no turn.
    ///
    /// Note what CANNOT happen here: the rejection event itself has nowhere to go, because the
    /// conversation is exactly the thing that is not registered, so there is no owning channel to
    /// route it to. The bus drops it and counts it rather than inventing a destination. A client
    /// learns about this from its own open failing, not from an event on a conversation it never
    /// successfully opened.
    /// </summary>
    [Fact]
    public async Task SubmissionToAnUnknownConversation_CreatesNoTurn()
    {
        var harness = Build();

        var outcome = await harness.Intake.SubmitAsync(ConversationRegistry.ReservedBandStart + 999, "hi");

        Assert.Null(outcome);
        Assert.Empty(harness.Drain());
        Assert.Empty(harness.Executor.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAgentExecutor.ExecuteAsync)));
    }

    // ── AC48: cancel acknowledgement ──────────────────────────────────────────

    /// <summary>
    /// AC48. When nothing was running, no turn.canceled follows and the ack is the whole story —
    /// which is exactly why hadRunningTask is on the wire rather than left for the client to infer
    /// from a timeout.
    /// </summary>
    [Fact]
    public async Task CancelWithNoRunningTask_AcksWithHadRunningTaskFalseAndNoTurnCanceled()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());

        var hadRunning = await harness.Intake.CancelAsync(open.RuntimeKey, CancelScope.Current);

        Assert.False(hadRunning);
        var events = harness.Drain();
        var ack = Assert.Single(events, e => e.Kind == ConversationEventKind.ControlAck);
        var payload = ack.PayloadAs<ControlAckPayload>()!;
        Assert.True(payload.Accepted);
        Assert.False(payload.HadRunningTask);
        Assert.DoesNotContain(events, e => e.Kind == ConversationEventKind.TurnCanceled);
    }

    // ── AC32 / Constraint 25: client text never reaches disk ──────────────────

    /// <summary>
    /// AC32 and Constraint 25. The SaveBuffers filter is mandatory rather than belt-and-braces:
    /// BufferBotResponse calls SaveBuffers() on every completion, so without it client text would
    /// be persisted through the completion path regardless of what the intake does.
    /// </summary>
    [Fact]
    public async Task ClientText_IsNeverWrittenToTheOnDiskHistory()
    {
        var harness = Build();
        var open = harness.Intake.Open(OpenPayload());
        const string secretish = "my private client message";

        await harness.Intake.SubmitAsync(open.RuntimeKey, secretish);

        // Force the persistence path the completion handler uses.
        harness.GroupBehavior.BufferBotResponse(open.RuntimeKey, "the reply");

        if (File.Exists(harness.HistoryPath))
        {
            var onDisk = await File.ReadAllTextAsync(harness.HistoryPath);
            Assert.DoesNotContain(secretish, onDisk);
            Assert.DoesNotContain(open.RuntimeKey.ToString(), onDisk);
        }
    }

    /// <summary>
    /// The negative control for the test above: a Telegram conversation in the same process is
    /// still persisted normally, so the filter is scoped to reserved keys rather than having
    /// disabled persistence outright.
    /// </summary>
    [Fact]
    public void TelegramConversations_AreStillPersisted()
    {
        var harness = Build();

        harness.GroupBehavior.AddAndPersist(555L, "user", "an ordinary telegram message", replyTo: null);

        Assert.True(File.Exists(harness.HistoryPath));
        var onDisk = File.ReadAllText(harness.HistoryPath);
        Assert.Contains("an ordinary telegram message", onDisk);
        Assert.Contains("555", onDisk);
    }
}
