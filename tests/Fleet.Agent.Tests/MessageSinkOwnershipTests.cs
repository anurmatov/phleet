using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// #277 D-1 / D-2 / D-2a: the sink seam, the split of the completion effects, and the single
/// source of <c>telegramMessageId</c>.
///
/// The property these cover together is that no first-party path depends on
/// <c>AgentTransport</c> being constructed. Every test here builds the runtime WITHOUT one.
/// </summary>
public class MessageSinkOwnershipTests
{
    private static IOptions<AgentOptions> AgentOpts() => Options.Create(new AgentOptions
    {
        Name = "test", Role = "test", WorkDir = Path.GetTempPath(), Provider = "claude",
    });

    // ── D-1: the holder, the null sink, and the absence of the transport ─────────────────

    /// <summary>
    /// T9 / P1. The negative control for this is the pre-#277 tree itself: with a settable
    /// <c>Sink</c> property left at <c>null!</c>, this call threw <see cref="NullReferenceException"/>.
    /// </summary>
    [Fact]
    public async Task UnattachedHolder_SendsAreNoOpsRatherThanNullReference()
    {
        var counter = new SinkSuppressionCounter();
        var holder = new MessageSinkHolder(counter);

        await holder.SendTextAsync(42, "hello");
        await holder.SendTypingAsync(42);
        await holder.SendHtmlTextAsync(42, "<b>hi</b>");
        await holder.SendPhotoAsync(42, "/tmp/x.png", null);

        Assert.False(holder.IsAttached);
        Assert.Equal(4, counter.GetSuppressed(SinkSuppressionCounter.ReasonNullSink));
    }

    /// <summary>
    /// The suppression must be COUNTED, not merely silent. An unobserved no-op sink is
    /// indistinguishable from a healthy one, which is the defect class #277 §9 exists to close.
    /// </summary>
    [Fact]
    public async Task NullSink_CountsEverySuppressedSend()
    {
        var counter = new SinkSuppressionCounter();
        var sink = new NullMessageSink(counter);

        await sink.SendTextAsync(1, "a");

        Assert.Equal(1, counter.GetSuppressed(SinkSuppressionCounter.ReasonNullSink));
    }

    [Fact]
    public async Task Attach_RoutesSubsequentSendsToTheRealSink()
    {
        var counter = new SinkSuppressionCounter();
        var holder = new MessageSinkHolder(counter);
        var real = Substitute.For<IMessageSink>();

        holder.Attach(real);
        await holder.SendTextAsync(7, "routed");

        Assert.True(holder.IsAttached);
        await real.Received(1).SendTextAsync(7, "routed", Arg.Any<CancellationToken>());
        Assert.Equal(0, counter.GetSuppressed(SinkSuppressionCounter.ReasonNullSink));
    }

    [Fact]
    public void Attach_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => new MessageSinkHolder().Attach(null!));

    /// <summary>
    /// The holder is a holder, not a router (#277 D-1, MUST NOT 3). It must not inspect the
    /// conversation key: a reserved-band key is forwarded exactly like a Telegram one, because the
    /// four reserved-key guards live in the transport's render path and stay there.
    /// </summary>
    [Fact]
    public async Task Holder_DoesNotInspectTheRuntimeKey()
    {
        var holder = new MessageSinkHolder();
        var real = Substitute.For<IMessageSink>();
        holder.Attach(real);

        var reservedKey = ConversationRegistry.ReservedBandStart + 5;
        await holder.SendTextAsync(reservedKey, "forwarded");

        await real.Received(1).SendTextAsync(reservedKey, "forwarded", Arg.Any<CancellationToken>());
    }

    // ── D-2a: telegramMessageId has exactly one source ───────────────────────────────────

    /// <summary>
    /// T8c. A Telegram-free host buffers with <c>0</c> — the same value a Telegram chat that has
    /// not been sent to yet already produces, and already the default of
    /// <c>GroupBehavior.BufferBotResponse</c>. Behaviour is unchanged across the move.
    /// </summary>
    [Fact]
    public void UnattachedHolder_YieldsZeroLastSentMessageId() =>
        Assert.Equal(0L, new MessageSinkHolder().GetLastSentMessageId(1234));

    /// <summary>
    /// The interface default is what makes <see cref="NullMessageSink"/> and every non-Telegram
    /// sink honest without inventing an id. Mutating it to a non-zero value is a listed mutation
    /// in #277 §10 precisely because it would silently change persisted context.
    /// </summary>
    [Fact]
    public void NullSink_InheritsTheZeroDefault() =>
        // Through the interface: a default interface method is not callable on the concrete type,
        // which is exactly what keeps NullMessageSink from having to invent an id of its own.
        Assert.Equal(0L, ((IMessageSink)new NullMessageSink()).GetLastSentMessageId(1234));

    [Fact]
    public void AttachedHolder_ForwardsTheRealLastSentMessageId()
    {
        var holder = new MessageSinkHolder();
        var real = Substitute.For<IMessageSink>();
        real.GetLastSentMessageId(99).Returns(4242L);

        holder.Attach(real);

        Assert.Equal(4242L, holder.GetLastSentMessageId(99));
    }

    // ── D-2: one owner per effect ────────────────────────────────────────────────────────

    private sealed record Runtime(
        TaskManager Manager,
        GroupBehavior Behavior,
        GroupRelayService Relay,
        MessageSinkHolder Holder,
        string WorkDir);

    private static Runtime BuildRuntime()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"fleet-sink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "test", Role = "test", WorkDir = workDir, Provider = "claude",
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var rabbitOpts = Options.Create(new RabbitMqOptions());
        var executor = Substitute.For<IAgentExecutor>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var holder = new MessageSinkHolder();
        var manager = new TaskManager(agentOpts, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, sink: holder);
        var commands = new CommandDispatcher(manager, executor, agentOpts,
            NullLogger<CommandDispatcher>.Instance, sink: holder);
        var behavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, new PromptAssembler(executor),
            NullLogger<GroupBehavior>.Instance, sink: holder);

        return new Runtime(manager, behavior, relay, holder, workDir);
    }

    /// <summary>
    /// T8. One completion, exactly one <c>BufferBotResponse</c>. The failure this guards is the
    /// one #277 MUST NOT 4 names: after splitting <c>OnTaskCompleted</c> across two subscribers, a
    /// component left subscribed in both places buffers the same text twice.
    /// </summary>
    [Fact]
    public void OneCompletion_BuffersExactlyOnce()
    {
        var rt = BuildRuntime();
        try
        {
            using var buffer = new CompletionContextBuffer(rt.Manager, rt.Behavior, rt.Holder);

            rt.Manager.RaiseTaskCompletedForTest(chatId: 500, result: "the answer");

            var entries = rt.Behavior.GetGroupBuffer(500).GetEntries();
            Assert.Single(entries, e => e.Text.Contains("the answer", StringComparison.Ordinal));
        }
        finally { TryDelete(rt.WorkDir); }
    }

    /// <summary>
    /// #277 D-2a: the buffered id comes from the sink, through the holder, and from nowhere else.
    /// </summary>
    [Fact]
    public void ContextBuffer_ReadsTelegramMessageIdThroughTheSink()
    {
        var rt = BuildRuntime();
        try
        {
            var real = Substitute.For<IMessageSink>();
            real.GetLastSentMessageId(501).Returns(777L);
            rt.Holder.Attach(real);

            using var buffer = new CompletionContextBuffer(rt.Manager, rt.Behavior, rt.Holder);
            rt.Manager.RaiseTaskCompletedForTest(chatId: 501, result: "answer");

            real.Received(1).GetLastSentMessageId(501);
            // and the value actually lands in the persisted entry, not just in the call.
            var entry = Assert.Single(rt.Behavior.GetGroupBuffer(501).GetEntries());
            Assert.Equal(777L, entry.TelegramMessageId);
        }
        finally { TryDelete(rt.WorkDir); }
    }

    /// <summary>An empty result must not create a buffer entry — unchanged from the original site.</summary>
    [Fact]
    public void EmptyResult_IsNotBuffered()
    {
        var rt = BuildRuntime();
        try
        {
            using var buffer = new CompletionContextBuffer(rt.Manager, rt.Behavior, rt.Holder);
            rt.Manager.RaiseTaskCompletedForTest(chatId: 502, result: "");

            Assert.Empty(rt.Behavior.GetGroupBuffer(502).GetEntries());
        }
        finally { TryDelete(rt.WorkDir); }
    }

    /// <summary>
    /// Disposal detaches. Without it a disposed component keeps buffering into a dead instance —
    /// the symmetry the original constructor-time attachment already maintained.
    /// </summary>
    [Fact]
    public void DisposingTheBuffer_Detaches()
    {
        var rt = BuildRuntime();
        try
        {
            var buffer = new CompletionContextBuffer(rt.Manager, rt.Behavior, rt.Holder);
            Assert.True(buffer.IsAttached);

            buffer.Dispose();

            Assert.False(buffer.IsAttached);
            Assert.False(rt.Manager.HasCompletionSubscriber);
        }
        finally { TryDelete(rt.WorkDir); }
    }

    [Fact]
    public void DisposingThePublisher_Detaches()
    {
        var rt = BuildRuntime();
        try
        {
            var publisher = new RelayCompletionPublisher(rt.Manager, rt.Relay,
                NullLogger<RelayCompletionPublisher>.Instance);
            Assert.True(publisher.IsAttached);

            publisher.Dispose();

            Assert.False(publisher.IsAttached);
        }
        finally { TryDelete(rt.WorkDir); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
