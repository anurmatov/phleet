using Fleet.Protocol;
using System.Data;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations;

/// <summary>
/// The MySQL-backed durable conversation store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock order is fixed</b>: <c>conversations</c> → <c>submissions</c> →
/// <c>execution_attempts</c> → <c>conversation_events</c> → <c>event_outbox</c>. Any other order is
/// a deadlock waiting for load.
/// </para>
/// <para>
/// Isolation is MySQL's default <c>REPEATABLE READ</c>, but correctness does not rest on it: every
/// state transition is a conditional <c>UPDATE … WHERE state = &lt;expected&gt;</c> whose
/// affected-row count is checked.
/// </para>
/// <para>
/// The append serialization point is the <c>conversations</c> row lock rather than an in-process
/// actor. One deployable is not one instance, and an actor would break silently the first time this
/// scaled to two.
/// </para>
/// </remarks>
public sealed partial class MySqlConversationStore : IConversationStore
{
    private readonly string _connectionString;
    private readonly ConversationStoreOptions _options;
    private readonly ILogger<MySqlConversationStore> _logger;
    private readonly string _serviceOwner;

    /// <summary>MySQL's duplicate-key error. Caught by number, never by message.</summary>
    private const int DuplicateKeyError = 1062;

    public MySqlConversationStore(
        string connectionString,
        ConversationStoreOptions options,
        ILogger<MySqlConversationStore> logger,
        string? serviceOwner = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A conversation connection string is required.", nameof(connectionString));

        _connectionString = connectionString;
        _options = options;
        _logger = logger;

        // Identifies this process as a lease holder. It is bookkeeping, not a credential.
        _serviceOwner = serviceOwner ?? $"fleet-comms:{Environment.MachineName}:{Environment.ProcessId}";
    }

    internal string ServiceOwner => _serviceOwner;

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static MySqlCommand Command(
        string sql, MySqlConnection connection, MySqlTransaction? transaction = null)
        => new(sql, connection, transaction);

    /// <summary>
    /// Runs a real, bounded transaction against the store.
    /// </summary>
    /// <remarks>
    /// Used by readiness. It is a genuine transaction rather than a ping, because a wrong credential
    /// or a missing schema has to make the container unhealthy rather than quietly 503 every
    /// request — and only a transaction exercises both.
    /// </remarks>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = Command("SELECT last_healthy_at FROM service_health WHERE id = 1", connection, transaction);

            var result = await command.ExecuteScalarAsync(ct);
            await transaction.CommitAsync(ct);

            return result is not null;
        }
        catch (Exception e)
        {
            _logger.LogWarning("conversation store probe failed: {Error}", e.GetType().Name);
            return false;
        }
    }

    // ────────────────────────────────────────────────────────────── open

    public Task<OpenConversationResult> OpenConversationAsync(
        OpenConversationRequest request, CancellationToken ct = default) =>
        OpenConversationAsync(request, afterLostRace: false, ct);

    private async Task<OpenConversationResult> OpenConversationAsync(
        OpenConversationRequest request, bool afterLostRace, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // A new conversation always names its agent. An empty name is what rows from before
        // conversations had agents carry, and opening one of those would hand back a conversation
        // nothing can route.
        if (string.IsNullOrEmpty(request.AgentName))
            throw new ArgumentException("A conversation names its agent.", nameof(request));

        var existing = await ReadConversationByIdentityAsync(connection, transaction, request, forUpdate: true, ct);

        if (existing is { } row)
        {
            // Identity is (channel, principal, agent, reference), so a row found here is the
            // caller's own by construction. Someone else's conversation under the same reference is
            // a different row, which this lookup never sees.
            await transaction.CommitAsync(ct);

            return new OpenConversationResult
            {
                ConversationId = row.Id,
                NextSeq = row.NextSeq,
                RetainedFloorSeq = row.RetainedFloorSeq,
            };
        }

        var id = Ulid.NewUlid();

        await using (var insert = Command(
            """
            INSERT INTO conversations (id, channel_id, principal_id, agent_name, external_ref)
            VALUES (@id, @channel, @principal, @agent, @ref)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@channel", request.ChannelId);
            insert.Parameters.AddWithValue("@principal", request.PrincipalId);
            insert.Parameters.AddWithValue("@agent", request.AgentName);
            insert.Parameters.AddWithValue("@ref", request.ExternalRef);

            try
            {
                await insert.ExecuteNonQueryAsync(ct);
            }
            catch (MySqlException e) when (e.Number == DuplicateKeyError)
            {
                // Another opener won the race on ux_conv_identity. Re-read and return the
                // winner's row — the filter is that narrow on purpose, because a broad
                // swallow-and-reread turns a genuine write failure into a fabricated success.
                await transaction.RollbackAsync(ct);

                // Once. The winner's row is found by the re-read; if it is not, the key collided
                // with a row this lookup does not consider the caller's (the index compares the
                // principal without regard to case, the lookup does not), and trying again would
                // collide again, forever.
                if (afterLostRace) throw new ConversationNotFoundException();
                return await OpenConversationAsync(request, afterLostRace: true, ct);
            }
        }

        await transaction.CommitAsync(ct);

        return new OpenConversationResult
        {
            ConversationId = id,
            NextSeq = 1,
            RetainedFloorSeq = 1,
        };
    }

    private sealed record ConversationRow(
        string Id, string PrincipalId, ulong NextSeq, ulong RetainedFloorSeq,
        string? AppenderEpoch, ulong LastOrdinal);

    private static async Task<ConversationRow?> ReadConversationByIdentityAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        OpenConversationRequest request, bool forUpdate, CancellationToken ct)
    {
        var sql = """
            SELECT id, principal_id, next_seq, retained_floor_seq, appender_epoch, last_ordinal
            FROM conversations
            WHERE channel_id = @channel AND principal_id = @principal
              AND agent_name = @agent AND external_ref = @ref
            """ + (forUpdate ? " FOR UPDATE" : string.Empty);

        await using var command = Command(sql, connection, transaction);
        command.Parameters.AddWithValue("@channel", request.ChannelId);
        command.Parameters.AddWithValue("@principal", request.PrincipalId);
        command.Parameters.AddWithValue("@agent", request.AgentName);
        command.Parameters.AddWithValue("@ref", request.ExternalRef);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // The principal column compares without regard to case and a principal id does not.
            // Believe only the exact one.
            if (!string.Equals(reader.GetString(1), request.PrincipalId, StringComparison.Ordinal))
                continue;

            return new ConversationRow(
                reader.GetString(0), reader.GetString(1),
                reader.GetUInt64(2), reader.GetUInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetUInt64(5));
        }

        return null;
    }

    // ────────────────────────────────────────────────────────────── find, list

    public async Task<ConversationSummary?> FindConversationAsync(
        string conversationId, string principalId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            """
            SELECT id, principal_id, agent_name, external_ref, next_seq, retained_floor_seq, last_activity_at
            FROM conversations WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("@id", conversationId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        // Someone else's conversation is the same answer as none at all.
        return string.Equals(reader.GetString(1), principalId, StringComparison.Ordinal)
            ? ReadSummary(reader)
            : null;
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        string channelId, string principalId, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            """
            SELECT id, principal_id, agent_name, external_ref, next_seq, retained_floor_seq, last_activity_at
            FROM conversations
            WHERE principal_id = @principal AND channel_id = @channel AND agent_name <> ''
            ORDER BY last_activity_at DESC, id DESC
            LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("@principal", principalId);
        command.Parameters.AddWithValue("@channel", channelId);
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var result = new List<ConversationSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), principalId, StringComparison.Ordinal))
                result.Add(ReadSummary(reader));
        }

        return result;
    }

    private static ConversationSummary ReadSummary(MySqlDataReader reader) => new()
    {
        ConversationId = reader.GetString(0),
        AgentName = reader.GetString(2),
        ExternalRef = reader.GetString(3),
        NextSeq = reader.GetUInt64(4),
        RetainedFloorSeq = reader.GetUInt64(5),
        LastActivityAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
    };

    // ────────────────────────────────────────────────────────────── whose is it

    public Task<string?> FindAgentAsync(AgentTargetKind kind, string id, CancellationToken ct = default) =>
        FindOwnerColumnAsync(kind, id, "c.agent_name", ct);

    public Task<string?> FindConversationIdAsync(AgentTargetKind kind, string id, CancellationToken ct = default) =>
        FindOwnerColumnAsync(kind, id, "c.id", ct);

    /// <param name="column">A fixed column of <c>conversations c</c>, never caller input.</param>
    private async Task<string?> FindOwnerColumnAsync(
        AgentTargetKind kind, string id, string column, CancellationToken ct)
    {
        // One indexed lookup each. The relation these read never changes — a command, a submission,
        // an attempt and an attachment belong to one conversation for life, and a conversation to
        // one agent — so answering before the caller acts is sound, not a race.
        var from = kind switch
        {
            AgentTargetKind.Conversation =>
                "FROM conversations c WHERE c.id = @id",
            AgentTargetKind.Message =>
                """
                FROM command_outbox o
                  JOIN conversations c ON c.id = o.conversation_id
                 WHERE o.message_id = @id
                """,
            AgentTargetKind.Submission =>
                """
                FROM submissions s
                  JOIN conversations c ON c.id = s.conversation_id
                 WHERE s.id = @id
                """,
            AgentTargetKind.Attempt =>
                """
                FROM execution_attempts a
                  JOIN submissions s ON s.id = a.submission_id
                  JOIN conversations c ON c.id = s.conversation_id
                 WHERE a.id = @id
                """,
            AgentTargetKind.Attachment =>
                """
                FROM conversation_attachments t
                  JOIN conversations c ON c.id = t.conversation_id
                 WHERE t.id = @id
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand($"SELECT {column} {from}", connection);
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<ConversationRow?> LockConversationAsync(
        MySqlConnection connection, MySqlTransaction transaction, string conversationId, CancellationToken ct)
    {
        await using var command = Command(
            """
            SELECT id, principal_id, next_seq, retained_floor_seq, appender_epoch, last_ordinal
            FROM conversations WHERE id = @id FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", conversationId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ConversationRow(
            reader.GetString(0), reader.GetString(1),
            reader.GetUInt64(2), reader.GetUInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetUInt64(5));
    }

    // ────────────────────────────────────────────────────────────── accept (TX1a)

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// One transaction writes the submission, its attempt AND the command outbox row. That is not
    /// three writes that happen to be adjacent: a submission row with no command row, or a command
    /// row with no submission row, cannot be produced — including when the process is killed between
    /// them. A durable submission is therefore always dispatched, and one that is not durable is
    /// never dispatched.
    /// </para>
    /// <para>
    /// The conversation row is already locked when <c>next_seq</c> is read, so the accept floor
    /// comes for free and is <b>stored</b> on the submission. Reading it outside the transaction
    /// would let an append land between the read and the insert, and the floor would then point past
    /// the submission's own first event.
    /// </para>
    /// <para>
    /// <b>#305 — the transcript entry is appended HERE</b>, when the submission becomes durable,
    /// and it takes exactly that floor. Two consequences worth stating, because both are easy to
    /// lose in a later refactor:
    /// </para>
    /// <list type="bullet">
    ///   <item>The floor is now a seq that exists rather than one that is predicted, so
    ///   <c>acceptedSeq</c> names a real event on every path.</item>
    ///   <item>Two accepts on one conversation serialize on the row lock and now consume a seq
    ///   each, so an earlier submission's entry always sorts before a later one's. Before, neither
    ///   accept advanced <c>next_seq</c> and both stored the SAME floor.</item>
    /// </list>
    /// </remarks>
    public async Task<AcceptSubmissionResult> AcceptSubmissionAsync(
        AcceptSubmissionRequest request, CancellationToken ct = default)
    {
        // The route enforces this before anything becomes durable, so reaching it here is a
        // programming error rather than a client one. Checked anyway: the transcript's "never
        // truncated" property is what lets a client render a stored message as the whole message,
        // and an unenforced property is a comment.
        if (request.TranscriptText is { } text
            && System.Text.Encoding.UTF8.GetByteCount(text) > ProtocolLimits.MaxInboundTextBytes)
        {
            throw new ArgumentException(
                $"transcript text exceeds {ProtocolLimits.MaxInboundTextBytes} bytes; the caller must "
                + "reject an over-cap submission before it becomes durable, because the transcript "
                + "entry is never truncated.",
                nameof(request));
        }

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var conversation = await LockConversationAsync(connection, transaction, request.ConversationId, ct)
            ?? throw new ConversationNotFoundException();

        if (request.IdempotencyKey is { } key)
        {
            var existing = await ReadSubmissionByKeyAsync(connection, transaction, request.ConversationId, key, ct);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct);
                return ResolveReplay(existing, request.PayloadFingerprint);
            }
        }

        var submissionId = Ulid.NewUlid();
        var attemptId = Ulid.NewUlid();
        var messageId = Ulid.NewUlid();

        // The wire acceptedSeq: the first seq any event about this submission can occupy.
        var acceptFloorSeq = conversation.NextSeq;

        try
        {
            await using (var insert = Command(
                """
                INSERT INTO submissions
                  (id, conversation_id, external_ref, idempotency_key, payload_fingerprint, accept_floor_seq)
                VALUES (@id, @conv, @ref, @key, @fingerprint, @floor)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("@id", submissionId);
                insert.Parameters.AddWithValue("@conv", request.ConversationId);
                insert.Parameters.AddWithValue("@ref", request.ExternalSubmissionId);
                insert.Parameters.AddWithValue("@key", (object?)request.IdempotencyKey ?? DBNull.Value);
                insert.Parameters.AddWithValue("@fingerprint", PayloadFingerprint.ForColumn(request.PayloadFingerprint));
                insert.Parameters.AddWithValue("@floor", acceptFloorSeq);
                await insert.ExecuteNonQueryAsync(ct);
            }

            // The attempt is inserted ALREADY LEASED to this service, so the reconciler's predicate
            // never reads a NULL lease. Ownership transfers to the agent at the disposition — not at
            // turn start, which only starts a turn.
            await using (var attempt = Command(
                """
                INSERT INTO execution_attempts
                  (id, submission_id, attempt, lease_owner, lease_expires_at)
                VALUES (@id, @sub, 1, @owner, UTC_TIMESTAMP(6) + INTERVAL @lease SECOND)
                """, connection, transaction))
            {
                attempt.Parameters.AddWithValue("@id", attemptId);
                attempt.Parameters.AddWithValue("@sub", submissionId);
                attempt.Parameters.AddWithValue("@owner", _serviceOwner);
                attempt.Parameters.AddWithValue("@lease", (int)_options.LeaseDuration.TotalSeconds);
                await attempt.ExecuteNonQueryAsync(ct);
            }
        }
        catch (MySqlException e) when (e.Number == DuplicateKeyError)
        {
            // Lost the race on ux_sub_idem. Re-read the winner's row and return the winner's result.
            await transaction.RollbackAsync(ct);

            if (request.IdempotencyKey is not { } lostKey)
                throw;

            await using var reread = await OpenAsync(ct);
            await using var rereadTransaction = await reread.BeginTransactionAsync(ct);
            var winner = await ReadSubmissionByKeyAsync(reread, rereadTransaction, request.ConversationId, lostKey, ct);
            await rereadTransaction.CommitAsync(ct);

            return winner is null
                ? throw new InvalidOperationException("duplicate key on ux_sub_idem but no winning row could be read")
                : ResolveReplay(winner, request.PayloadFingerprint);
        }

        // #308. BETWEEN the submissions INSERT and the submission.text append, and both halves of
        // that sentence matter.
        //
        // After the INSERT, because the bind stamps `submission_id`. Before the append, because the
        // descriptors go ON that event and `event_seq` is the floor the event is about to take.
        //
        // And inside this transaction rather than after it: a crash between accept and a later bind
        // would leave a submission.text referencing an unbound row the sweep is entitled to collect
        // as "sealed, never submitted" — a transcript entry whose image a background job deletes
        // minutes later (MUST NOT 4).
        //
        // A shortfall throws, and `await using` rolls the whole transaction back: no submission row,
        // no transcript entry, no command outbox row. Accepting the text without its image is the
        // lie MUST NOT 1 forbids.
        IReadOnlyList<AttachmentDescriptor>? descriptors = null;

        if (request.AttachmentIds is { Count: > 0 } attachmentIds)
        {
            descriptors = Describe(await BindAttachmentsAsync(
                connection, transaction, request.ConversationId, submissionId,
                eventSeq: acceptFloorSeq, attachmentIds, ct));
        }

        // #305. Outside the catch above on purpose: that filter exists to recognise a lost race on
        // ux_sub_idem, and a duplicate key from ux_event_id reaching it would be read as one.
        //
        // Null for a cancel, which goes through this transaction but is not something the user said.
        if (request.TranscriptText is { } transcript)
        {
            // Unfenced: this is the accept path, not the agent's append stream. The conversation row
            // is locked, which is what actually serializes next_seq.
            var appended = await WriteEventAsync(
                connection, transaction, conversation,
                eventId: Ulid.NewUlid(),
                kind: ConversationEventKind.SubmissionText,
                // `descriptors` is null for a text-only submission, and WhenWritingNull then omits
                // the key entirely — so the stored payload is byte-identical to the pre-#308 one and
                // no deployed client sees a shape change (AC-4, AC-26).
                payloadJson: FleetProtocolJson.Serialize(
                    new SubmissionTextPayload { Text = transcript, Attachments = descriptors }),
                submissionId: submissionId,
                attemptId: null,
                retentionClass: EventRetentionClass.Durable,
                isTerminal: false,
                ordinal: null,
                ct);

            // The floor was read from the same locked row a few statements ago, so this cannot
            // disagree unless someone appends between them without the lock — which is the one thing
            // that would silently break `afterSeq = acceptedSeq - 1` for every client.
            if (appended != acceptFloorSeq)
                throw new InvalidOperationException(
                    $"submission.text took seq {appended} but the stored accept floor is "
                    + $"{acceptFloorSeq}; an append landed inside the accept transaction.");
        }

        await WriteCommandOutboxAsync(
            connection, transaction,
            messageId: messageId,
            conversationId: request.ConversationId,
            submissionId: submissionId,
            kind: request.CommandKind,
            payloadJson: request.CommandPayloadJson,
            ct);

        await transaction.CommitAsync(ct);

        return new AcceptSubmissionResult
        {
            Outcome = AcceptOutcome.Accepted,
            SubmissionId = submissionId,
            ExternalSubmissionId = request.ExternalSubmissionId,
            AcceptedSeq = acceptFloorSeq,
        };
    }

    private sealed record SubmissionRow(
        string Id, string ExternalRef, string Fingerprint, string State,
        ulong AcceptFloorSeq, ulong? AcceptedSeq, ulong? TerminalSeq);

    private static async Task<SubmissionRow?> ReadSubmissionByKeyAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string conversationId, string key, CancellationToken ct)
    {
        await using var command = Command(
            """
            SELECT id, external_ref, payload_fingerprint, state, accept_floor_seq, accepted_seq, terminal_seq
            FROM submissions
            WHERE conversation_id = @conv AND idempotency_key = @key
            """, connection, transaction);
        command.Parameters.AddWithValue("@conv", conversationId);
        command.Parameters.AddWithValue("@key", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new SubmissionRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetUInt64(4),
            reader.IsDBNull(5) ? null : reader.GetUInt64(5),
            reader.IsDBNull(6) ? null : reader.GetUInt64(6));
    }

    /// <summary>
    /// Decides which of #276 §7's outcomes a same-key arrival is.
    /// </summary>
    private static AcceptSubmissionResult ResolveReplay(SubmissionRow existing, string fingerprint)
    {
        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            return new AcceptSubmissionResult { Outcome = AcceptOutcome.Conflict };

        // A submission the reconciler abandoned BEFORE any disposition is terminal with a
        // terminal_seq and a NULL accepted_seq — so "replay the original submission.accepted" has
        // nothing to replay. It still gets a 201, carrying its stored floor, because the client has
        // to be able to stop retrying and the terminal is the only durable record it has. Catching
        // up from that floor returns the outcome_unknown that explains it.
        var dispositionKnown = existing.AcceptedSeq is not null || existing.TerminalSeq is not null;

        return new AcceptSubmissionResult
        {
            Outcome = dispositionKnown ? AcceptOutcome.Replay : AcceptOutcome.ReplayPending,
            SubmissionId = existing.Id,
            ExternalSubmissionId = existing.ExternalRef,

            // The SAME value at first accept, on an idempotent replay and on the abandoned replay.
            // Never accepted_seq, which is NULL until the disposition.
            AcceptedSeq = dispositionKnown ? existing.AcceptFloorSeq : null,
        };
    }

    // ────────────────────────────────────────────────────────────── catch-up

    /// <inheritdoc/>
    /// <remarks>
    /// The floor and the events are read in ONE transaction, so garbage collection running between
    /// the two statements cannot produce a short read the client would mistake for the end of
    /// history.
    /// </remarks>
    public async Task<ReadConversationResult> ReadAsync(
        ReadConversationRequest request, CancellationToken ct = default)
    {
        if (request.Limit < 1 || request.Limit > _options.MaxReadLimit)
            throw new InvalidCursorException($"limit must be between 1 and {_options.MaxReadLimit}");

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var conversation = await LockConversationAsync(connection, transaction, request.ConversationId, ct)
            ?? throw new ConversationNotFoundException();

        if (!string.Equals(conversation.PrincipalId, request.PrincipalId, StringComparison.Ordinal))
            throw new ConversationNotFoundException();

        // A cursor at or beyond next_seq means client corruption or a restored backup. Rejected,
        // never clamped — clamping hides exactly the condition worth knowing about.
        if (request.AfterSeq >= conversation.NextSeq)
            throw new InvalidCursorException("cursor is at or beyond the end of this conversation");

        ConversationReplayGapPayload? gap = null;
        var from = request.AfterSeq + 1;

        if (from < conversation.RetainedFloorSeq)
        {
            // Durable history the reader will never receive. Synthetic and unsequenced: sequencing
            // it would give it the newest seq and sort it AFTER the suffix it announces.
            gap = new ConversationReplayGapPayload
            {
                FromSeq = from,
                ToSeq = conversation.RetainedFloorSeq - 1,
                RetainedFloorSeq = conversation.RetainedFloorSeq,
            };
            from = conversation.RetainedFloorSeq;
        }

        var events = new List<StoredEvent>();

        await using (var command = Command(
            """
            SELECT seq, event_id, kind, emitted_at, payload_json, is_terminal, retention_class,
                   submission_id, attempt_id
            FROM conversation_events
            WHERE conversation_id = @conv AND seq >= @from
            ORDER BY seq
            LIMIT @limit
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@conv", request.ConversationId);
            command.Parameters.AddWithValue("@from", from);

            // One more than asked, so hasMore is observed rather than guessed.
            command.Parameters.AddWithValue("@limit", request.Limit + 1);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                events.Add(new StoredEvent
                {
                    Seq = reader.GetUInt64(0),
                    EventId = reader.GetString(1),
                    Kind = reader.GetString(2),
                    EmittedAt = new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                    PayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                    IsTerminal = reader.GetBoolean(5),
                    RetentionClass = ParseRetention(reader.GetString(6)),
                    SubmissionId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    AttemptId = reader.IsDBNull(8) ? null : reader.GetString(8),
                });
            }
        }

        await transaction.CommitAsync(ct);

        var hasMore = events.Count > request.Limit;
        if (hasMore) events.RemoveAt(events.Count - 1);

        return new ReadConversationResult
        {
            Gap = gap,
            Events = events,

            // The cursor the CLIENT should send next. It is the last seq delivered, not next_seq:
            // `afterSeq` means "processed up to and including", so handing back next_seq would skip
            // whatever is appended between this read and the next one.
            NextAfterSeq = events.Count > 0 ? events[^1].Seq : request.AfterSeq,
            HasMore = hasMore,

            // Both read from the conversation row inside THIS transaction, so they cannot disagree
            // with the page they arrived with.
            NextSeq = conversation.NextSeq,
            RetainedFloorSeq = conversation.RetainedFloorSeq,
        };
    }

    private static EventRetentionClass ParseRetention(string value) =>
        value switch
        {
            "ephemeral" => EventRetentionClass.Ephemeral,
            "durable" => EventRetentionClass.Durable,
            _ => throw new InvalidOperationException($"unknown retention class '{value}'"),
        };

    internal static string RetentionColumn(EventRetentionClass value) =>
        value switch
        {
            EventRetentionClass.Ephemeral => "ephemeral",
            EventRetentionClass.Durable => "durable",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    // ────────────────────────────────────────────────────────────── cursor

    /// <inheritdoc/>
    /// <remarks>
    /// Cursors are monotonic: <c>GREATEST</c> means a lower value is not an error, it is simply not
    /// a move. A cursor that could go backwards would let a restarted client re-request history it
    /// had already acknowledged, and the server would have no way to tell that from real progress.
    /// </remarks>
    public async Task AckCursorAsync(AckCursorRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var conversation = await LockConversationAsync(connection, transaction, request.ConversationId, ct)
            ?? throw new ConversationNotFoundException();

        var readSeq = request.ReadSeq ?? request.DeliveredSeq;

        if (request.DeliveredSeq >= conversation.NextSeq || readSeq >= conversation.NextSeq)
            throw new InvalidCursorException("cursor is at or beyond the end of this conversation");

        if (readSeq > request.DeliveredSeq)
            throw new InvalidCursorException("readSeq cannot exceed deliveredSeq");

        await using (var command = Command(
            """
            INSERT INTO client_cursors (client_instance_id, conversation_id, delivered_seq, read_seq)
            VALUES (@ci, @conv, @delivered, @read)
            ON DUPLICATE KEY UPDATE
              delivered_seq = GREATEST(delivered_seq, VALUES(delivered_seq)),
              read_seq      = GREATEST(read_seq,      VALUES(read_seq))
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@ci", request.ClientInstanceId);
            command.Parameters.AddWithValue("@conv", request.ConversationId);
            command.Parameters.AddWithValue("@delivered", request.DeliveredSeq);
            command.Parameters.AddWithValue("@read", readSeq);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}

/// <summary>The conversation does not exist, or does not belong to the caller.</summary>
/// <remarks>
/// Deliberately one exception for both, so the two are indistinguishable to a caller and there is no
/// existence oracle.
/// </remarks>
public sealed class ConversationNotFoundException() : Exception("The conversation is not open.");

/// <summary>A cursor or limit that is not a valid position. Never clamped.</summary>
public sealed class InvalidCursorException(string message) : Exception(message);
