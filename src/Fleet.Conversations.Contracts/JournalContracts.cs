namespace Fleet.Conversations.Contracts;

// The conversation journal's record contract (docs/comms-journal.md). One record is one Telegram
// message as one runtime observed it. BCL-only, like the rest of this project.

public enum JournalSenderKind { Human, Agent }

public enum JournalTextFormat { Plain, Html, Rich }

/// <summary>
/// The wire record's <c>origin</c>: which runtime observed the message. Not the task origin the
/// classifier takes (<see cref="JournalTaskOrigin"/>).
/// </summary>
/// <remarks>
/// <c>agent_tool</c> (#394) is a message an agent sent through a Telegram MCP tool
/// (<c>send_message</c>, <c>send_to_ceo</c>), captured from the tool-send receipt: always outbound,
/// always an agent sender. The schema already carries the values later slices write
/// (<c>client_submission</c>, <c>client_turn</c>), so those slices stay additive.
/// <c>agent_copy</c> identifies an outbound attachment resend with an operator-only source link.
/// </remarks>
public enum JournalRecordOrigin { TelegramUpdate, AgentRuntime, AgentTool, AgentCopy }

public enum JournalAttachmentKind { Photo, Document, Voice, Video, VideoNote, Audio, Animation, Sticker, Other }

/// <summary>Why an attachment's bytes are not in the journal. Required on every attachment in this slice.</summary>
public enum JournalNotArchivedReason
{
    MediaDisabled, OverBotApiLimit, OverSizeCap, UnsupportedKind, DownloadFailed, SourceExpired, Copied,
}

public sealed record JournalSender
{
    public required JournalSenderKind Kind { get; init; }
    public required string Id { get; init; }
    public string? Display { get; init; }
}

/// <summary>Links the chunks of one long outbound reply. <c>1 ≤ Part ≤ Parts ≤ 64</c>.</summary>
public sealed record JournalSendGroup
{
    public required string Id { get; init; }
    public required int Part { get; init; }
    public required int Parts { get; init; }
}

/// <summary>
/// One attachment in a record: its metadata, and either a reason its bytes are not archived or the
/// id of an upload the caller already proved bytes for.
/// </summary>
public sealed record JournalCopiedFrom
{
    public required string MessageId { get; init; }
    public required int Ordinal { get; init; }
}

public sealed record JournalAttachment
{
    public required int Ordinal { get; init; }
    public required JournalAttachmentKind Kind { get; init; }
    public required string MimeType { get; init; }
    public long? ByteSize { get; init; }
    public string? FileName { get; init; }
    public string? FileUniqueId { get; init; }

    /// <summary>Bot-scoped resend credential. Never exposed in read-tool responses or fingerprints.</summary>
    public string? FileId { get; init; }

    /// <summary>Operator-only audit link; retention does not depend on the source row.</summary>
    public JournalCopiedFrom? CopiedFrom { get; init; }

    /// <summary>
    /// The upload whose bytes this attachment names, when the caller proved them. Null exactly when
    /// <see cref="NotArchivedReason"/> is set: one of the two is always present, never both.
    /// </summary>
    public string? UploadId { get; init; }

    /// <summary>
    /// The lowercase hex SHA-256 of the bytes the caller uploaded for <see cref="UploadId"/>.
    /// Required with it, and checked against what the server hashed at PUT time — naming an id is
    /// not proof of holding its bytes.
    /// </summary>
    public string? UploadSha256 { get; init; }

    /// <summary>Set when the bytes are not archived. Null when <see cref="UploadId"/> names an upload.</summary>
    public JournalNotArchivedReason? NotArchivedReason { get; init; }

    /// <summary>
    /// The object this attachment resolved to, filled in by the SERVER from the validated
    /// <see cref="UploadId"/> (dedup winner included). A caller that sends it is refused — it is
    /// part of the record's fingerprint, so letting a client choose it would let one client forge
    /// another's duplicate as a conflict.
    /// </summary>
    public string? ObjectId { get; init; }
}

public sealed record JournalTelegramRef
{
    public required long BotId { get; init; }
    public required long ChatId { get; init; }
    public required JournalChatKind ChatKind { get; init; }
    public string? ChatTitle { get; init; }
    public required long MessageId { get; init; }
    public long? ReplyToMessageId { get; init; }

    /// <summary>An album is N records sharing this id, one per platform message id.</summary>
    public string? MediaGroupId { get; init; }
}

/// <summary>A validated journal record. The observer is never part of it: it is the token subject.</summary>
public sealed record JournalRecord
{
    /// <summary>The publisher's idempotency key (a ULID).</summary>
    public required string EventId { get; init; }

    public required JournalTelegramRef Telegram { get; init; }
    public required JournalDirection Direction { get; init; }
    public required JournalSender Sender { get; init; }

    /// <summary>The platform's message date, never the time it was received.</summary>
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Raw platform text or caption only. Never a path, hint, prompt or transcript.</summary>
    public string? Text { get; init; }

    public JournalTextFormat? TextFormat { get; init; }

    /// <summary>A speech-to-text result. Never merged into <see cref="Text"/>.</summary>
    public string? Transcript { get; init; }

    public bool TranscriptTruncated { get; init; }
    public required JournalRecordOrigin Origin { get; init; }
    public JournalSendGroup? SendGroup { get; init; }
    public IReadOnlyList<JournalAttachment> Attachments { get; init; } = [];
}

public enum JournalIngestOutcome
{
    /// <summary>A new natural key: conversation (upsert), message, observer and attachments written.</summary>
    Created,

    /// <summary>Already recorded for this observer, or this event id was already recorded. Nothing written.</summary>
    Duplicate,

    /// <summary>A known message seen by a new observer: one observer row written.</summary>
    ObserverAdded,

    /// <summary>Same natural key, different fingerprint. Nothing written.</summary>
    Conflict,

    /// <summary>The event id was already recorded with a different fingerprint. Nothing written.</summary>
    EventIdReused,

    /// <summary>
    /// An <c>uploadId</c> named an object that is unknown, foreign-owned, not yet uploaded, aborted,
    /// swept, or already committed as someone else's loser. Nothing written; the client re-uploads
    /// from its spool. Carries the offending attachment ordinals.
    /// </summary>
    UploadIncomplete,
}

public sealed record JournalIngestResult
{
    public required JournalIngestOutcome Outcome { get; init; }

    /// <summary>The journal message id; null for a conflict.</summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Attachment ordinals that could not be attached, for <see cref="JournalIngestOutcome.UploadIncomplete"/>.
    /// Sorted, distinct, and never empty when the outcome is that one.
    /// </summary>
    public IReadOnlyList<int> UploadOrdinals { get; init; } = [];
}

/// <summary>
/// The store cannot answer: the database is unreachable, or its schema is behind this binary.
/// </summary>
/// <remarks>The message carries no connection detail; <see cref="Reason"/> is a fixed code or null.</remarks>
public sealed class JournalStoreUnavailableException(string? reason, Exception? inner = null)
    : Exception(reason is null ? "journal store unavailable" : $"journal store unavailable: {reason}", inner)
{
    /// <summary>The schema-behind code, <c>schema_behind</c>, or null for a connection failure.</summary>
    public string? Reason { get; } = reason;

    public const string SchemaBehind = "schema_behind";
}

public sealed record JournalObserverStatus
{
    public required string Observer { get; init; }
    public required long Messages { get; init; }
    public DateTimeOffset? LastIngestAt { get; init; }
}

public sealed record JournalStoreStatus
{
    public int? SchemaVersion { get; init; }
    public required IReadOnlyList<JournalObserverStatus> Observers { get; init; }
}

/// <summary>
/// The wire and column spelling of every journal enum, in one table, so the parser and the store
/// cannot drift apart.
/// </summary>
public static class JournalWire
{
    public static string Of(JournalDirection value) => value switch
    {
        JournalDirection.Inbound => "inbound",
        JournalDirection.Outbound => "outbound",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(JournalChatKind value) => value switch
    {
        JournalChatKind.Private => "private",
        JournalChatKind.Group => "group",
        JournalChatKind.Supergroup => "supergroup",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(JournalSenderKind value) => value switch
    {
        JournalSenderKind.Human => "human",
        JournalSenderKind.Agent => "agent",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(JournalTextFormat value) => value switch
    {
        JournalTextFormat.Plain => "plain",
        JournalTextFormat.Html => "html",
        JournalTextFormat.Rich => "rich",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(JournalRecordOrigin value) => value switch
    {
        JournalRecordOrigin.TelegramUpdate => "telegram_update",
        JournalRecordOrigin.AgentRuntime => "agent_runtime",
        JournalRecordOrigin.AgentTool => "agent_tool",
        JournalRecordOrigin.AgentCopy => "agent_copy",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(JournalAttachmentKind value) => value switch
    {
        JournalAttachmentKind.Photo => "photo",
        JournalAttachmentKind.Document => "document",
        JournalAttachmentKind.Voice => "voice",
        JournalAttachmentKind.Video => "video",
        JournalAttachmentKind.VideoNote => "video_note",
        JournalAttachmentKind.Audio => "audio",
        JournalAttachmentKind.Animation => "animation",
        JournalAttachmentKind.Sticker => "sticker",
        JournalAttachmentKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(JournalNotArchivedReason value) => value switch
    {
        JournalNotArchivedReason.MediaDisabled => "media_disabled",
        JournalNotArchivedReason.OverBotApiLimit => "over_bot_api_limit",
        JournalNotArchivedReason.OverSizeCap => "over_size_cap",
        JournalNotArchivedReason.UnsupportedKind => "unsupported_kind",
        JournalNotArchivedReason.DownloadFailed => "download_failed",
        JournalNotArchivedReason.SourceExpired => "source_expired",
        JournalNotArchivedReason.Copied => "copied",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Parses an exact, case-sensitive wire value. Anything else is not a value.</summary>
    public static bool TryParse<T>(string? wire, out T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(OfAny(candidate), wire, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string OfAny<T>(T value) where T : struct, Enum => value switch
    {
        JournalDirection v => Of(v),
        JournalChatKind v => Of(v),
        JournalSenderKind v => Of(v),
        JournalTextFormat v => Of(v),
        JournalRecordOrigin v => Of(v),
        JournalAttachmentKind v => Of(v),
        JournalNotArchivedReason v => Of(v),
        _ => throw new NotSupportedException(typeof(T).Name),
    };
}
