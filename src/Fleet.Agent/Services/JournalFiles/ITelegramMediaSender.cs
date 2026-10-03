namespace Fleet.Agent.Services.JournalFiles;
public interface ITelegramMediaSender
{
    long? BotId { get; }
    Task<JournalMessage> SendFileIdAsync(long chatId, string kind, string fileId, CancellationToken ct);
    Task<JournalMessage> SendUploadAsync(long chatId, bool photo, HttpContent content, string fileName, CancellationToken ct);
}
public sealed class JournalTelegramException(int status, bool invalidFile = false, int? retryAfter = null) : Exception("telegram_media_rejected")
{
    public int Status { get; } = status;
    public bool InvalidFile { get; } = invalidFile;
    public int? RetryAfter { get; } = retryAfter is > 0 ? retryAfter : null;
}
