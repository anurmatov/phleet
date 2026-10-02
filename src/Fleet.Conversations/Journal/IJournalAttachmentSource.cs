using Fleet.Conversations.Contracts;

namespace Fleet.Conversations.Journal;

/// <summary>
/// Comms-only attachment resolution. Both identifier forms are intersected with the verified
/// reader's observership and the server's conversation binding in one read-committed statement.
/// Object locators never belong to the public read contracts or a serialized response.
/// </summary>
public interface IJournalAttachmentSource
{
    Task<JournalAttachmentLocator?> FindAttachmentAsync(
        JournalReader reader, string conversationKey, string? messageId,
        long? telegramMessageId, int ordinal, CancellationToken ct = default);
}

/// <summary>Internal delivery metadata, not a wire DTO; never serialize or log this value.</summary>
public sealed record JournalAttachmentLocator(
    string MessageId, int Ordinal, string Kind, string MimeType, long? ByteSize,
    string AttachmentState, string? NotArchivedReason, string? Sha256,
    string? ObjectKey, string? ObjectState, long? ObjectByteSize, string? ObjectSha256)
{
    // A diagnostic interpolation must not accidentally expose a locator or digest.
    public override string ToString() => nameof(JournalAttachmentLocator);
}
