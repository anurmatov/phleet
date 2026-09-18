using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// Retention (AC22, AC23), against a real database.
/// </summary>
/// <remarks>
/// The two prune kinds are different events and the tests are shaped around that: a pruned
/// ephemeral row is not a gap, and a pruned durable row is — with the floor moved in the same
/// transaction, so no reader ever sees the rows gone while the floor still says they are there.
/// </remarks>
[Collection("mysql")]
public sealed class GarbageCollectorTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();

    private GarbageCollector Collector(ConversationStoreOptions? options = null) =>
        new(fixture.ConnectionString, options ?? fixture.Options, NullLogger<GarbageCollector>.Instance);

    // ── AC23: an ephemeral hole is not a gap ─────────────────────────────────

    /// <summary>
    /// Pruning ephemeral rows between surviving durable rows advances no floor and produces no gap.
    /// </summary>
    /// <remarks>
    /// The seq sequence really does get a hole. That is not a loss: the retention class says the row
    /// was prunable, and announcing a gap for it would tell a client it had lost history it was never
    /// promised — on every reconnect, forever.
    /// </remarks>
    [Fact]
    public async Task Pruning_ephemeral_rows_between_durable_ones_produces_no_gap()
    {
        var conversation = await OpenAsync();

        await AppendAsync(conversation.ConversationId, EventRetentionClass.Durable, 1);
        await AppendAsync(conversation.ConversationId, EventRetentionClass.Ephemeral, 2);
        await AppendAsync(conversation.ConversationId, EventRetentionClass.Ephemeral, 3);
        await AppendAsync(conversation.ConversationId, EventRetentionClass.Durable, 4);

        var floorBefore = await FloorAsync(conversation.ConversationId);

        // Only the EPHEMERAL rows are aged. Ageing the durable ones too would trigger the durable
        // prune as well, and the floor would move for that reason rather than for the one under
        // test — which is exactly how this test first failed.
        await AgeEphemeralEventsAsync(conversation.ConversationId);

        var swept = await Collector().SweepOnceAsync();

        Assert.Equal(2, swept.EphemeralEvents);

        // The floor did not move, so a reader from the beginning gets no gap — a hole in the seq
        // sequence, and nothing announcing a loss.
        Assert.Equal(floorBefore, await FloorAsync(conversation.ConversationId));

        var read = await _store.ReadAsync(new ReadConversationRequest
        {
            ConversationId = conversation.ConversationId,
            AfterSeq = 0,
            Limit = 100,
            PrincipalId = "p_owner",
        });

        Assert.Null(read.Gap);
        Assert.Equal(2, read.Events.Count);
    }

    // ── AC22: a durable prune is a gap, with exact bounds ────────────────────

    /// <summary>
    /// Pruning durable history advances the floor and produces a gap with exact bounds, delivered
    /// before the suffix.
    /// </summary>
    [Fact]
    public async Task Pruning_durable_history_advances_the_floor_and_produces_an_exact_gap()
    {
        var conversation = await OpenAsync();

        for (ulong ordinal = 1; ordinal <= 4; ordinal++)
            await AppendAsync(conversation.ConversationId, EventRetentionClass.Durable, ordinal);

        // Age only the first two, so there is surviving durable history for the floor to land on.
        await fixture.ExecuteAsync($"""
            UPDATE conversation_events
               SET emitted_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 400 DAY)
             WHERE conversation_id = '{conversation.ConversationId}' AND seq <= 2
            """);

        var swept = await Collector().SweepOnceAsync();

        Assert.Equal(2, swept.DurableEvents);
        Assert.Equal(1, swept.FloorsAdvanced);

        // The floor is the lowest SURVIVING durable seq — a position a reader can actually read
        // from, rather than the highest deleted one plus one, which an ephemeral row could occupy.
        Assert.Equal("3", await FloorAsync(conversation.ConversationId));

        var read = await _store.ReadAsync(new ReadConversationRequest
        {
            ConversationId = conversation.ConversationId,
            AfterSeq = 0,
            Limit = 100,
            PrincipalId = "p_owner",
        });

        Assert.NotNull(read.Gap);
        Assert.Equal(1UL, read.Gap!.FromSeq);
        Assert.Equal(2UL, read.Gap.ToSeq);
        Assert.Equal(3UL, read.Gap.RetainedFloorSeq);

        // The suffix follows the gap, and starts at the floor.
        Assert.Equal(3UL, read.Events[0].Seq);
    }

    /// <summary>
    /// The delete and the floor move are ONE transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted structurally rather than by timing: after the sweep there is no surviving durable
    /// row beneath the floor, in either direction. A separate floor move would leave a window where
    /// the rows are gone and the floor still names them — a reader in that window gets a short page
    /// with no gap and concludes it has the whole suffix, which is a silent loss no cursor can
    /// detect.
    /// </para>
    /// <para>
    /// A timing test for this would be a race that usually passes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task No_durable_row_survives_beneath_the_floor_after_a_sweep()
    {
        var conversation = await OpenAsync();

        for (ulong ordinal = 1; ordinal <= 6; ordinal++)
            await AppendAsync(conversation.ConversationId, EventRetentionClass.Durable, ordinal);

        await fixture.ExecuteAsync($"""
            UPDATE conversation_events
               SET emitted_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 400 DAY)
             WHERE conversation_id = '{conversation.ConversationId}' AND seq <= 4
            """);

        await Collector().SweepOnceAsync();

        // Nothing durable below the floor, and nothing missing above it. Both halves: the first
        // catches a floor that did not move, the second a floor that moved too far.
        Assert.Equal("0", await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events e
              JOIN conversations c ON c.id = e.conversation_id
             WHERE e.conversation_id = '{conversation.ConversationId}'
               AND e.retention_class = 'durable'
               AND e.seq < c.retained_floor_seq
            """));

        Assert.Equal("0", await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversations c
             WHERE c.id = '{conversation.ConversationId}'
               AND c.retained_floor_seq > (SELECT MIN(e.seq) FROM conversation_events e
                                            WHERE e.conversation_id = c.id
                                              AND e.retention_class = 'durable')
            """));
    }

    // ── outbox and claims ────────────────────────────────────────────────────

    /// <summary>
    /// A PENDING outbox row is never collected, however old it is.
    /// </summary>
    /// <remarks>
    /// Pending is work that has not happened yet. Collecting one drops a command or an event with
    /// nothing left anywhere to notice — the submission row still says it was dispatched.
    /// </remarks>
    [Fact]
    public async Task A_pending_outbox_row_is_never_collected()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);

        await fixture.ExecuteAsync($"""
            UPDATE command_outbox
               SET next_attempt_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 400 DAY)
             WHERE submission_id = '{accept.SubmissionId}'
            """);

        await Collector().SweepOnceAsync();

        Assert.Equal("1", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM command_outbox WHERE submission_id = '{accept.SubmissionId}'"));
    }

    [Fact]
    public async Task A_published_outbox_row_past_its_retention_is_collected()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);

        await fixture.ExecuteAsync($"""
            UPDATE command_outbox
               SET state = 'published',
                   published_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 400 DAY)
             WHERE submission_id = '{accept.SubmissionId}'
            """);

        var swept = await Collector().SweepOnceAsync();

        Assert.True(swept.OutboxRows >= 1);
        Assert.Equal("0", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM command_outbox WHERE submission_id = '{accept.SubmissionId}'"));
    }

    /// <summary>
    /// A claim inside its retention survives, and one past it is collected.
    /// </summary>
    /// <remarks>
    /// ⚠️ The retention is the guard, not a housekeeping detail: a <c>done</c> claim within its
    /// window is the only thing standing between a redelivered command and a duplicate turn. This
    /// test exists so that shortening it is a visible change rather than a quiet one.
    /// </remarks>
    [Fact]
    public async Task A_claim_survives_its_retention_and_is_collected_past_it()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);

        await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = messageId,
            Owner = "agent-1",
        });

        await Collector().SweepOnceAsync();

        Assert.Equal("1", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM delivery_claims WHERE claim_key = 'inbound:{messageId}'"));

        await fixture.ExecuteAsync($"""
            UPDATE delivery_claims
               SET claimed_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 400 DAY)
             WHERE claim_key = 'inbound:{messageId}'
            """);

        await Collector().SweepOnceAsync();

        Assert.Equal("0", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM delivery_claims WHERE claim_key = 'inbound:{messageId}'"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<OpenConversationResult> OpenAsync() =>
        await _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = "p_owner",
        });

    private async Task<AcceptSubmissionResult> AcceptAsync(string conversationId) =>
        await _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversationId,
            ExternalSubmissionId = Guid.NewGuid().ToString("n"),
            PayloadFingerprint = PayloadFingerprint.Compute("hello", null, conversationId),
            CommandKind = ConversationEventKind.SubmissionCreate,
            CommandPayloadJson = """{"text":"hello"}""",
        });

    private Task<AppendBatchResult> AppendAsync(
        string conversationId, EventRetentionClass retention, ulong ordinal) =>
        _store.AppendBatchAsync(new AppendBatchRequest
        {
            ConversationId = conversationId,
            Epoch = Epoch,
            Events =
            [
                new StagedEvent
                {
                    Event = new EventDescriptor
                    {
                        Kind = retention == EventRetentionClass.Durable
                            ? ConversationEventKind.TurnNotice
                            : ConversationEventKind.TurnProgress,
                        EventId = Ulid.NewUlid(),
                        PayloadJson = """{"text":"x"}""",
                    },
                    Ordinal = ordinal,
                    RetentionClass = retention,
                },
            ],
        });

    private readonly string Epoch = Ulid.NewUlid();

    private Task AgeEphemeralEventsAsync(string conversationId) =>
        fixture.ExecuteAsync($"""
            UPDATE conversation_events
               SET emitted_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 400 DAY)
             WHERE conversation_id = '{conversationId}' AND retention_class = 'ephemeral'
            """);

    private Task<string> FloorAsync(string conversationId) =>
        fixture.ScalarRowAsync(
            $"SELECT retained_floor_seq FROM conversations WHERE id = '{conversationId}'");

    private async Task<string> MessageIdForAsync(string submissionId) =>
        await fixture.ScalarRowAsync(
            $"SELECT message_id FROM command_outbox WHERE submission_id = '{submissionId}'");
}
