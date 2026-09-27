namespace Fleet.Agent.Abstractions;

/// <summary>
/// Who an outbound Telegram message answers. The conversation journal records only
/// <see cref="Human"/> output; a reply to another agent's directive (<see cref="Relay"/>) or to a
/// workflow (<see cref="Bridge"/>) is operational traffic and is never journaled, even in a chat a
/// human can see.
/// </summary>
public enum OutboundOrigin { Human, Relay, Bridge }

public interface IMessageSink
{
    Task SendTextAsync(long chatId, string text, CancellationToken ct = default);
    Task SendTypingAsync(long chatId, CancellationToken ct = default);
    Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default);

    /// <summary>Send a pre-formatted HTML message (content must already be HTML-safe). Falls back to plain text if not overridden.</summary>
    Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
        => SendTextAsync(chatId, htmlText, ct);

    // The origin overloads. The methods without an origin mean Human, so a sink with no journal
    // inherits these and behaves exactly as before; only the Telegram transport reads the origin.

    Task SendTextAsync(long chatId, string text, OutboundOrigin origin, CancellationToken ct = default)
        => SendTextAsync(chatId, text, ct);

    Task SendHtmlTextAsync(long chatId, string htmlText, OutboundOrigin origin, CancellationToken ct = default)
        => SendHtmlTextAsync(chatId, htmlText, ct);

    Task SendPhotoAsync(long chatId, string filePath, string? caption, OutboundOrigin origin, CancellationToken ct = default)
        => SendPhotoAsync(chatId, filePath, caption, ct);

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

/// <summary>
/// Sends with an origin while keeping human output on the origin-less methods, so a sink that does
/// not journal (and every test double) sees exactly the calls it saw before #377. Only relay and
/// bridge output reaches the origin overloads.
/// </summary>
public static class MessageSinkOriginExtensions
{
    public static Task SendTextByOriginAsync(this IMessageSink sink, long chatId, string text, OutboundOrigin origin) =>
        origin == OutboundOrigin.Human ? sink.SendTextAsync(chatId, text) : sink.SendTextAsync(chatId, text, origin);

    public static Task SendHtmlTextByOriginAsync(this IMessageSink sink, long chatId, string htmlText, OutboundOrigin origin) =>
        origin == OutboundOrigin.Human ? sink.SendHtmlTextAsync(chatId, htmlText) : sink.SendHtmlTextAsync(chatId, htmlText, origin);
}
