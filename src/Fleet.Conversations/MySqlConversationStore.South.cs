using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations;

public sealed partial class MySqlConversationStore
{
    // ────────────────────────────────────────────────────────────── append primitive

    /// <summary>
    /// Appends one event, allocating its <c>seq</c> under a conversation lock the CALLER already
    /// holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>seq</c> does not exist before the row commits. Every cursor number in the contract depends
    /// on that, and a client that received an event which later vanished has no way to detect the
    /// loss.
    /// </para>
    /// <para>
    /// Ordinals are STRICTLY INCREASING, never contiguous. Coalescing and shedding destroy ordinals
    /// by design, so a contiguity check here would reject legitimate appends.
    /// </para>
    /// </remarks>
    /// <returns>The allocated seq, or null when the event was dropped after a terminal.</returns>
    private async Task<ulong?> AppendEventAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        ConversationRow conversation,
        string eventId,
        string kind,
        string? payloadJson,
        string? submissionId,
        string? attemptId,
        EventRetentionClass retentionClass,
        bool isTerminal,
        string epoch,
        ulong ordinal,
        CancellationToken ct)
    {
        // Idempotent on event_id: an append retried after a lost response returns the existing seq
        // rather than writing a second row.
        await using (var existing = Command(
            "SELECT seq FROM conversation_events WHERE event_id = @eid", connection, transaction))
        {
            existing.Parameters.AddWithValue("@eid", eventId);
            if (await existing.ExecuteScalarAsync(ct) is { } seq and not DBNull)
                return Convert.ToUInt64(seq);
        }

        // Epoch and ordinal gates. ULIDs sort by time, which is why the comparison is meaningful.
        var epochState = conversation.AppenderEpoch;

        if (epochState is not null)
        {
            var comparison = string.CompareOrdinal(epoch, epochState);

            if (comparison < 0)
                throw new StaleAppendException($"epoch {epoch} is older than the conversation's {epochState}");

            if (comparison > 0)
            {
                // A restart. The ordinal counter resets HERE and nowhere else.
                await using var reset = Command(
                    "UPDATE conversations SET appender_epoch = @epoch, last_ordinal = 0 WHERE id = @id",
                    connection, transaction);
                reset.Parameters.AddWithValue("@epoch", epoch);
                reset.Parameters.AddWithValue("@id", conversation.Id);
                await reset.ExecuteNonQueryAsync(ct);

                conversation = conversation with { AppenderEpoch = epoch, LastOrdinal = 0 };
            }
            else if (ordinal <= conversation.LastOrdinal)
            {
                throw new OutOfOrderAppendException(
                    $"ordinal {ordinal} is not above the conversation's last {conversation.LastOrdinal}");
            }
        }
        else
        {
            await using var adopt = Command(
                "UPDATE conversations SET appender_epoch = @epoch, last_ordinal = 0 WHERE id = @id",
                connection, transaction);
            adopt.Parameters.AddWithValue("@epoch", epoch);
            adopt.Parameters.AddWithValue("@id", conversation.Id);
            await adopt.ExecuteNonQueryAsync(ct);

            conversation = conversation with { AppenderEpoch = epoch, LastOrdinal = 0 };
        }

        // I6: nothing may be appended for a submission that already has a terminal — except a single
        // turn.recovered_answer, which is how a genuine answer arriving after abandonment is
        // preserved without overwriting the terminal that won.
        if (submissionId is not null && kind != ConversationEventKind.TurnRecoveredAnswer)
        {
            await using var terminalCheck = Command(
                "SELECT state FROM submissions WHERE id = @id", connection, transaction);
            terminalCheck.Parameters.AddWithValue("@id", submissionId);

            if (await terminalCheck.ExecuteScalarAsync(ct) is string state && state == "terminal")
            {
                _logger.LogInformation(
                    "append after terminal dropped for kind {Kind}", kind);
                return null;
            }
        }

        var allocated = conversation.NextSeq;

        await using (var bump = Command(
            """
            UPDATE conversations
               SET next_seq = next_seq + 1, last_ordinal = @ordinal, last_activity_at = UTC_TIMESTAMP(6)
             WHERE id = @id
            """, connection, transaction))
        {
            bump.Parameters.AddWithValue("@ordinal", ordinal);
            bump.Parameters.AddWithValue("@id", conversation.Id);
            await bump.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = Command(
            """
            INSERT INTO conversation_events
              (conversation_id, seq, event_id, kind, submission_id, attempt_id,
               payload_json, emitted_at, is_terminal, retention_class)
            VALUES (@conv, @seq, @eid, @kind, @sub, @attempt,
                    @payload, UTC_TIMESTAMP(6), @terminal, @retention)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@conv", conversation.Id);
            insert.Parameters.AddWithValue("@seq", allocated);
            insert.Parameters.AddWithValue("@eid", eventId);
            insert.Parameters.AddWithValue("@kind", kind);
            insert.Parameters.AddWithValue("@sub", (object?)submissionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@attempt", (object?)attemptId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@payload", (object?)payloadJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("@terminal", isTerminal);
            insert.Parameters.AddWithValue("@retention", RetentionColumn(retentionClass));
            await insert.ExecuteNonQueryAsync(ct);
        }

        // I7: every appended event has exactly one event_outbox row, written in the SAME
        // transaction. A row written afterwards could be lost while the event survived.
        await using (var outbox = Command(
            """
            INSERT INTO event_outbox (conversation_id, seq, event_id) VALUES (@conv, @seq, @eid)
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("@conv", conversation.Id);
            outbox.Parameters.AddWithValue("@seq", allocated);
            outbox.Parameters.AddWithValue("@eid", eventId);
            await outbox.ExecuteNonQueryAsync(ct);
        }

        return allocated;
    }

    private async Task WriteCommandOutboxAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string messageId, string conversationId, string? submissionId,
        string kind, string payloadJson, CancellationToken ct)
    {
        await using var command = Command(
            """
            INSERT INTO command_outbox (submission_id, conversation_id, message_id, kind, payload_json)
            VALUES (@sub, @conv, @msg, @kind, @payload)
            """, connection, transaction);

        command.Parameters.AddWithValue("@sub", (object?)submissionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@conv", conversationId);
        command.Parameters.AddWithValue("@msg", messageId);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@payload", payloadJson);

        await command.ExecuteNonQueryAsync(ct);
    }

    // ────────────────────────────────────────────────────────────── delivery claim

    /// <inheritdoc/>
    public async Task<ClaimDeliveryResult> ClaimDeliveryAsync(
        ClaimDeliveryRequest request, CancellationToken ct = default)
    {
        var key = "inbound:" + request.MessageId;

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await using var insert = Command(
                """
                INSERT INTO delivery_claims (claim_key, owner, expires_at)
                VALUES (@key, @owner, UTC_TIMESTAMP(6) + INTERVAL @ttl SECOND)
                """, connection, transaction);
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@owner", request.Owner);
            insert.Parameters.AddWithValue("@ttl", (int)_options.ClaimHoldDuration.TotalSeconds);
            await insert.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException e) when (e.Number == DuplicateKeyError)
        {
            await transaction.RollbackAsync(ct);
            return await ResolveExistingClaimAsync(key, request.Owner, ct);
        }

        var identifiers = await ResolveCommandIdentifiersAsync(connection, transaction, request.MessageId, ct);
        await transaction.CommitAsync(ct);

        return new ClaimDeliveryResult
        {
            Outcome = ClaimOutcome.Claimed,
            SubmissionId = identifiers.SubmissionId,
            AttemptId = identifiers.AttemptId,
            ConversationId = identifiers.ConversationId,
        };
    }

    private async Task<ClaimDeliveryResult> ResolveExistingClaimAsync(
        string key, string owner, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using var read = Command(
            """
            SELECT state, expires_at, owner FROM delivery_claims WHERE claim_key = @key FOR UPDATE
            """, connection, transaction);
        read.Parameters.AddWithValue("@key", key);

        string state;
        DateTime expiresAt;

        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                // Collected between the failed insert and this read. Treat as unclaimed.
                await transaction.CommitAsync(ct);
                return new ClaimDeliveryResult { Outcome = ClaimOutcome.HeldElsewhere };
            }

            state = reader.GetString(0);
            expiresAt = reader.GetDateTime(1);
        }

        // A true duplicate. Acknowledge and start NO turn — within its retention this row is the
        // only thing standing between a redelivery and a duplicate turn.
        if (state == "done")
        {
            await transaction.CommitAsync(ct);
            return new ClaimDeliveryResult { Outcome = ClaimOutcome.DuplicateDone };
        }

        // A live owner elsewhere. Never force-taken while live.
        if (expiresAt > DateTime.UtcNow)
        {
            await transaction.CommitAsync(ct);
            return new ClaimDeliveryResult { Outcome = ClaimOutcome.HeldElsewhere };
        }

        // Expired: take it over. Takeover never re-runs a turn that already started — that is the
        // attempt state machine's job, and it refuses a second start of a running attempt.
        await using (var take = Command(
            """
            UPDATE delivery_claims
               SET owner = @owner, claimed_at = UTC_TIMESTAMP(6),
                   expires_at = UTC_TIMESTAMP(6) + INTERVAL @ttl SECOND
             WHERE claim_key = @key AND state = 'held'
            """, connection, transaction))
        {
            take.Parameters.AddWithValue("@owner", owner);
            take.Parameters.AddWithValue("@ttl", (int)_options.ClaimHoldDuration.TotalSeconds);
            take.Parameters.AddWithValue("@key", key);
            await take.ExecuteNonQueryAsync(ct);
        }

        var messageId = key["inbound:".Length..];
        var identifiers = await ResolveCommandIdentifiersAsync(connection, transaction, messageId, ct);
        await transaction.CommitAsync(ct);

        return new ClaimDeliveryResult
        {
            Outcome = ClaimOutcome.Claimed,
            SubmissionId = identifiers.SubmissionId,
            AttemptId = identifiers.AttemptId,
            ConversationId = identifiers.ConversationId,
        };
    }

    private sealed record CommandIdentifiers(string? SubmissionId, string? AttemptId, string? ConversationId);

    private static async Task<CommandIdentifiers> ResolveCommandIdentifiersAsync(
        MySqlConnection connection, MySqlTransaction transaction, string messageId, CancellationToken ct)
    {
        await using var command = Command(
            """
            SELECT c.submission_id, c.conversation_id, a.id
              FROM command_outbox c
              LEFT JOIN execution_attempts a
                ON a.submission_id = c.submission_id AND a.state IN ('pending','running')
             WHERE c.message_id = @msg
             LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("@msg", messageId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new CommandIdentifiers(null, null, null);

        return new CommandIdentifiers(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    // ────────────────────────────────────────────────────────────── disposition (TX1b)

    /// <inheritdoc/>
    public Task<DispositionResult> RecordDispositionAsync(
        RecordDispositionRequest request, CancellationToken ct = default)
    {
        if (request.Disposition is SubmissionDisposition.QueueFull or SubmissionDisposition.Dropped)
            throw new ArgumentException(
                $"{request.Disposition} terminates on arrival and goes through CompleteDeliveryAsync. "
                + "Recording it here as well would append submission.accepted twice for one submission.",
                nameof(request));

        return RecordDispositionCoreAsync(
            request.MessageId, request.SubmissionId, request.AttemptId, request.Disposition,
            request.Epoch, request.Ordinal, request.Owner, terminal: false, ct);
    }

    /// <inheritdoc/>
    public Task<DispositionResult> CompleteDeliveryAsync(
        CompleteDeliveryRequest request, CancellationToken ct = default)
    {
        if (request.Disposition is not (SubmissionDisposition.QueueFull or SubmissionDisposition.Dropped))
            throw new ArgumentException(
                $"{request.Disposition} is dispositioned through RecordDispositionAsync. This call is "
                + "the terminal-on-arrival branch and is an ALTERNATIVE to it, never a sequence after it.",
                nameof(request));

        return RecordDispositionCoreAsync(
            request.MessageId, request.SubmissionId, request.AttemptId, request.Disposition,
            request.Epoch, request.Ordinal, owner: null, terminal: true, ct);
    }

    /// <summary>
    /// The one transaction both disposition calls run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appends <c>submission.accepted</c>, sets <c>accepted_seq</c>, moves the submission, transfers
    /// the lease (or terminates), AND marks the claim <c>done</c> — all inside one
    /// <c>BEGIN…COMMIT</c>. A process kill part-way leaves either both writes or neither, never a
    /// recorded disposition beside a held claim.
    /// </para>
    /// <para>
    /// The claim completes HERE rather than after the turn. A turn can run for minutes and can
    /// legitimately outlive the broker's consumer acknowledgement timeout; when that fires, the
    /// requeued message meets a claim that is still held and not expired, and the consumer
    /// negative-acknowledges it into a loop that repeats for as long as the turn lasts.
    /// </para>
    /// </remarks>
    private async Task<DispositionResult> RecordDispositionCoreAsync(
        string messageId, string submissionId, string attemptId, SubmissionDisposition disposition,
        string epoch, ulong ordinal, string? owner, bool terminal, CancellationToken ct)
    {
        var key = "inbound:" + messageId;

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // A retry after a successful call finds the claim already done, writes nothing, and returns
        // the recorded result — so no event is appended twice and no legitimate retry is refused.
        await using (var claim = Command(
            "SELECT state, accepted_seq FROM delivery_claims WHERE claim_key = @key FOR UPDATE",
            connection, transaction))
        {
            claim.Parameters.AddWithValue("@key", key);
            await using var reader = await claim.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct) && reader.GetString(0) == "done")
            {
                var recorded = reader.IsDBNull(1) ? 0UL : reader.GetUInt64(1);
                await reader.CloseAsync();
                await transaction.CommitAsync(ct);
                return new DispositionResult { AcceptedSeq = recorded, Replayed = true };
            }
        }

        var conversationId = await ReadSubmissionConversationAsync(connection, transaction, submissionId, ct)
            ?? throw new ConversationNotFoundException();

        var conversation = await LockConversationAsync(connection, transaction, conversationId, ct)
            ?? throw new ConversationNotFoundException();

        var payload = FleetProtocolJson.Serialize(new SubmissionAcceptedPayloadShim(DispositionWire(disposition)));

        var seq = await AppendEventAsync(
            connection, transaction, conversation,
            eventId: Ulid.NewUlid(),
            kind: ConversationEventKind.SubmissionAccepted,
            payloadJson: payload,
            submissionId: submissionId,
            attemptId: attemptId,
            retentionClass: EventRetentionClass.Durable,
            isTerminal: false,
            epoch: epoch,
            ordinal: ordinal,
            ct)
            ?? throw new InvalidOperationException(
                "submission.accepted was dropped after a terminal — a submission cannot be terminal "
                + "before its disposition is recorded.");

        if (terminal)
        {
            // queue_full and dropped: terminal in ONE transaction. terminal_seq = accepted_seq.
            await using var terminate = Command(
                """
                UPDATE submissions
                   SET state = 'terminal', disposition = @disposition,
                       accepted_seq = @seq, terminal_seq = @seq
                 WHERE id = @id
                """, connection, transaction);
            terminate.Parameters.AddWithValue("@disposition", DispositionWire(disposition));
            terminate.Parameters.AddWithValue("@seq", seq);
            terminate.Parameters.AddWithValue("@id", submissionId);
            await terminate.ExecuteNonQueryAsync(ct);

            await using var commitAttempt = Command(
                """
                UPDATE execution_attempts
                   SET state = 'committed', committed_at = UTC_TIMESTAMP(6),
                       lease_owner = NULL, lease_expires_at = NULL
                 WHERE id = @id
                """, connection, transaction);
            commitAttempt.Parameters.AddWithValue("@id", attemptId);
            await commitAttempt.ExecuteNonQueryAsync(ct);
        }
        else
        {
            var state = disposition switch
            {
                SubmissionDisposition.Ran => "running",
                SubmissionDisposition.Injected => "merged",
                SubmissionDisposition.Queued => "queued",
                _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
            };

            await using var move = Command(
                """
                UPDATE submissions
                   SET state = @state, disposition = @disposition, accepted_seq = @seq
                 WHERE id = @id
                """, connection, transaction);
            move.Parameters.AddWithValue("@state", state);
            move.Parameters.AddWithValue("@disposition", DispositionWire(disposition));
            move.Parameters.AddWithValue("@seq", seq);
            move.Parameters.AddWithValue("@id", submissionId);
            await move.ExecuteNonQueryAsync(ct);

            // Ownership transfers HERE — the moment the agent records what it decided to do — not at
            // turn start. A queued submission waits behind a turn that may run for many times the
            // lease duration while its attempt is still 'pending'; if the lease only moved at turn
            // start, the reconciler would abandon every queued submission at the lease bound and the
            // client would see an unknown outcome for work that was queued correctly.
            await using var lease = Command(
                """
                UPDATE execution_attempts
                   SET lease_owner = @owner,
                       lease_expires_at = UTC_TIMESTAMP(6) + INTERVAL @lease SECOND
                 WHERE id = @id
                """, connection, transaction);
            lease.Parameters.AddWithValue("@owner", owner ?? _serviceOwner);
            lease.Parameters.AddWithValue("@lease", (int)_options.LeaseDuration.TotalSeconds);
            lease.Parameters.AddWithValue("@id", attemptId);
            await lease.ExecuteNonQueryAsync(ct);
        }

        // Same transaction as the disposition write. Splitting these is how a recorded disposition
        // ends up beside a held claim that nothing will ever complete.
        await using (var done = Command(
            """
            UPDATE delivery_claims
               SET state = 'done', result_ref = @sub, disposition = @disposition,
                   accepted_seq = @seq, attempt_ref = @attempt
             WHERE claim_key = @key
            """, connection, transaction))
        {
            done.Parameters.AddWithValue("@sub", submissionId);
            done.Parameters.AddWithValue("@disposition", DispositionWire(disposition));
            done.Parameters.AddWithValue("@seq", seq);
            done.Parameters.AddWithValue("@attempt", attemptId);
            done.Parameters.AddWithValue("@key", key);
            await done.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new DispositionResult { AcceptedSeq = seq, Replayed = false };
    }

    /// <summary>Minimal shim so the accepted payload serializes with the protocol's options.</summary>
    private sealed record SubmissionAcceptedPayloadShim(string Disposition);

    internal static string DispositionWire(SubmissionDisposition disposition) =>
        disposition switch
        {
            SubmissionDisposition.Ran => "ran",
            SubmissionDisposition.Injected => "injected",
            SubmissionDisposition.Queued => "queued",
            SubmissionDisposition.QueueFull => "queue_full",
            SubmissionDisposition.Dropped => "dropped",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };

    private static async Task<string?> ReadSubmissionConversationAsync(
        MySqlConnection connection, MySqlTransaction transaction, string submissionId, CancellationToken ct)
    {
        await using var command = Command(
            "SELECT conversation_id FROM submissions WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("@id", submissionId);
        return await command.ExecuteScalarAsync(ct) as string;
    }
}

/// <summary>An append from an epoch older than the conversation's current appender.</summary>
public sealed class StaleAppendException(string message) : Exception(message);

/// <summary>An append whose ordinal did not advance. Appenders retry in order.</summary>
public sealed class OutOfOrderAppendException(string message) : Exception(message);
