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
    // inherits these and behaves exactly as before. None of them journals (#394): a notice, a
    // progress post or command output is never a record, whatever its origin.

    Task SendTextAsync(long chatId, string text, OutboundOrigin origin, CancellationToken ct = default)
        => SendTextAsync(chatId, text, ct);

    Task SendHtmlTextAsync(long chatId, string htmlText, OutboundOrigin origin, CancellationToken ct = default)
        => SendHtmlTextAsync(chatId, htmlText, ct);

    Task SendPhotoAsync(long chatId, string filePath, string? caption, OutboundOrigin origin, CancellationToken ct = default)
        => SendPhotoAsync(chatId, filePath, caption, ct);

    /// <summary>
    /// Sends a turn's answer: the ONLY outbound send the conversation journal records (#394).
    /// Capture is opt-in by this method, never decided by looking at the text, so a new notice is
    /// excluded without anyone having to remember it.
    ///
    /// The default composes the reply exactly as the task manager did before #394 and sends it on
    /// the origin methods, so a sink with no journal is unchanged. The Telegram transport renders it
    /// itself: Telegram gets the same bytes, the journal gets the body without the footer.
    /// </summary>
    Task SendReplyAsync(long chatId, AgentReply reply, OutboundOrigin origin, CancellationToken ct = default)
        => this.SendComposedByOriginAsync(chatId, reply, origin);

    /// <summary>
    /// True when this sink implements <see cref="SendReplyAsync"/> itself. Callers go through
    /// <see cref="MessageSinkOriginExtensions.SendReplyByOriginAsync"/>, which asks this first.
    ///
    /// Needed because a mocking proxy implements every interface member, default ones included,
    /// and never runs a default body: without the question, every test double would see one
    /// <c>SendReplyAsync</c> call instead of the sends it saw before #394 — the same reason the
    /// origin extensions below exist.
    /// </summary>
    bool RendersReplies => false;

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

    /// <summary>
    /// Sends a turn's answer (#394). A sink that renders replies gets
    /// <see cref="IMessageSink.SendReplyAsync"/>; any other gets the composed text on exactly the
    /// calls it got before.
    /// </summary>
    public static Task SendReplyByOriginAsync(this IMessageSink sink, long chatId, AgentReply reply, OutboundOrigin origin) =>
        sink.RendersReplies ? sink.SendReplyAsync(chatId, reply, origin) : sink.SendComposedByOriginAsync(chatId, reply, origin);

    /// <summary>
    /// Sends <paramref name="reply"/> composed as the task manager always composed it — Path T's
    /// HTML when there is a tool block, else the text with its stats line — on the origin methods,
    /// which never journal. The status lines that share a reply's footer go out this way.
    /// </summary>
    public static Task SendComposedByOriginAsync(this IMessageSink sink, long chatId, AgentReply reply, OutboundOrigin origin) =>
        reply.HasToolBlock
            ? sink.SendHtmlTextByOriginAsync(chatId, reply.ComposeHtml(), origin)
            : sink.SendTextByOriginAsync(chatId, reply.ComposeText(), origin);
}
