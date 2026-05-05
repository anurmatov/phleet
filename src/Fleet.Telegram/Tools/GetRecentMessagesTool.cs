using System.ComponentModel;
using System.Text.Json;
using Fleet.Telegram.Services;
using ModelContextProtocol.Server;

namespace Fleet.Telegram.Tools;

[McpServerToolType]
public sealed class GetRecentMessagesTool(MessageStore store)
{
    [McpServerTool(Name = "get_recent_messages")]
    [Description("Return the most recent messages in a chat from the ring buffer (up to 100). Useful for browsing recent context. Messages are returned oldest-first.")]
    public string GetRecentMessages(
        [Description("Telegram chat ID")] long chat_id,
        [Description("Maximum number of messages to return (1–100, default 20)")] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var messages = store.GetRecent(chat_id, limit);

        return JsonSerializer.Serialize(new
        {
            chat_id,
            count = messages.Count,
            messages = messages.Select(r => new
            {
                message_id = r.MessageId,
                text = r.Text,
                sender_user_id = r.SenderUserId,
                sender_username = r.SenderUsername,
                timestamp = r.Timestamp.ToString("O"),
            }),
        });
    }
}
