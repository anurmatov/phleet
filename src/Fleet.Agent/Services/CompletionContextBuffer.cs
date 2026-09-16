using Fleet.Agent.Abstractions;
using Fleet.Agent.Models;

namespace Fleet.Agent.Services;

/// <summary>
/// Buffers the agent's own answers and tool use back into shared conversation context (#277 D-2).
///
/// Two of the three effects <c>AgentTransport</c>'s constructor used to attach, and neither is
/// Telegram-specific: a cold executor needs this context whatever channel the turn came from. They
/// moved here so they survive the transport's absence.
///
/// <para>
/// The <c>telegramMessageId</c> question is the reason this component exists separately rather than
/// the whole completion handler moving as one piece. The value is a Telegram message id, so only
/// the transport can know one, and the map that holds it
/// (<c>AgentTransport._lastSentMessageIds</c>) is written by seven sites inside the render path
/// that #274 Constraint 1 freezes. So the map does NOT move: the transport publishes the value
/// through <see cref="IMessageSink.GetLastSentMessageId"/> and this component reads it through the
/// holder (#277 D-2a). One owner for the value, one owner for the buffering, one path between them
/// (#277 MUST NOT 19, MUST NOT 20).
/// </para>
///
/// <para>
/// In a Telegram-free host the holder returns <c>0</c>, which is already what a Telegram chat with
/// no prior send yields at the original call site and already the default of
/// <c>GroupBehavior.BufferBotResponse</c>. Buffering is therefore byte-identical across the move.
/// </para>
///
/// Subscribes in its constructor, for the same host-ordering reason as
/// <see cref="RelayCompletionPublisher"/>.
/// </summary>
public sealed class CompletionContextBuffer : IDisposable
{
    private readonly TaskManager _taskManager;
    private readonly GroupBehavior _groupBehavior;
    private readonly IMessageSink _sink;

    public CompletionContextBuffer(
        TaskManager taskManager,
        GroupBehavior groupBehavior,
        IMessageSink sink)
    {
        _taskManager = taskManager;
        _groupBehavior = groupBehavior;
        _sink = sink;

        _taskManager.OnTaskCompleted += OnTaskCompleted;
        _taskManager.OnToolUse += OnToolUse;
        IsAttached = true;
    }

    /// <summary>True once this instance has subscribed. Read by the startup guard (#277 D-3).</summary>
    public bool IsAttached { get; private set; }

    public void Dispose()
    {
        if (!IsAttached) return;
        _taskManager.OnTaskCompleted -= OnTaskCompleted;
        _taskManager.OnToolUse -= OnToolUse;
        IsAttached = false;
    }

    private void OnToolUse(long chatId, string toolName, string description) =>
        _groupBehavior.BufferToolUse(chatId, toolName, description);

    private void OnTaskCompleted(
        long chatId, string result, string? relaySender, TaskSource source, bool isPartial,
        string? correlationId, string? taskId, CompletionKind kind)
    {
        if (string.IsNullOrEmpty(result)) return;

        var lastSentId = _sink.GetLastSentMessageId(chatId);
        _groupBehavior.BufferBotResponse(chatId, result, telegramMessageId: lastSentId);
    }
}
