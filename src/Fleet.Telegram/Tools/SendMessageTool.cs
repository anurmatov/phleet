using System.ComponentModel;
using System.Text.Json;
using Fleet.Telegram.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tools;

[McpServerToolType]
public sealed class SendMessageTool(BotClientFactory factory, ILogger<SendMessageTool> logger)
{
    private const int TelegramMaxLength = 4096;

    [McpServerTool(Name = "send_message")]
    [Description("Post a text message to a Telegram chat. Returns {\"ok\":true,\"message_id\":N} on success, {\"ok\":true,\"message_id\":N,\"fallback\":true} when the notifier bot was used as fallback, or {\"ok\":false,\"error\":\"...\"} on failure.")]
    public async Task<string> SendAsync(
        [Description("Telegram chat ID (e.g. -1001234567890 for a group, or a positive integer for a DM)")] long chat_id,
        [Description("Message text (max 4096 chars per Telegram limit; longer text is split into multiple messages)")] string text,
        [Description("Agent name to send from (uses that agent's dedicated bot token; falls back to notifier bot if unknown)")] string agent_name = "",
        [Description("Parse mode for message formatting: HTML, Markdown, or MarkdownV2. Omit for plain text.")] string parse_mode = "",
        CancellationToken cancellationToken = default)
    {
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
            logger.LogWarning("Message to chat {ChatId} was split into {Count} chunks (exceeded {Limit} chars)", chat_id, chunks.Count, TelegramMaxLength);

        int lastMessageId = 0;
        bool usedFallback = false;

        foreach (var chunk in chunks)
        {
            var result = await TrySendAsync(client, chat_id, chunk, pm, agent_name, cancellationToken);
            if (!result.ok)
                return JsonSerializer.Serialize(new { ok = false, error = result.error });
            lastMessageId = result.messageId;
            if (result.fallback) usedFallback = true;
        }

        if (usedFallback)
            return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId, fallback = true });

        return JsonSerializer.Serialize(new { ok = true, message_id = lastMessageId });
    }

    private async Task<(bool ok, int messageId, string error, bool fallback)> TrySendAsync(
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
            return (true, msg.Id, string.Empty, false);
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
                        ? await fallback.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                        : await fallback.SendMessage(chatId, text, cancellationToken: ct);
                    return (true, msg.Id, string.Empty, true);
                }
                catch (Exception fbEx)
                {
                    logger.LogError(fbEx, "Fallback bot also failed for chat {ChatId}", chatId);
                    return (false, 0, fbEx.Message, true);
                }
            }

            logger.LogError(ex, "Bot for agent '{AgentName}' got 403 on chat {ChatId}", agentName, chatId);
            return (false, 0, ex.Message, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send message to chat {ChatId}", chatId);
            return (false, 0, ex.Message, false);
        }
    }

    private static bool Is403(Exception ex) =>
        ex.Message.Contains("403") ||
        ex.Message.Contains("Forbidden") ||
        ex.Message.Contains("bot was blocked") ||
        ex.Message.Contains("not a member");

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
