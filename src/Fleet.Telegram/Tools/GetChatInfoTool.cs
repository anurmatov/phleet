using System.ComponentModel;
using System.Text.Json;
using Fleet.Telegram.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Fleet.Telegram.Tools;

[McpServerToolType]
public sealed class GetChatInfoTool(BotClientFactory factory, ILogger<GetChatInfoTool> logger)
{
    [McpServerTool(Name = "get_chat_info")]
    [Description("Get basic info (title, type) for a Telegram chat ID. Useful for verifying chat IDs before use. Returns {\"ok\":true,\"chat_id\":N,\"title\":\"...\",\"type\":\"...\"} or {\"ok\":false,\"error\":\"...\"}")]
    public async Task<string> GetInfoAsync(
        [Description("Telegram chat ID to look up")] long chat_id,
        [Description("Agent name to query with (falls back to notifier bot if unknown)")] string agent_name = "",
        CancellationToken cancellationToken = default)
    {
        var client = factory.GetClient(agent_name);
        if (client is null)
        {
            const string err = "No bot client available — notifier bot token not configured";
            return JsonSerializer.Serialize(new { ok = false, error = err });
        }

        try
        {
            var chat = await client.GetChat(new ChatId(chat_id), cancellationToken);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                chat_id = chat.Id,
                title = chat.Title ?? chat.Username ?? "(no title)",
                type = chat.Type.ToString().ToLowerInvariant()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get chat info for {ChatId}", chat_id);
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }
}
