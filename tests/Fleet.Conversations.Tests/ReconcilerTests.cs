using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The reconciler, against a real database (AC25, AC21g, AC21l's negative half).
/// </summary>
/// <remarks>
/// Every test here expires a lease by writing <c>lease_expires_at</c> into the past rather than by
/// waiting. Waiting out a 120-second lease in a test suite is a two-minute test that still cannot
/// say whether the bound is the one configured; setting the column asserts the predicate directly.
/// </remarks>
[Collection("mysql")]
public sealed class ReconcilerTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();

    private Reconciler Reconciler() =>
        new(fixture.ConnectionString, fixture.Options, NullLogger<Reconciler>.Instance);

    // ── AC25: one reason, one producer, and nothing re-run ───────────────────

    /// <summary>
    /// An expired lease produces exactly one <c>attempt_abandoned</c>, and the submission becomes
    /// terminal.
    /// </summary>
    [Fact]
    public async Task An_expired_lease_is_abandoned_once()
    {
        var reconciler = Reconciler();
        await reconciler.RecordHealthyAsync();

        var (conversation, accept) = await PendingSubmissionAsync();
        await ExpireLeaseAsync(accept.SubmissionId!);

        var first = await reconciler.ScanOnceAsync();
        Assert.Equal(1, first.Abandoned);
        Assert.False(first.WithinGrace);

        // The event, its reason, and its terminal flag — read from the row rather than inferred.
        var appended = await fixture.ScalarRowAsync($"""
            SELECT kind, payload_json, is_terminal, retention_class
              FROM conversation_events
             WHERE conversation_id = '{conversation.ConversationId}'
               AND kind = '{ConversationEventKind.TurnOutcomeUnknown}'
            """);

        Assert.StartsWith(ConversationEventKind.TurnOutcomeUnknown, appended, StringComparison.Ordinal);
        Assert.Contains("attempt_abandoned", appended, StringComparison.Ordinal);
        // TINYINT(1) reads back as a bool through the driver, so the terminal flag is "True".
        Assert.EndsWith("|True|durable", appended, StringComparison.Ordinal);

        // The submission is terminal with that event as its terminal seq, so a legitimately
        // abandoned submission is distinguishable from one the agent never received.
        Assert.Equal("terminal", await fixture.ScalarRowAsync(
            $"SELECT state FROM submissions WHERE id = '{accept.SubmissionId}'"));

        // A SECOND scan does nothing. "Exactly one" is the assertion, and a reconciler that
        // re-appended on every scan would produce a terminal per scan forever.
        var second = await reconciler.ScanOnceAsync();
        Assert.Equal(0, second.Abandoned);

        Assert.Equal("1", await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE conversation_id = '{conversation.ConversationId}'
               AND kind = '{ConversationEventKind.TurnOutcomeUnknown}'
            """));
    }

    /// <summary>
    /// ⚠️ Nothing is re-dispatched. The command outbox is not touched.
    /// </summary>
    /// <remarks>
    /// MUST NOT 2, and the mutation the design names as "re-dispatch on an unknown outcome". A
    /// retry here would be a second real-world side effect for work that may already have happened,
    /// so the assertion is on the outbox row count before and after, not on the absence of a log
    /// line.
    /// </remarks>
    [Fact]
    public async Task Abandoning_dispatches_nothing()
    {
        var reconciler = Reconciler();
        await reconciler.RecordHealthyAsync();

        var (_, accept) = await PendingSubmissionAsync();

        var before = await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM command_outbox WHERE submission_id = '{accept.SubmissionId}'");

        await ExpireLeaseAsync(accept.SubmissionId!);
        Assert.Equal(1, (await reconciler.ScanOnceAsync()).Abandoned);

        var after = await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM command_outbox WHERE submission_id = '{accept.SubmissionId}'");

        Assert.Equal(before, after);
    }

    /// <summary>
    /// A committed attempt is never abandoned, even with an expired lease.
    /// </summary>
    /// <remarks>
    /// This is the race the transaction's <c>WHERE state IN ('pending','running')</c> exists for: an
    /// agent that commits between the scan's select and its update wins, and a successful answer is
    /// never destroyed by a store-side sweep.
    /// </remarks>
    [Fact]
    public async Task A_committed_attempt_is_left_alone()
    {
        var reconciler = Reconciler();
        await reconciler.RecordHealthyAsync();

        var (conversation, accept) = await PendingSubmissionAsync();

        await fixture.ExecuteAsync($"""
            UPDATE execution_attempts
               SET state = 'committed',
                   lease_expires_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 HOUR)
             WHERE submission_id = '{accept.SubmissionId}'
            """);

        Assert.Equal(0, (await reconciler.ScanOnceAsync()).Abandoned);

        Assert.Equal("0", await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE conversation_id = '{conversation.ConversationId}'
               AND kind = '{ConversationEventKind.TurnOutcomeUnknown}'
            """));
    }

    // ── the grace period ─────────────────────────────────────────────────────

    /// <summary>
    /// After a store outage the scan holds off rather than abandoning everything at once.
    /// </summary>
    /// <remarks>
    /// When this service has been away, every lease in the database looks expired — because nothing
    /// was renewing them, not because any agent died. Abandoning them all would report a mass
    /// failure to every client for a fault that was entirely on this side.
    /// </remarks>
    [Fact]
    public async Task A_scan_holds_off_while_the_service_has_not_been_healthy_for_the_grace_period()
    {
        var reconciler = Reconciler();
        var (_, accept) = await PendingSubmissionAsync();
        await ExpireLeaseAsync(accept.SubmissionId!);

        // The service has not recorded liveness for far longer than the grace period.
        await fixture.ExecuteAsync(
            "UPDATE service_health SET last_healthy_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 DAY) "
            + "WHERE id = 1");

        var held = await reconciler.ScanOnceAsync();

        Assert.True(held.WithinGrace);
        Assert.Equal(0, held.Abandoned);

        // And once it is healthy again, the same attempt is abandoned — so the hold-off is a delay,
        // not a permanent exemption.
        await reconciler.RecordHealthyAsync();
        Assert.Equal(1, (await reconciler.ScanOnceAsync()).Abandoned);
    }

    // ── AC21l, the negative half ─────────────────────────────────────────────

    /// <summary>
    /// A queue of waiting attempts whose leases are renewed is NOT abandoned; the same queue with
    /// renewal suppressed is abandoned in full, one event each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves, in one test, because a test that ran only the second would pass against a
    /// reconciler that abandoned everything unconditionally — and a test that ran only the first
    /// would pass against one that abandoned nothing at all.
    /// </para>
    /// <para>
    /// The queued case is the one the wrong reading breaks: a `queued` attempt is `pending` for as
    /// long as it waits, which can be many lease durations behind a running turn.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_heartbeated_queue_survives_and_an_unheartbeated_one_is_abandoned_in_full()
    {
        var reconciler = Reconciler();
        await reconciler.RecordHealthyAsync();

        var conversation = await OpenAsync();
        var waiting = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            var accept = await AcceptAsync(conversation.ConversationId, $"queued-{i}");
            var claim = await ClaimAsync(accept.SubmissionId!);

            await _store.RecordDispositionAsync(new RecordDispositionRequest
            {
                MessageId = await MessageIdForAsync(accept.SubmissionId!),
                SubmissionId = claim.SubmissionId!,
                AttemptId = claim.AttemptId!,
                Disposition = SubmissionDisposition.Queued,
                Epoch = Ulid.NewUlid(),
                Ordinal = 1,
                Owner = "agent-1",
            });

            waiting.Add(claim.AttemptId!);
        }

        // Age every lease past its bound, then have the agent heartbeat every attempt it owns —
        // which is what an agent that heartbeats queued work as well as running work does.
        await AgeLeasesAsync(waiting);

        var renewed = await _store.HeartbeatAsync(new HeartbeatRequest
        {
            AttemptIds = waiting,
            Owner = "agent-1",
        });

        Assert.Equal(waiting.Count, renewed.Renewed.Count);
        Assert.Empty(renewed.NotOwned);

        Assert.Equal(0, (await reconciler.ScanOnceAsync()).Abandoned);

        Assert.Equal("0", await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE conversation_id = '{conversation.ConversationId}'
               AND kind = '{ConversationEventKind.TurnOutcomeUnknown}'
            """));

        // Now the agent stops heartbeating. Every waiting attempt is abandoned, one event each.
        await AgeLeasesAsync(waiting);

        Assert.Equal(waiting.Count, (await reconciler.ScanOnceAsync()).Abandoned);

        Assert.Equal(waiting.Count.ToString(), await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE conversation_id = '{conversation.ConversationId}'
               AND kind = '{ConversationEventKind.TurnOutcomeUnknown}'
            """));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(OpenConversationResult Conversation, AcceptSubmissionResult Accept)>
        PendingSubmissionAsync()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId, "hello");
        return (conversation, accept);
    }

    private async Task<OpenConversationResult> OpenAsync() =>
        await _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = "p_owner",
        });

    private async Task<AcceptSubmissionResult> AcceptAsync(string conversationId, string text) =>
        await _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversationId,
            ExternalSubmissionId = Guid.NewGuid().ToString("n"),
            PayloadFingerprint = PayloadFingerprint.Compute(text, null, conversationId),
            CommandKind = ConversationEventKind.SubmissionCreate,
            CommandPayloadJson = """{"text":"hello"}""",
        });

    private async Task<ClaimDeliveryResult> ClaimAsync(string submissionId) =>
        await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = await MessageIdForAsync(submissionId),
            Owner = "agent-1",
        });

    private async Task<string> MessageIdForAsync(string submissionId) =>
        await fixture.ScalarRowAsync(
            $"SELECT message_id FROM command_outbox WHERE submission_id = '{submissionId}'");

    private Task ExpireLeaseAsync(string submissionId) =>
        fixture.ExecuteAsync($"""
            UPDATE execution_attempts
               SET lease_expires_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 HOUR)
             WHERE submission_id = '{submissionId}'
            """);

    private Task AgeLeasesAsync(IReadOnlyList<string> attemptIds) =>
        fixture.ExecuteAsync($"""
            UPDATE execution_attempts
               SET lease_expires_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 HOUR)
             WHERE id IN ({string.Join(",", attemptIds.Select(id => $"'{id}'"))})
            """);
}
