using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Fleet.Comms.Routes;

/// <summary>
/// The three read tools: <c>search_messages</c>, <c>get_message</c>, <c>get_conversation</c> (#394).
/// </summary>
/// <remarks>
/// <para>
/// <b>No oracle.</b> The reader is the read-token subject, never an argument, and the store applies
/// its scope inside every query. So a message the caller may not see and one that does not exist
/// reach this class as the same null, and leave it as the same bytes: <see cref="NotFoundBody"/>.
/// Nothing here counts, totals or distinguishes the two, and no argument check consults stored data
/// — a malformed id or a mismatched cursor is refused from the arguments alone.
/// </para>
/// <para>
/// ⚠️ No text, query, transcript or token in any log line or metric label. The meter carries the
/// tool and a fixed result code.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class JournalReadTools(
    IJournalReadStore store, JournalReadGrants grants, JournalRuntimeStats stats, IHttpContextAccessor http)
{
    public const string SearchTool = "search_messages";
    public const string GetMessageTool = "get_message";
    public const string ConversationTool = "get_conversation";

    // The fixed error bodies. One constant each, so "out of scope" and "absent" cannot drift apart.
    internal const string NotFoundBody = "{\"error\":\"not_found\"}";
    internal const string InvalidCursorBody = "{\"error\":\"invalid_cursor\"}";
    internal const string InvalidQueryBody = "{\"error\":\"invalid_query\"}";
    internal const string StoreUnavailableBody = "{\"error\":\"store_unavailable\",\"retryable\":true}";

    /// <summary>
    /// Output JSON. Relaxed escaping because the consumer is a model reading text, not an HTML page,
    /// and <c><</c> in every HTML-formatted reply only costs it tokens. Nulls are written, so a
    /// field's presence never depends on the row.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>ISO-8601 with an offset, as the <c>since</c>/<c>until</c> contract requires.</summary>
    private static readonly Regex Instant = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [McpServerTool(Name = SearchTool, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Search journaled Telegram messages you may read, newest first (sent_at, then id, descending). "
        + "Every filter is optional. Returns {items, next_cursor}; pass next_cursor back with the SAME "
        + "filters to get the next page. There is no total count. Items carry a text preview; use "
        + "get_message or get_conversation for full records.")]
    public Task<CallToolResult> SearchMessagesAsync(
        [Description("Full-text query over text and transcripts, at most 256 characters. Every word is required; words under 3 characters and common stopwords are ignored.")]
        string? query = null,
        [Description("Only messages in this conversation (a conversation_id from a previous result).")]
        string? conversation_id = null,
        [Description("Only messages from this kind of sender: human or agent.")]
        string? sender_kind = null,
        [Description("Only messages from this sender id.")]
        string? sender_id = null,
        [Description("Only inbound or outbound messages.")]
        string? direction = null,
        [Description("Only messages sent at or after this instant: ISO-8601 with an offset, e.g. 2026-01-02T03:04:05Z.")]
        string? since = null,
        [Description("Only messages sent before this instant: ISO-8601 with an offset.")]
        string? until = null,
        [Description("Page size, 1-100. Default 20.")]
        int limit = JournalReadLimits.SearchDefault,
        [Description("next_cursor from the previous page of this same search.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(SearchTool, async reader =>
        {
            query = Blank(query);
            conversation_id = Blank(conversation_id);
            sender_kind = Blank(sender_kind);
            sender_id = Blank(sender_id);
            direction = Blank(direction);
            since = Blank(since);
            until = Blank(until);
            cursor = Blank(cursor);

            IReadOnlyList<string>? terms = null;
            if (query is not null)
            {
                if (query.Length > JournalReadLimits.QueryMaxChars) return Refuse("invalid_query", InvalidQueryBody);

                terms = JournalSearchTerms.Parse(query);
                if (terms.Count == 0) return Refuse("invalid_query", InvalidQueryBody);
            }

            if (conversation_id is not null && !Ulid.IsValid(conversation_id)) return InvalidArgument("conversation_id");
            if (sender_kind is not null and not ("human" or "agent")) return InvalidArgument("sender_kind");
            if (sender_id is not null && sender_id.Length > 128) return InvalidArgument("sender_id");
            if (direction is not null and not ("inbound" or "outbound")) return InvalidArgument("direction");
            if (limit is < 1 or > JournalReadLimits.SearchMax) return InvalidArgument("limit");

            DateTimeOffset? from = null, to = null;
            if (since is not null && (from = ParseInstant(since)) is null) return InvalidArgument("since");
            if (until is not null && (to = ParseInstant(until)) is null) return InvalidArgument("until");

            var filterHash = JournalCursor.HashFilters(
                ("query", query), ("conversation_id", conversation_id), ("sender_kind", sender_kind),
                ("sender_id", sender_id), ("direction", direction), ("since", since), ("until", until));

            JournalKeyPosition? after = null;
            if (cursor is not null)
            {
                if (!JournalCursor.TryDecode(cursor, out var decoded)
                    || decoded!.Tool != SearchTool
                    || decoded.FilterHash != filterHash
                    || decoded.ConversationId is not null
                    || decoded.Direction is not null
                    || !JournalCursor.IsSentAtKey(decoded.Last.Key))
                    return Refuse("invalid_cursor", InvalidCursorBody);

                after = decoded.Last;
            }

            var page = await store.SearchAsync(reader, new JournalSearchQuery
            {
                Terms = terms,
                ConversationId = conversation_id,
                SenderKind = sender_kind,
                SenderId = sender_id,
                Direction = direction,
                Since = from,
                Until = to,
                Limit = limit,
                After = after,
            }, cancellationToken);

            return Ok(new
            {
                items = page.Items.Select(Summary),
                next_cursor = page.Next is { } next
                    ? new JournalCursor { Tool = SearchTool, FilterHash = filterHash, Last = next }.Encode()
                    : null,
            });
        }, cancellationToken);
    }

    [McpServerTool(Name = GetMessageTool, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Get one journaled message in full, by message_id OR by telegram_chat_id plus "
        + "telegram_message_id (exactly one form). The Telegram form can match one message per bot "
        + "conversation; when more than one matches you get error 'ambiguous' with candidate "
        + "message_ids. A message you may not read answers exactly like one that does not exist.")]
    public Task<CallToolResult> GetMessageAsync(
        [Description("The journal message id.")]
        string? message_id = null,
        [Description("Telegram chat id; use together with telegram_message_id.")]
        long? telegram_chat_id = null,
        [Description("Telegram message id within that chat.")]
        long? telegram_message_id = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(GetMessageTool, async reader =>
        {
            message_id = Blank(message_id);
            var telegramForm = telegram_chat_id is not null || telegram_message_id is not null;

            if ((message_id is not null) == telegramForm) return InvalidArgument("message_id");

            if (message_id is not null)
            {
                if (!Ulid.IsValid(message_id)) return InvalidArgument("message_id");

                var record = await store.GetMessageAsync(reader, message_id, cancellationToken);
                return record is null ? Refuse("not_found", NotFoundBody) : Ok(Record(record));
            }

            if (telegram_chat_id is not { } chat) return InvalidArgument("telegram_chat_id");
            if (telegram_message_id is not { } messageId) return InvalidArgument("telegram_message_id");

            var lookup = await store.FindByTelegramAsync(reader, chat, messageId, cancellationToken);

            if (lookup.Candidates.Count > 1)
            {
                return Refuse("ambiguous", JsonSerializer.Serialize(
                    new { error = "ambiguous", candidates = lookup.Candidates }, Json));
            }

            return lookup.Message is null ? Refuse("not_found", NotFoundBody) : Ok(Record(lookup.Message));
        }, cancellationToken);
    }

    [McpServerTool(Name = ConversationTool, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Read a conversation in message order, one page of full records at a time. Returns "
        + "{conversation, items, next_cursor}; pass next_cursor back with the SAME conversation_id, "
        + "from_message_id and direction. A page also ends early at 256 KiB of text. A conversation "
        + "you may not read answers exactly like one that does not exist.")]
    public Task<CallToolResult> GetConversationAsync(
        [Description("The conversation id (conversation.id or conversation_id from another result).")]
        string conversation_id,
        [Description("Start after this message (exclusive). It must be a message of this conversation.")]
        string? from_message_id = null,
        [Description("forward (default): from the oldest. backward: from the newest.")]
        string? direction = null,
        [Description("Page size, 1-200. Default 50.")]
        int limit = JournalReadLimits.ReplayDefault,
        [Description("next_cursor from the previous page of this same read.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(ConversationTool, async reader =>
        {
            from_message_id = Blank(from_message_id);
            direction = Blank(direction) ?? "forward";
            cursor = Blank(cursor);

            if (!Ulid.IsValid(conversation_id)) return InvalidArgument("conversation_id");
            if (from_message_id is not null && !Ulid.IsValid(from_message_id)) return InvalidArgument("from_message_id");
            if (direction is not ("forward" or "backward")) return InvalidArgument("direction");
            if (limit is < 1 or > JournalReadLimits.ReplayMax) return InvalidArgument("limit");

            var filterHash = JournalCursor.HashFilters(
                ("conversation_id", conversation_id), ("from_message_id", from_message_id),
                ("direction", direction));

            JournalKeyPosition? after = null;
            if (cursor is not null)
            {
                if (!JournalCursor.TryDecode(cursor, out var decoded)
                    || decoded!.Tool != ConversationTool
                    || decoded.FilterHash != filterHash
                    || decoded.ConversationId != conversation_id
                    || decoded.Direction != direction)
                    return Refuse("invalid_cursor", InvalidCursorBody);

                after = decoded.Last;
            }

            var page = await store.ReadConversationAsync(reader, new JournalReplayQuery
            {
                ConversationId = conversation_id,
                FromMessageId = from_message_id,
                Backward = direction == "backward",
                Limit = limit,
                After = after,
            }, cancellationToken);

            if (page is null) return Refuse("not_found", NotFoundBody);

            return Ok(new
            {
                conversation = Conversation(page.Conversation),
                items = page.Items.Select(Record),
                next_cursor = page.Next is { } next
                    ? new JournalCursor
                    {
                        Tool = ConversationTool,
                        FilterHash = filterHash,
                        ConversationId = conversation_id,
                        Direction = direction,
                        Last = next,
                    }.Encode()
                    : null,
            });
        }, cancellationToken);
    }

    // ── one call ─────────────────────────────────────────────────────────────

    private readonly record struct Answer(string Result, CallToolResult Call);

    /// <summary>
    /// Resolves the reader, runs one tool body, and records the call. A store that cannot answer is
    /// <see cref="StoreUnavailableBody"/> — retryable, and never a partial page.
    /// </summary>
    private async Task<CallToolResult> RunAsync(
        string tool, Func<JournalReader, Task<Answer>> body, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var result = "internal";

        try
        {
            var answer = await body(Reader());
            result = answer.Result;
            return answer.Call;
        }
        catch (JournalStoreUnavailableException)
        {
            result = "store_unavailable";
            return Error(StoreUnavailableBody);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = "cancelled";
            throw;
        }
        finally
        {
            stats.RecordRead(tool, result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>
    /// The caller, from the verified read token. <see cref="JournalAuth"/> has already refused every
    /// request without one, so a missing subject here is a composition fault, not a caller.
    /// </summary>
    private JournalReader Reader() =>
        grants.ReaderFor(http.HttpContext?.Items[JournalAuth.SubjectItem] as string
            ?? throw new InvalidOperationException("a read tool ran without an authenticated subject"));

    // ── output ───────────────────────────────────────────────────────────────

    private static object Summary(JournalMessageSummary m) => new
    {
        message_id = m.MessageId,
        conversation_id = m.ConversationId,
        telegram_chat_id = m.TelegramChatId,
        telegram_message_id = m.TelegramMessageId,
        chat_kind = m.ChatKind,
        direction = m.Direction,
        sender = Sender(m.Sender),
        sent_at = Time(m.SentAt),
        origin = m.Origin,
        text_preview = m.TextPreview,
        text_truncated = m.TextTruncated,
        has_transcript = m.HasTranscript,
        attachment_count = m.AttachmentCount,
    };

    /// <summary>
    /// The full record. <c>reply_to</c> is null when the message replies to nothing; when it does,
    /// the shape is always both keys, and <c>message_id</c> is null for a target that is absent and
    /// for one the caller may not see alike.
    /// </summary>
    private static object Record(JournalReadMessage m) => new
    {
        message_id = m.MessageId,
        conversation = Conversation(m.Conversation),
        telegram_message_id = m.TelegramMessageId,
        reply_to = m.ReplyToTelegramMessageId is null
            ? null
            : new { telegram_message_id = m.ReplyToTelegramMessageId, message_id = m.ReplyToMessageId },
        media_group_id = m.MediaGroupId,
        direction = m.Direction,
        sender = Sender(m.Sender),
        sent_at = Time(m.SentAt),
        recorded_at = Time(m.RecordedAt),
        text = m.Text,
        text_format = m.TextFormat,
        transcript = m.Transcript,
        transcript_truncated = m.TranscriptTruncated,
        origin = m.Origin,
        delivery_state = m.DeliveryState,
        send_group = m.SendGroup is { } group ? new { id = group.Id, part = group.Part, parts = group.Parts } : null,

        // Metadata only: the contract type has no object id, key, digest, file id or URL to emit.
        attachments = m.Attachments.Select(a => new
        {
            ordinal = a.Ordinal,
            kind = a.Kind,
            mime_type = a.MimeType,
            byte_size = a.ByteSize,
            file_name = a.FileName,
            state = a.State,
            not_archived_reason = a.NotArchivedReason,
        }),
    };

    private static object Conversation(JournalReadConversation c) => new
    {
        id = c.Id,
        chat_kind = c.ChatKind,
        telegram_chat_id = c.TelegramChatId,
        title = c.Title,
    };

    private static object Sender(JournalReadSender s) => new { kind = s.Kind, id = s.Id, display = s.Display };

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static Answer Ok(object payload) =>
        new("ok", new CallToolResult { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, Json) }] });

    private static Answer Refuse(string result, string body) => new(result, Error(body));

    private static Answer InvalidArgument(string field) =>
        Refuse("invalid_argument", JsonSerializer.Serialize(new { error = "invalid_argument", field }, Json));

    private static CallToolResult Error(string body) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = body }] };

    // ── arguments ────────────────────────────────────────────────────────────

    /// <summary>An empty string is an omitted argument: clients fill optional fields with one.</summary>
    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// An instant with an explicit offset, inside the range the column can hold; null otherwise. A
    /// value without an offset is refused rather than read in this server's time zone.
    /// </summary>
    private static DateTimeOffset? ParseInstant(string value)
    {
        if (!Instant.IsMatch(value)) return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
               && parsed.UtcDateTime.Year is >= 1000 and <= 9998
            ? parsed
            : null;
    }
}
