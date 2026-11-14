using System.Collections.Concurrent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.Dispatch;

/// <summary>What became of an attempt to join the pending queue entry for a chat.</summary>
internal enum PendingQueueResult
{
    NoPending,
    Merged,
    EnqueueFresh,
}

/// <summary>
/// Puts a waiting message into the dispatch queue and tells whoever has to know: the log, the
/// sender ("I'm busy", "queue is full"), the orchestrator status and, for a dropped workflow
/// task, its caller. It decides nothing about whether a turn may start; the admission gate in
/// <see cref="TaskManager"/> does, and enqueues under the gate itself when the two must be one step.
/// </summary>
internal sealed class QueueIntake(
    DispatchQueue messageQueue,
    ConcurrentDictionary<long, ChatTaskState> chatTasks,
    AgentOptions agentConfig,
    InjectionOutcomeCounter injectionCounter,
    IMessageSink sink,
    ILogger logger,
    IDispatchHost host)
{
    private readonly DispatchQueue _messageQueue = messageQueue;
    private readonly ConcurrentDictionary<long, ChatTaskState> _chatTasks = chatTasks;
    private readonly AgentOptions _agentConfig = agentConfig;
    private readonly InjectionOutcomeCounter _injectionCounter = injectionCounter;
    private readonly IMessageSink _sink = sink;
    private readonly ILogger _logger = logger;
    private readonly IDispatchHost _host = host;

    internal void DrainInboxToGlobalQueue(long chatId, RunningTask running)
    {
        while (running.Inbox.Reader.TryRead(out var pending))
        {
            if (EnqueueMessage(chatId, pending, notifyUser: false))
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
        }
    }

    internal bool EnqueueMessage(long chatId, MidTurnMessage message, bool notifyUser)
    {
        var part = CreateQueuedPart(message.Task, message.DisplayText, message.IsSessionTask, message.Source,
            message.RelaySender, message.CorrelationId, message.TaskId, message.Images, message.Documents, message.UserId,
            message.ArrivedAt, message.Identity, message.Priority, message.TelegramMessageId, message.ColdStartTask);

        var pendingResult = TryAppendToPendingQueue(chatId, part);
        if (pendingResult == PendingQueueResult.Merged)
        {
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.MergedIntoQueue);
            _host.RaiseStatusChanged();
            return true;
        }

        return EnqueueFreshMessage(chatId, part, notifyUser, completeBridgeOnDrop: false);
    }

    internal static IEnumerable<long> KeysOfPart(QueuedMessagePart part) =>
        (part.PrimaryMessageIds ?? []).Concat(part.Priority == TaskPriority.PrimaryHuman && part.TelegramMessageId > 0 ? [part.TelegramMessageId] : []);

    internal static QueuedMessagePart CreateQueuedPart(string task, string displayText, bool isSessionTask, TaskSource source,
        string? relaySender, string? correlationId, string? taskId, IReadOnlyList<MessageImage>? images,
        IReadOnlyList<MessageDocument>? documents, long userId, DateTimeOffset? arrivedAt = null,
        ConversationIdentity? identity = null, TaskPriority priority = TaskPriority.Routine, long telegramMessageId = 0,
        string? coldStartTask = null)
    {
        var senderDisplay = relaySender ?? source.ToString().ToLowerInvariant();
        var arrival = arrivedAt?.ToLocalTime() ?? DateTimeOffset.Now;
        return new QueuedMessagePart(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
            images, documents, userId, arrival, senderDisplay, identity, priority, telegramMessageId,
            ColdStartTask: coldStartTask);
    }

    internal PendingQueueResult TryAppendToPendingQueue(long chatId, QueuedMessagePart part)
    {
        if (_messageQueue.TryAppend(chatId, part)) return PendingQueueResult.Merged;
        return _messageQueue.HasPending(chatId) ? PendingQueueResult.EnqueueFresh : PendingQueueResult.NoPending;
    }

    internal bool EnqueueFreshMessage(long chatId, QueuedMessagePart part, bool notifyUser, bool completeBridgeOnDrop) =>
        EnqueueFreshMessage(chatId, part, notifyUser, completeBridgeOnDrop, out _, out _);

    /// <summary>
    /// Enqueues outside the admission gate, for callers that already know a turn is running or a
    /// handoff is in flight: a turn-end fallback or an overflow under the running turn's dispatch
    /// lock, or a message that found a pending entry it could not join. A caller that cannot know
    /// that (the turn it holds may already have gone) follows up with a queue drain.
    /// </summary>
    internal bool EnqueueFreshMessage(long chatId, QueuedMessagePart part, bool notifyUser, bool completeBridgeOnDrop,
        out QueuedMessage? queued, out int queuePos, bool deferNotice = false)
    {
        var enqueued = _messageQueue.TryEnqueue(chatId, part, out queued, out queuePos);
        return AfterEnqueue(enqueued, chatId, part, notifyUser, completeBridgeOnDrop, queued, queuePos, deferNotice);
    }

    internal bool BusyNoticeDue(long chatId, bool notifyUser, bool deferNotice) =>
        !deferNotice && notifyUser && !(_agentConfig.SuppressToolMessages && chatId < 0);

    /// <summary>Logs, notices and callbacks for an enqueue attempt that has already been decided.</summary>
    internal bool AfterEnqueue(bool enqueued, long chatId, QueuedMessagePart part, bool notifyUser, bool completeBridgeOnDrop,
        QueuedMessage? queued, int queuePos, bool deferNotice)
    {
        if (!enqueued)
        {
            _logger.LogWarning("Message queue full ({Max}) — dropping incoming task from chat {ChatId}", DispatchQueue.MaxQueueDepth, chatId);
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DroppedAtQueueCap);
            // DebouncedGroupBatch drops silently — the notice is noise for automated checks.
            if (part.Source != TaskSource.DebouncedGroupBatch)
                _ = _sink.SendTextByOriginAsync(chatId, $"Queue is full ({DispatchQueue.MaxQueueDepth} messages waiting). Please wait for tasks to complete.", TaskManager.OriginOf(part.Source));
            // Production relay passes taskId but no correlationId — gate on either so relay
            // completions are not dead code when the source has only a taskId.
            if (completeBridgeOnDrop && part.Source is TaskSource.Bridge or TaskSource.Relay
                    && (part.CorrelationId is not null || part.TaskId is not null))
                _host.RaiseTaskCompleted(chatId, "agent queue full",
                    part.Source == TaskSource.Bridge ? "bridge" : part.RelaySender,
                    part.Source, false, part.CorrelationId, part.TaskId, CompletionKind.Failed);
            return false;
        }

        _logger.LogInformation("Message queued (position {Pos}) for chat {ChatId}; queue entries={EntryCount}, max parts per entry={MaxParts}",
            queuePos, chatId, _messageQueue.Count, QueuedMessage.MaxParts);
        if (BusyNoticeDue(chatId, notifyUser, deferNotice))
        {
            queued!.BusyNoticeSent = true;
            _ = _sink.SendTextByOriginAsync(chatId, $"I'm busy right now — your message is queued (position {queuePos}). I'll get to it once my current task finishes.", TaskManager.OriginOf(part.Source));
        }
        _host.RaiseStatusChanged();
        return true;
    }

    /// <summary>
    /// Whether a message from <paramref name="chatId"/> that has to wait gets the busy notice (#369).
    /// It does only when the agent is busy on work that did not come from this chat: a turn or queued
    /// entry for another chat, or a Relay/Bridge directive. Waiting behind the chat's own turn,
    /// including its injected-answer read window, is silent. Relay/Bridge arrivals keep the notice.
    /// </summary>
    internal bool BusyNoticeApplies(long chatId, TaskSource source)
    {
        if (source is TaskSource.Relay or TaskSource.Bridge)
            return true;

        foreach (var (taskChatId, state) in _chatTasks)
        {
            if (state.Snapshot().Any(t => !IsChatsOwnWork(chatId, taskChatId, t.Source)))
                return true;
        }

        return _messageQueue.Snapshot().Any(q => !IsChatsOwnWork(chatId, q.ChatId, q.Source));
    }

    private static bool IsChatsOwnWork(long chatId, long workChatId, TaskSource workSource) =>
        workChatId == chatId && workSource is not (TaskSource.Relay or TaskSource.Bridge);
}
