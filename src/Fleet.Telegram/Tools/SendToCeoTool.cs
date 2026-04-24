using System.ComponentModel;
using System.Text.Json;
using Fleet.Telegram.Configuration;
using Fleet.Telegram.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tools;

[McpServerToolType]
public sealed class SendToCeoTool(
    BotClientFactory factory,
    IHttpContextAccessor httpContextAccessor,
    IOptions<CeoOptions> ceoOptions,
    ILogger<SendToCeoTool> logger)
{
    private const int TelegramMaxLength = 4096;

    [McpServerTool(Name = "send_to_ceo")]
    [Description("Send a direct message to the CEO. The CEO's chat ID is resolved server-side — it never appears in agent context or logs. Returns {\"ok\":true,\"message_id\":N} on success, {\"ok\":true,\"message_id\":N,\"fallback\":true} when the notifier bot was used as fallback, or {\"ok\":false,\"error\":\"...\"} on failure.")]
    public async Task<string> SendAsync(
        [Description("Message text (max 4096 chars; auto-split if longer)")] string text,
        [Description("Agent name to send from (uses agent's dedicated bot token; falls back to notifier bot)")] string agent_name = "",
        [Description("Parse mode: HTML, Markdown, or MarkdownV2. Omit for plain text.")] string parse_mode = "",
        CancellationToken ct = default)
    {
        var chatId = ceoOptions.Value.ChatId;
        if (chatId == 0)
            return JsonSerializer.Serialize(new { ok = false, error = "CEO chat ID not configured" });

        if (string.IsNullOrWhiteSpace(agent_name))
            agent_name = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault() ?? "";

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

        var chunks = SplitText(text);
        if (chunks.Count > 1)
            logger.LogWarning("Message to CEO was split into {Count} chunks (exceeded {Limit} chars)", chunks.Count, TelegramMaxLength);

        int lastMessageId = 0;
        bool usedFallback = false;

        foreach (var chunk in chunks)
        {
            var result = await TrySendAsync(client, chatId, chunk, pm, agent_name, ct);
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
                    return (true, msg.Id, string.Empty, true);
                }
                catch (Exception fbEx)
                {
                    // Do not log chatId to keep CEO ID out of logs
                    logger.LogError(fbEx, "Fallback bot also failed sending to CEO");
                    return (false, 0, fbEx.Message, true);
                }
            }

            logger.LogError(ex, "Bot for agent '{AgentName}' got 403 sending to CEO", agentName);
            return (false, 0, ex.Message, false);
        }
        catch (Exception ex)
        {
            // Do not log chatId to keep CEO ID out of logs
            logger.LogError(ex, "Failed to send message to CEO");
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
