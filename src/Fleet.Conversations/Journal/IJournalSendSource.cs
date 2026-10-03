using Fleet.Conversations.Contracts;
namespace Fleet.Conversations.Journal;
/// <summary>Observed-only send lookup, independent of read-all grants.</summary>
public interface IJournalSendSource
{
    Task<JournalSendSource?> FindSendSourceAsync(string subject, string boundKey, long requester,
        string? messageId, long? telegramMessageId, int ordinal, CancellationToken ct = default);
}
public sealed record JournalSendSource(JournalAttachmentLocator Attachment, string ConversationKey,
    string ChatKind, long ChatId, bool RequesterWasPresent, string? FileId, long? FileIdBotId)
{
    public override string ToString() => nameof(JournalSendSource);
}
