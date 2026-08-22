namespace Fleet.Agent.Models;

/// <summary>
/// One ordered part of a queued conversational entry. Task is already fully
/// formatted by PromptAssembler and must not be rebuilt from raw user text.
/// </summary>
public sealed record QueuedMessagePart(
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
    DateTimeOffset ArrivedAt,
    string SenderDisplay);

/// <summary>
/// A pending FIFO queue entry. User-message entries may accumulate several
/// parts while keeping the first part's original queue position.
/// </summary>
public sealed class QueuedMessage
{
    public QueuedMessage(long chatId, QueuedMessagePart firstPart)
    {
        ChatId = chatId;
        _parts.Add(firstPart);
    }

    private readonly List<QueuedMessagePart> _parts = [];

    public const int MaxParts = 10;

    public long ChatId { get; }

    /// <summary>Serializes append-vs-drain decisions for this pending entry.</summary>
    public SemaphoreSlim QueueDispatchLock { get; } = new(1, 1);

    /// <summary>Set while holding QueueDispatchLock once DrainQueue has claimed this entry.</summary>
    public bool Claimed { get; set; }

    public QueuedMessagePart FirstPart => _parts[0];
    public IReadOnlyList<QueuedMessagePart> Parts => [.. _parts];
    public int PartCount => _parts.Count;

    public string Task => FirstPart.Task;
    public string DisplayText => PartCount == 1
        ? FirstPart.DisplayText
        : $"{FirstPart.DisplayText} (+{PartCount - 1} more)";
    public bool IsSessionTask => FirstPart.IsSessionTask;
    public TaskSource Source => FirstPart.Source;
    public string? RelaySender => FirstPart.RelaySender;
    public string? CorrelationId => FirstPart.CorrelationId;
    public string? TaskId => FirstPart.TaskId;
    public IReadOnlyList<MessageImage>? Images => FirstPart.Images;
    public IReadOnlyList<MessageDocument>? Documents => FirstPart.Documents;
    public long UserId => FirstPart.UserId;
    public DateTimeOffset QueuedAt => FirstPart.ArrivedAt;
    public string SenderDisplay => FirstPart.SenderDisplay;

    public bool CanMerge(QueuedMessagePart part) =>
        !Claimed &&
        Source == TaskSource.UserMessage &&
        part.Source == TaskSource.UserMessage &&
        PartCount < MaxParts;

    public bool TryAppendPart(QueuedMessagePart part)
    {
        if (!CanMerge(part)) return false;
        _parts.Add(part);
        return true;
    }

    public bool ContainsTaskId(string taskId)
    {
        QueueDispatchLock.Wait();
        try
        {
            return _parts.Any(part => part.TaskId == taskId);
        }
        finally
        {
            QueueDispatchLock.Release();
        }
    }

    public QueuedMessagePayload BuildPayload(DateTimeOffset startedAt) =>
        new(
            Task: BuildCombinedTask(startedAt),
            DisplayText: DisplayText,
            IsSessionTask: IsSessionTask,
            Source: Source,
            RelaySender: RelaySender,
            CorrelationId: CorrelationId,
            TaskId: TaskId,
            Images: _parts.SelectMany(part => part.Images ?? []).ToList(),
            Documents: _parts.SelectMany(part => part.Documents ?? []).ToList(),
            UserId: UserId);

    private string BuildCombinedTask(DateTimeOffset startedAt)
    {
        if (_parts.Count == 1)
            return _parts[0].Task;

        var lines = new List<string>
        {
            $"[This batch of {_parts.Count} messages waited {FormatElapsed(startedAt - QueuedAt)} in queue before this turn began]",
            _parts[0].Task
        };

        for (var i = 1; i < _parts.Count; i++)
        {
            var previous = _parts[i - 1];
            var current = _parts[i];
            lines.Add($"[ADDITIONAL MESSAGE — sent {FormatElapsed(current.ArrivedAt - previous.ArrivedAt)} after the one above at {current.ArrivedAt:HH:mm:ss}, same conversation]");
            lines.Add(current.Task);
        }

        return string.Join("\n\n", lines);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1)
            return $"{Math.Max(0, (int)Math.Round(elapsed.TotalSeconds))}s";
        if (elapsed.TotalHours < 1)
            return $"{(int)Math.Round(elapsed.TotalMinutes)}m";
        return elapsed.TotalHours < 24
            ? $"{elapsed.TotalHours:0.#}h"
            : $"{(int)Math.Round(elapsed.TotalDays)}d";
    }
}

public sealed record QueuedMessagePayload(
    string Task,
    string DisplayText,
    bool IsSessionTask,
    TaskSource Source,
    string? RelaySender,
    string? CorrelationId,
    string? TaskId,
    IReadOnlyList<MessageImage>? Images,
    IReadOnlyList<MessageDocument>? Documents,
    long UserId);
