using System.Text.Json;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The durable transcript entry for an accepted submission (#305), against a real MySQL.
/// </summary>
/// <remarks>
/// <para>
/// The defect this closes is that a client which reconnected and caught up from its cursor got the
/// agent's events and nothing it had sent itself — answers with no questions. So almost every
/// assertion here is made by <b>reading the store back</b> rather than by trusting the value a
/// write returned: the whole question is what a reader finds later.
/// </para>
/// <para>
/// The store is not a fake for the same reason. Seq allocation, the conversation row lock and the
/// retained floor are all database behaviour, and a substitute agrees with whatever it was told.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class SubmissionTranscriptTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();

    private const string Owner = "p_owner";

    // ── the headline case ────────────────────────────────────────────────────

    /// <summary>
    /// A submission's own text is durable at accept, at the seq the accept reports, and a client
    /// that kept nothing locally finds it by catching up from <c>acceptedSeq - 1</c>.
    /// </summary>
    /// <remarks>
    /// The cursor arithmetic is asserted rather than assumed. <c>afterSeq</c> means "processed up to
    /// and including", so the documented way to replay a submission's whole lifecycle is
    /// <c>acceptedSeq - 1</c>; an implementation that appended the entry anywhere else would still
    /// store the text and would still fail here.
    /// </remarks>
    [Fact]
    public async Task An_accepted_submission_is_readable_as_its_own_text_from_the_reported_seq()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId, "what is open?");

        Assert.Equal(AcceptOutcome.Accepted, accept.Outcome);

        var page = await ReadFromAsync(conversation.ConversationId, accept.AcceptedSeq!.Value - 1);

        var entry = Assert.Single(page.Events);
        Assert.Equal(ConversationEventKind.SubmissionText, entry.Kind);
        Assert.Equal(accept.AcceptedSeq!.Value, entry.Seq);
        Assert.Equal(accept.SubmissionId, entry.SubmissionId);
        Assert.Equal("what is open?", TextOf(entry));

        // The reported floor is now a seq that EXISTS. Before this change it was a prediction, and
        // an off-by-one would have been invisible because there was no event to disagree with.
        Assert.Equal(
            accept.AcceptedSeq!.Value.ToString(),
            await fixture.ScalarRowAsync(
                $"""
                 SELECT seq FROM conversation_events
                  WHERE conversation_id = '{conversation.ConversationId}'
                    AND kind = '{ConversationEventKind.SubmissionText}'
                 """));
    }

    /// <summary>
    /// The entry sorts before every event the resulting turn produces — the disposition, the turn
    /// start and the terminal.
    /// </summary>
    /// <remarks>
    /// This is the ordering half of the contract. Appending the text at the DISPOSITION instead
    /// would satisfy "it is in the transcript" and still put the user's message after events it
    /// caused on the `ran` path, where the agent can start a turn before the disposition lands.
    /// </remarks>
    [Fact]
    public async Task The_entry_sorts_before_every_event_the_turn_produces()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId, "hello");

        await RunToTerminalAsync(accept.SubmissionId!);

        var kinds = await KindsInSeqOrderAsync(conversation.ConversationId);

        Assert.Equal(
            [
                ConversationEventKind.SubmissionText,
                ConversationEventKind.SubmissionAccepted,
                ConversationEventKind.TurnStarted,
                ConversationEventKind.TurnFinal,
            ],
            kinds);
    }

    /// <summary>
    /// Two submissions on one conversation keep their accept order, and neither takes the other's
    /// seq.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second assertion is the one worth keeping. Before this change no accept advanced
    /// <c>next_seq</c>, so two accepts on one conversation both stored the SAME
    /// <c>accept_floor_seq</c> — two submissions claiming one position. Now each consumes a seq
    /// under the conversation row lock, which is what makes "cannot interleave ahead of an earlier
    /// submission" true rather than likely.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_submissions_keep_their_accept_order_and_never_share_a_seq()
    {
        var conversation = await OpenAsync();

        var first = await AcceptAsync(conversation.ConversationId, "first");
        var second = await AcceptAsync(conversation.ConversationId, "second");

        Assert.NotEqual(first.AcceptedSeq, second.AcceptedSeq);
        Assert.True(second.AcceptedSeq > first.AcceptedSeq);

        var page = await ReadFromAsync(conversation.ConversationId, 0);

        Assert.Equal(["first", "second"], page.Events.Select(TextOf).ToArray());
    }

    /// <summary>
    /// A cancel goes through the same transaction and leaves no transcript entry.
    /// </summary>
    /// <remarks>
    /// A cancel is a control action, not something the owner said. An entry for it would render a
    /// button press as a message, and the client has no way to tell the two apart once they are the
    /// same kind.
    /// </remarks>
    [Fact]
    public async Task A_cancel_leaves_no_transcript_entry()
    {
        var conversation = await OpenAsync();

        await _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversation.ConversationId,
            ExternalSubmissionId = Ulid.NewUlid(),
            PayloadFingerprint = PayloadFingerprint.Compute("current", null, conversation.ConversationId),
            CommandKind = ConversationEventKind.SubmissionCancel,
            CommandPayloadJson = """{"scope":"current"}""",

            // No TranscriptText. Stated here so the test fails if a caller starts supplying one.
        });

        Assert.Equal("0", await fixture.ScalarRowAsync(
            $"""
             SELECT COUNT(*) FROM conversation_events
              WHERE conversation_id = '{conversation.ConversationId}'
                AND kind = '{ConversationEventKind.SubmissionText}'
             """));

        // And the command still reached the agent — the cancel is durable, it simply is not speech.
        Assert.Equal("1", await fixture.ScalarRowAsync(
            $"""
             SELECT COUNT(*) FROM command_outbox
              WHERE conversation_id = '{conversation.ConversationId}'
                AND kind = '{ConversationEventKind.SubmissionCancel}'
             """));
    }

    // ── exactly once ─────────────────────────────────────────────────────────

    /// <summary>
    /// A same-key retry replays the original and appends no second entry.
    /// </summary>
    [Fact]
    public async Task An_idempotent_retry_appends_no_second_entry()
    {
        var conversation = await OpenAsync();

        var first = await AcceptAsync(conversation.ConversationId, "hello", key: "k-1");
        var retry = await AcceptAsync(conversation.ConversationId, "hello", key: "k-1");

        Assert.Equal(AcceptOutcome.ReplayPending, retry.Outcome);
        Assert.Equal(first.SubmissionId, retry.SubmissionId);

        Assert.Equal("1", await fixture.ScalarRowAsync(
            $"""
             SELECT COUNT(*) FROM conversation_events
              WHERE conversation_id = '{conversation.ConversationId}'
                AND kind = '{ConversationEventKind.SubmissionText}'
             """));
    }

    /// <summary>
    /// A same-key retry carrying DIFFERENT text is a conflict, and changes nothing — the stored
    /// entry still reads as the text that was actually accepted.
    /// </summary>
    /// <remarks>
    /// Without this, a client that reused a key by accident could rewrite what its own transcript
    /// says it said.
    /// </remarks>
    [Fact]
    public async Task A_conflicting_retry_does_not_rewrite_the_stored_text()
    {
        var conversation = await OpenAsync();

        await AcceptAsync(conversation.ConversationId, "the real message", key: "k-1");
        var conflict = await AcceptAsync(conversation.ConversationId, "something else", key: "k-1");

        Assert.Equal(AcceptOutcome.Conflict, conflict.Outcome);

        var page = await ReadFromAsync(conversation.ConversationId, 0);
        var entry = Assert.Single(page.Events);

        Assert.Equal("the real message", TextOf(entry));
    }

    /// <summary>
    /// Replaying the same cursor twice yields the same entry, with the same <c>eventId</c> — the
    /// key a client dedupes on.
    /// </summary>
    [Fact]
    public async Task Replaying_the_same_cursor_twice_yields_the_same_entry()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId, "hello");

        var first = await ReadFromAsync(conversation.ConversationId, accept.AcceptedSeq!.Value - 1);
        var second = await ReadFromAsync(conversation.ConversationId, accept.AcceptedSeq!.Value - 1);

        Assert.Equal(
            first.Events.Select(e => (e.Seq, e.EventId, TextOf(e))),
            second.Events.Select(e => (e.Seq, e.EventId, TextOf(e))));
    }

    // ── steered and coalesced: one entry each, never merged away ─────────────

    /// <summary>
    /// A steer gets its own entry, at its own seq, exactly like a create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A steer is a submission the user sent while a turn was already running — a correction typed
    /// in a hurry, which is precisely the message a transcript most needs to keep. It reaches the
    /// store through the same accept transaction with a different <c>CommandKind</c>, and nothing
    /// in that transaction branches on the kind.
    /// </para>
    /// <para>
    /// "Nothing branches on it" is the kind of claim that is true until someone adds a branch, so
    /// it is pinned here rather than left to the reader of <c>AcceptSubmissionAsync</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_steer_gets_its_own_entry_at_its_own_seq()
    {
        var conversation = await OpenAsync();

        var create = await AcceptAsync(conversation.ConversationId, "what is open?");
        var steer = await AcceptAsync(
            conversation.ConversationId, "actually, just the blockers",
            commandKind: ConversationEventKind.SubmissionSteer);

        Assert.NotEqual(create.AcceptedSeq, steer.AcceptedSeq);

        var page = await ReadFromAsync(conversation.ConversationId, 0);

        Assert.Equal(
            ["what is open?", "actually, just the blockers"],
            page.Events.Select(TextOf).ToArray());

        // The steer's own entry is at the seq its own accept reported — the same contract a create
        // gets, so a client replays a steer from `acceptedSeq - 1` without a special case.
        var steerEntry = page.Events.Single(e => e.SubmissionId == steer.SubmissionId);
        Assert.Equal(ConversationEventKind.SubmissionText, steerEntry.Kind);
        Assert.Equal(steer.AcceptedSeq!.Value, steerEntry.Seq);

        // The command really did dispatch as a steer — otherwise this test would pass while
        // asserting nothing about steers at all.
        Assert.Equal(
            ConversationEventKind.SubmissionSteer,
            await fixture.ScalarRowAsync(
                $"SELECT kind FROM command_outbox WHERE submission_id = '{steer.SubmissionId}'"));
    }

    /// <summary>
    /// Two submissions coalesced into one turn keep both entries, at distinct seqs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coalescing is what the agent does when a second message arrives mid-turn: it folds it into
    /// the running turn and answers both at once, so ONE terminal closes TWO submissions through
    /// <c>MergedSubmissionIds</c>. The turn-level events collapse by design — that is the point of
    /// coalescing — and the transcript entries must not, or the visible conversation silently loses
    /// exactly the messages the user sent in a hurry.
    /// </para>
    /// <para>
    /// The sequence below is the real one, not a convenient one: the host is dispositioned and
    /// started before the child arrives, because a child that arrived earlier would have been
    /// queued rather than merged.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_submissions_coalesced_into_one_turn_keep_both_entries()
    {
        var conversation = await OpenAsync();

        var host = await AcceptAsync(conversation.ConversationId, "summarise the open PRs");

        var hostClaim = await ClaimAsync(host.SubmissionId!);
        var epoch = Ulid.NewUlid();

        await _store.RecordDispositionAsync(new RecordDispositionRequest
        {
            MessageId = await MessageIdAsync(host.SubmissionId!),
            SubmissionId = hostClaim.SubmissionId!,
            AttemptId = hostClaim.AttemptId!,
            Disposition = SubmissionDisposition.Ran,
            Epoch = epoch,
            Ordinal = 1,
            Owner = "agent-1",
        });

        await _store.StartTurnAsync(new StartTurnRequest
        {
            AttemptId = hostClaim.AttemptId!,
            TurnId = "t-" + Ulid.NewUlid(),
            Epoch = epoch,
            Ordinal = 2,
        });

        // The child arrives while that turn is running, and is injected into it.
        var child = await AcceptAsync(conversation.ConversationId, "and only the blocked ones");
        var childClaim = await ClaimAsync(child.SubmissionId!);

        await _store.RecordDispositionAsync(new RecordDispositionRequest
        {
            MessageId = await MessageIdAsync(child.SubmissionId!),
            SubmissionId = childClaim.SubmissionId!,
            AttemptId = childClaim.AttemptId!,
            Disposition = SubmissionDisposition.Injected,
            Epoch = epoch,
            Ordinal = 3,
            Owner = "agent-1",
        });

        // ONE terminal answers BOTH.
        await _store.CommitTerminalAsync(new CommitTerminalRequest
        {
            AttemptId = hostClaim.AttemptId!,
            TerminalEvent = new EventDescriptor
            {
                Kind = ConversationEventKind.TurnFinal,
                EventId = Ulid.NewUlid(),
                PayloadJson = FleetProtocolJson.Serialize(new TurnFinalPayload
                {
                    Text = "two of them are blocked",
                    Completion = TurnCompletion.Completed,
                    IsPartial = false,
                    Truncated = false,
                    MergedSubmissionIds = [hostClaim.SubmissionId!, childClaim.SubmissionId!],
                }),
            },
            MergedSubmissionIds = [childClaim.SubmissionId!],
            Epoch = epoch,
            Ordinal = 4,
        });

        var page = await ReadFromAsync(conversation.ConversationId, 0);

        var entries = page.Events
            .Where(e => e.Kind == ConversationEventKind.SubmissionText)
            .ToArray();

        // Both messages survive the coalescing, in the order they were sent.
        Assert.Equal(
            ["summarise the open PRs", "and only the blocked ones"],
            entries.Select(TextOf).ToArray());

        // Distinct seqs, and each attributed to its own submission — two entries sharing a seq, or
        // one entry standing in for both, is the loss this asserts against.
        Assert.Equal(2, entries.Select(e => e.Seq).Distinct().Count());
        Assert.Equal(
            [host.SubmissionId!, child.SubmissionId!],
            entries.Select(e => e.SubmissionId ?? string.Empty).ToArray());

        // The turn events really did collapse — one terminal, not two — so the entries above
        // survived a genuine coalesce rather than two independent turns.
        Assert.Single(page.Events, e => e.IsTerminal);

        // And both submissions are closed by that single terminal, so neither is left waiting.
        Assert.Equal("terminal", await fixture.ScalarRowAsync(
            $"SELECT state FROM submissions WHERE id = '{child.SubmissionId}'"));
    }

    // ── the case that motivated the change ───────────────────────────────────

    /// <summary>
    /// A submission that was accepted and never answered appears in the transcript, and is
    /// distinguishable from one that was answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is why the entry is a new kind rather than a field on <c>submission.accepted</c>. That
    /// event is written at the agent's disposition; an attempt the agent never claims is abandoned
    /// by the reconciler and never gets one, so a field on it would go missing in exactly the case
    /// the client most needs to render — and a local echo would render it identically to a message
    /// that was answered and then lost.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_submission_accepted_but_never_answered_appears_and_is_distinguishable()
    {
        var reconciler = new Reconciler(
            fixture.ConnectionString, fixture.Options, NullLogger<Reconciler>.Instance);
        await reconciler.RecordHealthyAsync();

        var conversation = await OpenAsync();

        var answered = await AcceptAsync(conversation.ConversationId, "answered");
        await RunToTerminalAsync(answered.SubmissionId!);

        var unanswered = await AcceptAsync(conversation.ConversationId, "never answered");

        // Nobody ever claimed it. The lease is expired directly rather than waited out, so the
        // assertion is about the predicate rather than about the clock.
        await fixture.ExecuteAsync($"""
            UPDATE execution_attempts
               SET lease_expires_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 HOUR)
             WHERE submission_id = '{unanswered.SubmissionId}'
            """);

        Assert.Equal(1, (await reconciler.ScanOnceAsync()).Abandoned);

        var page = await ReadFromAsync(conversation.ConversationId, 0);

        // Both messages are in the transcript.
        Assert.Equal(
            ["answered", "never answered"],
            page.Events
                .Where(e => e.Kind == ConversationEventKind.SubmissionText)
                .Select(TextOf)
                .ToArray());

        // And they end differently: one has an answer, the other has an explicitly unknown outcome.
        Assert.Equal(
            ConversationEventKind.TurnFinal,
            page.Events.Single(e => e.SubmissionId == answered.SubmissionId && e.IsTerminal).Kind);

        Assert.Equal(
            ConversationEventKind.TurnOutcomeUnknown,
            page.Events.Single(e => e.SubmissionId == unanswered.SubmissionId && e.IsTerminal).Kind);

        // The unanswered one never got a disposition, which is the fact a field on
        // `submission.accepted` would have been lost behind.
        Assert.DoesNotContain(
            page.Events,
            e => e.SubmissionId == unanswered.SubmissionId
                 && e.Kind == ConversationEventKind.SubmissionAccepted);
    }

    // ── durability and retention ─────────────────────────────────────────────

    /// <summary>
    /// The entry is durable, and its collection is announced as a gap rather than passed over in
    /// silence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retention horizon is the existing <c>DurableEventRetention</c> — this change adds no
    /// second knob, so the transcript is collected by the same rule, at the same time, as every
    /// other durable event.
    /// </para>
    /// <para>
    /// The gap is what makes "caught up" still mean "has everything": a client whose cursor fell
    /// beneath the floor is told what it will never receive, instead of rendering a conversation
    /// that is quietly missing its own messages.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_entry_is_durable_and_its_collection_is_announced_as_a_gap()
    {
        var conversation = await OpenAsync();
        var old = await AcceptAsync(conversation.ConversationId, "an old message");

        Assert.Equal("durable", await fixture.ScalarRowAsync(
            $"""
             SELECT retention_class FROM conversation_events
              WHERE conversation_id = '{conversation.ConversationId}'
                AND kind = '{ConversationEventKind.SubmissionText}'
             """));

        // A second, recent submission, so something survives the sweep and the floor lands on a
        // readable position rather than on next_seq.
        await AcceptAsync(conversation.ConversationId, "a recent message");

        await fixture.ExecuteAsync($"""
            UPDATE conversation_events
               SET emitted_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 400 DAY)
             WHERE conversation_id = '{conversation.ConversationId}'
               AND seq = {old.AcceptedSeq}
            """);

        await new GarbageCollector(
            fixture.ConnectionString, fixture.Options, NullLogger<GarbageCollector>.Instance)
            .SweepOnceAsync();

        var page = await ReadFromAsync(conversation.ConversationId, 0);

        Assert.NotNull(page.Gap);
        Assert.Equal(old.AcceptedSeq!.Value, page.Gap!.FromSeq);
        Assert.Equal(["a recent message"], page.Events.Select(TextOf).ToArray());
    }

    // ── the accept path is not part of the agent's append stream ─────────────

    /// <summary>
    /// Accepting a submission does not touch the agent's appender bookkeeping, so a stale append is
    /// still refused afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The accept transaction appends WITHOUT an ordinal, deliberately. <c>appender_epoch</c> and
    /// <c>last_ordinal</c> order the AGENT's append stream against itself; the accept path is a
    /// different writer, serialized by the conversation row lock instead.
    /// </para>
    /// <para>
    /// ⚠️ <b>This is the test that makes that deliberate.</b> Passing an ordinal from the accept
    /// leaves every other test in this suite green — measured, not assumed — because it only shows
    /// up once the agent has already adopted an epoch: the accept then rewinds <c>last_ordinal</c>
    /// underneath it, and the next stale or replayed append that should have been refused as
    /// out-of-order is silently accepted. A conversation whose second message arrives while the
    /// first turn is running is the ordinary case, not an exotic one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Accepting_a_submission_does_not_rewind_the_agents_ordinal_counter()
    {
        var conversation = await OpenAsync();

        // A first submission the agent picks up, so the conversation has an adopted epoch and a
        // counter that has moved. Without this the accept's damage is wiped by the epoch adoption.
        var first = await AcceptAsync(conversation.ConversationId, "first");
        var (attemptId, epoch) = await DispositionAndStartAsync(first.SubmissionId!);

        var ordinalBefore = await fixture.ScalarRowAsync(
            $"SELECT last_ordinal FROM conversations WHERE id = '{conversation.ConversationId}'");

        Assert.NotEqual("0", ordinalBefore);

        // The second message, arriving while that turn is still running.
        await AcceptAsync(conversation.ConversationId, "second");

        Assert.Equal(
            ordinalBefore,
            await fixture.ScalarRowAsync(
                $"SELECT last_ordinal FROM conversations WHERE id = '{conversation.ConversationId}'"));

        // And behaviourally: an append at an ordinal the agent has already used is still refused.
        // Asserted as well as the column, because the column is the mechanism and this is the
        // property — a future fence keyed on something else must keep this true.
        await Assert.ThrowsAsync<OutOfOrderAppendException>(() =>
            _store.AppendBatchAsync(new AppendBatchRequest
            {
                ConversationId = conversation.ConversationId,
                Epoch = epoch,
                Events =
                [
                    new StagedEvent
                    {
                        Event = new EventDescriptor
                        {
                            Kind = ConversationEventKind.TurnNotice,
                            EventId = Ulid.NewUlid(),
                            PayloadJson = """{"text":"a replayed append"}""",
                        },
                        Ordinal = ulong.Parse(ordinalBefore),
                        AttemptId = attemptId,
                        RetentionClass = EventRetentionClass.Ephemeral,
                    },
                ],
            }));
    }

    // ── limits and rejection ─────────────────────────────────────────────────

    /// <summary>
    /// Over-cap text is refused and nothing becomes durable, so there is no truncated entry.
    /// </summary>
    /// <remarks>
    /// The route refuses an over-cap submission with <c>413</c> before it reaches the store, so this
    /// guard is unreachable in production. It is asserted anyway: "the stored text is never
    /// truncated" is what lets a client render a stored message as the whole message, and a property
    /// nothing checks is a comment. The refusal leaves no submission, no command and no event — a
    /// half-written accept would be worse than a truncated one.
    /// </remarks>
    [Fact]
    public async Task Text_beyond_the_inbound_cap_is_refused_and_writes_nothing()
    {
        var conversation = await OpenAsync();
        var oversize = new string('x', ProtocolLimits.MaxInboundTextBytes + 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AcceptAsync(conversation.ConversationId, oversize));

        Assert.Equal("0", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM submissions WHERE conversation_id = '{conversation.ConversationId}'"));

        Assert.Equal("0", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM conversation_events WHERE conversation_id = '{conversation.ConversationId}'"));

        Assert.Equal("1", await fixture.ScalarRowAsync(
            $"SELECT next_seq FROM conversations WHERE id = '{conversation.ConversationId}'"));
    }

    /// <summary>
    /// Text exactly at the cap is stored whole.
    /// </summary>
    /// <remarks>
    /// The cap is measured in BYTES, and the boundary is checked from both sides so an off-by-one
    /// cannot start refusing a submission the route already accepted.
    /// </remarks>
    [Fact]
    public async Task Text_exactly_at_the_inbound_cap_is_stored_whole()
    {
        var conversation = await OpenAsync();
        var atCap = new string('x', ProtocolLimits.MaxInboundTextBytes);

        var accept = await AcceptAsync(conversation.ConversationId, atCap);
        var page = await ReadFromAsync(conversation.ConversationId, accept.AcceptedSeq!.Value - 1);

        Assert.Equal(atCap, TextOf(Assert.Single(page.Events)));
    }

    /// <summary>
    /// Multi-byte text survives the round trip unchanged, including the astral plane.
    /// </summary>
    /// <remarks>
    /// The column is <c>utf8mb4</c>, and the difference between that and MySQL's <c>utf8</c> is
    /// exactly the characters that would be silently mangled here — an emoji in a stored message is
    /// worth one assertion.
    /// </remarks>
    [Fact]
    public async Task Multi_byte_text_survives_the_round_trip()
    {
        const string text = "привет — что открыто? 🛠️";

        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId, text);

        var page = await ReadFromAsync(conversation.ConversationId, accept.AcceptedSeq!.Value - 1);

        Assert.Equal(text, TextOf(Assert.Single(page.Events)));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<OpenConversationResult> OpenAsync() =>
        _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = Owner,
        });

    /// <param name="commandKind">
    /// <c>submission.create</c> or <c>submission.steer</c>. Both reach the store through the same
    /// transaction — there is no second accept path for a steer — so this is the only difference
    /// between the two in a test.
    /// </param>
    private Task<AcceptSubmissionResult> AcceptAsync(
        string conversationId, string text, string? key = null,
        string commandKind = ConversationEventKind.SubmissionCreate) =>
        _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversationId,
            ExternalSubmissionId = Ulid.NewUlid(),
            PayloadFingerprint = PayloadFingerprint.Compute(text, null, conversationId),
            IdempotencyKey = key,
            CommandKind = commandKind,
            CommandPayloadJson = FleetProtocolJson.Serialize(new { payload = new { text } }),
            TranscriptText = text,
        });

    private Task<string> MessageIdAsync(string submissionId) =>
        fixture.ScalarRowAsync(
            $"SELECT message_id FROM command_outbox WHERE submission_id = '{submissionId}'");

    private async Task<ClaimDeliveryResult> ClaimAsync(string submissionId, string owner = "agent-1") =>
        await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = await MessageIdAsync(submissionId),
            Owner = owner,
        });

    private Task<ReadConversationResult> ReadFromAsync(string conversationId, ulong afterSeq) =>
        _store.ReadAsync(new ReadConversationRequest
        {
            ConversationId = conversationId,
            AfterSeq = afterSeq,
            Limit = 50,
            PrincipalId = Owner,
        });

    /// <summary>
    /// Claim, disposition and start a turn, leaving it running. Returns the attempt and the epoch
    /// the agent adopted.
    /// </summary>
    private async Task<(string AttemptId, string Epoch)> DispositionAndStartAsync(string submissionId)
    {
        var messageId = await fixture.ScalarRowAsync(
            $"SELECT message_id FROM command_outbox WHERE submission_id = '{submissionId}'");

        var claim = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = messageId, Owner = "agent-1",
        });

        var epoch = Ulid.NewUlid();

        await _store.RecordDispositionAsync(new RecordDispositionRequest
        {
            MessageId = messageId,
            SubmissionId = claim.SubmissionId!,
            AttemptId = claim.AttemptId!,
            Disposition = SubmissionDisposition.Ran,
            Epoch = epoch,
            Ordinal = 1,
            Owner = "agent-1",
        });

        await _store.StartTurnAsync(new StartTurnRequest
        {
            AttemptId = claim.AttemptId!,
            TurnId = "t-" + Ulid.NewUlid(),
            Epoch = epoch,
            Ordinal = 2,
        });

        return (claim.AttemptId!, epoch);
    }

    /// <summary>Claim, disposition, start and terminate one submission, as the agent would.</summary>
    private async Task RunToTerminalAsync(string submissionId)
    {
        var messageId = await fixture.ScalarRowAsync(
            $"SELECT message_id FROM command_outbox WHERE submission_id = '{submissionId}'");

        var claim = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = messageId, Owner = "agent-1",
        });

        var epoch = Ulid.NewUlid();

        await _store.RecordDispositionAsync(new RecordDispositionRequest
        {
            MessageId = messageId,
            SubmissionId = claim.SubmissionId!,
            AttemptId = claim.AttemptId!,
            Disposition = SubmissionDisposition.Ran,
            Epoch = epoch,
            Ordinal = 1,
            Owner = "agent-1",
        });

        await _store.StartTurnAsync(new StartTurnRequest
        {
            AttemptId = claim.AttemptId!,
            TurnId = "t-" + Ulid.NewUlid(),
            Epoch = epoch,
            Ordinal = 2,
        });

        await _store.CommitTerminalAsync(new CommitTerminalRequest
        {
            AttemptId = claim.AttemptId!,
            TerminalEvent = new EventDescriptor
            {
                Kind = ConversationEventKind.TurnFinal,
                EventId = Ulid.NewUlid(),
                PayloadJson = FleetProtocolJson.Serialize(new TurnFinalPayload
                {
                    Text = "done",
                    Completion = TurnCompletion.Completed,
                    IsPartial = false,
                    Truncated = false,
                    MergedSubmissionIds = [claim.SubmissionId!],
                }),
            },
            Epoch = epoch,
            Ordinal = 3,
        });
    }

    private async Task<string[]> KindsInSeqOrderAsync(string conversationId)
    {
        var page = await ReadFromAsync(conversationId, 0);
        return page.Events.Select(e => e.Kind).ToArray();
    }

    /// <summary>Reads the text out of a stored event's payload, whatever kind carries one.</summary>
    private static string TextOf(StoredEvent stored) =>
        JsonDocument.Parse(stored.PayloadJson!).RootElement.GetProperty("text").GetString()!;
}
