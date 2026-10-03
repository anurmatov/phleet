using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Http;

namespace Fleet.Comms.Routes;

/// <summary>
/// Turns one ingest body into a validated <see cref="JournalRecord"/>, or names the first field
/// that is wrong.
/// </summary>
/// <remarks>
/// <para>
/// Read field by field from a <see cref="JsonDocument"/> rather than bound to a DTO, so every
/// failure can say which field it was, an unknown field is refused rather than silently dropped, and
/// a duplicated property cannot smuggle a second value past the first.
/// </para>
/// <para>
/// ⚠️ An <c>observer</c> field is refused: the observer is the token subject and nothing else. Any
/// attachment byte reference is <c>409 media_disabled</c>: this slice stores metadata only.
/// </para>
/// </remarks>
internal static class JournalRecordParser
{
    public const int MaxBodyBytes = 1024 * 1024;
    public const int MaxTextBytes = 65_536;
    public const int MaxAttachments = 16;
    public const int MaxSendParts = 64;

    public static readonly DateTimeOffset EarliestSentAt = new(2013, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(10);

    /// <summary>What was wrong. <see cref="Field"/> is set for <c>invalid_record</c>.</summary>
    public sealed record Failure(int Status, string Error, string? Field = null);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] RootFields =
    [
        "eventId", "channel", "telegram", "direction", "sender", "sentAt", "text", "textFormat",
        "transcript", "transcriptTruncated", "origin", "sendGroup", "attachments",
    ];

    private static readonly string[] TelegramFields =
        ["botId", "chatId", "chatKind", "chatTitle", "messageId", "replyToMessageId", "mediaGroupId"];

    private static readonly string[] SenderFields = ["kind", "id", "display"];

    private static readonly string[] SendGroupFields = ["id", "part", "parts"];

    /// <remarks>
    /// <c>uploadId</c> and <c>uploadSha256</c> arrive with media (slice 4). They are in this list
    /// because <see cref="Fields"/> refuses an unknown field by name, and an attachment field that
    /// the contract accepts but this list forgot is a record refused as malformed — a caller that
    /// did everything right told it had done something wrong.
    /// </remarks>
    private static readonly string[] AttachmentFields =
        ["ordinal", "kind", "mimeType", "byteSize", "fileName", "fileUniqueId", "notArchivedReason",
         "uploadId", "uploadSha256", "fileId", "copiedFrom"];

    /// <summary>
    /// Fields that would reference stored bytes. <c>uploadId</c> is the one media slice 4 accepts;
    /// <c>objectId</c> and <c>bytes</c> are never accepted from a caller — the first is what the
    /// server resolves a validated upload into, the second does not exist.
    /// </summary>
    private static readonly string[] ByteReferenceFields = ["objectId", "bytes"];

    /// <summary>The fields that mean "this record names bytes I already uploaded".</summary>
    private static readonly string[] UploadReferenceFields = ["uploadId", "uploadSha256"];

    /// <summary>
    /// Cheap structural check for the routes: does this body reference stored bytes at all?
    /// </summary>
    /// <remarks>
    /// Answers for the whole body so a deployment without media can refuse it before parsing, and
    /// so the byte-reference fields can be refused by name inside a record that does use uploads.
    /// A parse failure here is false — the parser reports the malformed body, which is the more
    /// useful answer.
    /// </remarks>
    public static bool ReferencesBytes(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("attachments", out var attachments)
                || attachments.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var attachment in attachments.EnumerateArray())
            {
                if (attachment.ValueKind != JsonValueKind.Object) continue;

                foreach (var name in ByteReferenceFields.Concat(UploadReferenceFields))
                {
                    if (attachment.TryGetProperty(name, out _)) return true;
                }
            }

            return false;
        }
    }

    /// <summary>The one MIME grammar this listener accepts. Shared with the upload declaration.</summary>
    internal static bool IsMimeType(string value) => MimePattern.IsMatch(value);

    private static readonly Regex MimePattern = new(
        "^[A-Za-z0-9!#$&^_.+-]{1,63}/[A-Za-z0-9!#$&^_.+-]{1,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Printable ASCII: the platform ids stored in <c>ascii_bin</c> columns.</summary>
    private static readonly Regex AsciiIdPattern = new("^[\\x21-\\x7E]{1,64}$", RegexOptions.Compiled);

    private static readonly Regex OffsetSuffix = new(
        "(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SafeFieldName = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.Compiled);

    public static JournalRecord? Parse(ReadOnlyMemory<byte> body, DateTimeOffset now, out Failure? failure)
    {
        failure = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            failure = Invalid("body");
            return null;
        }

        using (document)
        {
            try
            {
                return Read(document.RootElement, now);
            }
            catch (RecordException e)
            {
                failure = e.Failure;
                return null;
            }
            catch (InvalidOperationException)
            {
                // A property NAME that is not a valid string (an escaped lone surrogate) is the one
                // read not routed through the typed accessors. Still the record's fault: 422.
                failure = Invalid("body");
                return null;
            }
        }
    }

    private static JournalRecord Read(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Refuse("body");

        // Before anything else: the observer is the token subject, never the body.
        if (root.TryGetProperty("observer", out _)) throw Refuse("observer");

        var fields = Fields(root, RootFields, prefix: "");

        var eventId = RequiredString(fields, "eventId");
        if (!Ulid.IsValid(eventId.ToUpperInvariant())) throw Refuse("eventId");

        if (RequiredString(fields, "channel") != "telegram") throw Refuse("channel");

        var telegram = ReadTelegram(Required(fields, "telegram", JsonValueKind.Object));
        var direction = RequiredEnum<JournalDirection>(fields, "direction");
        var sender = ReadSender(Required(fields, "sender", JsonValueKind.Object));

        var sentAt = ReadSentAt(RequiredString(fields, "sentAt"), now);

        var text = OptionalText(fields, "text");
        JournalTextFormat? textFormat = null;
        if (fields.TryGetValue("textFormat", out var format) && format.ValueKind != JsonValueKind.Null)
        {
            if (!JournalWire.TryParse<JournalTextFormat>(StringAt(format, "textFormat"), out var parsed))
                throw Refuse("textFormat");
            textFormat = parsed;
        }

        if (text is not null && textFormat is null) throw Refuse("textFormat");

        var transcript = OptionalText(fields, "transcript");

        var truncated = false;
        if (fields.TryGetValue("transcriptTruncated", out var t) && t.ValueKind != JsonValueKind.Null)
        {
            truncated = t.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Refuse("transcriptTruncated"),
            };
        }

        var origin = RequiredEnum<JournalRecordOrigin>(fields, "origin");

        // A tool send (#394) is only ever an agent's own outbound message. Any other record claiming
        // that origin is refused rather than stored under it. Origin is not in the fingerprint.
        if (origin == JournalRecordOrigin.AgentTool
            && (direction != JournalDirection.Outbound || sender.Kind != JournalSenderKind.Agent))
            throw Refuse("origin");

        JournalSendGroup? sendGroup = null;
        if (fields.TryGetValue("sendGroup", out var group) && group.ValueKind != JsonValueKind.Null)
            sendGroup = ReadSendGroup(group, direction);

        var attachments = new List<JournalAttachment>();
        if (fields.TryGetValue("attachments", out var list) && list.ValueKind != JsonValueKind.Null)
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxAttachments)
                throw Refuse("attachments");

            var ordinals = new HashSet<int>();
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                var attachment = ReadAttachment(item, $"attachments[{index}]");
                if (!ordinals.Add(attachment.Ordinal)) throw Refuse($"attachments[{index}].ordinal");
                attachments.Add(attachment);
                index++;
            }
        }

        if (origin == JournalRecordOrigin.AgentCopy)
        {
            if (direction != JournalDirection.Outbound || sender.Kind != JournalSenderKind.Agent
                || text is not null || attachments.Count == 0
                || attachments.Any(a => a.NotArchivedReason != JournalNotArchivedReason.Copied || a.CopiedFrom is null))
                throw Refuse("origin");
        }
        else if (attachments.Any(a => a.CopiedFrom is not null || a.NotArchivedReason == JournalNotArchivedReason.Copied))
            throw Refuse("origin");

        return new JournalRecord
        {
            EventId = eventId.ToUpperInvariant(),
            Telegram = telegram,
            Direction = direction,
            Sender = sender,
            SentAt = sentAt,
            Text = text,
            TextFormat = textFormat,
            Transcript = transcript,
            TranscriptTruncated = truncated,
            Origin = origin,
            SendGroup = sendGroup,
            Attachments = attachments,
        };
    }

    private static JournalTelegramRef ReadTelegram(JsonElement element)
    {
        var fields = Fields(element, TelegramFields, prefix: "telegram.");

        var chatId = RequiredInt64(fields, "chatId", "telegram.chatId");
        if (chatId == 0) throw Refuse("telegram.chatId");

        var title = OptionalString(fields, "chatTitle", "telegram.chatTitle", maxCodePoints: 256);

        string? mediaGroupId = null;
        if (fields.TryGetValue("mediaGroupId", out var group) && group.ValueKind != JsonValueKind.Null)
        {
            mediaGroupId = StringAt(group, "telegram.mediaGroupId");
            if (!AsciiIdPattern.IsMatch(mediaGroupId)) throw Refuse("telegram.mediaGroupId");
        }

        long? replyTo = null;
        if (fields.TryGetValue("replyToMessageId", out var reply) && reply.ValueKind != JsonValueKind.Null)
        {
            replyTo = Int64At(reply, "telegram.replyToMessageId");
        }

        return new JournalTelegramRef
        {
            BotId = RequiredInt64(fields, "botId", "telegram.botId"),
            ChatId = chatId,
            ChatKind = RequiredEnum<JournalChatKind>(fields, "chatKind", "telegram.chatKind"),
            ChatTitle = title,
            MessageId = RequiredInt64(fields, "messageId", "telegram.messageId"),
            ReplyToMessageId = replyTo,
            MediaGroupId = mediaGroupId,
        };
    }

    private static JournalSender ReadSender(JsonElement element)
    {
        var fields = Fields(element, SenderFields, prefix: "sender.");

        var id = OptionalString(fields, "id", "sender.id", maxCodePoints: 128);
        if (string.IsNullOrEmpty(id)) throw Refuse("sender.id");

        return new JournalSender
        {
            Kind = RequiredEnum<JournalSenderKind>(fields, "kind", "sender.kind"),
            Id = id,
            Display = OptionalString(fields, "display", "sender.display", maxCodePoints: 128),
        };
    }

    private static JournalSendGroup ReadSendGroup(JsonElement element, JournalDirection direction)
    {
        // Every sendGroup violation is reported as the one field.
        if (element.ValueKind != JsonValueKind.Object || direction != JournalDirection.Outbound)
            throw Refuse("sendGroup");

        Dictionary<string, JsonElement> fields;
        try
        {
            fields = Fields(element, SendGroupFields, prefix: "sendGroup.");
        }
        catch (RecordException)
        {
            throw Refuse("sendGroup");
        }

        if (!fields.TryGetValue("id", out var id) || !fields.TryGetValue("part", out var part)
            || !fields.TryGetValue("parts", out var parts))
            throw Refuse("sendGroup");

        var groupId = StringAt(id, "sendGroup").ToUpperInvariant();
        var p = Int32At(part, "sendGroup");
        var n = Int32At(parts, "sendGroup");
        if (!Ulid.IsValid(groupId) || p < 1 || p > n || n > MaxSendParts) throw Refuse("sendGroup");

        return new JournalSendGroup { Id = groupId, Part = p, Parts = n };
    }

    private static JournalAttachment ReadAttachment(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Refuse(path);

        // `objectId` and `bytes` are refused on EVERY deployment, media enabled or not. A caller
        // naming the object it wants is naming one it may not own: the server resolves an uploadId
        // into an object id, and the digest the fingerprint encodes is the resolved one. `bytes` is
        // not a field — bytes go to the upload route and nowhere else.
        foreach (var name in ByteReferenceFields)
        {
            if (element.TryGetProperty(name, out _))
                throw new RecordException(new Failure(StatusCodes.Status409Conflict, "media_disabled"));
        }

        var fields = Fields(element, AttachmentFields, prefix: path + ".");

        if (!fields.TryGetValue("ordinal", out var ordinal)) throw Refuse(path + ".ordinal");
        var o = Int32At(ordinal, path + ".ordinal");
        if (o is < 0 or > 15) throw Refuse(path + ".ordinal");

        var mime = OptionalString(fields, "mimeType", path + ".mimeType", maxCodePoints: 127);
        if (mime is null || !MimePattern.IsMatch(mime)) throw Refuse(path + ".mimeType");

        long? byteSize = null;
        if (fields.TryGetValue("byteSize", out var size) && size.ValueKind != JsonValueKind.Null)
        {
            byteSize = Int64At(size, path + ".byteSize");
            if (byteSize < 0) throw Refuse(path + ".byteSize");
        }

        string? fileUniqueId = null;
        if (fields.TryGetValue("fileUniqueId", out var unique) && unique.ValueKind != JsonValueKind.Null)
        {
            fileUniqueId = StringAt(unique, path + ".fileUniqueId");
            if (!AsciiIdPattern.IsMatch(fileUniqueId)) throw Refuse(path + ".fileUniqueId");
        }

        string? fileId = null;
        if (fields.TryGetValue("fileId", out var file) && file.ValueKind != JsonValueKind.Null)
        {
            fileId = StringAt(file, path + ".fileId");
            if (fileId.Length is < 1 or > 255 || fileId.Any(c => c is < '\x21' or > '\x7e'))
                throw Refuse(path + ".fileId");
        }

        JournalCopiedFrom? copiedFrom = null;
        if (fields.TryGetValue("copiedFrom", out var copy) && copy.ValueKind != JsonValueKind.Null)
        {
            var copyFields = Fields(copy, ["messageId", "ordinal"], path + ".copiedFrom.");
            var messageId = RequiredString(copyFields, "messageId").ToUpperInvariant();
            if (!Ulid.IsValid(messageId)) throw Refuse(path + ".copiedFrom.messageId");
            if (!copyFields.TryGetValue("ordinal", out var sourceOrdinal)) throw Refuse(path + ".copiedFrom.ordinal");
            var ordinalValue = Int32At(sourceOrdinal, path + ".copiedFrom.ordinal");
            if (ordinalValue is < 0 or > 255) throw Refuse(path + ".copiedFrom.ordinal");
            copiedFrom = new JournalCopiedFrom { MessageId = messageId, Ordinal = ordinalValue };
        }

        // ── exactly one of: a reason, or an upload ──────────────────────────────
        //
        // `notArchivedReason` and `uploadId` are mutually exclusive and one is required. A record
        // that gave neither would be an attachment whose bytes are archived without an object and
        // declined without a reason — the two states this row exists to distinguish.
        var hasUpload = fields.TryGetValue("uploadId", out var uploadNode)
            && uploadNode.ValueKind != JsonValueKind.Null;
        var hasReason = fields.TryGetValue("notArchivedReason", out var reasonNode)
            && reasonNode.ValueKind != JsonValueKind.Null;

        if (hasUpload == hasReason) throw Refuse(path + ".uploadId");

        if (!hasUpload)
        {
            return new JournalAttachment
            {
                Ordinal = o,
                Kind = RequiredEnum<JournalAttachmentKind>(fields, "kind", path + ".kind"),
                MimeType = mime,
                ByteSize = byteSize,
                FileName = OptionalString(fields, "fileName", path + ".fileName", maxCodePoints: 255),
                FileUniqueId = fileUniqueId,
                FileId = fileId,
                CopiedFrom = copiedFrom,
                NotArchivedReason = RequiredEnum<JournalNotArchivedReason>(
                    fields, "notArchivedReason", path + ".notArchivedReason"),
            };
        }

        // An upload reference is a ULID and a lowercase-hex digest, or nothing. The digest is
        // REQUIRED with the id: it is the claim the commit compares against the bytes the server
        // hashed at PUT time, and without it the commit would be trusting a name alone.
        var uploadId = StringAt(uploadNode, path + ".uploadId").ToUpperInvariant();
        if (!Ulid.IsValid(uploadId)) throw Refuse(path + ".uploadId");

        if (!fields.TryGetValue("uploadSha256", out var shaNode)
            || shaNode.ValueKind != JsonValueKind.String
            || !IsLowerHexSha256(shaNode.GetString()!))
        {
            throw Refuse(path + ".uploadSha256");
        }

        return new JournalAttachment
        {
            Ordinal = o,
            Kind = RequiredEnum<JournalAttachmentKind>(fields, "kind", path + ".kind"),
            MimeType = mime,
            ByteSize = byteSize,
            FileName = OptionalString(fields, "fileName", path + ".fileName", maxCodePoints: 255),
            FileUniqueId = fileUniqueId,
            FileId = fileId,
            CopiedFrom = copiedFrom,
            UploadId = uploadId,
            UploadSha256 = shaNode.GetString(),
        };
    }

    /// <summary>64 lowercase hex characters. Uppercase is refused: digests are stored lowercase.</summary>
    private static bool IsLowerHexSha256(string value)
    {
        if (value.Length != 64) return false;

        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9') && c is not (>= 'a' and <= 'f')) return false;
        }

        return true;
    }

    private static DateTimeOffset ReadSentAt(string value, DateTimeOffset now)
    {
        // An explicit offset is required: a bare local time would be read in this server's zone.
        if (!OffsetSuffix.IsMatch(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sentAt))
            throw Refuse("sentAt");

        if (sentAt < EarliestSentAt || sentAt > now + FutureSkew) throw Refuse("sentAt");
        return sentAt;
    }

    // ── primitives ───────────────────────────────────────────────────────────

    /// <summary>
    /// The object's properties by name, refusing an unknown or duplicated one.
    /// </summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement element, string[] allowed, string prefix)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Refuse(prefix.TrimEnd('.'));

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0 || !fields.TryAdd(property.Name, property.Value))
            {
                // The client's own field name is echoed only when it is plainly a name.
                throw Refuse(SafeFieldName.IsMatch(property.Name) ? prefix + property.Name : "body");
            }
        }

        return fields;
    }

    private static JsonElement Required(Dictionary<string, JsonElement> fields, string name, JsonValueKind kind)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind != kind) throw Refuse(name);
        return value;
    }

    private static string RequiredString(Dictionary<string, JsonElement> fields, string name) =>
        StringAt(Required(fields, name, JsonValueKind.String), name);

    private static long RequiredInt64(Dictionary<string, JsonElement> fields, string name, string path)
    {
        if (!fields.TryGetValue(name, out var value)) throw Refuse(path);
        return Int64At(value, path);
    }

    private static T RequiredEnum<T>(Dictionary<string, JsonElement> fields, string name, string? path = null)
        where T : struct, Enum
    {
        if (!fields.TryGetValue(name, out var value)
            || !JournalWire.TryParse<T>(StringAt(value, path ?? name), out var parsed))
            throw Refuse(path ?? name);
        return parsed;
    }

    /// <summary>A nullable string of at most <paramref name="maxCodePoints"/> code points.</summary>
    private static string? OptionalString(
        Dictionary<string, JsonElement> fields, string name, string path, int maxCodePoints)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = StringAt(value, path);
        if (!IsWellFormed(text, out _) || CodePoints(text) > maxCodePoints) throw Refuse(path);
        return text;
    }

    /// <summary>A nullable string of at most <see cref="MaxTextBytes"/> UTF-8 bytes.</summary>
    private static string? OptionalText(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = StringAt(value, name);
        if (!IsWellFormed(text, out var bytes) || bytes > MaxTextBytes) throw Refuse(name);
        return text;
    }

    // ⚠️ The typed accessors below are the ONLY way a value is read. JsonElement's own getters
    // throw InvalidOperationException on a wrong kind (TryGetInt64 on a string) or on an escaped
    // lone surrogate (GetString), and an exception there used to escape as a 500 that a publisher
    // would retry. Every such case is a field violation: 422 naming the field.

    /// <summary>A JSON string, or a refusal naming <paramref name="path"/>.</summary>
    private static string StringAt(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String) throw Refuse(path);

        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // An escaped lone surrogate (e.g. "\ud800") has no UTF-16 string.
            throw Refuse(path);
        }
    }

    /// <summary>A JSON integer that fits a <see cref="long"/>, or a refusal.</summary>
    private static long Int64At(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw Refuse(path);
        return number;
    }

    /// <summary>A JSON integer that fits an <see cref="int"/>, or a refusal.</summary>
    private static int Int32At(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Refuse(path);
        return number;
    }

    /// <summary>True when the string has no lone surrogate; <paramref name="utf8Bytes"/> is its UTF-8 length.</summary>
    private static bool IsWellFormed(string value, out int utf8Bytes)
    {
        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            utf8Bytes = 0;
            return false;
        }
    }

    private static int CodePoints(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) count++;
        return count;
    }

    private static Failure Invalid(string field) =>
        new(StatusCodes.Status422UnprocessableEntity, "invalid_record", field);

    private static RecordException Refuse(string field) => new(Invalid(field));

    private sealed class RecordException(Failure failure) : Exception
    {
        public Failure Failure { get; } = failure;
    }
}
