namespace Fleet.Agent.Abstractions;

public interface IMessageSink
{
    Task SendTextAsync(long chatId, string text, CancellationToken ct = default);
    Task SendTypingAsync(long chatId, CancellationToken ct = default);
    Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default);

    /// <summary>Send a pre-formatted HTML message (content must already be HTML-safe). Falls back to plain text if not overridden.</summary>
    Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
        => SendTextAsync(chatId, htmlText, ct);

    /// <summary>
    /// The id of the last message this sink delivered to <paramref name="chatId"/>, or <c>0</c>
    /// when it has sent none — which is also what every non-Telegram sink returns.
    ///
    /// This exists so the completion-context buffer can persist the outbound message id without
    /// the Telegram transport's private <c>_lastSentMessageIds</c> map moving anywhere. The value
    /// is a Telegram message id and only the transport can know one, so the transport keeps both
    /// the map and its seven render-path writers and publishes the value through here instead
    /// (#277 D-2a, MUST NOT 19).
    ///
    /// A default interface method for the same reason <see cref="SendHtmlTextAsync"/> is one:
    /// every sink that has no such concept inherits the honest answer rather than being forced to
    /// invent it.
    /// </summary>
    long GetLastSentMessageId(long chatId) => 0L;
}
