namespace Fleet.Conversations.Contracts;

// The journal's read side (#394): what the three read tools ask the store and what it answers.
// BCL-only, like the rest of this project.
//
// ⚠️ Every string enum value below is the STORED column value, read back as it is. The columns
// already carry values later slices write (`agent_copy`, `copied`, `lost`, ...), and a read path
// that mapped them onto this assembly's enums would fail on the first row such a slice writes.

/// <summary>Which messages a reader may see.</summary>
public enum JournalReadScope
{
    /// <summary>
    /// Only messages with a <c>journal_message_observers</c> row naming the reader: exactly the
    /// messages its own runtime journaled. The default for every read token.
    /// </summary>
    Observed,

    /// <summary>Every message. Granted only by <c>Comms__Journal__ReadAllSubjects</c>.</summary>
    All,
}

/// <summary>
/// The caller of a read: the read-token subject and the scope the deployment grants it. Never taken
/// from a tool argument.
/// </summary>
public sealed record JournalReader(string Subject, JournalReadScope Scope);

/// <summary>
/// A keyset position: the last row a page returned. Search keys on <c>sent_at</c> in microseconds
/// since the Unix epoch; replay keys on <c>order_key</c>. <see cref="Id"/> breaks ties.
/// </summary>
public readonly record struct JournalKeyPosition(long Key, string Id);

/// <summary>Fixed bounds of the read tools. Constants, not settings.</summary>
public static class JournalReadLimits
{
    public const int SearchDefault = 20;
    public const int SearchMax = 100;
    public const int ReplayDefault = 50;
    public const int ReplayMax = 200;

    /// <summary>Longest <c>query</c> <c>search_messages</c> accepts, in characters.</summary>
    public const int QueryMaxChars = 256;

    /// <summary>Longest <c>text_preview</c>, in characters.</summary>
    public const int PreviewChars = 500;

    /// <summary>
    /// A <c>get_conversation</c> page ends early once its text and transcripts reach this many UTF-8
    /// bytes. The first record of a page is always returned, so a traversal cannot stall.
    /// </summary>
    public const int ReplayPageTextBytes = 262_144;
}

/// <summary><c>search_messages</c>, already validated.</summary>
public sealed record JournalSearchQuery
{
    /// <summary>
    /// Full-text terms, each required. Already stripped of operators and of terms the index cannot
    /// hold; null means no full-text condition.
    /// </summary>
    public IReadOnlyList<string>? Terms { get; init; }

    public string? ConversationId { get; init; }

    /// <summary><c>human</c> or <c>agent</c>.</summary>
    public string? SenderKind { get; init; }

    public string? SenderId { get; init; }

    /// <summary><c>inbound</c> or <c>outbound</c>.</summary>
    public string? Direction { get; init; }

    /// <summary>Inclusive lower bound on <c>sent_at</c>.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper bound on <c>sent_at</c>.</summary>
    public DateTimeOffset? Until { get; init; }

    public required int Limit { get; init; }

    /// <summary>The last row of the previous page; null for the first page.</summary>
    public JournalKeyPosition? After { get; init; }
}

/// <summary><c>get_conversation</c>, already validated.</summary>
public sealed record JournalReplayQuery
{
    public required string ConversationId { get; init; }

    /// <summary>
    /// Exclusive start, used only when <see cref="After"/> is null. Must be a message the reader may
    /// see AND in <see cref="ConversationId"/>; anything else answers exactly as a conversation that
    /// does not exist.
    /// </summary>
    public string? FromMessageId { get; init; }

    /// <summary>True: newest first. False: oldest first.</summary>
    public bool Backward { get; init; }

    public required int Limit { get; init; }

    public JournalKeyPosition? After { get; init; }
}

public sealed record JournalReadConversation
{
    public required string Id { get; init; }
    public required string ChatKind { get; init; }
    public required long TelegramChatId { get; init; }
    public string? Title { get; init; }
}

public sealed record JournalReadSender
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public string? Display { get; init; }
}

/// <summary>
/// An attachment as a reader sees it: metadata only.
/// </summary>
/// <remarks>
/// ⚠️ There is deliberately no object id, object key, bucket, digest, platform file id or URL here —
/// not a property that is left null, but no property at all, so no serializer can emit one. The
/// bytes belong to Comms (the S4 media boundary).
/// </remarks>
public sealed record JournalReadAttachment
{
    public required int Ordinal { get; init; }
    public required string Kind { get; init; }
    public required string MimeType { get; init; }
    public long? ByteSize { get; init; }
    public string? FileName { get; init; }
    public required string State { get; init; }
    public string? NotArchivedReason { get; init; }
}

/// <summary>One journal message, in full.</summary>
public sealed record JournalReadMessage
{
    public required string MessageId { get; init; }
    public required JournalReadConversation Conversation { get; init; }
    public long? TelegramMessageId { get; init; }

    /// <summary>The platform id this message replies to, as stored on the reader's visible row.</summary>
    public long? ReplyToTelegramMessageId { get; init; }

    /// <summary>
    /// The journal id of the reply target, or null when it is not in the journal OR not visible to
    /// the reader. The two are indistinguishable by design.
    /// </summary>
    public string? ReplyToMessageId { get; init; }

    public string? MediaGroupId { get; init; }
    public required string Direction { get; init; }
    public required JournalReadSender Sender { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public string? Text { get; init; }
    public string? TextFormat { get; init; }
    public string? Transcript { get; init; }
    public bool TranscriptTruncated { get; init; }
    public required string Origin { get; init; }
    public required string DeliveryState { get; init; }
    public JournalSendGroup? SendGroup { get; init; }
    public IReadOnlyList<JournalReadAttachment> Attachments { get; init; } = [];
}

/// <summary>One <c>search_messages</c> hit.</summary>
public sealed record JournalMessageSummary
{
    public required string MessageId { get; init; }
    public required string ConversationId { get; init; }
    public required long TelegramChatId { get; init; }
    public long? TelegramMessageId { get; init; }
    public required string ChatKind { get; init; }
    public required string Direction { get; init; }
    public required JournalReadSender Sender { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string Origin { get; init; }
    public string? TextPreview { get; init; }
    public bool TextTruncated { get; init; }
    public bool HasTranscript { get; init; }
    public int AttachmentCount { get; init; }
}

public sealed record JournalSearchPage
{
    public required IReadOnlyList<JournalMessageSummary> Items { get; init; }

    /// <summary>Where the next page starts; null when this page is the last.</summary>
    public JournalKeyPosition? Next { get; init; }
}

public sealed record JournalConversationPage
{
    public required JournalReadConversation Conversation { get; init; }
    public required IReadOnlyList<JournalReadMessage> Items { get; init; }
    public JournalKeyPosition? Next { get; init; }
}
