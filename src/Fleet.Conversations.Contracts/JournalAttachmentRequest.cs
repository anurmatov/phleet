using System.Text.Json.Serialization;
using Fleet.Protocol;
namespace Fleet.Conversations.Contracts;

/// <summary>Arguments shared by the private-file tool and Comms content route.</summary>
public sealed record JournalAttachmentRequest(
    [property: JsonPropertyName("message_id")] string? MessageId = null,
    [property: JsonPropertyName("telegram_message_id")] long? TelegramMessageId = null,
    [property: JsonPropertyName("telegram_chat_id")] long? TelegramChatId = null,
    [property: JsonPropertyName("ordinal")] int Ordinal = 0)
{
    public const string ContentPath = "/journal/v1/attachments/content";
    public const long MaxBytes = 20L * 1024 * 1024;
    public string? Error()
    {
        var field = TelegramChatId is not null ? "telegram_chat_id"
            : (MessageId is not null) == (TelegramMessageId is not null) ? "message_id"
            : MessageId is not null && !Ulid.IsValid(MessageId) ? "message_id"
            : TelegramMessageId is <= 0 ? "telegram_message_id"
            : Ordinal is < 0 or > 255 ? "ordinal" : null;
        return field is null ? null : Invalid(field);
    }
    public static string Invalid(string field) => System.Text.Json.JsonSerializer.Serialize(new { error = "invalid_argument", field });
    public static string Unavailable(string reason) => System.Text.Json.JsonSerializer.Serialize(new { error = "unavailable", reason });
}
