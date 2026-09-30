using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations.Journal;

/// <summary>
/// The journal store over MySQL, with explicit SQL like the rest of this assembly.
/// </summary>
/// <remarks>
/// <para>
/// Every ingest is ONE transaction with the conversation row locked <c>FOR UPDATE</c>, and every
/// outcome other than <c>created</c> and <c>observer_added</c> writes nothing. A deadlock, a
/// lock-wait timeout or a lost insert race retries the whole transaction once; anything else from
/// the database is <see cref="JournalStoreUnavailableException"/>, which the listener turns into
/// <c>503 store_unavailable</c>.
/// </para>
/// <para>
/// ⚠️ <b>READ COMMITTED, on purpose.</b> An ingest's first reads look up keys that usually do not
/// exist yet: its event id and, for a first message, its conversation key. Under REPEATABLE READ a
/// locking read of a missing key takes a gap lock, and two parallel first messages holding gap
/// locks on the same range then deadlock on their inserts — measured at 24 to 47 of 48 parallel
/// distinct records answering 503 after the single retry. Under READ COMMITTED no gap lock is
/// taken; the unique keys arbitrate instead. Two first messages for one new conversation both
/// insert it, the loser waits on the winner's row and gets a duplicate key, and its one retry finds
/// the committed row and serializes behind its lock. InnoDB then needs row-based binary logging,
/// the MySQL 8.0 default.
/// </para>
/// <para>
/// ⚠️ It never migrates. It reads <c>MAX(version)</c> from <c>schema_migrations</c> and, below 4,
/// refuses without touching a journal table. Only a CURRENT answer is cached (30 s): a behind answer
/// is re-read on the next request, so the first post after <c>conversations migrate</c> succeeds.
/// </para>
/// </remarks>
public sealed partial class MySqlJournalStore : IJournalStore
{
    /// <summary>
    /// The object store, when media is configured (#388). Null on a deployment without media, which
    /// is the state every install was in before this slice: an <c>uploadId</c> is then refused by
    /// the route with <c>409 media_disabled</c> and this store never sees one.
    /// </summary>
    public MySqlJournalObjectStore? Objects { get; init; }

    /// <summary>
    /// The schema version this binary needs. <b>5, not 4:</b> slice 4 (#388) adds
    /// <c>journal_objects</c> and the attachment→object key, and a record with an
    /// <c>uploadId</c> cannot be written against 0004. Refusing a schema behind this binary is the
    /// existing rule; raising the number is what makes an unmigrated database answer
    /// <c>503 schema_behind</c> rather than failing mid-transaction.
    /// </summary>
    public const int RequiredSchemaVersion = 5;

    private static readonly TimeSpan SchemaCacheDuration = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    private long _schemaCurrentUntilTicks;

    public MySqlJournalStore(string connectionString, ILogger logger, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A journal connection string is required.", nameof(connectionString));

        // Bounded, so an unreachable database is a prompt 503 rather than a request that hangs for
        // the driver's defaults. A shorter value already in the string is kept.
        var builder = new MySqlConnectionStringBuilder(connectionString);
        builder.ConnectionTimeout = Math.Min(builder.ConnectionTimeout, 5);
        if (builder.DefaultCommandTimeout is 0 or > 10) builder.DefaultCommandTimeout = 10;

        _connectionString = builder.ConnectionString;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<JournalIngestResult> IngestAsync(
        JournalRecord recordInput, string observer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recordInput);

        if (!JournalTokens.IsValidSubject(observer))
            throw new ArgumentException("an observer is 1-128 characters of [A-Za-z0-9_-]", nameof(observer));

        // Reassigned once, after the media plan resolves each upload to the object it points at.
        // Everything downstream — fingerprint, attachment rows — reads the resolved record.
        var record = recordInput;
        var conversationKey = JournalKeys.ConversationKey(record.Telegram);

        // ── media proof, BEFORE the conversation lock ────────────────────────────
        //
        // Resolved outside the transaction, and the lock is never held across an S3 round trip.
        // Every ingest takes this conversation's row lock, so a stalled bucket under a held lock
        // would stall every publisher in the chat. The state is re-checked inside the transaction
        // below, which is where the write is made atomic; these rows are only ever advanced
        // forward by their owner or backward by the sweeper, and the in-transaction guard is the
        // one the write depends on.
        var media = ResolveUploads(record, observer);
        if (media.Refusal is not null)
        {
            JournalRuntimeStats.Ingest("upload_incomplete");
            return media.Refusal;
        }

        // ⚠️ The fingerprint is computed over the record with the SERVER-resolved object ids, not
        //    with the caller's upload ids. `objectId` is part of the attachment fingerprint (see
        //    JournalFingerprint.Canonical), so encoding the resolved id — the dedup winner's id for
        //    a loser as much as for a winner — is what makes two subjects that archived the same
        //    photo agree on the fingerprint of one message, and what makes a retry of a committed
        //    submission replay as `duplicate` rather than `conflict`.
        //
        //    A record with no uploadId has no object ids and fingerprints exactly as it did before
        //    slice 4: text-only ingest is byte-for-byte unchanged.
        record = WithObjectIds(record, media.Resolved);
        var fingerprint = JournalFingerprint.Compute(conversationKey, record);

        try
        {
            await using var connection = await OpenAsync(ct);
            await EnsureSchemaAsync(connection, ct);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await IngestOnceAsync(
                        connection, record, observer, conversationKey, fingerprint, media, ct);
                }
                catch (MySqlException e) when (attempt == 0 && IsRetryable(e))
                {
                    _logger.LogInformation(
                        "journal ingest from {Observer} retried once after {Error}", observer, e.ErrorCode);
                    // A concurrent digest commit may have selected a different winner.
                    // Re-plan from the original upload ids, not the already-resolved record.
                    media = ResolveUploads(recordInput, observer);
                    if (media.Refusal is not null)
                    {
                        JournalRuntimeStats.Ingest("upload_incomplete");
                        return media.Refusal;
                    }
                    record = WithObjectIds(recordInput, media.Resolved);
                    fingerprint = JournalFingerprint.Compute(conversationKey, record);
                }
            }
        }
        catch (JournalStoreUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or System.Net.Sockets.SocketException
                                     or IOException or TimeoutException or OperationCanceledException)
        {
            // Type only: a driver message can carry the connection string.
            _logger.LogWarning("journal store unavailable for {Observer}: {Error}", observer, e.GetType().Name);
            throw new JournalStoreUnavailableException(null, e);
        }
    }

    public async Task<JournalStoreStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = await OpenAsync(ct);
            var version = await ReadSchemaVersionAsync(connection, ct);

            if (version is null or < RequiredSchemaVersion)
                return new JournalStoreStatus { SchemaVersion = version, Observers = [] };

            var observers = new List<JournalObserverStatus>();

            await using var command = new MySqlCommand(
                """
                SELECT observer, COUNT(*), MAX(observed_at)
                  FROM journal_message_observers
                 GROUP BY observer
                 ORDER BY observer
                """, connection);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                observers.Add(new JournalObserverStatus
                {
                    Observer = reader.GetString(0),
                    Messages = reader.GetInt64(1),
                    LastIngestAt = reader.IsDBNull(2) ? null : Utc(reader.GetDateTime(2)),
                });
            }

            return new JournalStoreStatus { SchemaVersion = version, Observers = observers };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or System.Net.Sockets.SocketException
                                     or IOException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("journal status unavailable: {Error}", e.GetType().Name);
            throw new JournalStoreUnavailableException(null, e);
        }
    }

    // ── ingest ───────────────────────────────────────────────────────────────

    private async Task<JournalIngestResult> IngestOnceAsync(
        MySqlConnection connection, JournalRecord record, string observer,
        string conversationKey, string fingerprint, MediaPlan media, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var sentAt = record.SentAt.UtcDateTime;
        var sourceKey = JournalKeys.SourceKey(record.Telegram.MessageId);

        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);

        // 1. The conversation row, locked when it exists. When it does not, READ COMMITTED takes no
        //    gap lock: a concurrent first message for the same chat meets this one at the unique
        //    conversation key on insert, and the loser retries.
        string? conversationId;
        await using (var select = Command(connection, transaction,
            "SELECT id FROM journal_conversations WHERE conversation_key = @key FOR UPDATE"))
        {
            select.Parameters.AddWithValue("@key", conversationKey);
            conversationId = await select.ExecuteScalarAsync(ct) as string;
        }

        // 2. The event id, before anything is written: a reused id is a duplicate only when this
        //    observer's earlier submission had the same fingerprint. A plain read of the latest
        //    committed row; the unique event key catches a concurrent first use at insert.
        await using (var byEvent = Command(connection, transaction,
            "SELECT message_id, fingerprint FROM journal_message_observers WHERE event_id = @event"))
        {
            byEvent.Parameters.AddWithValue("@event", record.EventId);
            await using var reader = await byEvent.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                var recordedMessage = reader.GetString(0);
                var recordedFingerprint = reader.GetString(1);
                await reader.DisposeAsync();
                await transaction.RollbackAsync(ct);

                return string.Equals(recordedFingerprint, fingerprint, StringComparison.Ordinal)
                    ? new JournalIngestResult { Outcome = JournalIngestOutcome.Duplicate, MessageId = recordedMessage }
                    : new JournalIngestResult { Outcome = JournalIngestOutcome.EventIdReused };
            }
        }

        // 3. The natural key (conversation, source key).
        if (conversationId is not null)
        {
            string? messageId = null, storedFingerprint = null;
            var transcriptMissing = false;

            await using (var byKey = Command(connection, transaction,
                """
                SELECT id, fingerprint, transcript IS NULL
                  FROM journal_messages
                 WHERE conversation_id = @conversation AND source_key = @source
                   FOR UPDATE
                """))
            {
                byKey.Parameters.AddWithValue("@conversation", conversationId);
                byKey.Parameters.AddWithValue("@source", sourceKey);
                await using var reader = await byKey.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    messageId = reader.GetString(0);
                    storedFingerprint = reader.GetString(1);
                    transcriptMissing = reader.GetInt64(2) == 1;
                }
            }

            if (messageId is not null)
            {
                if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(ct);
                    return new JournalIngestResult { Outcome = JournalIngestOutcome.Conflict };
                }

                bool seen;
                await using (var byObserver = Command(connection, transaction,
                    "SELECT 1 FROM journal_message_observers WHERE message_id = @message AND observer = @observer FOR UPDATE"))
                {
                    byObserver.Parameters.AddWithValue("@message", messageId);
                    byObserver.Parameters.AddWithValue("@observer", observer);
                    seen = await byObserver.ExecuteScalarAsync(ct) is not null;
                }

                // A repeat from the same observer writes nothing — not even a transcript it did not
                // send the first time. Every publisher sends the transcript on its first attempt.
                if (seen)
                {
                    await transaction.RollbackAsync(ct);
                    return new JournalIngestResult { Outcome = JournalIngestOutcome.Duplicate, MessageId = messageId };
                }

                if (!await LockProofRowsAsync(connection, transaction, media, observer, ct))
                {
                    await transaction.RollbackAsync(ct);
                    return RefuseUploads(media.Resolved.Keys);
                }
                await InsertObserverAsync(connection, transaction, messageId, observer, record.EventId, fingerprint, now, ct);

                // First non-null transcript wins and is never overwritten.
                if (transcriptMissing && record.Transcript is not null)
                {
                    await using var fill = Command(connection, transaction,
                        """
                        UPDATE journal_messages
                           SET transcript = @transcript, transcript_truncated = @truncated
                         WHERE id = @message AND transcript IS NULL
                        """);
                    fill.Parameters.AddWithValue("@transcript", record.Transcript);
                    fill.Parameters.AddWithValue("@truncated", record.TranscriptTruncated);
                    fill.Parameters.AddWithValue("@message", messageId);
                    await fill.ExecuteNonQueryAsync(ct);
                }

                // ⚠️ An observer that archived what an earlier observer declined.
                //
                //    The stored attachment row is the FIRST observer's answer, and this path used to
                //    write nothing against it. So when the first observer declined the photo —
                //    download failed, media off, a lower size cap — and this one proved the bytes,
                //    the message kept a `not_archived` attachment forever while this subject's
                //    uploaded object sat unreferenced and was swept 24 h later. The archive kept
                //    answering "not archived" about bytes that were sitting in the bucket, and the
                //    sweeper deleted the only copy.
                //
                //    So, in THIS transaction: commit the object this subject proved, exactly as the
                //    insert path commits it — owner-bound, so a subject can never advance someone
                //    else's row — and then point the stored attachment at it. Both writes or neither:
                //    an attachment naming an `uploaded` object would be a pointer the sweeper treats
                //    as abandoned, and a committed object naming nothing is the hole described above.
                //
                //    Only at an ordinal whose stored row is `not_archived`. A row already `committed`
                //    belongs to the observer that got there first: it is left exactly as it is, and
                //    this subject's own object falls through to the loser path below.
                //
                //    This is also why the fingerprint must not encode the object id. The two
                //    observers legitimately disagree about archiving; the fix is to let the later one
                //    improve the stored row, not to call the disagreement a conflict.
                foreach (var ordinal in media.Resolved.Keys)
                {
                    var resolvedId = media.Resolved[ordinal];
                    if (!media.Proved.Contains(resolvedId))
                    {
                        // A winner owned by another subject is read and locked, never modified.
                        await using var winner = Command(connection, transaction,
                            "SELECT committed_sha256 FROM journal_objects WHERE id = @id AND state = 'committed' FOR UPDATE");
                        winner.Parameters.AddWithValue("@id", resolvedId);
                        if (!string.Equals(await winner.ExecuteScalarAsync(ct) as string,
                                media.Digests[resolvedId], StringComparison.OrdinalIgnoreCase))
                        {
                            await transaction.RollbackAsync(ct);
                            JournalRuntimeStats.Ingest("upload_incomplete");
                            return RefuseUploads(media.Resolved.Keys);
                        }
                    }

                    if (media.Proved.Contains(resolvedId))
                    {
                        await using (var commit = Command(connection, transaction,
                            """
                            UPDATE journal_objects
                               SET state = 'committed', committed_sha256 = @sha, updated_at = @now
                             WHERE id = @id AND owner = @owner
                               AND state IN ('uploaded', 'committed')
                            """))
                        {
                            commit.Parameters.AddWithValue("@id", resolvedId);
                            commit.Parameters.AddWithValue("@owner", observer);
                            commit.Parameters.AddWithValue("@sha", media.Digests[resolvedId]);
                            commit.Parameters.AddWithValue("@now", now);

                            if (await commit.ExecuteNonQueryAsync(ct) != 1)
                            {
                                // The row stopped being this subject's live upload between the plan and
                                // here. Same answer the insert path gives, and nothing above survives it.
                                await transaction.RollbackAsync(ct);
                                JournalRuntimeStats.Ingest("upload_incomplete");
                                return new JournalIngestResult
                                {
                                    Outcome = JournalIngestOutcome.UploadIncomplete,
                                    UploadOrdinals = media.Resolved.Keys.Order().ToArray(),
                                };
                            }
                        }
                    }

                    await using var upgrade = Command(connection, transaction,
                        """
                        UPDATE journal_attachments
                           SET object_id = @object, state = 'committed', not_archived_reason = NULL,
                               sha256 = @sha, committed_at = @now
                         WHERE message_id = @message AND ordinal = @ordinal AND state = 'not_archived'
                        """);
                    upgrade.Parameters.AddWithValue("@object", resolvedId);
                    upgrade.Parameters.AddWithValue("@sha", media.Digests[resolvedId]);
                    upgrade.Parameters.AddWithValue("@message", messageId);
                    upgrade.Parameters.AddWithValue("@ordinal", ordinal);
                    upgrade.Parameters.AddWithValue("@now", now);
                    await upgrade.ExecuteNonQueryAsync(ct);
                }

                // A loser this subject proved is parked, not deleted: this path writes no
                // attachment row for a loser, so the row is the only thing that keeps the sweeper
                // able to see the bytes. The insert path parks losers the same way.
                foreach (var loser in media.Losers)
                {
                    await using var park = Command(connection, transaction,
                        """
                        UPDATE journal_objects
                           SET state = 'aborted', updated_at = @now
                         WHERE id = @id AND owner = @owner AND state = 'uploaded'
                        """);
                    park.Parameters.AddWithValue("@id", loser);
                    park.Parameters.AddWithValue("@owner", observer);
                    park.Parameters.AddWithValue("@now", now);
                    await park.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return new JournalIngestResult { Outcome = JournalIngestOutcome.ObserverAdded, MessageId = messageId };
            }

            await using var touch = Command(connection, transaction,
                """
                UPDATE journal_conversations
                   SET last_message_at = GREATEST(last_message_at, @sent),
                       title = COALESCE(@title, title)
                 WHERE id = @conversation
                """);
            touch.Parameters.AddWithValue("@sent", sentAt);
            touch.Parameters.AddWithValue("@title", record.Telegram.ChatTitle);
            touch.Parameters.AddWithValue("@conversation", conversationId);
            await touch.ExecuteNonQueryAsync(ct);
        }
        else
        {
            conversationId = Ulid.NewUlid(_time.GetUtcNow());

            await using var create = Command(connection, transaction,
                """
                INSERT INTO journal_conversations
                    (id, conversation_key, channel, chat_kind, telegram_bot_id, telegram_chat_id,
                     title, created_at, last_message_at)
                VALUES (@id, @key, 'telegram', @kind, @bot, @chat, @title, @now, @sent)
                """);
            create.Parameters.AddWithValue("@id", conversationId);
            create.Parameters.AddWithValue("@key", conversationKey);
            create.Parameters.AddWithValue("@kind", JournalWire.Of(record.Telegram.ChatKind));

            // A supergroup's conversation is shared by every bot in it, so it names none.
            create.Parameters.AddWithValue("@bot",
                record.Telegram.ChatKind == JournalChatKind.Supergroup ? null : (object)record.Telegram.BotId);
            create.Parameters.AddWithValue("@chat", record.Telegram.ChatId);
            create.Parameters.AddWithValue("@title", record.Telegram.ChatTitle);
            create.Parameters.AddWithValue("@now", now);
            create.Parameters.AddWithValue("@sent", sentAt);
            await create.ExecuteNonQueryAsync(ct);
        }

        // 4. A new message: the row, this observer, and attachment metadata. Reaching step 4 means
        //    this submission INSERTS the message — the observer-added case returned from step 3.
        var newMessageId = Ulid.NewUlid(_time.GetUtcNow());

        await using (var insert = Command(connection, transaction,
            """
            INSERT INTO journal_messages
                (id, conversation_id, source_key, order_key, fingerprint, direction, sender_kind,
                 sender_id, sender_display, reply_to_source_key, media_group_id, sent_at, recorded_at,
                 text, text_format, transcript, transcript_truncated, origin, delivery_state,
                 send_group_id, send_part, send_parts)
            VALUES
                (@id, @conversation, @source, @order, @fingerprint, @direction, @senderKind,
                 @senderId, @senderDisplay, @replyTo, @mediaGroup, @sent, @now,
                 @text, @textFormat, @transcript, @truncated, @origin, @delivery,
                 @sendGroup, @sendPart, @sendParts)
            """))
        {
            insert.Parameters.AddWithValue("@id", newMessageId);
            insert.Parameters.AddWithValue("@conversation", conversationId);
            insert.Parameters.AddWithValue("@source", sourceKey);
            insert.Parameters.AddWithValue("@order", record.Telegram.MessageId);
            insert.Parameters.AddWithValue("@fingerprint", fingerprint);
            insert.Parameters.AddWithValue("@direction", JournalWire.Of(record.Direction));
            insert.Parameters.AddWithValue("@senderKind", JournalWire.Of(record.Sender.Kind));
            insert.Parameters.AddWithValue("@senderId", record.Sender.Id);
            insert.Parameters.AddWithValue("@senderDisplay", record.Sender.Display);
            insert.Parameters.AddWithValue("@replyTo",
                record.Telegram.ReplyToMessageId is { } replyTo ? JournalKeys.SourceKey(replyTo) : null);
            insert.Parameters.AddWithValue("@mediaGroup", record.Telegram.MediaGroupId);
            insert.Parameters.AddWithValue("@sent", sentAt);
            insert.Parameters.AddWithValue("@now", now);
            insert.Parameters.AddWithValue("@text", record.Text);
            insert.Parameters.AddWithValue("@textFormat",
                record.TextFormat is { } format ? JournalWire.Of(format) : null);
            insert.Parameters.AddWithValue("@transcript", record.Transcript);
            insert.Parameters.AddWithValue("@truncated", record.TranscriptTruncated);
            insert.Parameters.AddWithValue("@origin", JournalWire.Of(record.Origin));

            // Derived, never requested: `copied` belongs to a later slice.
            insert.Parameters.AddWithValue("@delivery",
                record.Direction == JournalDirection.Inbound ? "received" : "sent");
            insert.Parameters.AddWithValue("@sendGroup", record.SendGroup?.Id);
            insert.Parameters.AddWithValue("@sendPart", record.SendGroup?.Part);
            insert.Parameters.AddWithValue("@sendParts", record.SendGroup?.Parts);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await InsertObserverAsync(connection, transaction, newMessageId, observer, record.EventId, fingerprint, now, ct);

        if (!await LockProofRowsAsync(connection, transaction, media, observer, ct))
        {
            await transaction.RollbackAsync(ct);
            return RefuseUploads(media.Resolved.Keys);
        }

        // ── the media state transitions, in THIS transaction ───────────────────
        //
        // Two objects per attachment id are possible here and they are opposite jobs:
        //
        //  * the object THIS subject proved (`media.Proved`) must become `committed`, and only this
        //    subject's own row may be written — `owner = @owner` is what keeps a commit from
        //    advancing someone else's row;
        //  * the object the attachment POINTS at (`media.Resolved`) may already be committed and
        //    owned by someone else. That is the dedup winner. Nothing is written to it; it only has
        //    to still be committed, which the lock below and the re-read prove.
        //
        // Locking first is what makes "the winner is still committed" true at the moment the
        // attachment is written, rather than true when the plan read it.
        foreach (var target in media.Resolved.Values.Distinct(StringComparer.Ordinal))
        {
            await using (var relock = Command(connection, transaction,
                "SELECT state FROM journal_objects WHERE id = @id FOR UPDATE"))
            {
                relock.Parameters.AddWithValue("@id", target);
                var state = await relock.ExecuteScalarAsync(ct) as string;

                if (state != "committed" && state != "uploaded")
                {
                    // Swept, aborted, or scheduled for deletion between the plan and here. The same
                    // answer the pre-flight gives, and nothing above this survives it.
                    await transaction.RollbackAsync(ct);
                    JournalRuntimeStats.Ingest("upload_incomplete");
                    return new JournalIngestResult
                    {
                        Outcome = JournalIngestOutcome.UploadIncomplete,
                        UploadOrdinals = media.Resolved.Keys.Order().ToArray(),
                    };
                }
            }

            // Only a row this subject owns is written. The dedup winner is typically someone
            // else's committed object: it is re-locked above and left exactly as it was.
            if (!media.Proved.Contains(target)) continue;

            await using var commit = Command(connection, transaction,
                """
                UPDATE journal_objects
                   SET state = 'committed', committed_sha256 = @sha, updated_at = @now
                 WHERE id = @id AND owner = @owner
                   AND state IN ('uploaded', 'committed')
                """);
            commit.Parameters.AddWithValue("@id", target);
            commit.Parameters.AddWithValue("@owner", observer);
            commit.Parameters.AddWithValue("@sha", media.Digests[target]);
            commit.Parameters.AddWithValue("@now", now);

            if (await commit.ExecuteNonQueryAsync(ct) != 1)
            {
                // The row stopped being this subject's live upload between the plan and here.
                await transaction.RollbackAsync(ct);
                JournalRuntimeStats.Ingest("upload_incomplete");
                return new JournalIngestResult
                {
                    Outcome = JournalIngestOutcome.UploadIncomplete,
                    UploadOrdinals = media.Resolved.Keys.Order().ToArray(),
                };
            }
        }

        // One row per attachment, and exactly two shapes.
        //
        // An attachment that names an upload becomes `committed` and points at an object; anything
        // else is `not_archived` naming its reason. Never both, never neither — which is why the
        // object id is bound only on the committed branch and the reason only on the other.
        //
        // ⚠️ A message is never visible with a dangling reference: the state transition and the
        // attachment inserts are in THIS transaction, and every refusal above happens before
        // anything is written.
        foreach (var attachment in record.Attachments)
        {
            // `objectId` is non-null exactly when the attachment is committed, which the compiler
            // cannot see through TryGetValue's bool return.
            media.Resolved.TryGetValue(attachment.Ordinal, out var attachmentObject);
            var committed = !string.IsNullOrEmpty(attachmentObject);

            await using var attach = Command(connection, transaction,
                """
                INSERT INTO journal_attachments
                    (id, message_id, ordinal, kind, mime_type, byte_size, sha256, original_file_name,
                     telegram_file_unique_id, object_id, state, not_archived_reason, created_at, committed_at)
                VALUES
                    (@id, @message, @ordinal, @kind, @mime, @size, @sha, @fileName,
                     @fileUniqueId, @object, @state, @reason, @now, @committedAt)
                """);
            attach.Parameters.AddWithValue("@id", Ulid.NewUlid(_time.GetUtcNow()));
            attach.Parameters.AddWithValue("@message", newMessageId);
            attach.Parameters.AddWithValue("@ordinal", attachment.Ordinal);
            attach.Parameters.AddWithValue("@kind", JournalWire.Of(attachment.Kind));
            attach.Parameters.AddWithValue("@mime", attachment.MimeType);
            attach.Parameters.AddWithValue("@size", attachment.ByteSize);
            attach.Parameters.AddWithValue("@sha",
                committed && media.Digests.TryGetValue(attachmentObject!, out var digest) ? digest : null);
            attach.Parameters.AddWithValue("@fileName", attachment.FileName);
            attach.Parameters.AddWithValue("@fileUniqueId", attachment.FileUniqueId);
            attach.Parameters.AddWithValue("@object", committed ? (object?)attachmentObject : null);
            attach.Parameters.AddWithValue("@state", committed ? "committed" : "not_archived");
            attach.Parameters.AddWithValue("@reason", committed ? null
                : attachment.NotArchivedReason is { } reasonValue ? JournalWire.Of(reasonValue) : null);
            attach.Parameters.AddWithValue("@now", now);
            attach.Parameters.AddWithValue("@committedAt", committed ? (object?)now : null);
            await attach.ExecuteNonQueryAsync(ct);
        }

        // Park unreferenced dedup losers for the 24-hour sweeper on every ingest path.
        foreach (var loser in media.Losers)
        {
            await using var park = Command(connection, transaction,
                "UPDATE journal_objects SET state = 'aborted', updated_at = @now "
                + "WHERE id = @id AND owner = @owner AND state = 'uploaded'");
            park.Parameters.AddWithValue("@id", loser);
            park.Parameters.AddWithValue("@owner", observer);
            park.Parameters.AddWithValue("@now", now);
            await park.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return new JournalIngestResult { Outcome = JournalIngestOutcome.Created, MessageId = newMessageId };
    }

    private static async Task<bool> LockProofRowsAsync(
        MySqlConnection connection, MySqlTransaction transaction, MediaPlan media, string owner, CancellationToken ct)
    {
        foreach (var id in media.Proved.Order(StringComparer.Ordinal))
        {
            await using var command = Command(connection, transaction,
                "SELECT owner, state FROM journal_objects WHERE id = @id FOR UPDATE");
            command.Parameters.AddWithValue("@id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != owner
                || reader.GetString(1) is not ("uploaded" or "committed")) return false;
        }
        return true;
    }

    private static async Task InsertObserverAsync(
        MySqlConnection connection, MySqlTransaction transaction, string messageId, string observer,
        string eventId, string fingerprint, DateTime now, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            """
            INSERT INTO journal_message_observers (message_id, observer, event_id, fingerprint, observed_at)
            VALUES (@message, @observer, @event, @fingerprint, @now)
            """);
        command.Parameters.AddWithValue("@message", messageId);
        command.Parameters.AddWithValue("@observer", observer);
        command.Parameters.AddWithValue("@event", eventId);
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Deadlock and lock-wait timeout, plus a lost insert race on a unique key: the retry sees the
    /// winner's row and answers from it.
    /// </summary>
    private static bool IsRetryable(MySqlException e) =>
        e.ErrorCode is MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout
            or MySqlErrorCode.DuplicateKeyEntry;

    // ── schema ───────────────────────────────────────────────────────────────

    private async Task EnsureSchemaAsync(MySqlConnection connection, CancellationToken ct)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        if (nowTicks < Interlocked.Read(ref _schemaCurrentUntilTicks)) return;

        var version = await ReadSchemaVersionAsync(connection, ct);

        if (version is null or < RequiredSchemaVersion)
            throw new JournalStoreUnavailableException(JournalStoreUnavailableException.SchemaBehind);

        Interlocked.Exchange(ref _schemaCurrentUntilTicks, nowTicks + SchemaCacheDuration.Ticks);
    }

    private static async Task<int?> ReadSchemaVersionAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            """
            SELECT MAX(version) FROM schema_migrations
            WHERE EXISTS (SELECT 1 FROM information_schema.tables
                          WHERE table_schema = DATABASE() AND table_name = 'schema_migrations')
            """, connection);

        try
        {
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? null : Convert.ToInt32(value);
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            // Never migrated.
            return null;
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static MySqlCommand Command(MySqlConnection connection, MySqlTransaction transaction, string sql) =>
        new(sql, connection, transaction);

    internal static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
