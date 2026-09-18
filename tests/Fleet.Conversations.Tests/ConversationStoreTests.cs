using Fleet.Conversations.Contracts;
using Fleet.Protocol;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The #276 transaction boundaries, exercised against a real MySQL.
/// </summary>
[Collection("mysql")]
public sealed class ConversationStoreTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();

    private async Task<OpenConversationResult> OpenAsync(string? principal = null) =>
        await _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = principal ?? "p_owner",
        });

    private static string Fingerprint(string text, string conversationId) =>
        PayloadFingerprint.Compute(text, null, conversationId);

    private async Task<AcceptSubmissionResult> AcceptAsync(
        string conversationId, string text = "hello", string? key = null) =>
        await _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversationId,
            ExternalSubmissionId = Guid.NewGuid().ToString("n"),
            PayloadFingerprint = Fingerprint(text, conversationId),
            IdempotencyKey = key,
            CommandKind = ConversationEventKind.SubmissionCreate,
            CommandPayloadJson = """{"text":"hello"}""",
        });

    private async Task<string> MessageIdForAsync(string submissionId) =>
        await fixture.ScalarRowAsync(
            $"SELECT message_id FROM command_outbox WHERE submission_id = '{submissionId}'");

    // ───────────────────────────────────────────────────────── open

    [Fact]
    public async Task Opening_twice_resolves_to_the_same_conversation()
    {
        var first = await OpenAsync();

        var second = await _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = await fixture.ScalarRowAsync(
                $"SELECT external_ref FROM conversations WHERE id = '{first.ConversationId}'"),
            PrincipalId = "p_owner",
        });

        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(1UL, first.NextSeq);
        Assert.Equal(1UL, first.RetainedFloorSeq);
    }

    /// <summary>
    /// Deliberately indistinguishable from "does not exist", so there is no existence oracle.
    /// </summary>
    [Fact]
    public async Task A_conversation_belonging_to_another_principal_is_not_found()
    {
        var conversation = await OpenAsync();

        await Assert.ThrowsAsync<ConversationNotFoundException>(() =>
            _store.ReadAsync(new ReadConversationRequest
            {
                ConversationId = conversation.ConversationId,
                AfterSeq = 0,
                Limit = 10,
                PrincipalId = "p_someone_else",
            }));
    }

    // ───────────────────────────────────────────────────────── TX1a

    /// <summary>
    /// A first accept is <c>201</c>, never <c>202</c>, and it carries a real floor.
    /// </summary>
    /// <remarks>
    /// This is the case that catches returning #276's internal <c>accepted_seq</c> column as the
    /// wire field: that column is NULL until the disposition, so the mistake shows up here as a
    /// null rather than as a wrong number.
    /// </remarks>
    [Fact]
    public async Task A_first_accept_is_accepted_and_carries_the_floor()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);

        Assert.Equal(AcceptOutcome.Accepted, accept.Outcome);
        Assert.Equal(1UL, accept.AcceptedSeq);
    }

    /// <summary>
    /// The submission row and its command outbox row are written in ONE transaction.
    /// </summary>
    [Fact]
    public async Task The_command_outbox_row_is_written_with_the_submission()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);

        var counts = await fixture.ScalarRowAsync($"""
            SELECT (SELECT COUNT(*) FROM submissions WHERE id = '{accept.SubmissionId}'),
                   (SELECT COUNT(*) FROM command_outbox WHERE submission_id = '{accept.SubmissionId}')
            """);

        Assert.Equal("1|1", counts);
    }

    /// <summary>
    /// The attempt is leased to the SERVICE from TX1a, so the reconciler's predicate never reads a
    /// NULL lease.
    /// </summary>
    [Fact]
    public async Task The_attempt_is_leased_to_the_service_before_any_disposition()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);

        var lease = await fixture.ScalarRowAsync($"""
            SELECT lease_owner, lease_expires_at IS NOT NULL, state
              FROM execution_attempts WHERE submission_id = '{accept.SubmissionId}'
            """);

        Assert.Equal("fleet-comms:test|1|pending", lease);
    }

    // ───────────────────────────────────────────────────────── idempotency

    [Fact]
    public async Task The_same_key_with_a_different_payload_is_a_conflict_and_writes_nothing()
    {
        var conversation = await OpenAsync();
        await AcceptAsync(conversation.ConversationId, "hello", key: "idem-1");

        var conflict = await AcceptAsync(conversation.ConversationId, "DIFFERENT", key: "idem-1");

        Assert.Equal(AcceptOutcome.Conflict, conflict.Outcome);
        Assert.Equal("1", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM submissions WHERE conversation_id = '{conversation.ConversationId}'"));
    }

    /// <summary>
    /// Fifty concurrent submissions with one key produce exactly one submission, one attempt and one
    /// command.
    /// </summary>
    /// <remarks>
    /// Dropping the unique index on <c>(conversation_id, idempotency_key)</c> is what this catches;
    /// without it these become fifty rows and fifty turns.
    /// </remarks>
    [Fact]
    public async Task Fifty_concurrent_submissions_with_one_key_produce_exactly_one()
    {
        var conversation = await OpenAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => AcceptAsync(conversation.ConversationId, "hello", key: "idem-concurrent")));

        var counts = await fixture.ScalarRowAsync($"""
            SELECT (SELECT COUNT(*) FROM submissions WHERE conversation_id = '{conversation.ConversationId}'),
                   (SELECT COUNT(*) FROM execution_attempts a JOIN submissions s ON s.id = a.submission_id
                     WHERE s.conversation_id = '{conversation.ConversationId}'),
                   (SELECT COUNT(*) FROM command_outbox WHERE conversation_id = '{conversation.ConversationId}')
            """);

        Assert.Equal("1|1|1", counts);

        // Every caller is told about the same submission — none is left without an answer.
        var ids = results.Select(r => r.SubmissionId).Distinct().ToList();
        Assert.Single(ids);
        Assert.All(results, r => Assert.NotEqual(AcceptOutcome.Conflict, r.Outcome));
    }

    // ───────────────────────────────────────────────────────── claim

    [Fact]
    public async Task A_claim_returns_the_identifiers_every_later_call_is_keyed_on()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);

        var claim = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = messageId,
            Owner = "agent-1",
        });

        Assert.Equal(ClaimOutcome.Claimed, claim.Outcome);
        Assert.Equal(accept.SubmissionId, claim.SubmissionId);
        Assert.NotNull(claim.AttemptId);
        Assert.Equal(conversation.ConversationId, claim.ConversationId);
    }

    /// <summary>
    /// A redelivery finds a <c>done</c> claim, starts no second turn, and is told nothing it could
    /// act on.
    /// </summary>
    [Fact]
    public async Task A_redelivered_command_starts_one_turn()
    {
        var (conversation, _, attemptId, messageId, epoch) = await DispositionedAsync(SubmissionDisposition.Ran);

        await _store.StartTurnAsync(new StartTurnRequest
        {
            AttemptId = attemptId, TurnId = "turn-1", Epoch = epoch, Ordinal = 10,
        });

        for (var i = 0; i < 3; i++)
        {
            var redelivery = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
            {
                MessageId = messageId, Owner = "agent-1",
            });

            Assert.Equal(ClaimOutcome.DuplicateDone, redelivery.Outcome);
            Assert.Null(redelivery.AttemptId);
            Assert.Null(redelivery.SubmissionId);
        }

        var started = await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE conversation_id = '{conversation}' AND kind = '{ConversationEventKind.TurnStarted}'
            """);

        Assert.Equal("1", started);
    }

    /// <summary>A live claim held elsewhere is never force-taken.</summary>
    [Fact]
    public async Task A_delivery_claimed_elsewhere_is_reported_as_held()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);

        await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest { MessageId = messageId, Owner = "agent-1" });

        var second = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = messageId, Owner = "agent-2",
        });

        Assert.Equal(ClaimOutcome.HeldElsewhere, second.Outcome);
        Assert.Null(second.AttemptId);
    }

    // ───────────────────────────────────────────────────────── disposition

    private async Task<(string Conversation, string Submission, string Attempt, string MessageId, string Epoch)>
        DispositionedAsync(SubmissionDisposition disposition, string owner = "agent-1")
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);

        var claim = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = messageId, Owner = owner,
        });

        var epoch = Ulid.NewUlid();

        if (disposition is SubmissionDisposition.QueueFull or SubmissionDisposition.Dropped)
        {
            await _store.CompleteDeliveryAsync(new CompleteDeliveryRequest
            {
                MessageId = messageId,
                SubmissionId = claim.SubmissionId!,
                AttemptId = claim.AttemptId!,
                Disposition = disposition,
                Epoch = epoch,
                Ordinal = 1,
            });
        }
        else
        {
            await _store.RecordDispositionAsync(new RecordDispositionRequest
            {
                MessageId = messageId,
                SubmissionId = claim.SubmissionId!,
                AttemptId = claim.AttemptId!,
                Disposition = disposition,
                Epoch = epoch,
                Ordinal = 1,
                Owner = owner,
            });
        }

        return (conversation.ConversationId, claim.SubmissionId!, claim.AttemptId!, messageId, epoch);
    }

    /// <summary>
    /// Ownership transfers at the DISPOSITION, not at turn start — asserted for a queued attempt
    /// too, since that is the case the wrong reading breaks.
    /// </summary>
    [Theory]
    [InlineData(SubmissionDisposition.Ran)]
    [InlineData(SubmissionDisposition.Injected)]
    [InlineData(SubmissionDisposition.Queued)]
    public async Task The_lease_transfers_to_the_agent_at_the_disposition(SubmissionDisposition disposition)
    {
        var (_, _, attemptId, _, _) = await DispositionedAsync(disposition);

        Assert.Equal("agent-1", await fixture.ScalarRowAsync(
            $"SELECT lease_owner FROM execution_attempts WHERE id = '{attemptId}'"));
    }

    /// <summary>All five dispositions end with the claim <c>done</c> and no held row.</summary>
    [Theory]
    [InlineData(SubmissionDisposition.Ran)]
    [InlineData(SubmissionDisposition.Injected)]
    [InlineData(SubmissionDisposition.Queued)]
    [InlineData(SubmissionDisposition.QueueFull)]
    [InlineData(SubmissionDisposition.Dropped)]
    public async Task Every_disposition_completes_its_claim(SubmissionDisposition disposition)
    {
        var (_, _, _, messageId, _) = await DispositionedAsync(disposition);

        Assert.Equal("done", await fixture.ScalarRowAsync(
            $"SELECT state FROM delivery_claims WHERE claim_key = 'inbound:{messageId}'"));
    }

    /// <summary><c>submission.accepted</c> is appended exactly once, on every path.</summary>
    [Theory]
    [InlineData(SubmissionDisposition.Ran)]
    [InlineData(SubmissionDisposition.Injected)]
    [InlineData(SubmissionDisposition.Queued)]
    [InlineData(SubmissionDisposition.QueueFull)]
    [InlineData(SubmissionDisposition.Dropped)]
    public async Task Submission_accepted_is_appended_exactly_once(SubmissionDisposition disposition)
    {
        var (_, submissionId, _, _, _) = await DispositionedAsync(disposition);

        Assert.Equal("1", await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE submission_id = '{submissionId}' AND kind = '{ConversationEventKind.SubmissionAccepted}'
            """));
    }

    /// <summary>
    /// The two disposition calls are alternatives, never a sequence: recording in both would append
    /// <c>submission.accepted</c> twice for one submission.
    /// </summary>
    [Fact]
    public async Task Each_disposition_call_refuses_the_other_dispositions()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);
        var claim = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest { MessageId = messageId, Owner = "a" });

        await Assert.ThrowsAsync<ArgumentException>(() => _store.RecordDispositionAsync(
            new RecordDispositionRequest
            {
                MessageId = messageId, SubmissionId = claim.SubmissionId!, AttemptId = claim.AttemptId!,
                Disposition = SubmissionDisposition.Dropped, Epoch = Ulid.NewUlid(), Ordinal = 1, Owner = "a",
            }));

        await Assert.ThrowsAsync<ArgumentException>(() => _store.CompleteDeliveryAsync(
            new CompleteDeliveryRequest
            {
                MessageId = messageId, SubmissionId = claim.SubmissionId!, AttemptId = claim.AttemptId!,
                Disposition = SubmissionDisposition.Ran, Epoch = Ulid.NewUlid(), Ordinal = 1,
            }));
    }

    /// <summary>
    /// A terminal-on-arrival command is fully resolved in one transaction, and its lease is released
    /// so the reconciler has nothing to act on.
    /// </summary>
    [Theory]
    [InlineData(SubmissionDisposition.QueueFull)]
    [InlineData(SubmissionDisposition.Dropped)]
    public async Task A_command_that_terminates_on_arrival_is_terminal_with_no_turn(
        SubmissionDisposition disposition)
    {
        var (_, submissionId, attemptId, _, _) = await DispositionedAsync(disposition);

        var submission = await fixture.ScalarRowAsync($"""
            SELECT state, accepted_seq = terminal_seq FROM submissions WHERE id = '{submissionId}'
            """);
        Assert.Equal("terminal|1", submission);

        var attempt = await fixture.ScalarRowAsync($"""
            SELECT state, lease_expires_at IS NULL FROM execution_attempts WHERE id = '{attemptId}'
            """);
        Assert.Equal("committed|1", attempt);

        Assert.Equal("0", await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE submission_id = '{submissionId}' AND kind = '{ConversationEventKind.TurnStarted}'
            """));
    }

    // ───────────────────────────────────────────────────────── south idempotency

    /// <summary>
    /// A retried south write changes no row and returns the recorded result, so a lost response
    /// cannot append a second event or fail a legitimate retry.
    /// </summary>
    [Fact]
    public async Task A_retried_disposition_writes_nothing_and_replays()
    {
        var (_, submissionId, attemptId, messageId, epoch) = await DispositionedAsync(SubmissionDisposition.Ran);

        var replay = await _store.RecordDispositionAsync(new RecordDispositionRequest
        {
            MessageId = messageId, SubmissionId = submissionId, AttemptId = attemptId,
            Disposition = SubmissionDisposition.Ran, Epoch = epoch, Ordinal = 2, Owner = "agent-1",
        });

        Assert.True(replay.Replayed);
        Assert.Equal(1UL, replay.AcceptedSeq);
        Assert.Equal("1", await fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE submission_id = '{submissionId}' AND kind = '{ConversationEventKind.SubmissionAccepted}'
            """));
    }

    [Fact]
    public async Task A_retried_turn_start_replays_and_a_different_turn_id_is_refused()
    {
        var (_, _, attemptId, _, epoch) = await DispositionedAsync(SubmissionDisposition.Ran);

        var first = await _store.StartTurnAsync(new StartTurnRequest
        {
            AttemptId = attemptId, TurnId = "turn-1", Epoch = epoch, Ordinal = 10,
        });

        var replay = await _store.StartTurnAsync(new StartTurnRequest
        {
            AttemptId = attemptId, TurnId = "turn-1", Epoch = epoch, Ordinal = 11,
        });

        Assert.True(replay.Replayed);
        Assert.Equal(first.Seq, replay.Seq);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.StartTurnAsync(
            new StartTurnRequest
            {
                AttemptId = attemptId, TurnId = "turn-DIFFERENT", Epoch = epoch, Ordinal = 12,
            }));
    }

    [Fact]
    public async Task A_retried_terminal_commit_replays()
    {
        var (_, _, attemptId, _, epoch) = await DispositionedAsync(SubmissionDisposition.Ran);

        await _store.StartTurnAsync(new StartTurnRequest
        {
            AttemptId = attemptId, TurnId = "turn-1", Epoch = epoch, Ordinal = 10,
        });

        var terminal = new EventDescriptor
        {
            Kind = ConversationEventKind.TurnFinal,
            EventId = Ulid.NewUlid(),
            PayloadJson = """{"text":"done"}""",
        };

        var first = await _store.CommitTerminalAsync(new CommitTerminalRequest
        {
            AttemptId = attemptId, TerminalEvent = terminal, Epoch = epoch, Ordinal = 20,
        });

        var replay = await _store.CommitTerminalAsync(new CommitTerminalRequest
        {
            AttemptId = attemptId, TerminalEvent = terminal, Epoch = epoch, Ordinal = 21,
        });

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Seq, replay.Seq);
    }

    // ───────────────────────────────────────────────────────── leases

    /// <summary>
    /// The heartbeat renews PENDING as well as running attempts.
    /// </summary>
    /// <remarks>
    /// A queued submission stays pending behind a running turn. Renewing only running attempts would
    /// abandon a whole queue at the lease bound and report it to the client as an unknown outcome —
    /// which is why this asserts the queued case specifically.
    /// </remarks>
    [Fact]
    public async Task A_heartbeat_renews_a_queued_attempt_that_has_not_started()
    {
        var (_, _, attemptId, _, _) = await DispositionedAsync(SubmissionDisposition.Queued);

        await fixture.ExecuteAsync(
            $"UPDATE execution_attempts SET lease_expires_at = UTC_TIMESTAMP(6) WHERE id = '{attemptId}'");

        var result = await _store.HeartbeatAsync(new HeartbeatRequest
        {
            AttemptIds = [attemptId], Owner = "agent-1",
        });

        Assert.Equal([attemptId], result.Renewed);
        Assert.Empty(result.NotOwned);

        Assert.Equal("pending|1", await fixture.ScalarRowAsync($"""
            SELECT state, lease_expires_at > UTC_TIMESTAMP(6)
              FROM execution_attempts WHERE id = '{attemptId}'
            """));
    }

    [Fact]
    public async Task A_heartbeat_for_an_attempt_owned_by_someone_else_reports_it_as_not_owned()
    {
        var (_, _, attemptId, _, _) = await DispositionedAsync(SubmissionDisposition.Ran);

        var result = await _store.HeartbeatAsync(new HeartbeatRequest
        {
            AttemptIds = [attemptId], Owner = "a-different-agent",
        });

        Assert.Empty(result.Renewed);
        Assert.Equal([attemptId], result.NotOwned);
    }

    // ───────────────────────────────────────────────────────── catch-up

    /// <summary>
    /// <c>afterSeq = acceptedSeq - 1</c> returns the submission's whole lifecycle, in order.
    /// </summary>
    [Fact]
    public async Task Catch_up_from_the_accept_floor_returns_the_whole_lifecycle()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);
        var claim = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest { MessageId = messageId, Owner = "a" });
        var epoch = Ulid.NewUlid();

        await _store.RecordDispositionAsync(new RecordDispositionRequest
        {
            MessageId = messageId, SubmissionId = claim.SubmissionId!, AttemptId = claim.AttemptId!,
            Disposition = SubmissionDisposition.Ran, Epoch = epoch, Ordinal = 1, Owner = "a",
        });

        await _store.StartTurnAsync(new StartTurnRequest
        {
            AttemptId = claim.AttemptId!, TurnId = "turn-1", Epoch = epoch, Ordinal = 10,
        });

        await _store.CommitTerminalAsync(new CommitTerminalRequest
        {
            AttemptId = claim.AttemptId!,
            TerminalEvent = new EventDescriptor
            {
                Kind = ConversationEventKind.TurnFinal,
                EventId = Ulid.NewUlid(),
                PayloadJson = """{"text":"done"}""",
            },
            Epoch = epoch,
            Ordinal = 20,
        });

        var read = await _store.ReadAsync(new ReadConversationRequest
        {
            ConversationId = conversation.ConversationId,
            AfterSeq = accept.AcceptedSeq!.Value - 1,
            Limit = 200,
            PrincipalId = "p_owner",
        });

        Assert.Null(read.Gap);
        Assert.Equal(
            [ConversationEventKind.SubmissionAccepted, ConversationEventKind.TurnStarted, ConversationEventKind.TurnFinal],
            read.Events.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task The_same_catch_up_request_twice_returns_the_same_body()
    {
        var conversation = await OpenAsync();
        await DispositionedForAsync(conversation.ConversationId);

        var request = new ReadConversationRequest
        {
            ConversationId = conversation.ConversationId, AfterSeq = 0, Limit = 200, PrincipalId = "p_owner",
        };

        var first = await _store.ReadAsync(request);
        var second = await _store.ReadAsync(request);

        Assert.Equal(
            first.Events.Select(e => (e.Seq, e.EventId, e.Kind)),
            second.Events.Select(e => (e.Seq, e.EventId, e.Kind)));
        Assert.Equal(first.NextAfterSeq, second.NextAfterSeq);
        Assert.Equal(first.HasMore, second.HasMore);
    }

    private async Task DispositionedForAsync(string conversationId)
    {
        var accept = await AcceptAsync(conversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);
        var claim = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest { MessageId = messageId, Owner = "a" });

        await _store.RecordDispositionAsync(new RecordDispositionRequest
        {
            MessageId = messageId, SubmissionId = claim.SubmissionId!, AttemptId = claim.AttemptId!,
            Disposition = SubmissionDisposition.Ran, Epoch = Ulid.NewUlid(), Ordinal = 1, Owner = "a",
        });
    }

    /// <summary>Never clamped. Both of these hide a real client fault behind partial history.</summary>
    [Fact]
    public async Task An_over_range_cursor_and_an_over_range_limit_are_both_rejected()
    {
        var conversation = await OpenAsync();

        await Assert.ThrowsAsync<InvalidCursorException>(() => _store.ReadAsync(
            new ReadConversationRequest
            {
                ConversationId = conversation.ConversationId, AfterSeq = 99, Limit = 10, PrincipalId = "p_owner",
            }));

        await Assert.ThrowsAsync<InvalidCursorException>(() => _store.ReadAsync(
            new ReadConversationRequest
            {
                ConversationId = conversation.ConversationId, AfterSeq = 0, Limit = 1001, PrincipalId = "p_owner",
            }));
    }

    // ───────────────────────────────────────────────────────── acceptedSeq identity

    /// <summary>
    /// The value returned at first accept and on an idempotent replay are IDENTICAL, and both are
    /// the stored floor rather than the internal column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ An event is appended between the accept and the disposition <b>on purpose</b>, so that the
    /// floor and <c>accepted_seq</c> are DIFFERENT NUMBERS.
    /// </para>
    /// <para>
    /// Without it they coincide — in a fresh conversation the first accept's floor is 1 and the
    /// <c>submission.accepted</c> that follows also lands at 1 — and returning the internal column
    /// here, which is the mistake this test is named for, passes. Measured: swapping
    /// <c>AcceptFloorSeq</c> for <c>AcceptedSeq</c> in <c>ResolveReplay</c> left the whole suite
    /// green before this line existed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Accepted_seq_means_one_thing_everywhere()
    {
        var conversation = await OpenAsync();
        var first = await AcceptAsync(conversation.ConversationId, "hello", key: "idem-same");

        // Advance the log so the floor and the accepted_seq cannot be the same number.
        await _store.AppendBatchAsync(new AppendBatchRequest
        {
            ConversationId = conversation.ConversationId,
            Epoch = Ulid.NewUlid(),
            Events =
            [
                new StagedEvent
                {
                    Event = new EventDescriptor
                    {
                        Kind = ConversationEventKind.TurnNotice,
                        EventId = Ulid.NewUlid(),
                        PayloadJson = """{"text":"between"}""",
                    },
                    Ordinal = 1,
                    RetentionClass = EventRetentionClass.Ephemeral,
                },
            ],
        });

        // Before a disposition there is nothing to replay, so a same-key retry is 202.
        var pending = await AcceptAsync(conversation.ConversationId, "hello", key: "idem-same");
        Assert.Equal(AcceptOutcome.ReplayPending, pending.Outcome);
        Assert.Null(pending.AcceptedSeq);

        var messageId = await MessageIdForAsync(first.SubmissionId!);
        var claim = await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest { MessageId = messageId, Owner = "a" });

        await _store.RecordDispositionAsync(new RecordDispositionRequest
        {
            MessageId = messageId, SubmissionId = claim.SubmissionId!, AttemptId = claim.AttemptId!,
            Disposition = SubmissionDisposition.Ran, Epoch = Ulid.NewUlid(), Ordinal = 1, Owner = "a",
        });

        var replay = await AcceptAsync(conversation.ConversationId, "hello", key: "idem-same");

        Assert.Equal(AcceptOutcome.Replay, replay.Outcome);
        Assert.Equal(first.AcceptedSeq, replay.AcceptedSeq);

        // STRICTLY below the seq of that submission's submission.accepted event, which is what the
        // intervening append bought: `<=` is satisfied by equality, and equality is exactly the
        // state in which returning the wrong column is invisible.
        var acceptedSeq = ulong.Parse(await fixture.ScalarRowAsync(
            $"SELECT accepted_seq FROM submissions WHERE id = '{first.SubmissionId}'"));

        Assert.True(replay.AcceptedSeq < acceptedSeq,
            $"floor {replay.AcceptedSeq} should be below accepted_seq {acceptedSeq}");
    }

    // ───────────────────────────────────────────────── the guards, asserted directly

    /// <summary>
    /// TX1a takes the conversation row's write lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #276 §4.1 does not say it does; it has to. The accept floor is <c>next_seq</c> as read inside
    /// the accept transaction, and reading it unlocked lets an append land between the read and the
    /// insert, leaving the stored floor pointing past the submission's own first event.
    /// </para>
    /// <para>
    /// Asserted by holding the row from a second connection and watching the accept fail to
    /// complete. The property is otherwise unobservable from outside: removing <c>FOR UPDATE</c>
    /// left all eighty-eight tests green, because the unique index on
    /// <c>(conversation_id, idempotency_key)</c> independently catches the one race the suite
    /// staged. Two guards covering each other means neither is pinned — this pins the lock, and
    /// <see cref="The_idempotency_constraint_is_unique"/> pins the index.
    /// </para>
    /// <para>
    /// ⚠️ The held lock is SHARED. An exclusive one blocks the accept regardless, through the
    /// foreign-key check on the insert — so the first version of this test passed with the store's
    /// lock removed and proved nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Accepting_a_submission_takes_the_conversation_row_lock()
    {
        var conversation = await OpenAsync();
        await using var held = await fixture.HoldSharedConversationLockAsync(conversation.ConversationId);

        var accept = Task.Run(() => AcceptAsync(conversation.ConversationId));

        // Blocked. The lock wait defaults to fifty seconds, so this is not racing a timeout.
        var early = await Task.WhenAny(accept, Task.Delay(TimeSpan.FromMilliseconds(750)));
        Assert.NotSame(accept, early);

        await held.DisposeAsync();

        var result = await accept.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(AcceptOutcome.Accepted, result.Outcome);
    }

    /// <summary>
    /// The idempotency index really is UNIQUE in the applied schema.
    /// </summary>
    /// <remarks>
    /// The schema calls this index the thing standing between fifty concurrent same-key submissions
    /// and fifty rows. Measured, that is not quite true — the row lock alone also holds the line, so
    /// downgrading the index to an ordinary key left every test green. It is a real second line of
    /// defence for any future writer that does not take the lock, and a defence nothing asserts is
    /// one somebody deletes during a refactor with a green build.
    /// </remarks>
    [Fact]
    public async Task The_idempotency_constraint_is_unique()
    {
        var nonUnique = await fixture.ScalarRowAsync(
            """
            SELECT COUNT(*) FROM information_schema.statistics
             WHERE table_schema = DATABASE() AND table_name = 'submissions'
               AND index_name = 'ux_sub_idem' AND non_unique = 1
            """);

        Assert.Equal("0", nonUnique);

        var columns = await fixture.ScalarRowAsync(
            """
            SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index)
              FROM information_schema.statistics
             WHERE table_schema = DATABASE() AND table_name = 'submissions'
               AND index_name = 'ux_sub_idem'
            """);

        Assert.Equal("conversation_id,idempotency_key", columns);
    }
}
