namespace Fleet.Agent.Models;

/// <summary>
/// A message waiting in the agent's FIFO queue to be processed after the current task completes.
/// </summary>
public sealed record QueuedMessage(
    long ChatId,
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
    DateTimeOffset QueuedAt,
    /// <summary>Human-readable sender label for display in heartbeats and dashboard.</summary>
    string SenderDisplay);
