using System.Text.Json;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations;

/// <summary>
/// Terminates attempts whose lease has expired, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never re-dispatches.</b> An unknown outcome is terminal and is rendered as indeterminate;
/// a retry here would be a second real-world side effect for work that may already have happened.
/// That is MUST NOT 2, and it is the reason this class appends an event and stops.
/// </para>
/// <para>
/// <b>It produces exactly one <c>outcome_unknown</c> reason: <c>attempt_abandoned</c>.</b>
/// <c>turn_reaped</c> belongs to the agent's own run loop and is stored verbatim when it arrives;
/// producing it here would make two components answerable for the same reason and neither
/// answerable for its own.
/// </para>
/// <para>
/// <b>The grace period is what stops a store outage from abandoning everything at once.</b> After
/// this service has been unable to record liveness, every lease in the database looks expired —
/// because nothing was renewing them, not because any agent died. So a scan only acts once the
/// service has been healthy for longer than the grace period, and the agents have had a full
/// heartbeat interval to renew.
/// </para>
/// </remarks>
public sealed class Reconciler(
    string connectionString,
    ConversationStoreOptions options,
    ILogger logger)
{
    /// <summary>What one scan did, for the counter and for tests.</summary>
    public sealed record ScanResult
    {
        /// <summary>Attempts abandoned, each with exactly one <c>attempt_abandoned</c> event.</summary>
        public required int Abandoned { get; init; }

        /// <summary>True when the scan deliberately did nothing because the grace period is open.</summary>
        public required bool WithinGrace { get; init; }
    }

    /// <summary>
    /// How long it has been since this service last recorded that it was alive.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Read BEFORE stamping, or the answer is always zero.</b> The maintenance loop stamps on
    /// every tick, so a caller that stamped first and asked afterwards would measure its own write
    /// and conclude the service had never been away — which is exactly how a grace period that
    /// exists in the code never fires in the deployment.
    /// </remarks>
    public async Task<TimeSpan> ReadHealthGapAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            "SELECT TIMESTAMPDIFF(MICROSECOND, last_healthy_at, UTC_TIMESTAMP(6)) FROM service_health WHERE id = 1",
            connection);

        var raw = await command.ExecuteScalarAsync(ct);

        return raw is null or DBNull
            ? TimeSpan.Zero
            : TimeSpan.FromMicroseconds(Convert.ToDouble(raw));
    }

    /// <summary>Records that this service is alive, so the grace window can be measured.</summary>
    public async Task RecordHealthyAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            "UPDATE service_health SET last_healthy_at = UTC_TIMESTAMP(6) WHERE id = 1", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>One scan. Abandons every attempt whose lease has expired.</summary>
    public async Task<ScanResult> ScanOnceAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        if (await WithinGraceAsync(connection, ct))
        {
            ConversationMetrics.ReconcilerActions.Add(1, new KeyValuePair<string, object?>(
                "outcome", "held_within_grace"));

            return new ScanResult { Abandoned = 0, WithinGrace = true };
        }

        // `pending` and `running` only. A `merged` attempt belongs to its host turn and a
        // `committed` one is finished; abandoning either would append a terminal for work that
        // already has one.
        var expired = new List<(string AttemptId, string SubmissionId, string ConversationId)>();

        await using (var select = new MySqlCommand(
            """
            SELECT a.id, a.submission_id, s.conversation_id
              FROM execution_attempts a
              JOIN submissions s ON s.id = a.submission_id
             WHERE a.state IN ('pending','running')
               AND a.lease_expires_at IS NOT NULL
               AND a.lease_expires_at < UTC_TIMESTAMP(6)
             ORDER BY a.lease_expires_at
             LIMIT @batch
            """, connection))
        {
            select.Parameters.AddWithValue("@batch", options.OutboxBatchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
                expired.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var abandoned = 0;

        foreach (var (attemptId, submissionId, conversationId) in expired)
        {
            if (await AbandonAsync(connection, attemptId, submissionId, conversationId, ct))
            {
                abandoned++;

                ConversationMetrics.ReconcilerActions.Add(1, new KeyValuePair<string, object?>(
                    "outcome", "abandoned"));

                ConversationMetrics.OutcomeUnknown.Add(1, new KeyValuePair<string, object?>(
                    "reason", "attempt_abandoned"));
            }
            else
            {
                // Someone finished it first. Counted, because a sustained stream of these means the
                // scan is racing agents rather than cleaning up after them.
                ConversationMetrics.ReconcilerActions.Add(1, new KeyValuePair<string, object?>(
                    "outcome", "skipped"));
            }
        }

        if (abandoned > 0)
            logger.LogInformation("reconciler abandoned {Count} attempt(s)", abandoned);

        return new ScanResult { Abandoned = abandoned, WithinGrace = false };
    }

    /// <summary>
    /// True while the service has not been continuously healthy for longer than the grace period.
    /// </summary>
    private async Task<bool> WithinGraceAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            "SELECT TIMESTAMPDIFF(MICROSECOND, last_healthy_at, UTC_TIMESTAMP(6)) FROM service_health WHERE id = 1",
            connection);

        var raw = await command.ExecuteScalarAsync(ct);
        if (raw is null or DBNull) return false;

        // A LARGE gap means this service was away and every lease in the database looks expired for
        // a reason that has nothing to do with the agents. Hold off until it has been back for the
        // grace period, which is also long enough for a live agent to have heartbeated.
        var sinceHealthy = TimeSpan.FromMicroseconds(Convert.ToDouble(raw));
        return sinceHealthy > options.ReconcilerGraceAfterRecovery;
    }

    /// <summary>
    /// Abandon one attempt: append the terminal, mark the attempt, close the submission — in ONE
    /// transaction, under the conversation row lock that allocates <c>seq</c>.
    /// </summary>
    /// <remarks>
    /// The <c>WHERE state IN ('pending','running')</c> on the update is the concurrency guard: an
    /// agent committing the turn between the select above and this transaction wins, the update
    /// affects zero rows, and nothing is appended. A successful answer is never destroyed by a
    /// reconciler scan.
    /// </remarks>
    private async Task<bool> AbandonAsync(
        MySqlConnection connection, string attemptId, string submissionId, string conversationId,
        CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await using (var claim = new MySqlCommand(
                """
                UPDATE execution_attempts
                   SET state = 'abandoned', committed_at = UTC_TIMESTAMP(6)
                 WHERE id = @id AND state IN ('pending','running')
                """, connection, transaction))
            {
                claim.Parameters.AddWithValue("@id", attemptId);

                if (await claim.ExecuteNonQueryAsync(ct) == 0)
                {
                    // Someone else finished it first. Not an error, and not something to retry.
                    await transaction.RollbackAsync(ct);
                    return false;
                }
            }

            ulong seq;
            var eventId = Ulid.NewUlid();

            await using (var conversation = new MySqlCommand(
                "SELECT next_seq FROM conversations WHERE id = @id FOR UPDATE",
                connection, transaction))
            {
                conversation.Parameters.AddWithValue("@id", conversationId);
                var value = await conversation.ExecuteScalarAsync(ct);

                if (value is null or DBNull)
                {
                    await transaction.RollbackAsync(ct);
                    return false;
                }

                seq = Convert.ToUInt64(value);
            }

            var payload = JsonSerializer.Serialize(
                new { reason = OutcomeUnknownReason.AttemptAbandoned }, FleetProtocolJson.Options);

            await using (var append = new MySqlCommand(
                """
                INSERT INTO conversation_events
                  (conversation_id, seq, event_id, kind, submission_id, attempt_id,
                   payload_json, emitted_at, is_terminal, retention_class)
                VALUES
                  (@conv, @seq, @event, @kind, @sub, @attempt,
                   @payload, UTC_TIMESTAMP(6), 1, 'durable')
                """, connection, transaction))
            {
                append.Parameters.AddWithValue("@conv", conversationId);
                append.Parameters.AddWithValue("@seq", seq);
                append.Parameters.AddWithValue("@event", eventId);
                append.Parameters.AddWithValue("@kind", ConversationEventKind.TurnOutcomeUnknown);
                append.Parameters.AddWithValue("@sub", submissionId);
                append.Parameters.AddWithValue("@attempt", attemptId);
                append.Parameters.AddWithValue("@payload", payload);
                await append.ExecuteNonQueryAsync(ct);
            }

            await using (var advance = new MySqlCommand(
                "UPDATE conversations SET next_seq = @next, last_activity_at = UTC_TIMESTAMP(6) WHERE id = @id",
                connection, transaction))
            {
                advance.Parameters.AddWithValue("@next", seq + 1);
                advance.Parameters.AddWithValue("@id", conversationId);
                await advance.ExecuteNonQueryAsync(ct);
            }

            // The submission is terminal with this event as its terminal seq. Leaving it
            // `pending_dispatch` would make a legitimately abandoned submission indistinguishable
            // from one the agent never received.
            await using (var close = new MySqlCommand(
                """
                UPDATE submissions
                   SET state = 'terminal', terminal_seq = @seq
                 WHERE id = @id AND state <> 'terminal'
                """, connection, transaction))
            {
                close.Parameters.AddWithValue("@seq", seq);
                close.Parameters.AddWithValue("@id", submissionId);
                await close.ExecuteNonQueryAsync(ct);
            }

            // The event outbox, in the same transaction as the append — same rule every other
            // append follows.
            await using (var outbox = new MySqlCommand(
                """
                INSERT INTO event_outbox (conversation_id, seq, event_id, state, next_attempt_at)
                VALUES (@conv, @seq, @event, 'pending', UTC_TIMESTAMP(6))
                """, connection, transaction))
            {
                outbox.Parameters.AddWithValue("@conv", conversationId);
                outbox.Parameters.AddWithValue("@seq", seq);
                outbox.Parameters.AddWithValue("@event", eventId);
                await outbox.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return true;
        }
        catch (Exception e)
        {
            await transaction.RollbackAsync(CancellationToken.None);

            // Type only. The next scan retries; nothing is lost because nothing was committed.
            logger.LogWarning("reconciler could not abandon an attempt: {Error}", e.GetType().Name);
            return false;
        }
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
