using System.ComponentModel;
using System.Text.Json;
using Fleet.Shared;
using Fleet.Telegram.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tools;

[McpServerToolType]
public sealed class SendMessageTool(BotClientFactory factory, IHttpContextAccessor httpContextAccessor, ILogger<SendMessageTool> logger)
{
    [McpServerTool(Name = "send_message")]
    [Description("Post a text message to a Telegram chat. Supports a permissive Markdown-like subset (**bold**, `inline code`, fenced code blocks, [label](url) links) — automatically escaped and rendered as Telegram HTML. Returns {\"ok\":true,\"message_id\":N} on success, with optional flags: \"fallback\":true (notifier bot used), \"reply_fallback\":true (reply target not found, sent standalone), \"format_fallback\":true (formatting rejected by API, resent as plain text).")]
    public async Task<string> SendAsync(
        [Description("Telegram chat ID as integer or string (e.g. -1001234567890 or \"-1001234567890\" for a group, positive integer for a DM)")] string chat_id,
        [Description("Message text. Supports **bold**, `inline code`, fenced code blocks, and [label](url) links. List syntax (- item, 1. item) is not supported by Telegram and renders as literal text.")] string text,
        [Description("Parse mode override. Omit or empty for auto-detect (converts recognized Markdown subset to HTML). \"PLAIN\" disables all formatting. Legacy values HTML/MARKDOWN/MARKDOWNV2 are accepted but treated as auto-detect.")] string parse_mode = "",
        [Description("Optional message ID to reply to. When supplied, the message is sent as a threaded reply. If the target message is not found, the message is sent standalone with a reply_fallback flag in the response.")] int? reply_to_message_id = null,
        CancellationToken cancellationToken = default)
    {
        var agent_name = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault() ?? "";

        if (!long.TryParse(chat_id?.Trim(), out var chatIdLong))
            return JsonSerializer.Serialize(new { ok = false, error = $"Invalid chat_id '{chat_id}' — must be a numeric value" });
        if (chat_id != null && (chat_id.StartsWith('"') || chat_id.EndsWith('"')))
            logger.LogWarning("chat_id was passed as a quoted string '{ChatId}' — coerced to long {Parsed}", chat_id, chatIdLong);

        var client = factory.GetClient(agent_name);
        if (client is null)
        {
            const string err = "No bot client available — notifier bot token not configured";
            logger.LogError(err);
            return JsonSerializer.Serialize(new { ok = false, error = err });
        }

        // Empty/whitespace messages are rejected by Telegram
        if (string.IsNullOrWhiteSpace(text))
            return JsonSerializer.Serialize(new { ok = false, error = "empty message" });

        bool forcePlain = parse_mode?.Trim().Equals("PLAIN", StringComparison.OrdinalIgnoreCase) == true;

        List<string> chunks;
        bool usingHtml;

        if (forcePlain)
        {
            // Explicit plain-text opt-out: HTML-escape so every character renders
            // literally (including & < >), but keep markdown markers as-is — they
            // are not HTML-special and will appear as literal * ` [ characters.
            // ParseMode.Html is required for the escaping to take effect correctly.
            var plain = TelegramFormatter.StripToPlain(text);
            chunks = SplitPlain(plain);
            usingHtml = true;
        }
        else
        {
            // Auto-detect: convert recognized Markdown subset to Telegram HTML.
            // The formatter always produces valid escaped HTML, so always use Html parse_mode —
            // even for plain prose with < > & characters (which would render as &lt; etc.
            // without parse_mode and confuse readers).
            chunks = TelegramFormatter.FormatAndSplit(text);
            usingHtml = true;
        }

        if (chunks.Count > 1)
            logger.LogWarning("Message to chat {ChatId} was split into {Count} chunks", chatIdLong, chunks.Count);

        ParseMode? pm = usingHtml ? ParseMode.Html : null;

        // Pre-flight: validate HTML structure before the API call to avoid unnecessary rejections.
        // This catches formatter bugs; in normal operation every chunk should pass.
        if (usingHtml && !chunks.All(TelegramFormatter.ValidateHtml))
        {
            logger.LogWarning("Pre-flight HTML validation failed for chat {ChatId} — falling back to plain text", chatIdLong);
            // StripHtmlTags already unescapes entities and strips tags — use directly as plain text.
            var fallbackText = string.Join("\n", chunks.Select(StripHtmlTags));
            chunks = SplitPlain(fallbackText);
            usingHtml = false;
            pm = null;
        }

        int lastMessageId = 0;
        bool usedFallback = false;
        bool usedReplyFallback = false;
        bool usedFormatFallback = false;
        bool replyConsumed = false;

        for (int idx = 0; idx < chunks.Count; idx++)
        {
            var chunk = chunks[idx];
            if (string.IsNullOrEmpty(chunk)) continue;

            var replyId = !replyConsumed ? reply_to_message_id : null;
            var result = await TrySendAsync(client, chatIdLong, chunk, pm, agent_name, replyId, cancellationToken);

            if (!result.ok && result.parseEntitiesError && usingHtml)
            {
                // Formatting rejected — fall back to plain text for this and remaining chunks.
                // Preserve the reply thread on the first plain-text send (replyConsumed not yet set).
                logger.LogWarning("parse-entities error on chunk {Idx}/{Total} for chat {ChatId} — falling back to plain text for remaining chunks",
                    idx + 1, chunks.Count, chatIdLong);
                usedFormatFallback = true;
                pm = null;
                usingHtml = false;

                // Rebuild remaining chunks as plain text (including current failed chunk).
                // StripHtmlTags already unescapes entities — use the result directly.
                var remainingText = string.Join("\n", chunks[idx..].Select(StripHtmlTags));
                var plainChunks = SplitPlain(remainingText);

                foreach (var plainChunk in plainChunks)
                {
                    if (string.IsNullOrEmpty(plainChunk)) continue;
                    var pr = !replyConsumed ? reply_to_message_id : null;
                    var plainResult = await TrySendAsync(client, chatIdLong, plainChunk, null, agent_name, pr, cancellationToken);
                    if (plainResult.ok) replyConsumed = true; // only mark consumed on success
                    if (!plainResult.ok)
                        return JsonSerializer.Serialize(new { ok = false, error = plainResult.error });
                    lastMessageId = plainResult.messageId;
                    if (plainResult.fallback) usedFallback = true;
                    if (plainResult.replyFallback) usedReplyFallback = true;
                }
                // Done — remaining chunks handled above
                break;
            }

            if (!result.ok)
                return JsonSerializer.Serialize(new { ok = false, error = result.error });

            // Only mark reply consumed after a successful send
            replyConsumed = true;
            lastMessageId = result.messageId;
            if (result.fallback) usedFallback = true;
            if (result.replyFallback) usedReplyFallback = true;
        }

        // Build response with whichever flags fired
        if (usedFallback && usedReplyFallback && usedFormatFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, fallback = true, reply_fallback = true, format_fallback = true });
        if (usedFallback && usedFormatFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, fallback = true, format_fallback = true });
        if (usedReplyFallback && usedFormatFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, reply_fallback = true, format_fallback = true,
                warning = "reply target not found, sent as standalone" });
        if (usedFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, fallback = true });
        if (usedReplyFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, reply_fallback = true,
                warning = "reply target not found, sent as standalone" });
        if (usedFormatFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, format_fallback = true });

        return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId });
    }

    private async Task<(bool ok, int messageId, string error, bool fallback, bool replyFallback, bool parseEntitiesError)> TrySendAsync(
        ITelegramBotClient client,
        long chatId,
        string text,
        ParseMode? parseMode,
        string agentName,
        int? replyToMessageId,
        CancellationToken ct)
    {
        ReplyParameters? replyParams = replyToMessageId.HasValue
            ? new ReplyParameters { MessageId = replyToMessageId.Value }
            : null;

        try
        {
            var msg = parseMode.HasValue
                ? await client.SendMessage(chatId, text, parseMode: parseMode.Value,
                    replyParameters: replyParams, cancellationToken: ct)
                : await client.SendMessage(chatId, text,
                    replyParameters: replyParams, cancellationToken: ct);
            return (true, msg.Id, string.Empty, false, false, false);
        }
        catch (Exception ex) when (IsParseEntitiesError(ex))
        {
            logger.LogWarning(ex, "Bot API parse-entities error for chat {ChatId}", chatId);
            return (false, 0, ex.Message, false, false, true);
        }
        catch (Exception ex) when (IsReplyNotFound(ex))
        {
            logger.LogWarning("Reply target {ReplyId} not found in chat {ChatId} — sending as standalone",
                replyToMessageId, chatId);
            try
            {
                var msg = parseMode.HasValue
                    ? await client.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                    : await client.SendMessage(chatId, text, cancellationToken: ct);
                return (true, msg.Id, string.Empty, false, true, false);
            }
            catch (Exception fbEx)
            {
                logger.LogError(fbEx, "Standalone fallback also failed for chat {ChatId}", chatId);
                return (false, 0, fbEx.Message, false, true, false);
            }
        }
        catch (Exception ex) when (Is403(ex))
        {
            var fallback = factory.GetFallbackClient();
            if (fallback is not null && fallback != client)
            {
                logger.LogWarning(
                    "Bot for agent '{AgentName}' got 403 on chat {ChatId} — retrying with fallback bot",
                    agentName, chatId);
                try
                {
                    var msg = parseMode.HasValue
                        ? await fallback.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                        : await fallback.SendMessage(chatId, text, cancellationToken: ct);
                    return (true, msg.Id, string.Empty, true, false, false);
                }
                catch (Exception fbEx)
                {
                    logger.LogError(fbEx, "Fallback bot also failed for chat {ChatId}", chatId);
                    return (false, 0, fbEx.Message, true, false, false);
                }
            }

            logger.LogError(ex, "Bot for agent '{AgentName}' got 403 on chat {ChatId}", agentName, chatId);
            return (false, 0, ex.Message, false, false, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send message to chat {ChatId}", chatId);
            return (false, 0, ex.Message, false, false, false);
        }
    }

    private static bool Is403(Exception ex) =>
        ex.Message.Contains("403") ||
        ex.Message.Contains("Forbidden") ||
        ex.Message.Contains("bot was blocked") ||
        ex.Message.Contains("not a member");

    private static bool IsReplyNotFound(Exception ex) =>
        ex.Message.Contains("message to be replied not found") ||
        ex.Message.Contains("reply message not found");

    private static bool IsParseEntitiesError(Exception ex) =>
        ex.Message.Contains("can't parse entities") ||
        ex.Message.Contains("Bad Request: can't parse") ||
        ex.Message.Contains("parse_mode");

    private static List<string> SplitPlain(string text)
    {
        const int max = 4096;
        if (text.Length <= max) return [text];

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= max)
            {
                chunks.Add(remaining.ToString());
                break;
            }
            int cut = max;
            // don't split a surrogate pair
            if (char.IsLowSurrogate(remaining[cut]) && cut > 0) cut--;
            // prefer newline
            int nl = remaining[..cut].LastIndexOf('\n');
            if (nl > cut / 2) cut = nl + 1;
            chunks.Add(remaining[..cut].ToString());
            remaining = remaining[cut..];
        }
        return chunks;
    }

    /// <summary>
    /// Strips HTML tags from a chunk of formatter output, preserving link URLs as
    /// "label (url)" so context is not lost when falling back to plain text.
    /// Also unescapes HTML entities (&amp; &lt; &gt; &quot;).
    /// </summary>
    private static string StripHtmlTags(string html)
    {
        var sb = new System.Text.StringBuilder(html.Length);
        string? pendingHref = null;
        int i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<') { sb.Append(html[i++]); continue; }

            int end = html.IndexOf('>', i + 1);
            if (end < 0) { sb.Append(html[i++]); continue; } // unclosed tag — treat < as literal

            string inner = html.Substring(i + 1, end - i - 1).Trim();
            i = end + 1;

            if (inner.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
            {
                // Extract href="..." manually to avoid a Regex dependency
                int hrefIdx = inner.IndexOf("href=\"", StringComparison.OrdinalIgnoreCase);
                if (hrefIdx >= 0)
                {
                    int hrefStart = hrefIdx + 6;
                    int hrefEnd = inner.IndexOf('"', hrefStart);
                    if (hrefEnd > hrefStart)
                        pendingHref = inner.Substring(hrefStart, hrefEnd - hrefStart);
                }
            }
            else if (inner.Equals("/a", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingHref != null)
                {
                    sb.Append(" (").Append(pendingHref).Append(')');
                    pendingHref = null;
                }
            }
            // All other tags: skip
        }

        return sb.ToString()
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"");
    }
}
