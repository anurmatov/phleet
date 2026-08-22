namespace Fleet.Agent.Models;

/// <summary>
/// A conversational item that arrived while a session turn was already running.
/// Used either for live mid-turn injection or as the fallback turn-end inbox payload.
/// </summary>
public sealed record MidTurnMessage(
    string Task,
    string DisplayText,
    bool IsSessionTask,
    TaskSource Source,
    string? RelaySender,
    string? CorrelationId,
    string? TaskId,
    IReadOnlyList<MessageImage>? Images,
    IReadOnlyList<MessageDocument>? Documents,
    long UserId,
    DateTimeOffset ArrivedAt);
