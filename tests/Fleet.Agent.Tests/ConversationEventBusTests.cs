using System.Diagnostics;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC34–41 and AC55–58: publication is non-blocking, terminal events cannot be evicted by a
/// chatty progress stream, ownership is deterministic, and an adapter can never fault a turn.
/// </summary>
public class ConversationEventBusTests
{
    private static (ConversationEventBus bus, ConversationRegistry registry, ConversationEventCounters counters)
        Build()
    {
        var registry = new ConversationRegistry();
        var counters = new ConversationEventCounters();
        var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
        return (bus, registry, counters);
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

    // ── seq ───────────────────────────────────────────────────────────────────

    /// <summary>AC37: seq is strictly increasing per conversation, in publish order.</summary>
    [Fact]
    public void Seq_IsStrictlyIncreasingPerConversation()
    {
        var (bus, registry, _) = Build();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);
        var identity = IdentityFor(reference);

        for (var i = 0; i < 5; i++)
            bus.Publish(key, ConversationEventKind.TurnStarted, identity, new TurnStartedPayload());

        var seqs = Drain(bus).Select(e => e.Seq).ToList();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, seqs);
    }

    /// <summary>AC37: still strictly increasing under concurrent publishes from two threads.</summary>
    [Fact]
    public void Seq_IsUniqueUnderConcurrentPublishes()
    {
        var (bus, registry, _) = Build();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);
        var identity = IdentityFor(reference);

        // 2 threads × 100 publishes fits inside the 256-slot progress channel, so nothing is
        // dropped and every assigned seq should still be distinct.
        Parallel.For(0, 200, _ =>
            bus.Publish(key, ConversationEventKind.TurnStarted, identity, new TurnStartedPayload()));

        var seqs = Drain(bus).Select(e => e.Seq).ToList();
        Assert.Equal(200, seqs.Count);
        Assert.Equal(200, seqs.Distinct().Count());
    }

    [Fact]
    public void Seq_IsIndependentPerConversation()
    {
        var (bus, registry, _) = Build();
        var a = new ConversationRef("example-adapter", "c_a", "p_1");
        var b = new ConversationRef("example-adapter", "c_b", "p_1");
        var keyA = registry.Resolve(a);
        var keyB = registry.Resolve(b);

        bus.Publish(keyA, ConversationEventKind.TurnStarted, IdentityFor(a), new TurnStartedPayload());
        bus.Publish(keyB, ConversationEventKind.TurnStarted, IdentityFor(b), new TurnStartedPayload());

        var byConversation = Drain(bus).ToDictionary(e => e.Identity.ConversationId, e => e.Seq);
        Assert.Equal(1, byConversation["c_a"]);
        Assert.Equal(1, byConversation["c_b"]);
    }

    // ── routing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// AC56 and D15/D20: a Telegram-owned conversation is EXPECTED to have no adapter. It must be
    /// counted as not_routed, never as a drop — otherwise the drop counter becomes the dominant
    /// metric in production and stops being a signal anyone reads.
    /// </summary>
    [Fact]
    public void TelegramOwnedConversation_CountsAsNotRoutedNotDropped()
    {
        var (bus, _, counters) = Build();
        // Deliberately NOT registered: Telegram conversations never are in production. The bus
        // must resolve "runtime-owned" from the event's own ChannelId.
        var identity = new ConversationIdentity
        {
            PrincipalId = "p_owner",
            Role = PrincipalRole.Owner,
            ChannelId = ChannelIds.Telegram,
            ConversationId = "12345",
            SubmissionId = "s_1",
            Attempt = 1,
        };

        bus.Publish(12345L, ConversationEventKind.TurnStarted, identity, new TurnStartedPayload());
        bus.Publish(12345L, ConversationEventKind.TurnFinal, identity, new TurnFinalPayload
        {
            Text = "done", Completion = TurnCompletion.Completed,
            IsPartial = false, Truncated = false, MergedSubmissionIds = [],
        });

        Assert.Equal(2, counters.NotRoutedCount(ChannelIds.Telegram));
        Assert.Equal(0, counters.TotalDropped());
        // Nothing was queued for delivery, so no adapter could ever see it.
        Assert.Empty(Drain(bus));
    }

    [Fact]
    public void RelayOwnedConversation_IsNeverQueuedForAnAdapter()
    {
        var (bus, registry, counters) = Build();
        var reference = new ConversationRef(ChannelIds.Relay, "777", "p_1");
        var key = registry.Resolve(reference);

        bus.Publish(key, ConversationEventKind.TurnFinal, IdentityFor(reference), new TurnFinalPayload
        {
            Text = "workflow answer", Completion = TurnCompletion.Completed,
            IsPartial = false, Truncated = false, MergedSubmissionIds = [],
        });

        Assert.Empty(Drain(bus));
        Assert.Equal(1, counters.NotRoutedCount(ChannelIds.Relay));
        Assert.Equal(0, counters.TotalDropped());
    }

    /// <summary>AC57: an unregistered conversation is dropped and counted, never thrown.</summary>
    [Fact]
    public void UnregisteredConversation_IsDroppedWithoutThrowing()
    {
        var (bus, _, counters) = Build();
        var identity = new ConversationIdentity
        {
            PrincipalId = "p_1", Role = PrincipalRole.Owner, ChannelId = "example-adapter",
            ConversationId = "c_unknown", SubmissionId = "s_1", Attempt = 1,
        };

        var accepted = bus.Publish(4242L, ConversationEventKind.TurnStarted, identity, new TurnStartedPayload());

        Assert.False(accepted);
        Assert.Equal(1, counters.DroppedCount(ConversationEventCounters.ReasonUnknownConversation));
    }

    // ── the two structures ────────────────────────────────────────────────────

    /// <summary>
    /// AC36 and AC41. The whole reason terminal events live in a separate per-conversation outbox:
    /// saturating the shared progress channel must not be able to evict a terminal event.
    /// </summary>
    [Fact]
    public void SaturatedProgressChannel_StillDeliversASubsequentTerminalEvent()
    {
        var (bus, registry, counters) = Build();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);
        var identity = IdentityFor(reference);

        // Overfill the 256-slot progress channel.
        for (var i = 0; i < ConversationEventBus.ProgressChannelCapacity + 50; i++)
            bus.Publish(key, ConversationEventKind.TurnProgress, identity,
                new TurnProgressPayload { Activity = ProgressActivity.Typing });

        var terminalAccepted = bus.Publish(key, ConversationEventKind.TurnFinal, identity, new TurnFinalPayload
        {
            Text = "answer", Completion = TurnCompletion.Completed,
            IsPartial = false, Truncated = false, MergedSubmissionIds = [],
        });

        Assert.True(terminalAccepted);
        Assert.Equal(50, counters.DroppedCount(ConversationEventCounters.ReasonProgressQueueFull));

        var drained = Drain(bus);
        Assert.Contains(drained, e => e.Kind == ConversationEventKind.TurnFinal);
    }

    /// <summary>
    /// AC40 and Constraint 21. The reaper publishes unconditionally from a finally block, so a
    /// full terminal outbox must neither block nor throw — otherwise the reaper could hang a
    /// turn's teardown, which is exactly the circular failure the per-conversation outbox removes.
    /// </summary>
    [Fact]
    public void FullTerminalOutbox_ReturnsImmediatelyWithoutThrowing()
    {
        var (bus, registry, counters) = Build();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);
        var identity = IdentityFor(reference);

        TurnOutcomeUnknownPayload Unknown() => new() { Reason = OutcomeUnknownReason.TurnReaped };

        for (var i = 0; i < ConversationEventBus.TerminalOutboxCapacity; i++)
            Assert.True(bus.Publish(key, ConversationEventKind.TurnOutcomeUnknown, identity, Unknown()));

        var stopwatch = Stopwatch.StartNew();
        var accepted = bus.Publish(key, ConversationEventKind.TurnOutcomeUnknown, identity, Unknown());
        stopwatch.Stop();

        Assert.False(accepted);
        // "Bounded" would pass even if this blocked for a second. The point is that TryWrite
        // returns synchronously, so assert a real ceiling.
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"publish took {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(1, counters.DroppedCount(ConversationEventCounters.ReasonTerminalOutboxOverflow));
    }

    // ── oversize ──────────────────────────────────────────────────────────────

    /// <summary>
    /// D10 case 3: a terminal event over the hard cap is replaced by turn.outcome_unknown so the
    /// submission still terminates, just indeterminately.
    /// </summary>
    [Fact]
    public void OversizeTerminalEvent_IsReplacedByOutcomeUnknown()
    {
        var (bus, registry, counters) = Build();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);
        var identity = IdentityFor(reference);

        bus.Publish(key, ConversationEventKind.TurnFinal, identity, new TurnFinalPayload
        {
            Text = new string('x', ProtocolLimits.MaxSerializedEventBytes + 1_000),
            Completion = TurnCompletion.Completed,
            IsPartial = false, Truncated = false, MergedSubmissionIds = [],
        });

        Assert.Equal(1, counters.DroppedCount(ConversationEventCounters.ReasonOversize));

        var drained = Drain(bus);
        var replacement = Assert.Single(drained);
        Assert.Equal(ConversationEventKind.TurnOutcomeUnknown, replacement.Kind);
        Assert.Equal(OutcomeUnknownReason.TerminalEventOversize,
            replacement.PayloadAs<TurnOutcomeUnknownPayload>()!.Reason);
    }

    [Fact]
    public void OversizeProgressEvent_IsDroppedWithNoReplacement()
    {
        var (bus, registry, counters) = Build();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);

        bus.Publish(key, ConversationEventKind.TurnNotice, IdentityFor(reference),
            new TurnNoticePayload { Text = new string('x', ProtocolLimits.MaxSerializedEventBytes + 1_000) });

        Assert.Equal(1, counters.DroppedCount(ConversationEventCounters.ReasonOversize));
        Assert.Empty(Drain(bus));
    }

    // ── conversation release ──────────────────────────────────────────────────

    [Fact]
    public void ReleaseConversation_FreesAnEmptyOutbox()
    {
        var (bus, registry, _) = Build();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);

        bus.Publish(key, ConversationEventKind.TurnFinal, IdentityFor(reference), new TurnFinalPayload
        {
            Text = "x", Completion = TurnCompletion.Completed,
            IsPartial = false, Truncated = false, MergedSubmissionIds = [],
        });
        Drain(bus);

        bus.ReleaseConversation(key);

        Assert.Empty(bus.TerminalReaders.Where(r => r.Count > 0));
    }

    private static List<ConversationEvent> Drain(ConversationEventBus bus)
    {
        var events = new List<ConversationEvent>();
        foreach (var reader in bus.TerminalReaders.ToList())
            while (reader.TryRead(out var pending))
                events.Add(pending.Event);
        while (bus.ProgressReader.TryRead(out var pending))
            events.Add(pending.Event);
        return events;
    }
}
