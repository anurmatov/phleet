namespace Fleet.Agent.Abstractions;

public interface IMessageSink
{
    Task SendTextAsync(long chatId, string text, CancellationToken ct = default);
    Task SendTypingAsync(long chatId, CancellationToken ct = default);
    Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default);

    /// <summary>Send a pre-formatted HTML message (content must already be HTML-safe). Falls back to plain text if not overridden.</summary>
    Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
        => SendTextAsync(chatId, htmlText, ct);
}
