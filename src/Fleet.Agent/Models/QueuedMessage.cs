using Fleet.Protocol;

namespace Fleet.Agent.Models;

/// <summary>
/// One ordered part of a queued conversational entry. Task is already fully
/// formatted by PromptAssembler and must not be rebuilt from raw user text.
///
/// <see cref="ColdStartTask"/> is that same prompt as PromptAssembler builds it for a provider
/// process that holds no conversation: with the recent history above the message. Both are built
/// when the message arrives, but which one the turn needs is only known when the turn starts: the
/// provider process can be restarted while the message waits (a token broadcast does that), and
/// a prompt built for a warm process then reaches a cold one with no context at all.
///
/// <see cref="Identity"/> is optional and trailing so existing construction sites are unchanged.
/// Each part keeps its OWN identity — a coalesced entry is one turn but several submissions, and
/// each part needs its own submission id so the client can correlate its acceptance event.
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
    string SenderDisplay,
    ConversationIdentity? Identity = null,
    TaskPriority Priority = TaskPriority.Routine,
    long TelegramMessageId = 0,
    IReadOnlyList<long>? PrimaryMessageIds = null,
    string? ColdStartTask = null)
{
    /// <summary>
    /// The prompt for a turn that starts now. A cold process gets <see cref="ColdStartTask"/> when
    /// there is one; a warm process gets <see cref="Task"/> as it was built, never less.
    /// </summary>
    public string TaskAtTurnStart(bool processCold) =>
        processCold && ColdStartTask is not null ? ColdStartTask : Task;

    /// <summary>
    /// Set once a steering copy of this human message entered a running workflow turn (#406), so
    /// the reply turn tells the model it has already seen it. Set under that turn's dispatch lock.
    /// </summary>
    public bool Steered { get; set; }
}

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
    public TaskPriority Priority { get; internal set; }
    public long Seq { get; internal set; }

    /// <summary>Serializes append-vs-drain decisions for this pending entry.</summary>
    public SemaphoreSlim QueueDispatchLock { get; } = new(1, 1);

    /// <summary>Set while holding QueueDispatchLock once DrainQueue has claimed this entry.</summary>
    public bool Claimed { get; set; }

    /// <summary>
    /// True once a busy or steering-delivered notice send was attempted for this entry, even if
    /// the send failed. DrainQueue sends "Now processing" only then; skipped notices stay silent.
    /// </summary>
    public bool BusyNoticeSent { get; set; }

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

    /// <summary>
    /// The identity the turn runs under: the FIRST part's. The remaining parts' submission ids
    /// travel separately in <see cref="QueuedMessagePayload.MergedSubmissionIds"/>.
    /// </summary>
    public ConversationIdentity? Identity => FirstPart.Identity;

    /// <summary>
    /// Every submission id this entry will answer, in part order. The single terminal event
    /// carries this list — without it, merged submissions would never terminate on the client.
    /// </summary>
    public IReadOnlyList<string> MergedSubmissionIds =>
        [.. _parts.Select(p => p.Identity?.SubmissionId).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!)];

    /// <param name="startedAt">When the turn that takes this entry begins.</param>
    /// <param name="processCold">
    /// True when the provider process holds no conversation at that moment. The entry's first part
    /// then carries its history (<see cref="QueuedMessagePart.ColdStartTask"/>); the parts after it
    /// follow in the same prompt and stay as they were built.
    /// </param>
    public QueuedMessagePayload BuildPayload(DateTimeOffset startedAt, bool processCold = false) =>
        new(
            Task: BuildCombinedTask(startedAt, processCold),
            DisplayText: DisplayText,
            IsSessionTask: IsSessionTask,
            Source: Source,
            RelaySender: RelaySender,
            CorrelationId: CorrelationId,
            TaskId: TaskId,
            Images: _parts.SelectMany(part => part.Images ?? []).ToList(),
            Documents: _parts.SelectMany(part => part.Documents ?? []).ToList(),
            UserId: UserId,
            Identity: Identity,
            MergedSubmissionIds: MergedSubmissionIds);

    private string BuildCombinedTask(DateTimeOffset startedAt, bool processCold)
    {
        var firstTask = _parts[0].TaskAtTurnStart(processCold);
        if (_parts.Count == 1)
            return firstTask;

        var lines = new List<string>
        {
            $"[This batch of {_parts.Count} messages waited {FormatElapsed(startedAt - QueuedAt)} in queue before this turn began]",
            firstTask
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
    long UserId,
    ConversationIdentity? Identity = null,
    IReadOnlyList<string>? MergedSubmissionIds = null);
