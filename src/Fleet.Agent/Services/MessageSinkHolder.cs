using Fleet.Agent.Abstractions;

namespace Fleet.Agent.Services;

/// <summary>
/// The dependency-free seam between the four services that send outbound text and whichever
/// <see cref="IMessageSink"/> — if any — is actually present (#277 D-1).
///
/// Having NO dependencies is the whole point. It is what broke the circular DI that
/// <c>AgentTransport</c> was working around when it assigned itself into four already-constructed
/// objects (<c>AgentTransport</c> → <c>TaskManager</c> → sink). Consumers constructor-inject
/// <see cref="IMessageSink"/>, the container hands them this holder, and the transport attaches
/// itself to it during its own construction. Nobody reaches backwards into a built object graph
/// and no consumer has an ordering assumption left to break.
///
/// This is a holder, NOT a router. It does not inspect the runtime conversation key and it makes
/// no Telegram formatting decision. The four reserved-key guards stay exactly where they are, in
/// the transport's render path (#277 MUST NOT 3, #274 Constraint 1).
/// </summary>
public sealed class MessageSinkHolder : IMessageSink
{
    private volatile IMessageSink _inner;

    public MessageSinkHolder(SinkSuppressionCounter? counter = null)
        => _inner = new NullMessageSink(counter);

    /// <summary>True once a real sink has been attached. False in a Telegram-free host (S4).</summary>
    public bool IsAttached { get; private set; }

    /// <summary>
    /// Attach the real sink. Called from <c>AgentTransport</c>'s constructor, replacing the four
    /// self-injecting property assignments it used to perform.
    /// </summary>
    public void Attach(IMessageSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _inner = sink;
        IsAttached = true;
    }

    public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
        => _inner.SendTextAsync(chatId, text, ct);

    public Task SendTypingAsync(long chatId, CancellationToken ct = default)
        => _inner.SendTypingAsync(chatId, ct);

    public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default)
        => _inner.SendPhotoAsync(chatId, filePath, caption, ct);

    public Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
        => _inner.SendHtmlTextAsync(chatId, htmlText, ct);

    /// <summary>
    /// Forwards to the attached sink, yielding <c>0</c> when none is attached — the same value a
    /// Telegram chat that has not been sent to yet already produces (#277 D-2a). This is the ONLY
    /// path by which <c>telegramMessageId</c> reaches <see cref="CompletionContextBuffer"/>; the
    /// map itself never leaves the transport (#277 MUST NOT 19, MUST NOT 20).
    /// </summary>
    public long GetLastSentMessageId(long chatId) => _inner.GetLastSentMessageId(chatId);
}
