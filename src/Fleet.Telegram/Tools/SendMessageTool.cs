using System.ComponentModel;
using System.Text.Json;
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
    private const int TelegramMaxLength = 4096;

    [McpServerTool(Name = "send_message")]
    [Description("Post a text message to a Telegram chat. Returns {\"ok\":true,\"message_id\":N} on success, {\"ok\":true,\"message_id\":N,\"fallback\":true} when the notifier bot was used as fallback, {\"ok\":true,\"message_id\":N,\"reply_fallback\":true} when the reply target was not found and sent standalone, or {\"ok\":false,\"error\":\"...\"} on failure.")]
    public async Task<string> SendAsync(
        [Description("Telegram chat ID as integer or string (e.g. -1001234567890 or \"-1001234567890\" for a group, positive integer for a DM)")] string chat_id,
        [Description("Message text (max 4096 chars per Telegram limit; longer text is split into multiple messages)")] string text,
        [Description("Agent name to send from (uses that agent's dedicated bot token; falls back to notifier bot if unknown)")] string agent_name = "",
        [Description("Parse mode for message formatting: HTML, Markdown, or MarkdownV2. Omit for plain text.")] string parse_mode = "",
        [Description("Optional message ID to reply to. When supplied, the message is sent as a threaded reply. If the target message is not found, the message is sent standalone with a reply_fallback flag in the response.")] int? reply_to_message_id = null,
        CancellationToken cancellationToken = default)
    {
        // If the LLM didn't pass agent_name, resolve from the ?agent= query parameter
        // baked into the MCP URL at provision time.
        if (string.IsNullOrWhiteSpace(agent_name))
            agent_name = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault() ?? "";

        // Accept chat_id as string or integer — LLM agents often serialize numeric IDs as strings
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

        ParseMode? pm = parse_mode?.Trim().ToUpperInvariant() switch
        {
            "HTML" => ParseMode.Html,
            "MARKDOWN" => ParseMode.Markdown,
            "MARKDOWNV2" => ParseMode.MarkdownV2,
            _ => null
        };

        // Split text into chunks if it exceeds the Telegram limit
        var chunks = SplitText(text);
        if (chunks.Count > 1)
            logger.LogWarning("Message to chat {ChatId} was split into {Count} chunks (exceeded {Limit} chars)", chatIdLong, chunks.Count, TelegramMaxLength);

        int lastMessageId = 0;
        bool usedFallback = false;
        bool usedReplyFallback = false;
        bool replyConsumed = false;

        foreach (var chunk in chunks)
        {
            // Only the first chunk uses reply threading; subsequent chunks are standalone
            var replyId = !replyConsumed ? reply_to_message_id : null;
            var result = await TrySendAsync(client, chatIdLong, chunk, pm, agent_name, replyId, cancellationToken);
            replyConsumed = true;
            if (!result.ok)
                return JsonSerializer.Serialize(new { ok = false, error = result.error });
            lastMessageId = result.messageId;
            if (result.fallback) usedFallback = true;
            if (result.replyFallback) usedReplyFallback = true;
        }

        if (usedFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, fallback = true });
        if (usedReplyFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, reply_fallback = true,
                warning = "reply target not found, sent as standalone" });

        return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId });
    }

    private async Task<(bool ok, int messageId, string error, bool fallback, bool replyFallback)> TrySendAsync(
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
            return (true, msg.Id, string.Empty, false, false);
        }
        catch (Exception ex) when (IsReplyNotFound(ex))
        {
            // Reply target gone (e.g. message deleted) — fall back to standalone send with a warning
            logger.LogWarning("Reply target {ReplyId} not found in chat {ChatId} — sending as standalone",
                replyToMessageId, chatId);
            try
            {
                var msg = parseMode.HasValue
                    ? await client.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                    : await client.SendMessage(chatId, text, cancellationToken: ct);
                return (true, msg.Id, string.Empty, false, true);
            }
            catch (Exception fbEx)
            {
                logger.LogError(fbEx, "Standalone fallback also failed for chat {ChatId}", chatId);
                return (false, 0, fbEx.Message, false, true);
            }
        }
        catch (Exception ex) when (Is403(ex))
        {
            // 403 Forbidden: bot not a member or blocked — try fallback if not already using it
            var fallback = factory.GetFallbackClient();
            if (fallback is not null && fallback != client)
            {
                logger.LogWarning(
                    "Bot for agent '{AgentName}' got 403 on chat {ChatId} — retrying with fallback bot",
                    agentName, chatId);
                try
                {
                    var msg = parseMode.HasValue
                        ? await fallback.SendMessage(chatId, text, parseMode: parseMode.Value,
                            replyParameters: replyParams, cancellationToken: ct)
                        : await fallback.SendMessage(chatId, text,
                            replyParameters: replyParams, cancellationToken: ct);
                    return (true, msg.Id, string.Empty, true, false);
                }
                catch (Exception fbEx)
                {
                    logger.LogError(fbEx, "Fallback bot also failed for chat {ChatId}", chatId);
                    return (false, 0, fbEx.Message, true, false);
                }
            }

            logger.LogError(ex, "Bot for agent '{AgentName}' got 403 on chat {ChatId}", agentName, chatId);
            return (false, 0, ex.Message, false, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send message to chat {ChatId}", chatId);
            return (false, 0, ex.Message, false, false);
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

    private static List<string> SplitText(string text)
    {
        if (text.Length <= TelegramMaxLength)
            return [text];

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            var chunk = remaining.Length > TelegramMaxLength
                ? remaining[..TelegramMaxLength]
                : remaining;
            chunks.Add(chunk.ToString());
            remaining = remaining.Length > TelegramMaxLength
                ? remaining[TelegramMaxLength..]
                : [];
        }
        return chunks;
    }
}
