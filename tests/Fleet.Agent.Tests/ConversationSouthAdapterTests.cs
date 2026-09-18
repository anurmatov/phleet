using System.Reflection;
using Fleet.Agent.Services;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="ConversationSouthAdapter"/> — translation only (#303 D10).
/// </summary>
/// <remarks>
/// The adapter runs on the pump's loop, under a five-second timeout, with every exception swallowed.
/// Everything asserted here is about what it must NOT do there: no HTTP, no retry, no blocking, no
/// ordinal.
/// </remarks>
public sealed class ConversationSouthAdapterTests
{
    private const string ConversationId = "c_01JABCDEF";

    private static (ConversationSouthAdapter Adapter, ConversationSouthHandoff Handoff, ConversationSouthCounters Counters) Build()
    {
        var handoff = new ConversationSouthHandoff();
        var counters = new ConversationSouthCounters();
        return (new ConversationSouthAdapter(handoff, counters, NullLogger<ConversationSouthAdapter>.Instance),
            handoff, counters);
    }

    private static ConversationEvent Event(string kind, string? submissionId = "s_1", string? turnId = null) =>
        ConversationEvent.Create(
            kind,
            new ConversationIdentity
            {
                PrincipalId = "p_1",
                Role = PrincipalRole.Owner,
                ChannelId = ConversationSouthAdapter.ClientChannel,
                ConversationId = ConversationId,
                SubmissionId = submissionId ?? "",
                TurnId = turnId,
                Attempt = 1,
            },
            eventId: Ulid.NewUlid(),
            seq: 7,
            emittedAt: DateTimeOffset.UtcNow,
            payload: new TurnStartedPayload());

    // ── channel ownership ────────────────────────────────────────────────────

    /// <summary>
    /// The adapter claims <c>client</c> — the same id the service stamps on every command envelope.
    /// A different id and the pump would never route anything to it, silently.
    /// </summary>
    [Fact]
    public void TheAdapterServesTheClientChannel()
    {
        var (adapter, _, _) = Build();

        Assert.Equal("client", adapter.ChannelId);
        Assert.False(ChannelIds.IsRuntimeOwned(adapter.ChannelId));
    }

    // ── suppression (D4, D4a, MUST NOT 6) ────────────────────────────────────

    /// <summary>
    /// <c>submission.accepted</c> is never forwarded.
    /// </summary>
    /// <remarks>
    /// The disposition endpoint appends its own, with a different <c>eventId</c>, and append
    /// idempotency keys on that id — so forwarding the runtime's copy writes a SECOND
    /// <c>submission.accepted</c> row for one submission rather than deduplicating. AC9 counts the
    /// rows; this asserts the cause.
    /// </remarks>
    [Fact]
    public async Task SubmissionAcceptedIsDiscardedRatherThanHandedOff()
    {
        var (adapter, handoff, counters) = Build();

        await adapter.DeliverAsync(Event(ConversationEventKind.SubmissionAccepted), CancellationToken.None);

        Assert.Equal(0, handoff.Depth(ConversationId));
        Assert.Equal(1, counters.AdapterDiscardedCount("submission_accepted_suppressed"));
    }

    /// <summary>
    /// <c>turn.started</c> IS handed off — but as a <c>/turns:start</c> call, never as an append.
    /// </summary>
    /// <remarks>
    /// <c>/turns:start</c> writes the event and returns its seq, so appending it as well would write
    /// it twice. The event is still needed: it is what tells the consumer a queued submission now has
    /// a turn, and it carries the runtime's own turn id (D4).
    /// </remarks>
    [Fact]
    public void TurnStartedBecomesAStartCallAndNotAnAppend() =>
        Assert.Equal(
            SouthOutboundAction.StartTurn,
            ConversationSouthAdapter.Classify(ConversationEventKind.TurnStarted));

    // ── classification (D6) ──────────────────────────────────────────────────

    /// <summary>
    /// Every terminal kind goes to <c>/turns:commit</c>.
    /// </summary>
    /// <remarks>
    /// Appending a terminal would write the event without closing the attempt, and the reconciler
    /// would later abandon an attempt that had already answered — the client is then told
    /// <c>outcome_unknown</c> for a turn it already saw finish.
    /// </remarks>
    [Theory]
    [InlineData(ConversationEventKind.TurnFinal)]
    [InlineData(ConversationEventKind.TurnError)]
    [InlineData(ConversationEventKind.TurnCanceled)]
    [InlineData(ConversationEventKind.TurnOutcomeUnknown)]
    public void TerminalKindsCommit(string kind) =>
        Assert.Equal(SouthOutboundAction.Commit, ConversationSouthAdapter.Classify(kind));

    [Theory]
    [InlineData(ConversationEventKind.TurnProgress)]
    [InlineData(ConversationEventKind.TurnNotice)]
    [InlineData(ConversationEventKind.TurnRecoveredAnswer)]
    [InlineData(ConversationEventKind.ControlAck)]
    [InlineData(ConversationEventKind.ProtocolRejected)]
    public void EverythingElseClientVisibleAppends(string kind) =>
        Assert.Equal(SouthOutboundAction.Append, ConversationSouthAdapter.Classify(kind));

    /// <summary>
    /// <c>conversation.replay_gap</c> is synthetic and never stored; carrying it south would write a
    /// per-reader artefact into a log every reader shares.
    /// </summary>
    [Fact]
    public void TheSyntheticReplayGapIsNotCarriedSouth() =>
        Assert.Null(ConversationSouthAdapter.Classify(ConversationEventKind.ConversationReplayGap));

    // ── retention (D5) ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(ConversationEventKind.TurnProgress)]
    [InlineData(ConversationEventKind.TurnNotice)]
    public void ProgressAndNoticesAreEphemeral(string kind) =>
        Assert.Equal(EventRetentionClass.Ephemeral, ConversationSouthAdapter.RetentionFor(kind));

    /// <summary>
    /// Every kind whose absence a client would experience as a lie is durable — including
    /// <c>protocol.rejected</c>, which D5's list does not name. A refusal that ages out reads to a
    /// returning client as a submission that was never made.
    /// </summary>
    [Theory]
    [InlineData(ConversationEventKind.TurnFinal)]
    [InlineData(ConversationEventKind.TurnError)]
    [InlineData(ConversationEventKind.TurnCanceled)]
    [InlineData(ConversationEventKind.TurnOutcomeUnknown)]
    [InlineData(ConversationEventKind.TurnStarted)]
    [InlineData(ConversationEventKind.TurnRecoveredAnswer)]
    [InlineData(ConversationEventKind.ControlAck)]
    [InlineData(ConversationEventKind.ProtocolRejected)]
    public void EverythingAClientMustNotLoseIsDurable(string kind) =>
        Assert.Equal(EventRetentionClass.Durable, ConversationSouthAdapter.RetentionFor(kind));

    // ── conversion (D5, MUST NOT 7) ──────────────────────────────────────────

    /// <summary>
    /// The locally-assigned <c>seq</c> is dropped and the <c>eventId</c> is carried through
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// The bus assigns an in-process per-conversation counter at publish; the store allocates the
    /// real seq at durable append and rebuilds the envelope on the way out. Sending the local one
    /// puts a number on the wire the store ignores and replaces — and is the only way the wrong one
    /// could ever leak. The event id is the append's idempotency key, so it must survive verbatim.
    /// </remarks>
    [Fact]
    public async Task ConversionCarriesTheEventIdAndDropsTheLocalSeq()
    {
        var (adapter, handoff, _) = Build();
        var evt = Event(ConversationEventKind.TurnProgress);

        await adapter.DeliverAsync(evt, CancellationToken.None);

        Assert.True(handoff.Reader(ConversationId).TryRead(out var item));
        Assert.Equal(evt.EventId, item!.Event.EventId);
        Assert.Equal(evt.Kind, item.Event.Kind);
        Assert.Equal(ConversationId, item.ConversationId);
        Assert.Equal("s_1", item.SubmissionId);

        // EventDescriptor has no seq at all — that is the structural half of MUST NOT 7.
        Assert.DoesNotContain(
            typeof(EventDescriptor).GetProperties(),
            p => p.Name.Contains("Seq", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An event with no conversation — a pre-turn rejection built by
    /// <c>ConversationIdentity.ForChannel</c> — has nothing durable to be appended to. Counted, not
    /// dropped in silence.
    /// </summary>
    [Fact]
    public async Task AnEventWithNoConversationIsCountedRatherThanEnqueued()
    {
        var (adapter, handoff, counters) = Build();

        var orphan = ConversationEvent.Create(
            ConversationEventKind.ProtocolRejected,
            ConversationIdentity.ForChannel(ConversationSouthAdapter.ClientChannel),
            Ulid.NewUlid(), 1, DateTimeOffset.UtcNow, new TurnStartedPayload());

        await adapter.DeliverAsync(orphan, CancellationToken.None);

        Assert.Equal(0, handoff.Depth(""));
        Assert.Equal(1, counters.AdapterDiscardedCount("no_conversation"));
    }

    // ── the hand-off (D10) ───────────────────────────────────────────────────

    /// <summary>
    /// A full queue refuses progress, counts the refusal, and still returns — it never blocks and
    /// never throws.
    /// </summary>
    /// <remarks>
    /// The pump cancels at five seconds and swallows exceptions, so an adapter that blocked here
    /// would be counted as a delivery timeout while the turn was reported successful.
    /// </remarks>
    [Fact]
    public async Task AFullQueueRefusesProgressAndCountsIt()
    {
        var (adapter, handoff, counters) = Build();

        for (var i = 0; i < ConversationSouthHandoff.Capacity; i++)
            await adapter.DeliverAsync(Event(ConversationEventKind.TurnProgress), CancellationToken.None);

        Assert.Equal(ConversationSouthHandoff.Capacity, handoff.Depth(ConversationId));

        await adapter.DeliverAsync(Event(ConversationEventKind.TurnProgress), CancellationToken.None);

        Assert.Equal(ConversationSouthHandoff.Capacity, handoff.Depth(ConversationId));
        Assert.Equal(1, counters.HandoffRefusedCount(ConversationEventKind.TurnProgress));
        Assert.Equal(0, counters.TerminalDroppedCount);
    }

    /// <summary>
    /// A terminal is admitted when progress has filled the queue — the reservation — and it lands
    /// BEHIND the progress already queued, not in front of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reservation is capacity, not an overtaking lane. Overtaking is what would make the
    /// ordinal order and the transmission order disagree, and the store's fence would then report a
    /// defect that was purely ours (D7a).
    /// </para>
    /// <para>
    /// The mutation this fails on is draining terminals first, the way the bus does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATerminalIsAdmittedOverCapacityAndStillTransmittedInOrder()
    {
        var (adapter, handoff, counters) = Build();

        for (var i = 0; i < ConversationSouthHandoff.Capacity; i++)
            await adapter.DeliverAsync(Event(ConversationEventKind.TurnProgress), CancellationToken.None);

        await adapter.DeliverAsync(Event(ConversationEventKind.TurnFinal), CancellationToken.None);

        Assert.Equal(ConversationSouthHandoff.Capacity + 1, handoff.Depth(ConversationId));
        Assert.Equal(0, counters.TerminalDroppedCount);

        var reader = handoff.Reader(ConversationId);
        Assert.True(reader.TryRead(out var first));
        Assert.Equal(ConversationEventKind.TurnProgress, first!.Event.Kind);
    }

    /// <summary>
    /// A refused TERMINAL is counted separately, because the consequence is different in kind: the
    /// attempt degrades to lease expiry and the client is told <c>attempt_abandoned</c> for a turn
    /// that answered.
    /// </summary>
    [Fact]
    public async Task ARefusedTerminalGetsItsOwnCounter()
    {
        var (adapter, _, counters) = Build();

        var admitted = ConversationSouthHandoff.Capacity + ConversationSouthHandoff.TerminalReserve;
        for (var i = 0; i < admitted; i++)
            await adapter.DeliverAsync(Event(ConversationEventKind.TurnFinal), CancellationToken.None);

        Assert.Equal(0, counters.TerminalDroppedCount);

        await adapter.DeliverAsync(Event(ConversationEventKind.TurnFinal), CancellationToken.None);

        Assert.Equal(1, counters.TerminalDroppedCount);
    }

    // ── the event id is a storage key ────────────────────────────────────────

    /// <summary>
    /// Every event id the bus mints is storable by the durable store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>event_id</c> is <c>CHAR(26) ascii_bin</c> and it is the append's idempotency key, so the
    /// adapter forwards it verbatim — minting a fresh one per call would make a retried append write
    /// a second row instead of returning the first one's seq. A 32-character GUID is rejected by the
    /// column outright, and the rejection surfaces as a raw <c>MySqlException</c> from
    /// <c>/events:append</c> and <c>/turns:commit</c> while the disposition, which mints its id
    /// server-side, succeeds beside it — so the symptom points at the store rather than at the id.
    /// </para>
    /// <para>
    /// Asserted through the real publish path rather than on the minting method: what matters is the
    /// id that reaches the adapter, which is the one that reaches the column.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryEventIdTheBusMintsFitsTheStoresColumn()
    {
        var (adapter, handoff, _) = Build();

        var registry = new ConversationRegistry();
        var reference = new ConversationRef(
            ConversationSouthAdapter.ClientChannel, ConversationId, "p_1");
        var runtimeKey = registry.Resolve(reference);

        var bus = new ConversationEventBus(
            registry, new ConversationEventCounters(), NullLogger<ConversationEventBus>.Instance);

        var identity = new ConversationIdentity
        {
            PrincipalId = "p_1",
            Role = PrincipalRole.Owner,
            ChannelId = ConversationSouthAdapter.ClientChannel,
            ConversationId = ConversationId,
            SubmissionId = "s_1",
            Attempt = 1,
        };

        for (var i = 0; i < 20; i++)
        {
            Assert.True(bus.Publish(
                runtimeKey, ConversationEventKind.TurnProgress, identity, new TurnStartedPayload()));
        }

        var pump = new ConversationEventPump(
            bus, registry, new ConversationEventCounters(), [adapter],
            NullLogger<ConversationEventPump>.Instance);

        await pump.DrainOnceAsync(CancellationToken.None);

        var reader = handoff.Reader(ConversationId);
        var seen = 0;

        while (reader.TryRead(out var item))
        {
            seen++;
            Assert.True(
                Ulid.IsValid(item.Event.EventId),
                $"event id '{item.Event.EventId}' ({item.Event.EventId.Length} chars) "
                + "does not fit the store's CHAR(26) ascii_bin column");
        }

        Assert.Equal(20, seen);
    }

    // ── AC12's structural half (MUST NOT 9) ──────────────────────────────────

    /// <summary>
    /// The adapter has no reference to <see cref="ConversationOrdinalAllocator"/>, anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The round-2 defect on #303 was allocating the ordinal at translation time. It is invisible
    /// behaviourally until a terminal overtakes queued progress — which the bus does by design — and
    /// then it trips the store's fence and silences the conversation. Only a structural assertion
    /// keeps it from coming back, because no behavioural test in this suite would notice.
    /// </para>
    /// <para>
    /// Checked on constructor parameters, fields AND properties: a constructor-only check is
    /// defeated by a service-locator field, and a field-only check by a property.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheAdapterCannotSeeTheOrdinalAllocator()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic;

        var adapter = typeof(ConversationSouthAdapter);
        var allocator = typeof(ConversationOrdinalAllocator);

        Assert.DoesNotContain(
            adapter.GetConstructors(all).SelectMany(c => c.GetParameters()),
            p => p.ParameterType == allocator);

        Assert.DoesNotContain(adapter.GetFields(all), f => f.FieldType == allocator);
        Assert.DoesNotContain(adapter.GetProperties(all), p => p.PropertyType == allocator);

        // And no method takes or returns one either — an allocator passed in per call would satisfy
        // all three checks above.
        Assert.DoesNotContain(
            adapter.GetMethods(all),
            m => m.ReturnType == allocator || m.GetParameters().Any(p => p.ParameterType == allocator));
    }

    /// <summary>
    /// The adapter holds no HTTP client either (MUST NOT 4). A retry written inside
    /// <c>DeliverAsync</c> is cancelled at five seconds and its failure discarded.
    /// </summary>
    [Fact]
    public void TheAdapterCannotPerformHttp()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var adapter = typeof(ConversationSouthAdapter);

        Assert.DoesNotContain(
            adapter.GetConstructors(all).SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(HttpClient) || p.ParameterType == typeof(ConversationSouthClient));

        Assert.DoesNotContain(
            adapter.GetFields(all),
            f => f.FieldType == typeof(HttpClient) || f.FieldType == typeof(ConversationSouthClient));
    }
}
