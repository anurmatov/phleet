using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations.Journal;

/// <summary>
/// The journal's read side over MySQL (#394): search, one message, and conversation replay, for the
/// read tools on the journal listener.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope is a condition inside the query, never a check after it.</b> With
/// <see cref="JournalReadScope.Observed"/> a message is visible only through a
/// <c>journal_message_observers</c> row naming the reader, and the same predicate guards the rows a
/// page returns, the reply target a record names, the anchor a replay starts from, and whether the
/// conversation "exists" at all. A hidden row therefore produces exactly what a missing row
/// produces, because to the query it IS a missing row.
/// </para>
/// <para>
/// ⚠️ <b>One READ COMMITTED statement per page</b>, keyset only. Attachments are aggregated and the
/// reply target resolved inside that statement, so a page can never mix two moments, and a failure
/// anywhere in it is <see cref="JournalStoreUnavailableException"/> rather than a partial page. A
/// consistent read takes no lock, so an ingest transaction held open does not stall a reader.
/// </para>
/// <para>
/// ⚠️ Attachment columns are selected by name, and the object id, digest and platform file id are
/// not among them. The media boundary holds in the SQL, not only in the serializer.
/// </para>
/// </remarks>
public sealed class MySqlJournalReadStore : IJournalReadStore
{
    private static readonly TimeSpan SchemaCacheDuration = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    private long _schemaCurrentUntilTicks;

    public MySqlJournalReadStore(string connectionString, ILogger logger, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A journal connection string is required.", nameof(connectionString));

        // The same bounds as the ingest store: an unreachable database is a prompt
        // store_unavailable, not a tool call that hangs for the driver's defaults.
        var builder = new MySqlConnectionStringBuilder(connectionString);
        builder.ConnectionTimeout = Math.Min(builder.ConnectionTimeout, 5);
        if (builder.DefaultCommandTimeout is 0 or > 10) builder.DefaultCommandTimeout = 10;

        _connectionString = builder.ConnectionString;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    // ── search ───────────────────────────────────────────────────────────────

    public Task<JournalSearchPage> SearchAsync(
        JournalReader reader, JournalSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);

        var sql = new StringBuilder(
            $"""
            SELECT m.id, m.conversation_id, c.telegram_chat_id, c.chat_kind, m.source_key, m.direction,
                   m.sender_kind, m.sender_id, m.sender_display, m.sent_at, m.origin,
                   LEFT(m.text, {JournalReadLimits.PreviewChars}),
                   CHAR_LENGTH(m.text) > {JournalReadLimits.PreviewChars},
                   m.transcript IS NOT NULL,
                   (SELECT COUNT(*) FROM journal_attachments a WHERE a.message_id = m.id)
              FROM journal_messages m
              JOIN journal_conversations c ON c.id = m.conversation_id
             WHERE {Visible(reader, "m")}
            """);

        if (query.ConversationId is not null) sql.Append(" AND m.conversation_id = @conversation");
        if (query.SenderKind is not null) sql.Append(" AND m.sender_kind = @senderKind");
        if (query.SenderId is not null) sql.Append(" AND m.sender_id = @senderId");
        if (query.Direction is not null) sql.Append(" AND m.direction = @direction");
        if (query.Since is not null) sql.Append(" AND m.sent_at >= @since");
        if (query.Until is not null) sql.Append(" AND m.sent_at < @until");

        if (query.Terms is { Count: > 0 })
            sql.Append(" AND MATCH(m.text, m.transcript) AGAINST (@query IN BOOLEAN MODE)");

        // Written so the range on ix_sent is visible to the optimizer: `sent_at <= x` bounds the scan,
        // the disjunction only settles ties on the last timestamp.
        if (query.After is not null)
            sql.Append(" AND m.sent_at <= @afterSent AND (m.sent_at < @afterSent OR m.id < @afterId)");

        // One row past the page, so "is there more" is answered by this statement and not a count.
        sql.Append($" ORDER BY m.sent_at DESC, m.id DESC LIMIT {query.Limit + 1}");

        return QueryAsync("search", sql.ToString(), parameters =>
        {
            parameters.AddWithValue("@caller", reader.Subject);
            parameters.AddWithValue("@conversation", query.ConversationId);
            parameters.AddWithValue("@senderKind", query.SenderKind);
            parameters.AddWithValue("@senderId", query.SenderId);
            parameters.AddWithValue("@direction", query.Direction);
            parameters.AddWithValue("@since", query.Since?.UtcDateTime);
            parameters.AddWithValue("@until", query.Until?.UtcDateTime);
            parameters.AddWithValue("@query",
                query.Terms is { Count: > 0 } terms ? JournalSearchTerms.BooleanExpression(terms) : null);

            if (query.After is { } after)
            {
                parameters.AddWithValue("@afterSent", JournalCursor.SentAtOf(after.Key));
                parameters.AddWithValue("@afterId", after.Id);
            }
        }, async (rows, token) =>
        {
            var items = new List<JournalMessageSummary>();
            while (await rows.ReadAsync(token))
            {
                items.Add(new JournalMessageSummary
                {
                    MessageId = rows.GetString(0),
                    ConversationId = rows.GetString(1),
                    TelegramChatId = rows.GetInt64(2),
                    ChatKind = rows.GetString(3),
                    TelegramMessageId = TelegramId(rows.GetString(4)),
                    Direction = rows.GetString(5),
                    Sender = new JournalReadSender
                    {
                        Kind = rows.GetString(6),
                        Id = rows.GetString(7),
                        Display = NullableString(rows, 8),
                    },
                    SentAt = Utc(rows.GetDateTime(9)),
                    Origin = rows.GetString(10),
                    TextPreview = NullableString(rows, 11),
                    TextTruncated = !rows.IsDBNull(12) && rows.GetInt64(12) == 1,
                    HasTranscript = rows.GetInt64(13) == 1,
                    AttachmentCount = Convert.ToInt32(rows.GetValue(14), CultureInfo.InvariantCulture),
                });
            }

            if (items.Count <= query.Limit)
                return new JournalSearchPage { Items = items };

            items.RemoveRange(query.Limit, items.Count - query.Limit);
            var last = items[^1];
            return new JournalSearchPage
            {
                Items = items,
                Next = new JournalKeyPosition(JournalCursor.SentAtKey(last.SentAt), last.MessageId),
            };
        }, ct);
    }

    // ── one message ──────────────────────────────────────────────────────────

    public Task<JournalReadMessage?> GetMessageAsync(
        JournalReader reader, string messageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var sql = $"""
            SELECT {RecordColumns(reader)}
              FROM journal_messages m
              JOIN journal_conversations c ON c.id = m.conversation_id
             WHERE m.id = @message AND {Visible(reader, "m")}
            """;

        return QueryAsync("get_message", sql, parameters =>
        {
            parameters.AddWithValue("@caller", reader.Subject);
            parameters.AddWithValue("@message", messageId);
        }, async (rows, token) => await rows.ReadAsync(token) ? ReadRecord(rows) : null, ct);
    }

    public Task<JournalReadMessage?> FindInConversationAsync(
        JournalReader reader, string conversationKey, long telegramMessageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(conversationKey);
        // uq_source makes the conversation/source intersection unique, even with scope all.
        var sql = $"""
            SELECT {RecordColumns(reader)}
              FROM journal_conversations c
              JOIN journal_messages m ON m.conversation_id = c.id
             WHERE c.conversation_key = @conversation AND m.source_key = @source AND {Visible(reader, "m")}
             LIMIT 1
            """;
        return QueryAsync("get_message", sql, parameters =>
        {
            parameters.AddWithValue("@caller", reader.Subject);
            parameters.AddWithValue("@conversation", conversationKey);
            parameters.AddWithValue("@source", JournalKeys.SourceKey(telegramMessageId));
        }, async (rows, token) => await rows.ReadAsync(token) ? ReadRecord(rows) : null, ct);
    }

    // ── replay ───────────────────────────────────────────────────────────────

    public Task<JournalConversationPage?> ReadConversationAsync(
        JournalReader reader, JournalReplayQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);

        var order = query.Backward ? "DESC" : "ASC";
        var beyond = query.Backward ? "<" : ">";

        // The anchor is resolved INSIDE the statement, under the same visibility rule and pinned to
        // this conversation. A hidden anchor, a missing one and one from another conversation all
        // make the derived table empty, so the whole statement returns no row — the same nothing a
        // conversation that does not exist returns. A cursor, when given, replaces the anchor.
        var anchored = query.After is null && query.FromMessageId is not null;

        var anchor = anchored
            ? $"""
               JOIN (SELECT an.order_key AS anchor_key, an.id AS anchor_id
                       FROM journal_messages an
                      WHERE an.id = @anchor AND an.conversation_id = @conversation
                        AND {Visible(reader, "an")}) anchor ON TRUE
               """
            : "";

        var keyset = anchored
            ? $" AND (m.order_key {beyond} anchor.anchor_key OR (m.order_key = anchor.anchor_key AND m.id {beyond} anchor.anchor_id))"
            : query.After is not null
                ? $" AND (m.order_key {beyond} @afterKey OR (m.order_key = @afterKey AND m.id {beyond} @afterId))"
                : "";

        // LEFT JOIN, so a conversation whose page is empty still returns its one header row, while
        // one with no visible message at all returns none (the EXISTS) and reads as absent.
        var sql = $"""
            SELECT {RecordColumns(reader)}
              FROM journal_conversations c
              {anchor}
              LEFT JOIN journal_messages m
                ON m.conversation_id = c.id AND {Visible(reader, "m")}{keyset}
             WHERE c.id = @conversation
               AND EXISTS (SELECT 1 FROM journal_messages x
                            WHERE x.conversation_id = c.id AND {Visible(reader, "x")})
             ORDER BY m.order_key {order}, m.id {order}
             LIMIT {query.Limit + 1}
            """;

        return QueryAsync("get_conversation", sql, parameters =>
        {
            parameters.AddWithValue("@caller", reader.Subject);
            parameters.AddWithValue("@conversation", query.ConversationId);
            parameters.AddWithValue("@anchor", query.FromMessageId);

            if (query.After is { } after)
            {
                parameters.AddWithValue("@afterKey", after.Key);
                parameters.AddWithValue("@afterId", after.Id);
            }
        }, async (rows, token) =>
        {
            JournalReadConversation? conversation = null;
            var fetched = new List<(JournalReadMessage Record, long OrderKey)>();

            while (await rows.ReadAsync(token))
            {
                conversation ??= ReadConversation(rows);
                if (rows.IsDBNull(0)) continue;
                fetched.Add((ReadRecord(rows), rows.GetInt64(OrderKeyColumn)));
            }

            if (conversation is null) return null;

            // The page ends at the row limit or at the text budget, whichever comes first. The first
            // record always fits, so one very long message cannot stall a traversal.
            var items = new List<JournalReadMessage>();
            var bytes = 0L;
            var more = false;
            (JournalReadMessage Record, long OrderKey)? last = null;

            foreach (var row in fetched)
            {
                var size = TextBytes(row.Record);
                if (items.Count == query.Limit
                    || (items.Count > 0 && bytes + size > JournalReadLimits.ReplayPageTextBytes))
                {
                    more = true;
                    break;
                }

                items.Add(row.Record);
                bytes += size;
                last = row;
            }

            return new JournalConversationPage
            {
                Conversation = conversation,
                Items = items,
                Next = more && last is { } end ? new JournalKeyPosition(end.OrderKey, end.Record.MessageId) : null,
            };
        }, ct);
    }

    // ── the record, in one statement ─────────────────────────────────────────

    /// <summary>Index of <c>m.order_key</c> in <see cref="RecordColumns"/>.</summary>
    private const int OrderKeyColumn = 25;

    /// <summary>
    /// Every column a full record needs, for aliases <c>m</c> (message) and <c>c</c> (conversation).
    /// The reply target and the attachments are sub-selects of the SAME statement.
    /// </summary>
    private static string RecordColumns(JournalReader reader) =>
        $"""
        m.id, c.id, c.chat_kind, c.telegram_chat_id, c.title,
        m.source_key, m.reply_to_source_key,
        (SELECT r.id FROM journal_messages r
          WHERE r.conversation_id = m.conversation_id AND r.source_key = m.reply_to_source_key
            AND {Visible(reader, "r")}),
        m.media_group_id, m.direction, m.sender_kind, m.sender_id, m.sender_display,
        m.sent_at, m.recorded_at, m.text, m.text_format, m.transcript, m.transcript_truncated,
        m.origin, m.delivery_state, m.send_group_id, m.send_part, m.send_parts,
        (SELECT JSON_ARRAYAGG(JSON_OBJECT(
                    'ordinal', a.ordinal, 'kind', a.kind, 'mime_type', a.mime_type,
                    'byte_size', a.byte_size, 'file_name', a.original_file_name, 'state', a.state,
                    'not_archived_reason', a.not_archived_reason))
           FROM journal_attachments a WHERE a.message_id = m.id),
        m.order_key
        """;

    private static JournalReadConversation ReadConversation(MySqlDataReader rows) => new()
    {
        Id = rows.GetString(1),
        ChatKind = rows.GetString(2),
        TelegramChatId = rows.GetInt64(3),
        Title = NullableString(rows, 4),
    };

    private static JournalReadMessage ReadRecord(MySqlDataReader rows) => new()
    {
        MessageId = rows.GetString(0),
        Conversation = ReadConversation(rows),
        TelegramMessageId = TelegramId(rows.GetString(5)),
        ReplyToTelegramMessageId = rows.IsDBNull(6) ? null : TelegramId(rows.GetString(6)),
        ReplyToMessageId = NullableString(rows, 7),
        MediaGroupId = NullableString(rows, 8),
        Direction = rows.GetString(9),
        Sender = new JournalReadSender
        {
            Kind = rows.GetString(10),
            Id = rows.GetString(11),
            Display = NullableString(rows, 12),
        },
        SentAt = Utc(rows.GetDateTime(13)),
        RecordedAt = Utc(rows.GetDateTime(14)),
        Text = NullableString(rows, 15),
        TextFormat = NullableString(rows, 16),
        Transcript = NullableString(rows, 17),
        TranscriptTruncated = rows.GetBoolean(18),
        Origin = rows.GetString(19),
        DeliveryState = rows.GetString(20),
        SendGroup = rows.IsDBNull(21) ? null : new JournalSendGroup
        {
            Id = rows.GetString(21),
            Part = Convert.ToInt32(rows.GetValue(22), CultureInfo.InvariantCulture),
            Parts = Convert.ToInt32(rows.GetValue(23), CultureInfo.InvariantCulture),
        },
        Attachments = rows.IsDBNull(24) ? [] : ReadAttachments(rows.GetString(24)),
    };

    private static IReadOnlyList<JournalReadAttachment> ReadAttachments(string json)
    {
        using var document = JsonDocument.Parse(json);
        var attachments = new List<JournalReadAttachment>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            attachments.Add(new JournalReadAttachment
            {
                Ordinal = element.GetProperty("ordinal").GetInt32(),
                Kind = element.GetProperty("kind").GetString()!,
                MimeType = element.GetProperty("mime_type").GetString()!,
                ByteSize = element.GetProperty("byte_size") is { ValueKind: JsonValueKind.Number } size
                    ? size.GetInt64() : null,
                FileName = element.GetProperty("file_name").GetString(),
                State = element.GetProperty("state").GetString()!,
                NotArchivedReason = element.GetProperty("not_archived_reason").GetString(),
            });
        }

        // JSON_ARRAYAGG promises no order.
        attachments.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        return attachments;
    }

    private static long TextBytes(JournalReadMessage record) =>
        (record.Text is null ? 0 : Encoding.UTF8.GetByteCount(record.Text))
        + (record.Transcript is null ? 0 : Encoding.UTF8.GetByteCount(record.Transcript));

    /// <summary>The platform id inside a <c>tg:&lt;id&gt;</c> source key; null for any other key.</summary>
    private static long? TelegramId(string sourceKey) =>
        sourceKey.StartsWith("tg:", StringComparison.Ordinal)
        && long.TryParse(sourceKey.AsSpan(3), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    // ── scope ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The visibility predicate for the message aliased <paramref name="alias"/>. Every read in this
    /// class — page rows, reply targets, anchors and the conversation's existence — goes through it.
    /// </summary>
    private static string Visible(JournalReader reader, string alias) => reader.Scope switch
    {
        JournalReadScope.All => "TRUE",
        JournalReadScope.Observed =>
            $"EXISTS (SELECT 1 FROM journal_message_observers o_{alias} "
            + $"WHERE o_{alias}.message_id = {alias}.id AND o_{alias}.observer = @caller)",
        _ => throw new ArgumentOutOfRangeException(nameof(reader)),
    };

    // ── plumbing ─────────────────────────────────────────────────────────────

    private async Task<T> QueryAsync<T>(
        string operation, string sql, Action<MySqlParameterCollection> bind,
        Func<MySqlDataReader, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await EnsureSchemaAsync(connection, ct);

            // READ COMMITTED: the statement sees every transaction committed before it started and
            // none that is still open. Read-only, so it takes no lock and cannot be anyone's
            // deadlock victim.
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, isReadOnly: true, ct);

            T result;
            await using (var command = new MySqlCommand(sql, connection, transaction))
            {
                bind(command.Parameters);
                await using var rows = await command.ExecuteReaderAsync(ct);
                result = await read(rows, ct);
            }

            await transaction.CommitAsync(ct);
            return result;
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
                                     or IOException or TimeoutException or OperationCanceledException
                                     or JsonException or FormatException or InvalidCastException)
        {
            // Type only: a driver message can carry the connection string.
            _logger.LogWarning("journal read {Operation} unavailable: {Error}", operation, e.GetType().Name);
            throw new JournalStoreUnavailableException(null, e);
        }
    }

    private async Task EnsureSchemaAsync(MySqlConnection connection, CancellationToken ct)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        if (nowTicks < Interlocked.Read(ref _schemaCurrentUntilTicks)) return;

        int? version;
        await using (var command = new MySqlCommand(
            """
            SELECT MAX(version) FROM schema_migrations
            WHERE EXISTS (SELECT 1 FROM information_schema.tables
                          WHERE table_schema = DATABASE() AND table_name = 'schema_migrations')
            """, connection))
        {
            try
            {
                var value = await command.ExecuteScalarAsync(ct);
                version = value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.NoSuchTable)
            {
                version = null;
            }
        }

        // The ingest store's floor, not the lowest version these reads could run on: a journal that
        // cannot be written is not one to answer questions about.
        if (version is null or < MySqlJournalStore.RequiredSchemaVersion)
            throw new JournalStoreUnavailableException(JournalStoreUnavailableException.SchemaBehind);

        Interlocked.Exchange(ref _schemaCurrentUntilTicks, nowTicks + SchemaCacheDuration.Ticks);
    }

    private static string? NullableString(MySqlDataReader rows, int ordinal) =>
        rows.IsDBNull(ordinal) ? null : rows.GetString(ordinal);

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>
/// <c>search_messages</c>' <c>query</c>, turned into terms the full-text index can match (#394).
/// </summary>
/// <remarks>
/// <para>
/// Every character that is not a letter, digit, combining mark or <c>_</c> separates terms — the
/// word boundary InnoDB's built-in parser uses — so the boolean operators <c>+-&lt;&gt;()~*"@</c> are
/// stripped by construction and a caller can never write an operator into the expression. Each
/// surviving term is then REQUIRED.
/// </para>
/// <para>
/// ⚠️ A required term the index does not hold matches nothing, so such terms are dropped rather than
/// passed through: shorter than <see cref="MinTermChars"/> or longer than <see cref="MaxTermChars"/>
/// (InnoDB's default <c>innodb_ft_min_token_size</c> and <c>innodb_ft_max_token_size</c>), and
/// InnoDB's default stopwords. Measured on MySQL 8.0: <c>+the +cat</c> and <c>+on +cat</c> both
/// return no row for text containing "the cat sat on the mat".
/// </para>
/// </remarks>
public static class JournalSearchTerms
{
    public const int MinTermChars = 3;
    public const int MaxTermChars = 84;

    /// <summary>InnoDB's <c>INNODB_FT_DEFAULT_STOPWORD</c> list.</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "an", "are", "as", "at", "be", "by", "com", "de", "en", "for", "from", "how",
        "i", "in", "is", "it", "la", "of", "on", "or", "that", "the", "this", "to", "was", "what",
        "when", "where", "who", "will", "with", "und", "www",
    };

    /// <summary>The distinct usable terms, in query order. Empty means nothing is searchable.</summary>
    public static IReadOnlyList<string> Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new StringBuilder();
        var runes = 0;

        foreach (var rune in query.EnumerateRunes())
        {
            if (IsWordRune(rune))
            {
                current.Append(rune.ToString());
                runes++;
                continue;
            }

            Flush();
        }

        Flush();
        return terms;

        void Flush()
        {
            if (current.Length == 0) return;

            var term = current.ToString();
            var length = runes;
            current.Clear();
            runes = 0;

            if (length is < MinTermChars or > MaxTermChars || Stopwords.Contains(term)) return;
            if (seen.Add(term)) terms.Add(term);
        }
    }

    /// <summary>Every term required: <c>+a +b</c>. The terms carry no operator character.</summary>
    public static string BooleanExpression(IReadOnlyList<string> terms) =>
        string.Join(' ', terms.Select(term => "+" + term));

    private static bool IsWordRune(Rune rune) =>
        rune.Value == '_' || Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark => true,
            _ => false,
        };
}
