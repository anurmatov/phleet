using System.Collections.Concurrent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.Dispatch;

/// <summary>
/// Everything that happens to a message whose chat, or whose agent, already has a turn running:
/// injecting it into that turn, offering a verified human's message to a running workflow turn
/// as a steering copy (#406), and holding it for the turn's end when neither is possible.
/// </summary>
/// <remarks>
/// Each decision is made under the running turn's <c>TurnDispatchLock</c>, which is the outermost
/// lock of the order documented on the admission gate in <see cref="TaskManager"/>.
/// </remarks>
internal sealed class MidTurnDelivery(
    ConcurrentDictionary<long, ChatTaskState> chatTasks,
    AgentOptions agentConfig,
    IAgentExecutor executor,
    InjectionOutcomeCounter injectionCounter,
    PrimaryHumanDedup primaryDedup,
    QueueIntake intake,
    IMessageSink sink,
    ILogger logger,
    IDispatchHost host)
{
    private const int MaxMidTurnInjectionsPerTurn = 3;

    private readonly ConcurrentDictionary<long, ChatTaskState> _chatTasks = chatTasks;
    private readonly AgentOptions _agentConfig = agentConfig;
    private readonly IAgentExecutor _executor = executor;
    private readonly InjectionOutcomeCounter _injectionCounter = injectionCounter;
    private readonly PrimaryHumanDedup _primaryDedup = primaryDedup;
    private readonly QueueIntake _intake = intake;
    private readonly IMessageSink _sink = sink;
    private readonly ILogger _logger = logger;
    private readonly IDispatchHost _host = host;

    /// <summary>
    /// The one running workflow turn a verified human may steer (#406), across every runtime chat
    /// key: a relay/bridge turn runs under the relay's chat id, never the human's. One turn runs
    /// at a time, so several candidates is a broken invariant and steers nothing.
    /// </summary>
    private bool TryGetSteerableTurn(out RunningTask running)
    {
        var candidates = _chatTasks.Values.SelectMany(s => s.Snapshot())
            .Where(t => t.Source is TaskSource.Relay or TaskSource.Bridge && t.IsSessionTask && !t.Closed && t.Launched)
            .Take(2).ToList();
        if (candidates.Count > 1)
            _logger.LogWarning("Human steering skipped: more than one running Relay or Bridge turn");
        running = candidates.Count == 1 ? candidates[0] : null!;
        return candidates.Count == 1;
    }

    /// <summary>
    /// Queue first, then steer (#406): the message is already queued for its own reply turn; this
    /// only offers a text-only copy to the running workflow turn. It never touches that turn's
    /// Inbox, resume list, merged submissions, injection count, dedup or binding.
    /// </summary>
    internal async Task SteerRunningTurnAsync(long chatId, long userId, QueuedMessagePart part,
        IReadOnlyList<MessageImage>? images, IReadOnlyList<MessageDocument>? documents)
    {
        _host.SteerAttemptStartingForTest?.Invoke();
        if (!TryGetSteerableTurn(out var running)) return;
        string outcome;
        await running.TurnDispatchLock.WaitAsync();
        try
        {
            if (running.Closed) outcome = "closed";
            else if (running.SteeringOwner is { } owner && owner != (chatId, userId)) outcome = "refused_other_human";
            else if (running.SteerCount >= MaxMidTurnInjectionsPerTurn) outcome = "cap";
            else if (_host.ExecutorBusyElsewhere) outcome = "executor_busy_elsewhere";
            else
            {
                var attachments = (images?.Count ?? 0) + (documents?.Count ?? 0);
                var result = await _executor.TryInjectMessageAsync(MidTurnText.FormatSteeringMessage(part.Task, attachments),
                    null, null, running.Cts.Token);
                outcome = result.Status switch
                {
                    MidTurnInjectionStatus.Injected => "injected",
                    MidTurnInjectionStatus.NoActiveTurn => "no_active_turn",
                    MidTurnInjectionStatus.Unsupported => "unsupported",
                    _ => "failed",
                };
                if (result.Status == MidTurnInjectionStatus.Injected)
                {
                    running.SteeringOwner ??= (chatId, userId);
                    running.SteerCount++;
                    part.Steered = true;
                }
            }
        }
        catch (Exception ex)
        {
            outcome = "failed";
            _logger.LogWarning("Human steering into task #{TaskId} failed: {Type}", running.Id, ex.GetType().Name);
        }
        finally
        {
            running.TurnDispatchLock.Release();
        }

        _injectionCounter.Increment(_agentConfig.Provider, outcome switch
        {
            "injected" => InjectionOutcomeCounter.SteeredNonHumanTurn,
            "refused_other_human" => InjectionOutcomeCounter.SteerRefusedOtherHuman,
            _ => InjectionOutcomeCounter.SteerNotDelivered,
        });
        _logger.LogInformation("Steered human message into running {Source} task #{TaskId}: {Outcome}",
            running.Source, running.Id, outcome);
    }

    /// <summary>
    /// Delivers a message that arrived while its chat's turn is running, and reports how it was
    /// accepted.
    /// </summary>
    /// <remarks>
    /// The acceptance is published while the turn's dispatch lock is still held. Every way the
    /// submission can then show up in a turn's events needs that lock first: the running turn
    /// takes it before it finishes or starts a continuation, and the drain below is run only after
    /// the acceptance is out. Published by the caller after this returned, it could follow the
    /// turn it was accepted into.
    /// </remarks>
    internal async Task<TaskDispatchOutcome> DeliverMidTurnMessageAsync(long chatId, RunningTask running, MidTurnMessage message)
    {
        _host.DispatchCheckpointForTest?.Invoke("mid_turn_delivery");
        await running.TurnDispatchLock.WaitAsync();
        try
        {
            var outcome = await DeliverMidTurnUnderLockAsync(chatId, running, message);
            _host.ReportDisposition(chatId, message.Identity, outcome);
            // The closed turn may have finished its own drain already, on an empty queue. Then
            // nothing is left to start this entry, so try here; it does nothing while a turn
            // is running or being handed over.
            if (outcome == TaskDispatchOutcome.Queued && running.Closed) _host.DrainQueue();
            return outcome;
        }
        finally
        {
            running.TurnDispatchLock.Release();
        }
    }

    private async Task<TaskDispatchOutcome> DeliverMidTurnUnderLockAsync(long chatId, RunningTask running, MidTurnMessage message)
    {
        try
        {
            if (running.Closed)
            {
                // Includes the injected-answer read window: the turn is closed but still this chat's.
                var enqueued = _intake.EnqueueMessage(chatId, message, notifyUser: _intake.BusyNoticeApplies(chatId, message.Source));
                if (enqueued) _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
                return enqueued ? TaskDispatchOutcome.Queued : TaskDispatchOutcome.QueueFull;
            }

            if (running.InjectionCount >= MaxMidTurnInjectionsPerTurn)
            {
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
                return await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: false);
            }

            if (_host.ExecutorBusyElsewhere)
            {
                // The executor's live turn may be the warmup ping or a /run command, not this one.
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
                _logger.LogInformation(
                    "Mid-turn injection skipped for chat {ChatId}: the executor is running a turn started outside the task queue; queued for turn-end delivery",
                    chatId);
                return await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: false);
            }

            var result = await _executor.TryInjectMessageAsync(MidTurnText.FormatInjectedMessage(message.Task),
                message.Images, message.Documents, running.Cts.Token);

            if (result.Status == MidTurnInjectionStatus.Injected)
            {
                if (message.Priority == TaskPriority.PrimaryHuman && message.TelegramMessageId > 0)
                    _primaryDedup.Dispatched(chatId, message.TelegramMessageId);
                running.InjectionCount++;
                running.InjectedMessagesForResume.Add(message);
                // The injected message's answer arrives inside THIS turn's terminal event, so its
                // submission id has to ride along in mergedSubmissionIds — otherwise the client
                // sees submission.accepted{injected} and then nothing, forever.
                if (message.Identity is { } injectedIdentity)
                    running.AddMergedSubmissionId(injectedIdentity.SubmissionId);
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.Injected);
                _logger.LogInformation("Injected mid-turn message into running task #{TaskId} for chat {ChatId}", running.Id, chatId);
                return TaskDispatchOutcome.Injected;
            }

            var counterOutcome = result.Status == MidTurnInjectionStatus.Failed
                ? InjectionOutcomeCounter.FailedThenQueued
                : InjectionOutcomeCounter.DegradedToQueue;
            _injectionCounter.Increment(_agentConfig.Provider, counterOutcome);
            _logger.LogInformation("Mid-turn injection unavailable for chat {ChatId} (status={Status}, error={Error}); queued for turn-end delivery",
                chatId, result.Status, result.Error);
            return await EnqueueForTurnEndAsync(chatId, running, message,
                notifyUser: result.Status == MidTurnInjectionStatus.Failed && _intake.BusyNoticeApplies(chatId, message.Source));
        }
        catch (Exception ex)
        {
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.FailedThenQueued);
            _logger.LogWarning(ex, "Mid-turn injection failed for chat {ChatId}; queued for turn-end delivery", chatId);
            return await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: _intake.BusyNoticeApplies(chatId, message.Source));
        }
    }

    internal async Task<TaskDispatchOutcome> DeferUntilTurnEndAsync(long chatId, RunningTask running, MidTurnMessage message, bool notifyUser)
    {
        await running.TurnDispatchLock.WaitAsync();
        try
        {
            // Check-ins are not conversational corrections, so do not fold them into
            // the current response chain. Queue them to start only after the current
            // task has sent its own terminal response and DrainQueue runs.
            var enqueued = _intake.EnqueueMessage(chatId, message, notifyUser);
            if (enqueued) _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
            var outcome = enqueued ? TaskDispatchOutcome.Queued : TaskDispatchOutcome.QueueFull;
            // Accepted before any drain, for the reason given on DeliverMidTurnMessageAsync.
            _host.ReportDisposition(chatId, message.Identity, outcome);
            // Same as a message that found its turn closed: that turn's drain may already be behind us.
            if (enqueued && running.Closed) _host.DrainQueue();
            return outcome;
        }
        finally
        {
            running.TurnDispatchLock.Release();
        }
    }

    internal async Task<TaskDispatchOutcome> EnqueueForTurnEndAsync(long chatId, RunningTask running, MidTurnMessage message, bool notifyUser)
    {
        // Relay/Bridge messages carry completion callbacks tied to their correlationId/taskId.
        // The turn-continuation loop only threads Task/Images/Documents across turns; a
        // Relay/Bridge merged into the Inbox would lose its callback and hang the caller.
        // Route them directly to the chat-level queue, preserving completeBridgeOnDrop=true
        // so the callback still fires if the queue is full.
        if (message.Source is TaskSource.Relay or TaskSource.Bridge
            || running.Source is not (TaskSource.UserMessage or TaskSource.DebouncedGroupBatch))
        {
            // Defence in depth: fallback delivery must not turn a workflow's Inbox into
            // a human continuation, even if a caller reaches this helper directly.
            if (message.Source == TaskSource.CheckIn)
                return TaskDispatchOutcome.Dropped;

            var relayPart = QueueIntake.CreateQueuedPart(message.Task, message.DisplayText, message.IsSessionTask, message.Source,
                message.RelaySender, message.CorrelationId, message.TaskId, message.Images, message.Documents,
                message.UserId, message.ArrivedAt, message.Identity, message.Priority, message.TelegramMessageId,
                message.ColdStartTask);
            var enqueued = _intake.EnqueueFreshMessage(chatId, relayPart, notifyUser, completeBridgeOnDrop: true);
            return enqueued ? TaskDispatchOutcome.Queued : TaskDispatchOutcome.QueueFull;
        }

        if (!running.Inbox.Writer.TryWrite(message))
        {
            var enqueued = _intake.EnqueueMessage(chatId, message, notifyUser);
            return enqueued ? TaskDispatchOutcome.Queued : TaskDispatchOutcome.QueueFull;
        }

        if (notifyUser && !(_agentConfig.SuppressToolMessages && chatId < 0))
        {
            try
            {
                await _sink.SendTextByOriginAsync(chatId, "I'm busy right now — your message is queued. I'll get to it once my current turn finishes.", TaskManager.OriginOf(message.Source));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send mid-turn queue notice for chat {ChatId}", chatId);
            }
        }
        _host.RaiseStatusChanged();
        return TaskDispatchOutcome.Queued;
    }

    internal async Task SteerAndNotifyAsync(long chatId, long userId, QueuedMessagePart part,
        IReadOnlyList<MessageImage>? images, IReadOnlyList<MessageDocument>? documents,
        QueuedMessage entry, int position, bool noticeDue)
    {
        try
        {
            await SteerRunningTurnAsync(chatId, userId, part, images, documents);
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Warning, ex, "Human steering attempt for chat {ChatId} escaped its handler", chatId);
        }
        finally
        {
            _host.SteeringNoticePendingForTest?.Invoke();
            if (noticeDue) await SendSteeringQueueNoticeAsync(entry, chatId, position, part.Source);
        }
    }

    private async Task SendSteeringQueueNoticeAsync(QueuedMessage entry, long chatId, int position, TaskSource source)
    {
        var acquired = false;
        Task? send = null;
        try
        {
            _host.NoticeLockWaitThrowsForTest?.Invoke();
            await entry.QueueDispatchLock.WaitAsync();
            acquired = true;
            if (entry.Claimed)
                TryLog(LogLevel.Information, null, "Steering notice skipped: queue entry already dispatched");
            else
            {
                var text = entry.FirstPart.Steered ? MidTurnText.SteeringDeliveredNotice
                    : $"I'm busy right now — your message is queued (position {position}). I'll get to it once my current task finishes.";
                entry.BusyNoticeSent = true;
                send = _sink.SendTextByOriginAsync(chatId, text, TaskManager.OriginOf(source));
            }
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Warning, ex, "Failed to send steering queue notice for chat {ChatId}", chatId);
        }
        finally
        {
            if (acquired) entry.QueueDispatchLock.Release();
        }
        // A pre-send throw leaves send null: there is no retry or fallback notice.
        if (send is not null)
        {
            try { await send; }
            catch (Exception ex)
            {
                TryLog(LogLevel.Warning, ex, "Failed to send steering queue notice for chat {ChatId}", chatId);
            }
        }
    }

    private void TryLog(LogLevel level, Exception? ex, string template, params object?[] args)
    {
        try { _logger.Log(level, ex, template, args); }
        catch { /* Best effort only: no logging, notice retry or state change. */ }
    }
}
