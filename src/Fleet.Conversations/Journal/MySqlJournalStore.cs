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
public sealed class MySqlJournalStore : IJournalStore
{
    /// <summary>The schema version that created the journal tables.</summary>
    public const int RequiredSchemaVersion = 4;

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
        JournalRecord record, string observer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!JournalTokens.IsValidSubject(observer))
            throw new ArgumentException("an observer is 1-128 characters of [A-Za-z0-9_-]", nameof(observer));

        var conversationKey = JournalKeys.ConversationKey(record.Telegram);
        var fingerprint = JournalFingerprint.Compute(conversationKey, record);

        try
        {
            await using var connection = await OpenAsync(ct);
            await EnsureSchemaAsync(connection, ct);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await IngestOnceAsync(connection, record, observer, conversationKey, fingerprint, ct);
                }
                catch (MySqlException e) when (attempt == 0 && IsRetryable(e))
                {
                    _logger.LogInformation(
                        "journal ingest from {Observer} retried once after {Error}", observer, e.ErrorCode);
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
        string conversationKey, string fingerprint, CancellationToken ct)
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

        // 4. A new message: the row, this observer, and attachment metadata.
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

        // Metadata only, and never visible with a dangling reference: every row is not_archived
        // and names why.
        foreach (var attachment in record.Attachments)
        {
            await using var attach = Command(connection, transaction,
                """
                INSERT INTO journal_attachments
                    (id, message_id, ordinal, kind, mime_type, byte_size, original_file_name,
                     telegram_file_unique_id, state, not_archived_reason, created_at)
                VALUES
                    (@id, @message, @ordinal, @kind, @mime, @size, @fileName,
                     @fileUniqueId, 'not_archived', @reason, @now)
                """);
            attach.Parameters.AddWithValue("@id", Ulid.NewUlid(_time.GetUtcNow()));
            attach.Parameters.AddWithValue("@message", newMessageId);
            attach.Parameters.AddWithValue("@ordinal", attachment.Ordinal);
            attach.Parameters.AddWithValue("@kind", JournalWire.Of(attachment.Kind));
            attach.Parameters.AddWithValue("@mime", attachment.MimeType);
            attach.Parameters.AddWithValue("@size", attachment.ByteSize);
            attach.Parameters.AddWithValue("@fileName", attachment.FileName);
            attach.Parameters.AddWithValue("@fileUniqueId", attachment.FileUniqueId);
            attach.Parameters.AddWithValue("@reason", JournalWire.Of(attachment.NotArchivedReason));
            attach.Parameters.AddWithValue("@now", now);
            await attach.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new JournalIngestResult { Outcome = JournalIngestOutcome.Created, MessageId = newMessageId };
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
