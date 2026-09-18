using System.Runtime.CompilerServices;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations;

public sealed partial class MySqlConversationStore
{
    // ────────────────────────────────────────────────────────────── TX2 — turn start

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Starts the turn. It does NOT transfer ownership — the lease moved to the agent at the
    /// disposition, which is the moment it recorded what it decided to do with the work.
    /// </para>
    /// <para>
    /// On the <c>ran</c> path the submission is already <c>running</c>, so the submission update
    /// below affects zero rows. That is expected rather than a failure: the affected-row count is
    /// checked on the ATTEMPT update alone.
    /// </para>
    /// </remarks>
    public async Task<StartTurnResult> StartTurnAsync(
        StartTurnRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var attempt = await ReadAttemptAsync(connection, transaction, request.AttemptId, ct)
            ?? throw new InvalidOperationException($"attempt {request.AttemptId} does not exist");

        // A retry after a lost response: already running with the SAME turnId writes nothing and
        // returns the recorded seq. A DIFFERENT turnId on a running attempt is refused rather than
        // silently accepted — two turns for one attempt is exactly what this refuses to record.
        if (attempt.State == "running")
        {
            if (!string.Equals(attempt.TurnId, request.TurnId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"attempt {request.AttemptId} is already running turn '{attempt.TurnId}'; "
                    + $"refusing to start a different turn '{request.TurnId}' on it");

            var recorded = await ReadTurnStartedSeqAsync(connection, transaction, request.AttemptId, ct);
            await transaction.CommitAsync(ct);
            return new StartTurnResult { Seq = recorded ?? 0, Replayed = true };
        }

        var conversation = await LockConversationAsync(connection, transaction, attempt.ConversationId, ct)
            ?? throw new ConversationNotFoundException();

        int affected;
        await using (var start = Command(
            """
            UPDATE execution_attempts
               SET state = 'running', turn_id = @turn,
                   lease_expires_at = UTC_TIMESTAMP(6) + INTERVAL @lease SECOND,
                   started_at = UTC_TIMESTAMP(6)
             WHERE id = @id AND state = 'pending'
            """, connection, transaction))
        {
            start.Parameters.AddWithValue("@turn", request.TurnId);
            start.Parameters.AddWithValue("@lease", (int)_options.LeaseDuration.TotalSeconds);
            start.Parameters.AddWithValue("@id", request.AttemptId);
            affected = await start.ExecuteNonQueryAsync(ct);
        }

        if (affected != 1)
        {
            // The reconciler already abandoned this attempt. This transaction is STAGED, so it
            // cannot stop a turn the executor has already begun — log it and abandon the children
            // rather than leaving them 'merged' under an 'abandoned' host, which the reconciler
            // never selects and which would therefore sit with no terminal forever. The host turn
            // later takes the late-terminal branch, which is what preserves its answer.
            _logger.LogWarning(
                "turn_start_lost_attempt: attempt {AttemptId} was no longer pending", request.AttemptId);

            await AbandonMergedChildrenAsync(
                connection, transaction, conversation, request.MergedSubmissionIds,
                request.Epoch, request.Ordinal, ct);

            await transaction.CommitAsync(ct);
            return new StartTurnResult { Seq = 0, Replayed = false };
        }

        if (request.MergedSubmissionIds is { Count: > 0 } merged)
        {
            await using var mergeAttempts = Command(
                $"""
                UPDATE execution_attempts SET state = 'merged', host_attempt_id = @host
                 WHERE submission_id IN ({Placeholders(merged.Count)}) AND state = 'pending'
                """, connection, transaction);
            mergeAttempts.Parameters.AddWithValue("@host", request.AttemptId);
            Bind(mergeAttempts, merged);
            await mergeAttempts.ExecuteNonQueryAsync(ct);

            await using var mergeSubs = Command(
                $"""
                UPDATE submissions SET state = 'merged'
                 WHERE id IN ({Placeholders(merged.Count)}) AND state = 'queued'
                """, connection, transaction);
            Bind(mergeSubs, merged);
            await mergeSubs.ExecuteNonQueryAsync(ct);
        }

        // Zero rows on the `ran` path, because the disposition already moved it. Not checked.
        await using (var runSubmission = Command(
            """
            UPDATE submissions SET state = 'running'
             WHERE id = @id AND state IN ('pending_dispatch','queued')
            """, connection, transaction))
        {
            runSubmission.Parameters.AddWithValue("@id", attempt.SubmissionId);
            await runSubmission.ExecuteNonQueryAsync(ct);
        }

        var seq = await AppendEventAsync(
            connection, transaction, conversation,
            eventId: Ulid.NewUlid(),
            kind: ConversationEventKind.TurnStarted,
            payloadJson: null,
            submissionId: attempt.SubmissionId,
            attemptId: request.AttemptId,
            retentionClass: EventRetentionClass.Durable,
            isTerminal: false,
            epoch: request.Epoch,
            ordinal: request.Ordinal,
            ct);

        await transaction.CommitAsync(ct);
        return new StartTurnResult { Seq = seq ?? 0, Replayed = false };
    }

    private async Task AbandonMergedChildrenAsync(
        MySqlConnection connection, MySqlTransaction transaction, ConversationRow conversation,
        IReadOnlyList<string>? merged, string epoch, ulong ordinal, CancellationToken ct)
    {
        if (merged is not { Count: > 0 }) return;

        await using (var abandon = Command(
            $"""
            UPDATE execution_attempts SET state = 'abandoned'
             WHERE submission_id IN ({Placeholders(merged.Count)}) AND state = 'pending'
            """, connection, transaction))
        {
            Bind(abandon, merged);
            await abandon.ExecuteNonQueryAsync(ct);
        }

        var next = ordinal;

        foreach (var submissionId in merged)
        {
            var seq = await AppendOutcomeUnknownAsync(
                connection, transaction, conversation, submissionId, attemptId: null,
                OutcomeUnknownReason.AttemptAbandoned, epoch, ++next, ct);

            if (seq is { } terminalSeq)
                await MarkSubmissionTerminalAsync(connection, transaction, submissionId, terminalSeq, ct);
        }
    }

    private async Task<ulong?> AppendOutcomeUnknownAsync(
        MySqlConnection connection, MySqlTransaction transaction, ConversationRow conversation,
        string submissionId, string? attemptId, OutcomeUnknownReason reason,
        string epoch, ulong ordinal, CancellationToken ct)
        => await AppendEventAsync(
            connection, transaction, conversation,
            eventId: Ulid.NewUlid(),
            kind: ConversationEventKind.TurnOutcomeUnknown,
            payloadJson: FleetProtocolJson.Serialize(new TurnOutcomeUnknownPayload { Reason = reason }),
            submissionId: submissionId,
            attemptId: attemptId,
            retentionClass: EventRetentionClass.Durable,
            isTerminal: true,
            epoch: epoch,
            ordinal: ordinal,
            ct);

    private static async Task MarkSubmissionTerminalAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string submissionId, ulong terminalSeq, CancellationToken ct)
    {
        await using var command = Command(
            "UPDATE submissions SET state = 'terminal', terminal_seq = @seq WHERE id = @id",
            connection, transaction);
        command.Parameters.AddWithValue("@seq", terminalSeq);
        command.Parameters.AddWithValue("@id", submissionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    // ────────────────────────────────────────────────────────────── terminal commit

    /// <inheritdoc/>
    /// <remarks>
    /// The claim is already <c>done</c> from the disposition and is not touched here. With nothing
    /// held across the turn there is no window between a committed terminal and a held claim, which
    /// is why this call carries no delivery message id.
    /// </remarks>
    public async Task<CommitTerminalResult> CommitTerminalAsync(
        CommitTerminalRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var attempt = await ReadAttemptAsync(connection, transaction, request.AttemptId, ct)
            ?? throw new InvalidOperationException($"attempt {request.AttemptId} does not exist");

        // Already committed: a retry writes nothing and returns the existing terminal.
        if (attempt.State == "committed")
        {
            var existing = await ReadTerminalSeqAsync(connection, transaction, attempt.SubmissionId, ct);
            await transaction.CommitAsync(ct);
            return new CommitTerminalResult { Seq = existing ?? 0, Replayed = true, RecoveredAnswer = false };
        }

        var conversation = await LockConversationAsync(connection, transaction, attempt.ConversationId, ct)
            ?? throw new ConversationNotFoundException();

        // The late-terminal branch. The reconciler already abandoned this attempt and appended its
        // outcome_unknown, so a genuine answer arriving now is preserved as turn.recovered_answer —
        // it does not overwrite the terminal that won. A successful answer is never destroyed by a
        // store outage.
        var lateTerminal = attempt.State == "abandoned";

        var kind = lateTerminal
            ? ConversationEventKind.TurnRecoveredAnswer
            : request.TerminalEvent.Kind;

        var seq = await AppendEventAsync(
            connection, transaction, conversation,
            eventId: request.TerminalEvent.EventId,
            kind: kind,
            payloadJson: request.TerminalEvent.PayloadJson,
            submissionId: attempt.SubmissionId,
            attemptId: request.AttemptId,
            retentionClass: EventRetentionClass.Durable,
            isTerminal: !lateTerminal,
            epoch: request.Epoch,
            ordinal: request.Ordinal,
            ct);

        if (!lateTerminal)
        {
            await using (var commitAttempt = Command(
                """
                UPDATE execution_attempts
                   SET state = 'committed', committed_at = UTC_TIMESTAMP(6),
                       lease_owner = NULL, lease_expires_at = NULL
                 WHERE id = @id AND state = 'running'
                """, connection, transaction))
            {
                commitAttempt.Parameters.AddWithValue("@id", request.AttemptId);
                await commitAttempt.ExecuteNonQueryAsync(ct);
            }

            if (seq is { } terminalSeq)
            {
                await MarkSubmissionTerminalAsync(connection, transaction, attempt.SubmissionId, terminalSeq, ct);

                // A merged submission reaches terminal through its HOST turn's terminal transaction,
                // never through one of its own.
                if (request.MergedSubmissionIds is { Count: > 0 } merged)
                {
                    await using var closeChildren = Command(
                        $"""
                        UPDATE submissions SET state = 'terminal', terminal_seq = @seq
                         WHERE id IN ({Placeholders(merged.Count)}) AND state = 'merged'
                        """, connection, transaction);
                    closeChildren.Parameters.AddWithValue("@seq", terminalSeq);
                    Bind(closeChildren, merged);
                    await closeChildren.ExecuteNonQueryAsync(ct);

                    await using var closeChildAttempts = Command(
                        $"""
                        UPDATE execution_attempts
                           SET state = 'committed', committed_at = UTC_TIMESTAMP(6),
                               lease_owner = NULL, lease_expires_at = NULL
                         WHERE submission_id IN ({Placeholders(merged.Count)}) AND state = 'merged'
                        """, connection, transaction);
                    Bind(closeChildAttempts, merged);
                    await closeChildAttempts.ExecuteNonQueryAsync(ct);
                }
            }
        }

        await transaction.CommitAsync(ct);

        return new CommitTerminalResult
        {
            Seq = seq ?? 0,
            Replayed = false,
            RecoveredAnswer = lateTerminal,
        };
    }

    // ────────────────────────────────────────────────────────────── staged append

    /// <inheritdoc/>
    public async Task<AppendBatchResult> AppendBatchAsync(
        AppendBatchRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var conversation = await LockConversationAsync(connection, transaction, request.ConversationId, ct)
            ?? throw new ConversationNotFoundException();

        var seqs = new List<ulong?>(request.Events.Count);

        foreach (var staged in request.Events)
        {
            var seq = await AppendEventAsync(
                connection, transaction, conversation,
                eventId: staged.Event.EventId,
                kind: staged.Event.Kind,
                payloadJson: staged.Event.PayloadJson,
                submissionId: staged.SubmissionId,
                attemptId: staged.AttemptId,
                retentionClass: staged.RetentionClass,
                isTerminal: false,
                epoch: request.Epoch,
                ordinal: staged.Ordinal,
                ct);

            seqs.Add(seq);

            // next_seq and last_ordinal moved; re-read so the next append in the batch sees them.
            conversation = await LockConversationAsync(connection, transaction, request.ConversationId, ct)
                ?? throw new ConversationNotFoundException();
        }

        await transaction.CommitAsync(ct);
        return new AppendBatchResult { Seqs = seqs };
    }

    // ────────────────────────────────────────────────────────────── leases

    /// <inheritdoc/>
    /// <remarks>
    /// Renews PENDING as well as running attempts. A queued submission waits behind a turn that may
    /// run for many times the lease duration and stays pending the whole time; renewing only running
    /// attempts abandons a full queue at the lease bound and reports it to the client as an unknown
    /// outcome.
    /// </remarks>
    public async Task<HeartbeatResult> HeartbeatAsync(
        HeartbeatRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        // Service liveness, for the reconciler's grace period after a restart.
        await using (var health = Command(
            "UPDATE service_health SET last_healthy_at = UTC_TIMESTAMP(6) WHERE id = 1", connection))
        {
            await health.ExecuteNonQueryAsync(ct);
        }

        if (request.AttemptIds.Count == 0)
            return new HeartbeatResult { Renewed = [], NotOwned = [] };

        var renewed = new List<string>();

        await using (var renew = Command(
            $"""
            UPDATE execution_attempts
               SET lease_expires_at = UTC_TIMESTAMP(6) + INTERVAL @lease SECOND
             WHERE id IN ({Placeholders(request.AttemptIds.Count)})
               AND lease_owner = @owner
               AND state IN ('pending','running')
            """, connection))
        {
            renew.Parameters.AddWithValue("@lease", (int)_options.LeaseDuration.TotalSeconds);
            renew.Parameters.AddWithValue("@owner", request.Owner);
            Bind(renew, request.AttemptIds);
            await renew.ExecuteNonQueryAsync(ct);
        }

        await using (var check = Command(
            $"""
            SELECT id FROM execution_attempts
             WHERE id IN ({Placeholders(request.AttemptIds.Count)})
               AND lease_owner = @owner AND state IN ('pending','running')
            """, connection))
        {
            check.Parameters.AddWithValue("@owner", request.Owner);
            Bind(check, request.AttemptIds);

            await using var reader = await check.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) renewed.Add(reader.GetString(0));
        }

        return new HeartbeatResult
        {
            Renewed = renewed,
            NotOwned = request.AttemptIds.Where(id => !renewed.Contains(id)).ToList(),
        };
    }

    /// <inheritdoc/>
    public async Task MarkExternalEffectAsync(string attemptId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            "UPDATE execution_attempts SET had_external_effect = 1 WHERE id = @id", connection);
        command.Parameters.AddWithValue("@id", attemptId);
        await command.ExecuteNonQueryAsync(ct);
    }

    // ────────────────────────────────────────────────────────────── tail

    /// <inheritdoc/>
    public async IAsyncEnumerable<StoredEvent> TailAsync(
        string conversationId, ulong afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = afterSeq;

        while (!ct.IsCancellationRequested)
        {
            var batch = await ReadAsync(
                new ReadConversationRequest
                {
                    ConversationId = conversationId,
                    AfterSeq = cursor,
                    Limit = _options.DefaultReadLimit,

                    // The tail is server-side and already past the route's authorization check.
                    PrincipalId = await ReadConversationPrincipalAsync(conversationId, ct),
                },
                ct);

            foreach (var stored in batch.Events)
            {
                yield return stored;
                cursor = stored.Seq;
            }

            if (batch.Events.Count == 0)
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private async Task<string> ReadConversationPrincipalAsync(string conversationId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            "SELECT principal_id FROM conversations WHERE id = @id", connection);
        command.Parameters.AddWithValue("@id", conversationId);

        return await command.ExecuteScalarAsync(ct) as string
            ?? throw new ConversationNotFoundException();
    }

    // ────────────────────────────────────────────────────────────── helpers

    private sealed record AttemptRow(
        string Id, string SubmissionId, string ConversationId, string State, string? TurnId, string? LeaseOwner);

    private static async Task<AttemptRow?> ReadAttemptAsync(
        MySqlConnection connection, MySqlTransaction transaction, string attemptId, CancellationToken ct)
    {
        await using var command = Command(
            """
            SELECT a.id, a.submission_id, s.conversation_id, a.state, a.turn_id, a.lease_owner
              FROM execution_attempts a
              JOIN submissions s ON s.id = a.submission_id
             WHERE a.id = @id
             FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", attemptId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new AttemptRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static async Task<ulong?> ReadTurnStartedSeqAsync(
        MySqlConnection connection, MySqlTransaction transaction, string attemptId, CancellationToken ct)
    {
        await using var command = Command(
            """
            SELECT seq FROM conversation_events
             WHERE attempt_id = @id AND kind = @kind ORDER BY seq LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", attemptId);
        command.Parameters.AddWithValue("@kind", ConversationEventKind.TurnStarted);

        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToUInt64(result);
    }

    private static async Task<ulong?> ReadTerminalSeqAsync(
        MySqlConnection connection, MySqlTransaction transaction, string submissionId, CancellationToken ct)
    {
        await using var command = Command(
            "SELECT terminal_seq FROM submissions WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("@id", submissionId);

        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToUInt64(result);
    }

    /// <summary>Builds <c>@p0, @p1, …</c> for an IN list. Never string-concatenates a value.</summary>
    private static string Placeholders(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"@p{i}"));

    private static void Bind(MySqlCommand command, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
            command.Parameters.AddWithValue($"@p{i}", values[i]);
    }
}
