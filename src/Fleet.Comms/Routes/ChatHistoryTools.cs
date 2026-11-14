using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// Two read tools over an agent's own first-party chat: <c>search_chat_messages</c> and
/// <c>get_chat_message</c>. They sit on the journal's MCP server beside the Telegram read tools,
/// so an agent that is granted both can look in either place.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reader is the read-token subject, never an argument</b>, and the store applies that
/// scope inside every query: an agent reads the conversations people hold with it, and no other
/// agent's. A message it may not read and one that does not exist leave this class as the same
/// bytes, and no argument check consults stored data.
/// </para>
/// <para>
/// Granted like every other journal tool: explicitly, per agent, by an enabled tool row. No
/// template carries them and nothing grants them automatically.
/// </para>
/// <para>
/// ⚠️ No text, query or token in any log line or metric label. The meter carries the tool name
/// (one of this class's two constants) and a fixed result code.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ChatHistoryTools(IChatHistoryReadStore store, JournalRuntimeStats stats, IHttpContextAccessor http)
{
    public const string SearchTool = "search_chat_messages";
    public const string GetTool = "get_chat_message";

    internal const string NotFoundBody = "{\"error\":\"not_found\"}";
    internal const string InvalidCursorBody = "{\"error\":\"invalid_cursor\"}";
    internal const string InvalidQueryBody = "{\"error\":\"invalid_query\"}";
    internal const string StoreUnavailableBody = "{\"error\":\"store_unavailable\",\"retryable\":true}";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly Regex Instant = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [McpServerTool(Name = SearchTool, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Search the messages of your own chat with the people who talk to you through the dashboard and the phone app "
        + "(not Telegram: use search_messages for that). Newest first. Every filter is optional; with none, this is the "
        + "recent history. Returns {items, next_cursor}; pass next_cursor back with the SAME filters for the next page. "
        + "Items carry the start of each message; use get_chat_message for the whole of one. Only messages the chat "
        + "still keeps are found (30 days by default).")]
    public Task<CallToolResult> SearchChatMessagesAsync(
        [Description("Text the message must contain, 2-256 characters, matched as written without regard to case.")]
        string? query = null,
        [Description("Only messages written by: user (the person) or agent (you).")]
        string? from = null,
        [Description("Only messages written at or after this instant: ISO-8601 with an offset, e.g. 2026-01-02T03:04:05Z.")]
        string? since = null,
        [Description("Only messages written before this instant: ISO-8601 with an offset.")]
        string? until = null,
        [Description("Page size, 1-50. Default 20.")]
        int limit = ChatHistoryLimits.SearchDefault,
        [Description("next_cursor from the previous page of this same search.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(SearchTool, async agent =>
        {
            query = Blank(query);
            from = Blank(from);
            since = Blank(since);
            until = Blank(until);
            cursor = Blank(cursor);

            if (query is not null && (query.Length > ChatHistoryLimits.QueryMaxChars || query.Trim().Length < ChatHistoryLimits.QueryMinChars))
                return Refuse("invalid_query", InvalidQueryBody);

            if (from is not null and not ("user" or "agent")) return InvalidArgument("from");
            if (limit is < 1 or > ChatHistoryLimits.SearchMax) return InvalidArgument("limit");

            DateTimeOffset? sinceAt = null, untilAt = null;
            if (since is not null && (sinceAt = ParseInstant(since)) is null) return InvalidArgument("since");
            if (until is not null && (untilAt = ParseInstant(until)) is null) return InvalidArgument("until");

            var filterHash = HashFilters(query, from, since, until);

            ChatHistoryPosition? before = null;
            if (cursor is not null && !TryDecodeCursor(cursor, filterHash, out before))
                return Refuse("invalid_cursor", InvalidCursorBody);

            var page = await store.SearchAsync(agent, new ChatHistoryQuery
            {
                Contains = query?.Trim(),
                FromUser = from switch { "user" => true, "agent" => false, _ => null },
                Since = sinceAt,
                Until = untilAt,
                Limit = limit,
                Before = before,
            }, cancellationToken);

            return Ok(new
            {
                items = page.Items.Select(Summary),
                next_cursor = page.Next is { } next ? EncodeCursor(next, filterHash) : null,
            });
        }, cancellationToken);
    }

    [McpServerTool(Name = GetTool, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Get one message of your own dashboard and phone chat in full, by the message_id a search returned. "
        + "A message that is not in your chat answers exactly like one that does not exist.")]
    public Task<CallToolResult> GetChatMessageAsync(
        [Description("A message_id from search_chat_messages.")]
        string message_id,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(GetTool, async agent =>
        {
            if (!Ulid.IsValid(message_id)) return InvalidArgument("message_id");

            var message = await store.GetAsync(agent, message_id, cancellationToken);
            if (message is null) return Refuse("not_found", NotFoundBody);

            return Ok(new
            {
                message_id = message.EventId,
                conversation_id = message.ConversationId,
                from = message.FromUser ? "user" : "agent",
                kind = message.Kind,
                sent_at = Time(message.At),
                text = message.Text,
            });
        }, cancellationToken);
    }

    // ── one call ─────────────────────────────────────────────────────────────

    private readonly record struct Answer(string Result, CallToolResult Call);

    private async Task<CallToolResult> RunAsync(string tool, Func<string, Task<Answer>> body, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var result = "internal";

        try
        {
            // The caller, from the verified read token. JournalAuth has refused every request
            // without one, so a missing subject here is a composition fault, not a caller.
            var agent = http.HttpContext?.Items[JournalAuth.SubjectItem] as string
                ?? throw new InvalidOperationException("a chat history tool ran without an authenticated subject");

            var answer = await body(agent);
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

    // ── output ───────────────────────────────────────────────────────────────

    private static object Summary(ChatHistoryMessage m)
    {
        var truncated = m.Text.Length > ChatHistoryLimits.PreviewChars;
        var preview = truncated
            ? m.Text[..Fleet.Shared.TextTruncation.SafeCutIndex(m.Text, ChatHistoryLimits.PreviewChars)]
            : m.Text;

        return new
        {
            message_id = m.EventId,
            conversation_id = m.ConversationId,
            from = m.FromUser ? "user" : "agent",
            kind = m.Kind,
            sent_at = Time(m.At),
            text_preview = preview,
            text_truncated = truncated,
        };
    }

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static Answer Ok(object payload) =>
        new("ok", new CallToolResult { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, Json) }] });

    private static Answer Refuse(string result, string body) => new(result, Error(body));

    private static Answer InvalidArgument(string field) =>
        Refuse("invalid_argument", JsonSerializer.Serialize(new { error = "invalid_argument", field }, Json));

    private static CallToolResult Error(string body) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = body }] };

    // ── arguments and the cursor ─────────────────────────────────────────────

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static DateTimeOffset? ParseInstant(string value)
    {
        if (!Instant.IsMatch(value)) return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
               && parsed.UtcDateTime.Year is >= 1000 and <= 9998
            ? parsed
            : null;
    }

    /// <summary>
    /// A short digest of the filters a cursor was issued for, so a cursor from one search cannot
    /// page another. It hides nothing: the cursor is a position, not a permission.
    /// </summary>
    internal static string HashFilters(string? query, string? from, string? since, string? until)
    {
        var joined = string.Join('\u001f', query ?? "", from ?? "", since ?? "", until ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }

    internal static string EncodeCursor(ChatHistoryPosition position, string filterHash) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{position.At.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{position.EventId}|{filterHash}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryDecodeCursor(string cursor, string filterHash, out ChatHistoryPosition? position)
    {
        position = null;
        if (cursor.Length > 200) return false;

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');

            if (parts.Length != 3
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !Ulid.IsValid(parts[1])
                || !string.Equals(parts[2], filterHash, StringComparison.Ordinal))
            {
                return false;
            }

            position = new ChatHistoryPosition(new DateTimeOffset(ticks, TimeSpan.Zero), parts[1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
