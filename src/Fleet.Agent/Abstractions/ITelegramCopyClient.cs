namespace Fleet.Agent.Abstractions;

/// <summary>Only the polling transport's own bot. No content reads or fallback sends.</summary>
public interface ITelegramCopyClient
{
    Task<string> GetChatAsync(long chatId, CancellationToken ct);
    Task<int> SendPromptAsync(long chatId, int messageId, string label, string nonce, CancellationToken ct);
    Task<int> CopyMessageAsync(long chatId, long sourceChatId, int messageId, CancellationToken ct);
    Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct);
    Task EditPromptAsync(long chatId, int messageId, string text, CancellationToken ct);
}

public sealed record CopyCallback(string Id, long UserId, long ChatId, int MessageId, string? Data);

public sealed class TelegramCopyException(int status, string description, int? retryAfter = null) : Exception(description)
{
    public int Status { get; } = status;
    public int? RetryAfter { get; } = retryAfter;
}
