using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Conversations.Contracts;

namespace Fleet.Journal.Client;

/// <summary>
/// Writes a <see cref="JournalRecord"/> in the exact wire form the Comms journal listener parses.
/// </summary>
/// <remarks>
/// <para>
/// The listener refuses unknown fields and checks every bound, and a refused record is dead, not
/// retried. So this writer clamps what a Telegram payload can legitimately exceed (title, display
/// name, file name, transcript) instead of letting the server refuse it — and never touches
/// <c>text</c>, which is fingerprinted and must stay the raw source.
/// </para>
/// <para>
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> keeps Cyrillic and emoji as UTF-8
/// instead of <c>\uXXXX</c>, so the 1 MiB body cap measures real text.
/// </para>
/// </remarks>
public static class JournalRecordJson
{
    public const int MaxBodyBytes = 1024 * 1024;
    public const int MaxFieldBytes = 65_536;
    public const int MaxAttachments = 16;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// For re-serializing a stored record (spool file, request body): the same relaxed escaping,
    /// or Cyrillic would grow sixfold as <c>\uXXXX</c> and a body that fitted would not.
    /// </summary>
    public static readonly JsonSerializerOptions StoredOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly Regex MimePattern = new(
        "^[A-Za-z0-9!#$&^_.+-]{1,63}/[A-Za-z0-9!#$&^_.+-]{1,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AsciiId = new("^[\\x21-\\x7E]{1,64}$", RegexOptions.Compiled);

    /// <summary>
    /// Serializes <paramref name="record"/>. When the body would exceed 1 MiB, the transcript is
    /// cut to fit and <c>transcriptTruncated</c> is set.
    /// </summary>
    public static byte[] Serialize(JournalRecord record)
    {
        var transcript = TruncateUtf8(record.Transcript, MaxFieldBytes, out var cut);
        var normalized = record with
        {
            Transcript = transcript,
            TranscriptTruncated = record.TranscriptTruncated || cut,
        };

        var body = Write(normalized);
        if (body.Length <= MaxBodyBytes || normalized.Transcript is null) return body;

        // Over the cap: give the transcript whatever room is left, and say so.
        var overflow = body.Length - MaxBodyBytes;
        var room = Math.Max(0, Encoding.UTF8.GetByteCount(normalized.Transcript) - overflow - 64);
        return Write(normalized with
        {
            Transcript = TruncateUtf8(normalized.Transcript, room, out _),
            TranscriptTruncated = true,
        });
    }

    private static byte[] Write(JournalRecord r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("eventId", r.EventId);
            w.WriteString("channel", "telegram");

            w.WriteStartObject("telegram");
            w.WriteNumber("botId", r.Telegram.BotId);
            w.WriteNumber("chatId", r.Telegram.ChatId);
            w.WriteString("chatKind", JournalWire.Of(r.Telegram.ChatKind));
            if (Clamp(r.Telegram.ChatTitle, 256) is { } title) w.WriteString("chatTitle", title);
            w.WriteNumber("messageId", r.Telegram.MessageId);
            if (r.Telegram.ReplyToMessageId is { } reply) w.WriteNumber("replyToMessageId", reply);
            if (r.Telegram.MediaGroupId is { } group && AsciiId.IsMatch(group)) w.WriteString("mediaGroupId", group);
            w.WriteEndObject();

            w.WriteString("direction", JournalWire.Of(r.Direction));

            w.WriteStartObject("sender");
            w.WriteString("kind", JournalWire.Of(r.Sender.Kind));
            w.WriteString("id", Clamp(r.Sender.Id, 128));
            if (Clamp(r.Sender.Display, 128) is { Length: > 0 } display) w.WriteString("display", display);
            w.WriteEndObject();

            w.WriteString("sentAt", r.SentAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

            if (r.Text is not null)
            {
                w.WriteString("text", r.Text);
                w.WriteString("textFormat", JournalWire.Of(r.TextFormat ?? JournalTextFormat.Plain));
            }

            if (r.Transcript is not null) w.WriteString("transcript", r.Transcript);
            if (r.TranscriptTruncated) w.WriteBoolean("transcriptTruncated", true);

            w.WriteString("origin", JournalWire.Of(r.Origin));

            if (r.SendGroup is { } sendGroup)
            {
                w.WriteStartObject("sendGroup");
                w.WriteString("id", sendGroup.Id);
                w.WriteNumber("part", sendGroup.Part);
                w.WriteNumber("parts", sendGroup.Parts);
                w.WriteEndObject();
            }

            w.WriteStartArray("attachments");
            foreach (var a in r.Attachments.Take(MaxAttachments))
            {
                w.WriteStartObject();
                w.WriteNumber("ordinal", a.Ordinal);
                w.WriteString("kind", JournalWire.Of(a.Kind));
                w.WriteString("mimeType", MimePattern.IsMatch(a.MimeType) ? a.MimeType : "application/octet-stream");
                if (a.ByteSize is >= 0) w.WriteNumber("byteSize", a.ByteSize.Value);
                if (Clamp(a.FileName, 255) is { Length: > 0 } name) w.WriteString("fileName", name);
                if (a.FileUniqueId is { } unique && AsciiId.IsMatch(unique)) w.WriteString("fileUniqueId", unique);

                if (a.FileId is { Length: > 0 and <= 255 } fileId && fileId.All(c => c is >= ' ' and <= '~')) w.WriteString("fileId", fileId);
                if (a.CopiedFrom is { } copied)
                {
                    w.WriteStartObject("copiedFrom"); w.WriteString("messageId", copied.MessageId);
                    w.WriteNumber("ordinal", copied.Ordinal); w.WriteEndObject();
                }

                // Exactly one of the two, mirroring the contract: a declined attachment names its
                // reason, an archived one names its upload and the digest the server must match.
                // Writing both would be a record the listener refuses.
                if (a.UploadId is { } uploadId)
                {
                    w.WriteString("uploadId", uploadId);
                    if (a.UploadSha256 is { } digest) w.WriteString("uploadSha256", digest);
                }
                else if (a.NotArchivedReason is { } reason)
                {
                    w.WriteString("notArchivedReason", JournalWire.Of(reason));
                }

                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>At most <paramref name="maxCodePoints"/> code points, never splitting a surrogate pair.</summary>
    internal static string? Clamp(string? value, int maxCodePoints)
    {
        if (value is null) return null;

        var count = 0;
        var end = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count == maxCodePoints) return value[..end];
            count++;
            end += rune.Utf16SequenceLength;
        }

        return value;
    }

    /// <summary>At most <paramref name="maxBytes"/> UTF-8 bytes, cut on a code-point boundary.</summary>
    internal static string? TruncateUtf8(string? value, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (value is null || Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;

        truncated = true;
        var bytes = 0;
        var end = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            bytes += rune.Utf8SequenceLength;
            end += rune.Utf16SequenceLength;
        }

        return value[..end];
    }
}
