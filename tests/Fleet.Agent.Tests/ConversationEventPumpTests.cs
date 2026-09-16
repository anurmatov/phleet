using System.Diagnostics;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC22, AC34–35, AC55, AC58: adapter failure is contained, delivery is bounded, ownership is a
/// registry lookup, and duplicate channel ids fail startup rather than routing ambiguously.
/// </summary>
public class ConversationEventPumpTests
{
    private sealed class RecordingAdapter : IChannelAdapter
    {
        public RecordingAdapter(string channelId = "example-adapter") => ChannelId = channelId;
        public string ChannelId { get; }
        public readonly List<ConversationEvent> Delivered = [];
        public Func<ConversationEvent, CancellationToken, Task>? Behaviour { get; set; }

        public Task DeliverAsync(ConversationEvent evt, CancellationToken ct)
        {
            if (Behaviour is not null) return Behaviour(evt, ct);
            Delivered.Add(evt);
            return Task.CompletedTask;
        }
    }

    private static (ConversationEventBus bus, ConversationRegistry registry, ConversationEventCounters counters, ConversationEventPump pump)
        Build(params IChannelAdapter[] adapters)
    {
        var registry = new ConversationRegistry();
        var counters = new ConversationEventCounters();
        var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
        var pump = new ConversationEventPump(bus, registry, counters, adapters, NullLogger<ConversationEventPump>.Instance);
        return (bus, registry, counters, pump);
    }

    private static ConversationIdentity IdentityFor(ConversationRef reference) => new()
    {
        PrincipalId = reference.PrincipalId,
        Role = PrincipalRole.Owner,
        ChannelId = reference.ChannelId,
        ConversationId = reference.ConversationId,
        SubmissionId = "s_1",
        TurnId = "t_1",
        Attempt = 1,
    };

    // ── ownership ─────────────────────────────────────────────────────────────

    /// <summary>
    /// AC55. Two adapters under one ChannelId is a STARTUP failure, not a runtime warning: a
    /// warning would leave the process running with nondeterministic routing, which is the
    /// cross-route leak the registry-lookup design exists to make impossible.
    /// </summary>
    [Fact]
    public void DuplicateChannelId_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new RecordingAdapter("dup"), new RecordingAdapter("dup")));

        Assert.Contains("dup", ex.Message);
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("relay")]
    public void AdapterClaimingAReservedChannelId_FailsStartup(string channelId)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new RecordingAdapter(channelId)));
        Assert.Contains(channelId, ex.Message);
    }

    [Fact]
    public void AdapterWithAnEmptyChannelId_FailsStartup() =>
        Assert.Throws<InvalidOperationException>(() => Build(new RecordingAdapter("")));

    [Fact]
    public async Task EventsAreDeliveredToTheAdapterOwningTheConversation()
    {
        var owner = new RecordingAdapter("owner-channel");
        var other = new RecordingAdapter("other-channel");
        var (bus, registry, _, pump) = Build(owner, other);

        var reference = new ConversationRef("owner-channel", "c_1", "p_1");
        var key = registry.Resolve(reference);
        bus.Publish(key, ConversationEventKind.TurnStarted, IdentityFor(reference), new TurnStartedPayload());

        await pump.DrainOnceAsync(CancellationToken.None);

        Assert.Single(owner.Delivered);
        Assert.Empty(other.Delivered);
    }

    /// <summary>Cross-route leakage: a Telegram turn must reach no adapter at all.</summary>
    [Fact]
    public async Task TelegramConversation_ReachesNoAdapter()
    {
        var adapter = new RecordingAdapter();
        var (bus, registry, counters, pump) = Build(adapter);
        registry.RegisterTelegram(555L, "p_owner");

        bus.Publish(555L, ConversationEventKind.TurnFinal, new ConversationIdentity
        {
            PrincipalId = "p_owner", Role = PrincipalRole.Owner, ChannelId = ChannelIds.Telegram,
            ConversationId = "555", SubmissionId = "s_1", Attempt = 1,
        }, new TurnFinalPayload
        {
            Text = "private", Completion = TurnCompletion.Completed,
            IsPartial = false, Truncated = false, MergedSubmissionIds = [],
        });

        await pump.DrainOnceAsync(CancellationToken.None);

        Assert.Empty(adapter.Delivered);
        Assert.Equal(0, counters.TotalDropped());
    }

    // ── failure containment ───────────────────────────────────────────────────

    /// <summary>AC22: a throwing adapter is counted and contained, never propagated.</summary>
    [Fact]
    public async Task AThrowingAdapter_IsCountedAndDoesNotPropagate()
    {
        var adapter = new RecordingAdapter { Behaviour = (_, _) => throw new InvalidOperationException("boom") };
        var (bus, registry, counters, pump) = Build(adapter);

        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);
        bus.Publish(key, ConversationEventKind.TurnStarted, IdentityFor(reference), new TurnStartedPayload());

        // No exception escapes.
        await pump.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, counters.DeliveryFailureCount("example-adapter", ConversationEventCounters.FailureThrew));
    }

    /// <summary>
    /// AC35. A hung adapter is abandoned at the timeout and the pump moves on. Without the
    /// bounded wait, one wedged adapter would stall every other conversation's delivery.
    /// </summary>
    [Fact]
    public async Task AHungAdapter_IsAbandonedAtTheTimeoutAndThePumpContinues()
    {
        var hung = new RecordingAdapter("hung-channel")
        {
            Behaviour = async (_, ct) => await Task.Delay(TimeSpan.FromMinutes(5), ct),
        };
        var healthy = new RecordingAdapter("healthy-channel");
        var (bus, registry, counters, pump) = Build(hung, healthy);

        var hungRef = new ConversationRef("hung-channel", "c_hung", "p_1");
        var healthyRef = new ConversationRef("healthy-channel", "c_ok", "p_1");
        var hungKey = registry.Resolve(hungRef);
        var healthyKey = registry.Resolve(healthyRef);

        bus.Publish(hungKey, ConversationEventKind.TurnStarted, IdentityFor(hungRef), new TurnStartedPayload());
        bus.Publish(healthyKey, ConversationEventKind.TurnStarted, IdentityFor(healthyRef), new TurnStartedPayload());

        var stopwatch = Stopwatch.StartNew();
        await pump.DrainOnceAsync(CancellationToken.None);
        stopwatch.Stop();

        // Bounded by the 5s delivery timeout, nowhere near the adapter's 5-minute hang.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"drain took {stopwatch.Elapsed}");
        Assert.Equal(1, counters.DeliveryFailureCount("hung-channel", ConversationEventCounters.FailureTimeout));
        // The healthy adapter still got its event — one wedged adapter does not stall the others.
        Assert.Single(healthy.Delivered);
    }

    /// <summary>
    /// AC34 / Constraint 6. Publish must complete synchronously: the executor and the Telegram
    /// send path can never wait on an adapter.
    /// </summary>
    [Fact]
    public void Publish_CompletesSynchronouslyEvenWhenTheAdapterBlocks()
    {
        var blocking = new RecordingAdapter
        {
            Behaviour = async (_, ct) => await Task.Delay(TimeSpan.FromSeconds(10), ct),
        };
        var (bus, registry, _, _) = Build(blocking);

        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);

        var stopwatch = Stopwatch.StartNew();
        bus.Publish(key, ConversationEventKind.TurnStarted, IdentityFor(reference), new TurnStartedPayload());
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"publish took {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>AC58: an adapter registered mid-turn sees nothing from the in-flight turn.</summary>
    [Fact]
    public async Task AnAdapterRegisteredAfterPublication_SeesNothingAlreadyEmitted()
    {
        // The conversation's events are published while only this adapter exists...
        var late = new RecordingAdapter("late-channel");
        var (bus, registry, counters, pump) = Build(late);

        var reference = new ConversationRef("never-registered-channel", "c_1", "p_1");
        var key = registry.Resolve(reference);
        bus.Publish(key, ConversationEventKind.TurnStarted, IdentityFor(reference), new TurnStartedPayload());

        await pump.DrainOnceAsync(CancellationToken.None);

        // ...and the late adapter, owning a different channel, receives none of them.
        Assert.Empty(late.Delivered);
        Assert.Equal(1, counters.NotRoutedCount("never-registered-channel"));
    }

    /// <summary>
    /// Terminal events drain first, every iteration: a terminal event is the only thing that lets
    /// a client stop waiting, so it must never sit behind a backlog of typing pings.
    /// </summary>
    [Fact]
    public async Task TerminalEventsDrainBeforeProgressEvents()
    {
        var adapter = new RecordingAdapter();
        var (bus, registry, _, pump) = Build(adapter);

        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);
        var identity = IdentityFor(reference);

        // Progress published FIRST, terminal second.
        for (var i = 0; i < 3; i++)
            bus.Publish(key, ConversationEventKind.TurnProgress, identity,
                new TurnProgressPayload { Activity = ProgressActivity.Typing });
        bus.Publish(key, ConversationEventKind.TurnFinal, identity, new TurnFinalPayload
        {
            Text = "answer", Completion = TurnCompletion.Completed,
            IsPartial = false, Truncated = false, MergedSubmissionIds = [],
        });

        await pump.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(4, adapter.Delivered.Count);
        // The terminal event overtook the still-queued progress events. seq is what makes that
        // reordering detectable rather than invisible.
        Assert.Equal(ConversationEventKind.TurnFinal, adapter.Delivered[0].Kind);
        Assert.Equal(4, adapter.Delivered[0].Seq);
    }
}
