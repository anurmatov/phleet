using System.ComponentModel;
using System.Text.Json;
using Fleet.Shared;
using Fleet.Telegram.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tools;

[McpServerToolType]
public sealed class SendToCeoTool(
    BotClientFactory factory,
    IHttpContextAccessor httpContextAccessor,
    CeoConfigService ceoConfig,
    ILogger<SendToCeoTool> logger)
{
    [McpServerTool(Name = "send_to_ceo")]
    [Description("Send a direct message to the CEO. The CEO's chat ID and sending bot are resolved server-side — neither appears in agent context or logs. Supports a permissive Markdown-like subset (**bold**, `inline code`, fenced code blocks, [label](url) links) — automatically escaped and rendered as Telegram HTML. Returns {\"ok\":true,\"message_id\":N} on success, with optional flags: \"fallback\":true (notifier bot used), \"format_fallback\":true (formatting rejected by API, resent as plain text).")]
    public async Task<string> SendAsync(
        [Description("Message text. Supports **bold**, `inline code`, fenced code blocks, and [label](url) links.")] string text,
        [Description("Parse mode override. Omit or empty for auto-detect (converts recognized Markdown subset to HTML). \"PLAIN\" disables all formatting. Legacy values HTML/MARKDOWN/MARKDOWNV2 are accepted but treated as auto-detect.")] string parse_mode = "",
        CancellationToken ct = default)
    {
        var chatId = ceoConfig.ChatId;
        if (chatId == 0)
            return JsonSerializer.Serialize(new { ok = false, error = "CEO chat ID not configured" });

        var agent_name = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault() ?? "";

        var client = factory.GetClient(agent_name);
        if (client is null)
        {
            const string err = "No bot client available — notifier bot token not configured";
            logger.LogError(err);
            return JsonSerializer.Serialize(new { ok = false, error = err });
        }

        // F4: empty message guard (mirrors SendMessageTool:41)
        if (string.IsNullOrWhiteSpace(text))
            return JsonSerializer.Serialize(new { ok = false, error = "empty message" });

        bool forcePlain = parse_mode?.Trim().Equals("PLAIN", StringComparison.OrdinalIgnoreCase) == true;

        List<string> chunks;
        bool usingHtml;

        if (forcePlain)
        {
            // StripToPlain HTML-escapes, so ParseMode.Html is required for escaping to render correctly.
            var plain = TelegramFormatter.StripToPlain(text);
            chunks = SplitPlain(plain);
            usingHtml = true;
        }
        else
        {
            chunks = TelegramFormatter.FormatAndSplit(text);
            usingHtml = true;
        }

        if (chunks.Count > 1)
            logger.LogWarning("Message to CEO was split into {Count} chunks (exceeded Telegram limit)", chunks.Count);

        // F3: pre-flight validation must set usingHtml=false and pm=null when falling back to raw text,
        // because StripHtmlTags unescapes entities — sending raw '<' with ParseMode.Html causes a guaranteed
        // parse-entities error.
        if (usingHtml && !chunks.All(TelegramFormatter.ValidateHtml))
        {
            logger.LogWarning("Pre-flight HTML validation failed for CEO message — falling back to plain text");
            var fallbackText = string.Join("\n", chunks.Select(StripHtmlTags));
            chunks = SplitPlain(fallbackText);
            usingHtml = false;
        }

        ParseMode? pm = usingHtml ? ParseMode.Html : null;

        int lastMessageId = 0;
        bool usedFallback = false;
        bool usedFormatFallback = false;

        for (int idx = 0; idx < chunks.Count; idx++)
        {
            var chunk = chunks[idx];
            if (string.IsNullOrEmpty(chunk)) continue;

            var result = await TrySendAsync(client, chatId, chunk, pm, agent_name, ct);

            if (!result.ok && result.parseEntitiesError && usingHtml)
            {
                logger.LogWarning("parse-entities error on CEO message chunk {Idx}/{Total} — falling back to plain text",
                    idx + 1, chunks.Count);
                usedFormatFallback = true;
                usingHtml = false;

                // F1+F2: use SplitPlain (not a bare collection expression) so long stripped
                // text is correctly chunked; StripHtmlTags unescapes back to raw, pm=null.
                var remainingText = string.Join("\n", chunks[idx..].Select(StripHtmlTags));
                List<string> plainChunks = SplitPlain(remainingText);

                foreach (var plainChunk in plainChunks)
                {
                    if (string.IsNullOrEmpty(plainChunk)) continue;
                    var pr = await TrySendAsync(client, chatId, plainChunk, null, agent_name, ct);
                    if (!pr.ok)
                        return JsonSerializer.Serialize(new { ok = false, error = pr.error });
                    lastMessageId = pr.messageId;
                    if (pr.fallback) usedFallback = true;
                }
                break;
            }

            if (!result.ok)
                return JsonSerializer.Serialize(new { ok = false, error = result.error });

            lastMessageId = result.messageId;
            if (result.fallback) usedFallback = true;
        }

        if (usedFallback && usedFormatFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, fallback = true, format_fallback = true });
        if (usedFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, fallback = true });
        if (usedFormatFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, format_fallback = true });

        return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId });
    }

    private async Task<(bool ok, int messageId, string error, bool fallback, bool parseEntitiesError)> TrySendAsync(
        ITelegramBotClient client,
        long chatId,
        string text,
        ParseMode? parseMode,
        string agentName,
        CancellationToken ct)
    {
        try
        {
            var msg = parseMode.HasValue
                ? await client.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                : await client.SendMessage(chatId, text, cancellationToken: ct);
            return (true, msg.Id, string.Empty, false, false);
        }
        catch (Exception ex) when (IsParseEntitiesError(ex))
        {
            logger.LogWarning(ex, "Bot API parse-entities error sending to CEO");
            return (false, 0, ex.Message, false, true);
        }
        catch (Exception ex) when (Is403(ex))
        {
            var fallback = factory.GetFallbackClient();
            if (fallback is not null && fallback != client)
            {
                logger.LogWarning(
                    "Bot for agent '{AgentName}' got 403 sending to CEO — retrying with fallback bot",
                    agentName);
                try
                {
                    var msg = parseMode.HasValue
                        ? await fallback.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                        : await fallback.SendMessage(chatId, text, cancellationToken: ct);
                    return (true, msg.Id, string.Empty, true, false);
                }
                catch (Exception fbEx)
                {
                    logger.LogError(fbEx, "Fallback bot also failed sending to CEO");
                    return (false, 0, fbEx.Message, true, false);
                }
            }

            logger.LogError(ex, "Bot for agent '{AgentName}' got 403 sending to CEO", agentName);
            return (false, 0, ex.Message, false, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send message to CEO");
            return (false, 0, ex.Message, false, false);
        }
    }

    private static bool Is403(Exception ex) =>
        ex.Message.Contains("403") ||
        ex.Message.Contains("Forbidden") ||
        ex.Message.Contains("bot was blocked") ||
        ex.Message.Contains("not a member");

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
            if (char.IsLowSurrogate(remaining[cut]) && cut > 0) cut--;
            int nl = remaining[..cut].LastIndexOf('\n');
            if (nl > cut / 2) cut = nl + 1;
            chunks.Add(remaining[..cut].ToString());
            remaining = remaining[cut..];
        }
        return chunks;
    }

    private static string StripHtmlTags(string html)
    {
        var sb = new System.Text.StringBuilder(html.Length);
        string? pendingHref = null;
        int i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<') { sb.Append(html[i++]); continue; }

            int end = html.IndexOf('>', i + 1);
            if (end < 0) { sb.Append(html[i++]); continue; }

            string inner = html.Substring(i + 1, end - i - 1).Trim();
            i = end + 1;

            if (inner.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
            {
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
        }

        return sb.ToString()
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"");
    }
}
