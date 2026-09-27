using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fleet.Conversations.Contracts;

namespace Fleet.Conversations.Journal;

/// <summary>The keys the server derives for a record. Never taken from the request.</summary>
public static class JournalKeys
{
    /// <summary>
    /// The conversation key, from the platform's chat KIND — never from the sign of the chat id.
    /// </summary>
    /// <remarks>
    /// <para>Private chats and basic groups number their messages per bot account, so the bot id is
    /// part of the key: two bots in one basic group are two message sequences.</para>
    /// <para>A supergroup has one shared sequence, so every bot's copy of a message is one row with
    /// one observer row per bot.</para>
    /// </remarks>
    public static string ConversationKey(JournalTelegramRef telegram) => telegram.ChatKind switch
    {
        JournalChatKind.Private => $"tg:dm:{Invariant(telegram.BotId)}:{Invariant(telegram.ChatId)}",
        JournalChatKind.Group => $"tg:bgroup:{Invariant(telegram.BotId)}:{Invariant(telegram.ChatId)}",
        JournalChatKind.Supergroup => $"tg:group:{Invariant(telegram.ChatId)}",
        _ => throw new ArgumentOutOfRangeException(nameof(telegram)),
    };

    /// <summary>The per-conversation natural key of a platform message.</summary>
    public static string SourceKey(long messageId) => $"tg:{Invariant(messageId)}";

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The record fingerprint: lowercase hex SHA-256 over a HAND-BUILT canonical encoding.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Not a general-purpose serializer, on purpose.</b> Fingerprints are stored and compared
/// forever; a serializer upgrade that changed escaping or number form would turn every retry of an
/// old message into a conflict. Every byte of the encoding is decided here, and
/// <c>JournalFingerprintTests</c> pins it with golden vectors.
/// </para>
/// <para>The encoding:</para>
/// <list type="bullet">
///   <item>one JSON object, keys in ordinal order, no whitespace;</item>
///   <item>strings escape only <c>\"</c>, <c>\\</c> and <c>\u00xx</c> (lowercase hex) for control
///   characters below U+0020; everything else is raw UTF-8;</item>
///   <item>integers in invariant decimal, no exponent;</item>
///   <item><c>sentAt</c> as <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> in UTC;</item>
///   <item>an absent optional value is <c>null</c>, never omitted;</item>
///   <item>attachments sorted by ordinal.</item>
/// </list>
/// <para>
/// Encoded: the conversation key, message id, direction, sender kind and id, sent time and text,
/// and per attachment its ordinal, kind, MIME type, byte size and platform file id. <b>Excluded:</b>
/// transcript, text format, chat title, sender display name, not-archived reason and event id —
/// so two bots that saw the same supergroup message, one of which transcribed it, agree.
/// </para>
/// </remarks>
public static class JournalFingerprint
{
    public static string Compute(string conversationKey, JournalRecord record) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(conversationKey, record))));

    /// <summary>The exact text that is hashed. Public so the golden vectors can pin it.</summary>
    public static string Canonical(string conversationKey, JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(conversationKey);
        ArgumentNullException.ThrowIfNull(record);

        var b = new StringBuilder(256 + (record.Text?.Length ?? 0));

        // Top-level keys, in ordinal order:
        // attachments, conversationKey, direction, messageId, senderId, senderKind, sentAt, text.
        b.Append("{\"attachments\":[");

        var first = true;
        foreach (var a in record.Attachments.OrderBy(a => a.Ordinal))
        {
            if (!first) b.Append(',');
            first = false;

            // Attachment keys, in ordinal order: byteSize, fileUniqueId, kind, mimeType, ordinal.
            b.Append("{\"byteSize\":");
            Integer(b, a.ByteSize);
            b.Append(",\"fileUniqueId\":");
            String(b, a.FileUniqueId);
            b.Append(",\"kind\":");
            String(b, JournalWire.Of(a.Kind));
            b.Append(",\"mimeType\":");
            String(b, a.MimeType);
            b.Append(",\"ordinal\":");
            Integer(b, a.Ordinal);
            b.Append('}');
        }

        b.Append("],\"conversationKey\":");
        String(b, conversationKey);
        b.Append(",\"direction\":");
        String(b, JournalWire.Of(record.Direction));
        b.Append(",\"messageId\":");
        Integer(b, record.Telegram.MessageId);
        b.Append(",\"senderId\":");
        String(b, record.Sender.Id);
        b.Append(",\"senderKind\":");
        String(b, JournalWire.Of(record.Sender.Kind));
        b.Append(",\"sentAt\":");
        String(b, record.SentAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        b.Append(",\"text\":");
        String(b, record.Text);
        b.Append('}');

        return b.ToString();
    }

    private static void Integer(StringBuilder b, long? value)
    {
        if (value is null) b.Append("null");
        else b.Append(value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static void String(StringBuilder b, string? value)
    {
        if (value is null)
        {
            b.Append("null");
            return;
        }

        b.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case < ' ':
                    b.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                default: b.Append(c); break;
            }
        }

        b.Append('"');
    }
}
