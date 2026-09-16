using Fleet.Agent.Services;

namespace Fleet.Agent.Abstractions;

/// <summary>
/// The no-op <see cref="IMessageSink"/> served by <see cref="MessageSinkHolder"/> when no real
/// sink is attached (#277 D-1).
///
/// This is what makes a Telegram-free host possible. Before it, the four services that send
/// outbound text held a settable <c>Sink</c> property initialised to <c>null!</c> and assigned by
/// <c>AgentTransport</c>'s constructor, so omitting the transport turned every outbound call into
/// a <see cref="NullReferenceException"/> — and the startup guard that was supposed to make wiring
/// failures loud turned the process into a crash loop instead.
///
/// Every call is counted under <see cref="SinkSuppressionCounter.ReasonNullSink"/> rather than
/// silently swallowed: an unobserved no-op sink is indistinguishable from a healthy one.
///
/// <see cref="IMessageSink.GetLastSentMessageId"/> is deliberately NOT overridden. The interface
/// default returns <c>0</c>, which is exactly what a Telegram chat with no prior send already
/// yields and what <c>GroupBehavior.BufferBotResponse</c> already defaults to, so buffering in a
/// Telegram-free host is byte-identical to buffering before the first send in a Telegram one
/// (#277 D-2a).
/// </summary>
public sealed class NullMessageSink : IMessageSink
{
    private readonly SinkSuppressionCounter? _counter;

    public NullMessageSink(SinkSuppressionCounter? counter = null) => _counter = counter;

    /// <summary>
    /// Shared counter-less instance, used as the fallback for the optional constructor parameter
    /// on the four sink consumers so a hand-constructed instance (every test) never sees null.
    /// Production resolves the DI-registered holder instead.
    /// </summary>
    public static readonly NullMessageSink Instance = new();

    public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
    {
        _counter?.Suppressed(SinkSuppressionCounter.ReasonNullSink);
        return Task.CompletedTask;
    }

    public Task SendTypingAsync(long chatId, CancellationToken ct = default)
    {
        _counter?.Suppressed(SinkSuppressionCounter.ReasonNullSink);
        return Task.CompletedTask;
    }

    public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default)
    {
        _counter?.Suppressed(SinkSuppressionCounter.ReasonNullSink);
        return Task.CompletedTask;
    }

    public Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
    {
        _counter?.Suppressed(SinkSuppressionCounter.ReasonNullSink);
        return Task.CompletedTask;
    }
}
