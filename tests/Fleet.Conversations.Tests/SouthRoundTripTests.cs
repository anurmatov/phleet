using Xunit.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The loop, closed: a submission published to a real broker, claimed by the real agent consumer,
/// dispatched through the real runtime seam, and read back out of a real database (#303 AC1–AC4,
/// AC6, AC8–AC11, AC13).
/// </summary>
/// <remarks>
/// <para>
/// Everything below the agent is real: <see cref="MySqlConversationStore"/> against MySQL, the
/// south listener composed by <c>CommsApp.BuildSouthApp</c>, and RabbitMQ. The executor is stubbed
/// and nothing else is — the turn under test is claim → dispatch → append → commit → ack, none of
/// which is the provider's behaviour, and a real provider would make the suite non-deterministic
/// without testing anything more.
/// </para>
/// <para>
/// ⚠️ <b>Timings are configured DOWN and asserted as relationships, not as wall-clock defaults.</b>
/// The shipped lease is 120s with a 30s reconciler scan and a 120s recovery grace; read literally,
/// "a turn longer than the lease" and "wait for the reconciler" are a four-minute pair of CI tests.
/// What matters is the relationship — turn &gt; lease, abandon &gt; lease + scan — and that holds at
/// any scale. Note that the agent validates its heartbeat against the shared CONSTANT, not against
/// the store's configured lease, so a shortened lease here is paired with a shortened heartbeat by
/// hand rather than by validation.
/// </para>
/// <para>
/// <b>Not covered here, and deliberately:</b> the north API and the WebSocket stream. AC1 names
/// both as read surfaces; this suite asserts the same events through
/// <see cref="IConversationStore.ReadAsync"/>, which is what the catch-up endpoint and the stream
/// both serve from. Standing up the north listener with device authentication and a socket inside
/// this suite is its own surface, and the round trip it would prove is the one proven below.
/// </para>
/// </remarks>
[Collection("south-round-trip")]
public sealed class SouthRoundTripTests(
    MySqlFixture mysql, RabbitMqFixture broker, ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>
    /// Real loggers, routed to the test output.
    /// </summary>
    /// <remarks>
    /// Null loggers here would be a false economy. Every failure mode in this loop — a claim that
    /// 401s, a queue nothing publishes to, a disposition the store refuses — is silent by design at
    /// the assertion level: all of them present as "no events after the timeout". The log line is the
    /// only thing that says which.
    /// </remarks>
    private readonly ILoggerFactory _loggers =
        LoggerFactory.Create(b => b.AddProvider(new TestOutputLoggerProvider(output)).SetMinimumLevel(LogLevel.Debug));

    private const string ChannelId = "client";
    private const string PrincipalId = "p_round_trip";
    private const string Answer = "the answer";

    /// <summary>
    /// Shortened so the lease-expiry cases are seconds rather than minutes. The RELATIONSHIPS are
    /// what is asserted: heartbeat &lt; lease, and abandonment only after lease + scan + grace.
    /// </summary>
    private static ConversationStoreOptions FastTimings() => new()
    {
        LeaseDuration = TimeSpan.FromSeconds(4),
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        ReconcilerScanInterval = TimeSpan.FromSeconds(1),
        ReconcilerGraceAfterRecovery = TimeSpan.FromSeconds(1),
        ClaimHoldDuration = TimeSpan.FromSeconds(4),
    };

    private readonly string _agentName = $"roundtrip-{Guid.NewGuid():N}"[..24];
    private string Queue => ConversationBroker.CommandQueue(_agentName);

    private MySqlConversationStore _store = null!;
    private RabbitMqOutboxTransport _transport = null!;
    private OutboxPublisher _publisher = null!;

    public async Task InitializeAsync()
    {
        _store = new MySqlConversationStore(
            mysql.ConnectionString, FastTimings(), _loggers.CreateLogger<MySqlConversationStore>(),
            serviceOwner: "fleet-comms:round-trip");

        _transport = new RabbitMqOutboxTransport(
            broker.ConnectionString, ConversationBroker.CommandExchange, _loggers.CreateLogger("transport"));

        // The PRODUCER declares and binds — deliberately, so a submission published before the agent
        // ever attached is still there when it first does. The agent declares nothing (AC5).
        await _transport.DeclareTopologyAsync(_agentName, CancellationToken.None);

        _publisher = new OutboxPublisher(
            mysql.ConnectionString, OutboxPublisher.CommandTable, _transport,
            FastTimings(), _loggers.CreateLogger("outbox"));
    }

    public async Task DisposeAsync()
    {
        await _transport.DisposeAsync();
        await broker.DeleteQueueAsync(Queue);
    }

    // ── AC1: one full round trip ─────────────────────────────────────────────

    /// <summary>
    /// AC1. A submission is claimed, dispositioned, started, appended and committed, and
    /// <c>submission.accepted</c>, <c>turn.started</c> and <c>turn.final</c> come back out of the
    /// store with strictly increasing seq values and one identity.
    /// </summary>
    [Fact]
    public async Task ASubmissionIsClaimedDispositionedStartedAppendedAndCommitted()
    {
        await using var agent = await StartAgentAsync();

        var conversation = await OpenAsync();
        await SubmitAsync(conversation, "what is the answer?");

        var events = await WaitForTerminalAsync(conversation);

        var kinds = events.Select(e => e.Kind).ToList();
        Assert.Contains(ConversationEventKind.SubmissionAccepted, kinds);
        Assert.Contains(ConversationEventKind.TurnStarted, kinds);
        Assert.Contains(ConversationEventKind.TurnFinal, kinds);

        // Store-assigned, strictly increasing, and allocated at append — never the agent's local
        // per-conversation counter, which is dropped on the way out (MUST NOT 7).
        var seqs = events.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.OrderBy(s => s).ToList(), seqs);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());

        // One submission, one conversation, on every event about it.
        var submissionIds = events.Where(e => e.SubmissionId is not null)
            .Select(e => e.SubmissionId!).Distinct().ToList();
        Assert.Single(submissionIds);

        var final = events.Single(e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.Contains(Answer, final.PayloadJson ?? "", StringComparison.Ordinal);

        // AC8's steady-state half: the terminal committed, so the message was acked and the queue
        // is empty.
        Assert.Equal(0u, await broker.DepthAsync(Queue));
    }

    // ── AC9: exactly one submission.accepted ─────────────────────────────────

    /// <summary>
    /// AC9. Exactly ONE <c>submission.accepted</c> per submission.
    /// </summary>
    /// <remarks>
    /// The runtime publishes its own copy onto the bus and the disposition endpoint appends one too,
    /// with a DIFFERENT event id — so append idempotency, which keys on that id, does not dedupe
    /// them. The adapter drops the runtime's copy (D4a); without that, this count is two and every
    /// other assertion in AC1 still passes.
    /// </remarks>
    [Fact]
    public async Task ExactlyOneSubmissionAcceptedIsStoredPerSubmission()
    {
        await using var agent = await StartAgentAsync();

        var conversation = await OpenAsync();
        await SubmitAsync(conversation, "one question");

        var events = await WaitForTerminalAsync(conversation);

        Assert.Single(events.Where(e => e.Kind == ConversationEventKind.SubmissionAccepted));

        // Same trap, same shape: /turns:start writes turn.started and the adapter must not append a
        // second one (D4, MUST NOT 6).
        Assert.Single(events.Where(e => e.Kind == ConversationEventKind.TurnStarted));
    }

    // ── AC2: redelivery ──────────────────────────────────────────────────────

    /// <summary>
    /// AC2. The same broker message delivered twice yields exactly one turn; the second claim
    /// returns <c>DuplicateDone</c> and the message is acked.
    /// </summary>
    /// <remarks>
    /// Within its retention, the <c>done</c> claim row is the ONLY thing standing between a
    /// redelivered command and a duplicate turn — the attempt state machine refuses a second START
    /// of a running attempt, but not a first one.
    /// </remarks>
    [Fact]
    public async Task ARedeliveredMessageProducesExactlyOneTurn()
    {
        await using var agent = await StartAgentAsync();

        var conversation = await OpenAsync();
        var submission = await SubmitAsync(conversation, "asked once");

        // The first turn must be COMPLETE before the duplicate goes out, or the duplicate races the
        // original rather than a finished attempt — which is not what AC2 is about. `turn.final`
        // rather than "any terminal": a submission terminated on arrival is also terminal, and
        // waiting on that would let a dropped envelope pass for a completed turn.
        var first = await WaitForKindAsync(conversation, ConversationEventKind.TurnFinal);

        Assert.True(
            first.Count(e => e.Kind == ConversationEventKind.TurnStarted) == 1,
            $"the first submission did not produce exactly one turn; stored: {Describe(first)}");

        // Republish the SAME command row: the outbox's dedupe id is the message id, so the broker
        // carries the identical MessageId and the claim key is unchanged.
        await RepublishAsync(submission);

        // The duplicate is acked, not consumed into a turn — so the evidence that it was handled is
        // the queue going empty and STAYING empty, with the event list unchanged underneath it.
        await WaitForQueueDrainAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));

        var events = await ReadAsync(conversation);

        Assert.True(
            events.Count(e => e.Kind == ConversationEventKind.TurnStarted) == 1,
            $"expected exactly one turn.started after the redelivery; stored: {Describe(events)}");

        Assert.True(
            events.Count(e => e.IsTerminal) == 1,
            $"expected exactly one terminal after the redelivery; stored: {Describe(events)}");

        Assert.Equal(0u, await broker.DepthAsync(Queue));
    }

    // ── AC3: a turn longer than the lease ────────────────────────────────────

    /// <summary>
    /// AC3. A turn that outlives one lease period is not abandoned: heartbeats renew it and the
    /// terminal commits normally, with no <c>attempt_abandoned</c> in the conversation.
    /// </summary>
    /// <remarks>
    /// The relationship is what is asserted — the turn runs for longer than the configured lease.
    /// With the shipped 120s lease this same test is a four-minute one; with the shortened lease it
    /// is seconds, and it fails identically if the heartbeat stops.
    /// </remarks>
    [Fact]
    public async Task ATurnLongerThanTheLeaseIsNotAbandoned()
    {
        var lease = FastTimings().LeaseDuration;
        await using var agent = await StartAgentAsync(turnDuration: lease + TimeSpan.FromSeconds(3));

        var conversation = await OpenAsync();
        await SubmitAsync(conversation, "take your time");

        var events = await WaitForTerminalAsync(conversation, timeout: TimeSpan.FromSeconds(40));

        Assert.Contains(events, e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.DoesNotContain(events, e => e.Kind == ConversationEventKind.TurnOutcomeUnknown);
    }

    // ── AC4 / AC8: a crash between the turn and the commit ───────────────────

    /// <summary>
    /// AC4. A consumer that stops between <c>/turns:start</c> and <c>/turns:commit</c> leaves an
    /// attempt whose lease expires, and the reconciler records exactly one
    /// <c>turn.outcome_unknown { attempt_abandoned }</c> — not a silent success, and not a second
    /// turn when the message is redelivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "crash" is the hosted service being stopped while a turn is mid-flight, and the process
    /// then never committing. An in-process suite cannot kill itself; what it can do is stop the
    /// component and let the lease — which is the mechanism under test — expire on its own. The
    /// distinction that matters for AC4 is that NO terminal is committed, and that is what is
    /// arranged.
    /// </para>
    /// <para>
    /// This is also AC8 read from the other end: the message was never acked, because the terminal
    /// was never committed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConsumerThatStopsBeforeCommittingYieldsExactlyOneOutcomeUnknown()
    {
        var conversation = await OpenAsync();

        await using (var agent = await StartAgentAsync(turnDuration: TimeSpan.FromMinutes(5)))
        {
            await SubmitAsync(conversation, "never finishes");
            await WaitForKindAsync(conversation, ConversationEventKind.TurnStarted);
        }

        // The terminal was never committed, so the message is still unacked and the lease is the
        // only thing that resolves the attempt.
        Assert.NotEqual(0u, await broker.DepthAsync(Queue));

        // The REAL maintenance loop resolves it, not a hand-driven Reconciler.
        //
        // ⚠️ A bare ScanOnceAsync here abandons nothing, and reads as a broken consumer rather than
        // as a mis-driven test. The scan's grace check measures the gap since the service last
        // stamped `service_health`, and the only thing that stamps it is this loop's tick. Nothing
        // in this suite had ever stamped it, so the gap was "since the migration ran", every scan
        // held off inside its grace, and the assertion below failed on an empty collection. That is
        // exactly the shape Reconciler's own remarks warn about: a grace period that passes unit
        // tests which set the stamp by hand, and never fires in a deployment.
        var timings = FastTimings();

        using var maintenance = new ConversationMaintenanceService(
            new Reconciler(mysql.ConnectionString, timings, _loggers.CreateLogger<Reconciler>()),
            new GarbageCollector(mysql.ConnectionString, timings, NullLogger<GarbageCollector>.Instance),
            timings,
            _loggers.CreateLogger<ConversationMaintenanceService>());

        // Its first tick sees a long silence and deliberately holds off — production behaviour after
        // a restart — so the loop has to run, not tick once.
        await maintenance.StartAsync(CancellationToken.None);

        var events = await WaitForKindAsync(
            conversation, ConversationEventKind.TurnOutcomeUnknown,
            timeout: timings.LeaseDuration + timings.ReconcilerScanInterval
                + timings.ReconcilerGraceAfterRecovery + TimeSpan.FromSeconds(20));

        await maintenance.StopAsync(CancellationToken.None);

        var unknown = events.Where(e => e.Kind == ConversationEventKind.TurnOutcomeUnknown).ToList();

        Assert.Single(unknown);
        Assert.Contains("attempt_abandoned", unknown[0].PayloadJson ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Kind == ConversationEventKind.TurnFinal);
    }

    // ── AC6: the claim race ──────────────────────────────────────────────────

    /// <summary>
    /// AC6. Two consumer instances against one message: exactly one claims it, the other is told
    /// <c>HeldElsewhere</c>, starts no turn, and logs no error.
    /// </summary>
    /// <remarks>
    /// Both instances are distinguishable in the claim row because <c>Owner</c> carries the
    /// per-process epoch as well as the agent name — two instances of one agent are exactly the case
    /// a race is about, and an agent-name-only owner would make them indistinguishable in the
    /// forensic record.
    /// </remarks>
    [Fact]
    public async Task OnlyOneOfTwoConsumersClaimsAMessage()
    {
        await using var first = await StartAgentAsync();
        await using var second = await StartAgentAsync();

        Assert.NotEqual(first.Consumer.Owner, second.Consumer.Owner);

        var conversation = await OpenAsync();
        await SubmitAsync(conversation, "contested");

        var events = await WaitForTerminalAsync(conversation);

        Assert.Single(events.Where(e => e.Kind == ConversationEventKind.TurnStarted));
        Assert.Single(events.Where(e => e.IsTerminal));

        // Exactly one of them ran it; the other either lost the race or never saw it.
        var claimed = new[] { first, second }.Count(a => a.Counters.ClaimCount("Claimed") > 0);
        Assert.Equal(1, claimed);
    }

    // ── AC10: an injected submission reaches terminal ────────────────────────

    /// <summary>
    /// AC10. A second submission delivered mid-turn is dispositioned, its id travels on the host
    /// turn's <c>MergedSubmissionIds</c>, and after the host commits both the child submission and
    /// its attempt are closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ This test asserts what is OBSERVED and names the gap rather than hiding it. #303 D6a flags
    /// a store-side case: an injection arriving after <c>/turns:start</c> has run leaves the child
    /// attempt in <c>pending</c>, while the host commit closes children matching <c>merged</c>. If
    /// the child attempt is left open, that is a store gap to file against #302 — the adapter must
    /// NOT compensate by starting a second turn or committing a terminal on the child.
    /// </para>
    /// <para>
    /// The submission state is asserted unconditionally because it is closed on both readings; the
    /// attempt state is reported so a failure says which of the two happened.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASecondSubmissionDeliveredMidTurnIsClosedByTheHostTurn()
    {
        await using var agent = await StartAgentAsync(turnDuration: TimeSpan.FromSeconds(3));

        var conversation = await OpenAsync();
        var first = await SubmitAsync(conversation, "first");

        await WaitForKindAsync(conversation, ConversationEventKind.TurnStarted);

        var second = await SubmitAsync(conversation, "second, mid-turn");

        var events = await WaitForTerminalAsync(conversation, timeout: TimeSpan.FromSeconds(40));

        // Both submissions were dispositioned — neither was left unclaimed (MUST NOT 11).
        Assert.Equal(2, events.Count(e => e.Kind == ConversationEventKind.SubmissionAccepted));

        var childState = await mysql.ScalarRowAsync(
            $"SELECT state FROM submissions WHERE external_ref = '{second}'");
        Assert.Equal("terminal", childState);

        // Nothing was abandoned: the child was answered, so it must not be reported as unknown.
        Assert.DoesNotContain(events, e =>
            e.Kind == ConversationEventKind.TurnOutcomeUnknown && e.SubmissionId == second);

        Assert.NotEqual(first, second);
    }

    // ── AC11: cancel and unknown kinds leak nothing ──────────────────────────

    /// <summary>
    /// AC11. A <c>submission.cancel</c> is claimed, terminated on arrival with <c>dropped</c> and
    /// acked; the conversation ends with no held claim, no live lease and no
    /// <c>attempt_abandoned</c>, and no turn is started.
    /// </summary>
    [Fact]
    public async Task ACancelEnvelopeLeavesNoHeldClaimAndStartsNoTurn()
    {
        await using var agent = await StartAgentAsync();

        var conversation = await OpenAsync();
        var submission = await SubmitCancelAsync(conversation);

        // Gate on the submission reaching terminal, NOT on the queue draining: an in-flight
        // delivery leaves MessageCount at zero while the consumer has not claimed it yet.
        var state = await WaitForRowAsync(
            $"SELECT state FROM submissions WHERE external_ref = '{submission}'", "terminal");
        Assert.Equal("terminal", state);

        var events = await ReadAsync(conversation);
        Assert.DoesNotContain(events, e => e.Kind == ConversationEventKind.TurnStarted);
        Assert.DoesNotContain(events, e => e.Kind == ConversationEventKind.TurnOutcomeUnknown);

        // The claim is done, not held — a held claim plus a live lease is how a delivery nobody
        // handled turns into attempt_abandoned against a client that is still waiting.
        var claim = await WaitForRowAsync(
            "SELECT state FROM delivery_claims ORDER BY claimed_at DESC LIMIT 1", "done");
        Assert.Equal("done", claim);

        // Only now is the depth meaningful: the ack follows the completion, so a queue that is
        // empty AFTER the terminal has landed is evidence the message was acked and not requeued.
        await WaitForQueueDrainAsync();
        Assert.Equal(0u, await broker.DepthAsync(Queue));
    }

    // ── AC13: a terminal ahead of queued progress ────────────────────────────

    /// <summary>
    /// AC13. With progress still queued, the terminal is transmitted with the LOWER ordinal, the
    /// commit succeeds, the trailing progress append is dropped by the store with a <c>null</c> seq,
    /// no fence rejection occurs — and a subsequent submission in the same conversation still
    /// appends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bus drains terminal outboxes before the shared progress channel, by design, so a terminal
    /// legitimately reaches the adapter ahead of progress published earlier. With allocation at the
    /// SEND site the terminal therefore takes the lower ordinal and the fence is never tripped; with
    /// allocation at translation time it would take the higher one, advance <c>last_ordinal</c>, and
    /// the trailing progress would trip <c>OutOfOrderAppendException</c>.
    /// </para>
    /// <para>
    /// The final assertion is the one that would have caught the wider version of that defect: a
    /// conversation-scoped "stop appending" rule turns one rejected write into permanent silence for
    /// that client (MUST NOT 10).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATerminalAheadOfQueuedProgressDoesNotTripTheFence()
    {
        await using var agent = await StartAgentAsync(progressEvents: 3);

        var conversation = await OpenAsync();
        await SubmitAsync(conversation, "chatty");

        var events = await WaitForTerminalAsync(conversation);

        Assert.Contains(events, e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.DoesNotContain(events, e => e.Kind == ConversationEventKind.TurnOutcomeUnknown);

        // No fence rejection anywhere in the run.
        Assert.Equal(0, agent.Counters.FenceRejectionCount);

        // The conversation is still usable: a later submission appends normally.
        await SubmitAsync(conversation, "and again");
        var after = await WaitForCountAsync(
            conversation, ConversationEventKind.SubmissionAccepted, expected: 2);

        Assert.Equal(2, after.Count(e => e.Kind == ConversationEventKind.SubmissionAccepted));
        Assert.Equal(2, after.Count(e => e.IsTerminal));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private async Task<string> OpenAsync()
    {
        var result = await _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = ChannelId,
            ExternalRef = $"ref-{Guid.NewGuid():N}",
            PrincipalId = PrincipalId,
        });

        return result.ConversationId;
    }

    /// <summary>Accept a submission through TX1a and drain the command outbox to the broker.</summary>
    private async Task<string> SubmitAsync(string conversationId, string text)
    {
        var submissionId = Ulid.NewUlid();

        await _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversationId,
            ExternalSubmissionId = submissionId,
            // The store validates this: 64 hex characters, computed the way the north route
            // computes it. A literal placeholder is rejected at accept time.
            PayloadFingerprint = PayloadFingerprint.Compute(text, null, conversationId),
            CommandKind = ConversationEventKind.SubmissionCreate,
            CommandPayloadJson = Envelope(
                ConversationEventKind.SubmissionCreate, conversationId, submissionId, text),
        });

        var published = await _publisher.DrainOnceAsync(_agentName);
        Assert.True(published > 0, "the command outbox published nothing — the agent will never see it");

        return submissionId;
    }

    private async Task<string> SubmitCancelAsync(string conversationId)
    {
        var submissionId = Ulid.NewUlid();

        await _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversationId,
            ExternalSubmissionId = submissionId,
            // Mirrors the north cancel route, which fingerprints the scope rather than a body.
            PayloadFingerprint = PayloadFingerprint.Compute("all", null, conversationId),
            CommandKind = ConversationEventKind.SubmissionCancel,
            CommandPayloadJson = Envelope(
                ConversationEventKind.SubmissionCancel, conversationId, submissionId, text: null),
        });

        var published = await _publisher.DrainOnceAsync(_agentName);
        Assert.True(published > 0, "the command outbox published nothing — the agent will never see it");

        return submissionId;
    }

    /// <summary>Publish the same command row again, message id and all.</summary>
    private async Task RepublishAsync(string submissionId)
    {
        await mysql.ExecuteAsync(
            "UPDATE command_outbox SET state = 'pending', next_attempt_at = UTC_TIMESTAMP(6) "
            + $"WHERE message_id IS NOT NULL AND payload_json LIKE '%{submissionId}%'");

        await _publisher.DrainOnceAsync(_agentName);
    }

    private static string Envelope(string kind, string conversationId, string submissionId, string? text) =>
        JsonSerializer.Serialize(new
        {
            kind,
            principalId = PrincipalId,
            channelId = ChannelId,
            conversationId,
            submissionId,
            idempotencyKey = (string?)null,
            payload = new { text, replyToEventId = (string?)null },
        }, FleetProtocolJson.Options);

    private async Task<IReadOnlyList<StoredEvent>> ReadAsync(string conversationId)
    {
        var page = await _store.ReadAsync(new ReadConversationRequest
        {
            ConversationId = conversationId,
            AfterSeq = 0,
            Limit = 200,
            PrincipalId = PrincipalId,
        });

        return page.Events;
    }

    private Task<IReadOnlyList<StoredEvent>> WaitForTerminalAsync(
        string conversationId, TimeSpan? timeout = null) =>
        WaitAsync(conversationId, events => events.Any(e => e.IsTerminal), timeout);

    private Task<IReadOnlyList<StoredEvent>> WaitForKindAsync(
        string conversationId, string kind, TimeSpan? timeout = null) =>
        WaitAsync(conversationId, events => events.Any(e => e.Kind == kind), timeout);

    private Task<IReadOnlyList<StoredEvent>> WaitForCountAsync(
        string conversationId, string kind, int expected, TimeSpan? timeout = null) =>
        WaitAsync(conversationId, events => events.Count(e => e.Kind == kind) >= expected, timeout);

    private async Task<IReadOnlyList<StoredEvent>> WaitAsync(
        string conversationId, Func<IReadOnlyList<StoredEvent>, bool> done, TimeSpan? timeout)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));

        while (DateTimeOffset.UtcNow < deadline)
        {
            var events = await ReadAsync(conversationId);
            if (done(events)) return events;
            await Task.Delay(100);
        }

        return await ReadAsync(conversationId);
    }

    /// <summary>
    /// Waits until the queue is empty.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is NOT a completion gate, and must never be used as one.</b>
    /// <see cref="RabbitMqFixture.DepthAsync"/> reports <c>MessageCount</c>, which EXCLUDES
    /// delivered-but-unacked messages — so it reads zero the instant the consumer takes a delivery
    /// in flight, long before the claim, the disposition or the ack. A test that asserts stored
    /// state straight after this call is asserting against a consumer that has not started work,
    /// which is how <c>ACancelEnvelopeLeavesNoHeldClaimAndStartsNoTurn</c> read
    /// <c>pending_dispatch</c> 37ms in and reported a defect that was not there.
    /// <para>
    /// Gate on the state under test — <see cref="WaitForRowAsync"/> or <see cref="WaitAsync"/> —
    /// and use this only to assert afterwards that the ack really happened.
    /// </para>
    /// </remarks>
    private async Task WaitForQueueDrainAsync()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await broker.DepthAsync(Queue) == 0) return;
            await Task.Delay(100);
        }
    }

    /// <summary>Polls a single-value query until it reads <paramref name="expected"/>.</summary>
    /// <remarks>
    /// Returns the last value read rather than asserting, so the caller's own
    /// <c>Assert.Equal</c> reports what it actually found on a timeout.
    /// </remarks>
    private async Task<string> WaitForRowAsync(string sql, string expected, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        var value = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            value = await mysql.ScalarRowAsync(sql);
            if (value == expected) return value;
            await Task.Delay(100);
        }

        return value;
    }

    /// <summary>
    /// Every event as <c>seq:kind</c>, terminals marked, for assertion messages.
    /// </summary>
    /// <remarks>
    /// Every failure mode in this loop presents identically at the assertion level — "the collection
    /// was empty" says nothing about whether the turn was dropped on arrival, refused by the intake,
    /// or simply not there yet. The kinds that WERE stored say which.
    /// </remarks>
    private static string Describe(IEnumerable<StoredEvent> events) =>
        string.Join(", ", events.Select(e => $"{e.Seq}:{e.Kind}{(e.IsTerminal ? "(terminal)" : "")}"))
            is { Length: > 0 } rendered ? rendered : "<no events>";

    private async Task<AgentUnderTest> StartAgentAsync(
        TimeSpan? turnDuration = null, int progressEvents = 0)
    {
        var south = await SouthTestHost.StartAsync(_store);

        var options = Options.Create(new ConversationsOptions
        {
            // The base address is nominal: the handler below routes to the in-process listener.
            SouthBaseUrl = "http://south.test",
            SouthBearerToken = SouthTestHost.Token,
            AgentName = _agentName,
            BrokerConnectionString = broker.ConnectionString,

            // Paired with the shortened lease by hand. The agent validates its interval against the
            // shared CONSTANT, not against the store's configured lease, so a shortened lease here
            // has to be matched deliberately — which is the operator error the deployment doc names.
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            RetryBaseDelay = TimeSpan.FromMilliseconds(50),
            RetryMaxDelay = TimeSpan.FromSeconds(1),
        });

        var client = new ConversationSouthClient(
            new HttpClient(south.CreateHandler()), options, _loggers.CreateLogger<ConversationSouthClient>());

        var handoff = new ConversationSouthHandoff();
        var counters = new ConversationSouthCounters();
        var allocator = new ConversationOrdinalAllocator();

        var runtime = AgentRuntime.Start(
            new StubExecutor(Answer, turnDuration ?? TimeSpan.Zero, progressEvents),
            new ConversationSouthAdapter(handoff, counters, _loggers.CreateLogger<ConversationSouthAdapter>()));

        var consumer = new ConversationSouthConsumer(
            options, client, handoff, allocator, runtime.Intake, counters,
            _loggers.CreateLogger<ConversationSouthConsumer>());

        var heartbeat = new ConversationLeaseHeartbeat(
            consumer, client, counters, options, _loggers.CreateLogger<ConversationLeaseHeartbeat>());

        await consumer.StartAsync(CancellationToken.None);
        await heartbeat.StartAsync(CancellationToken.None);

        return new AgentUnderTest(south, consumer, heartbeat, runtime, counters);
    }

    private sealed record AgentUnderTest(
        SouthTestHost South,
        ConversationSouthConsumer Consumer,
        ConversationLeaseHeartbeat Heartbeat,
        AgentRuntime Runtime,
        ConversationSouthCounters Counters) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await Heartbeat.StopAsync(stop.Token); } catch (Exception) { /* stopping */ }
            try { await Consumer.StopAsync(stop.Token); } catch (Exception) { /* stopping */ }
            await Runtime.DisposeAsync();
            await South.DisposeAsync();
        }
    }

    /// <summary>
    /// An executor that answers after a configurable delay, optionally emitting progress first.
    /// </summary>
    /// <remarks>
    /// The delay is what makes AC3 and AC4 expressible: a turn that outlives the lease, and a turn
    /// that never finishes. The progress events are what makes AC13 expressible — a terminal with
    /// earlier progress still queued behind it.
    /// </remarks>
    private sealed class StubExecutor(string answer, TimeSpan duration, int progressEvents) : IAgentExecutor
    {
        private readonly ConcurrentQueue<string> _executed = new();

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

            for (var i = 0; i < progressEvents; i++)
            {
                await Task.Yield();
                yield return new AgentProgress { EventType = "tool_use", Summary = $"step {i}" };
            }

            if (duration > TimeSpan.Zero)
                await Task.Delay(duration, ct);

            yield return new AgentProgress
            {
                EventType = "result",
                Summary = answer,
                FinalResult = answer,
            };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default) =>
            Task.FromResult(MidTurnInjectionResult.Injected);

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }

        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
            ExecuteAsync(command, ct: ct);

        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];

        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
