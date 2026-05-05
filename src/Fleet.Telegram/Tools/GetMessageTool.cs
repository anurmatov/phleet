using System.ComponentModel;
using System.Text.Json;
using Fleet.Telegram.Services;
using ModelContextProtocol.Server;

namespace Fleet.Telegram.Tools;

[McpServerToolType]
public sealed class GetMessageTool(MessageStore store)
{
    [McpServerTool(Name = "get_message")]
    [Description("Look up a Telegram message by chat_id and message_id from the per-chat ring buffer (last ~100 messages per chat). Returns the message record or {\"found\":false} when outside the cache window. Use this to read the content of a message after receiving a reaction event that only carries a message_id.")]
    public string GetMessage(
        [Description("Telegram chat ID")] long chat_id,
        [Description("Telegram message ID to look up")] long message_id)
    {
        var record = store.Get(chat_id, message_id);
        if (record is null)
            return JsonSerializer.Serialize(new { found = false, chat_id, message_id });

        return JsonSerializer.Serialize(new
        {
            found = true,
            message_id = record.MessageId,
            text = record.Text,
            sender_user_id = record.SenderUserId,
            sender_username = record.SenderUsername,
            timestamp = record.Timestamp.ToString("O"),
        });
    }
}
