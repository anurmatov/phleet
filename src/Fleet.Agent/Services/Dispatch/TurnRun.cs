using System.Threading.Channels;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.Dispatch;

/// <summary>What every turn runs with: the same for each turn of one <see cref="TaskManager"/>.</summary>
internal sealed record TurnRunDependencies(
    AgentOptions AgentConfig,
    IAgentExecutor Executor,
    SessionManager Sessions,
    ILogger Logger,
    InjectionOutcomeCounter InjectionCounter,
    IMessageSink Sink,
    PrimaryHumanDedup PrimaryDedup,
    QueueIntake Intake,
    IDispatchHost Host,
    string? TelegramAttachmentDir,
    TimeProvider Time);

/// <summary>
/// One registered turn, from its first executor read to its terminal event: the executor loop,
/// the chat output, the relay callback, merged continuations and the resume after a process exit.
/// <see cref="TaskManager"/> creates and runs it on the turn's own task, after admission has
/// registered the turn. It never decides whether a turn may start and never takes the admission gate.
/// </summary>
internal sealed class TurnRun(
    TurnRunDependencies deps,
    long chatId, int taskId, string task,
    bool isSessionTask, TaskSource source, string? relaySender, string? correlationId, string? relayTaskId,
    IReadOnlyList<MessageImage>? images, IReadOnlyList<MessageDocument>? documents,
    ConversationIdentity identity, CancellationToken ct)
{
    private readonly AgentOptions _agentConfig = deps.AgentConfig;
    private readonly IAgentExecutor _executor = deps.Executor;
    private readonly SessionManager _sessions = deps.Sessions;
    private readonly ILogger _logger = deps.Logger;
    private readonly InjectionOutcomeCounter _injectionCounter = deps.InjectionCounter;
    private readonly IMessageSink _sink = deps.Sink;
    private readonly PrimaryHumanDedup _primaryDedup = deps.PrimaryDedup;
    private readonly QueueIntake _intake = deps.Intake;
    private readonly IDispatchHost _host = deps.Host;
    private readonly TimeProvider _time = deps.Time;
    private readonly string? _attachmentDir = deps.TelegramAttachmentDir;

    private readonly ChatTaskState _state = deps.Host.GetChatState(chatId);
    private readonly OutboundOrigin _origin = TaskManager.OriginOf(source);

    /// <summary>The identity this turn's events carry. A continuation and a resume each mint a new one.</summary>
    private ConversationIdentity _identity = identity;

    private string _currentTask = task;
    private IReadOnlyList<MessageImage>? _currentImages = images;
    private IReadOnlyList<MessageDocument>? _currentDocuments = documents;
    private int _injectedIntoLastTurn;

    private string? _lastResult;
    private string? _lastError;
    private int _significantUpdates;
    private readonly List<string> _allAssistantTexts = [];
    private ExecutionStats? _stats;
    private bool _errorResult;
    private bool _processExitResult;
    private List<(string Name, string Args)> _toolCalls = [];

    /// <summary>The inbox for this task, to receive mid-execution messages.</summary>
    private ChannelReader<MidTurnMessage>? _inboxReader;

    /// <summary>The silence limit for the executor read in flight; a fresh one for every read.</summary>
    private TurnStallWatch? _stall;

    /// <summary>
    /// Starts the silence limit for one read of the executor's event stream. See
    /// <see cref="TurnStallWatch"/>; <c>Agent:TurnStallTimeoutMinutes</c> = 0 disables it.
    /// </summary>
    private TurnStallWatch WatchForStall(CancellationToken turnToken) =>
        new(TimeSpan.FromMinutes(_agentConfig.TurnStallTimeoutMinutes), turnToken, _time);

    private string Prefix() => _state.Count > 1 ? $"[#{taskId}] " : "";

    private void PublishEvent<TPayload>(long chat, string kind, ConversationIdentity? eventIdentity, TPayload? payload)
        where TPayload : class => _host.PublishEvent(chat, kind, eventIdentity, payload);

    private void PublishTerminal<TPayload>(long chat, int turnTaskId, string kind, ConversationIdentity? eventIdentity, TPayload? payload)
        where TPayload : class => _host.PublishTerminal(chat, turnTaskId, kind, eventIdentity, payload);

    private void PublishTerminalError(long chat, int turnTaskId, ConversationIdentity? eventIdentity, ProtocolErrorCode code) =>
        _host.PublishTerminalError(chat, turnTaskId, eventIdentity, code);

    /// <summary>
    /// Runs the turn to its terminal event. A cancellation, a stall and an executor failure each
    /// end as a reported outcome here; only a failure while reporting one escapes to the caller.
    /// </summary>
    internal async Task RunAsync()
    {
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingTask = RunTypingLoopAsync(chatId, typingCts.Token, _identity);

        // Grab the inbox for this task to receive mid-execution messages
        _inboxReader = _state.Get(taskId)?.Inbox.Reader;

        try
        {
            while (true)
            {
                // #406: this executor turn consumes the late-result guard whatever its first event is.
                var steeringResidue = _host.TakeSteeringResidue();
                var firstEvent = true;
                _stall?.Dispose();
                _stall = WatchForStall(ct);
                await foreach (var progress in _executor.ExecuteAsync(_currentTask, _currentImages, _currentDocuments, _stall.Token))
                {
                    _stall.Touch();
                    var guardsThisEvent = steeringResidue && firstEvent;
                    firstEvent = false;
                    // Generic provider recovery has no original conversation/submission owner.
                    // Segment tags describe provenance, not ownership. Suppress before even a
                    // recovered FinalResult can enter this client's completion text or sinks.
                    // Scoped injected answers use DeliverInjectedTurnAnswersAsync instead.
                    if (progress.EventType == "recovered_answer"
                        && _identity.ChannelId == ConversationSouthAdapter.ClientChannel)
                    {
                        _injectionCounter.Increment(_agentConfig.Provider,
                            InjectionOutcomeCounter.UnownedClientRecoverySuppressed);
                        continue;
                    }

                    await HandleProgressAsync(progress, guardsThisEvent);
                }

                // The read is over. The watch must not outlive it: a timer that ran out later, while
                // the answer is being delivered, would make an unrelated cancellation look like a stall.
                _stall.Dispose();
                _stall = null;

                if (!await FinishExecutorTurnAsync())
                    break;
            }

            await DeliverOutcomeAsync();
        }
        catch (OperationCanceledException) when (_stall is { Stalled: true })
        {
            // Nobody cancelled this turn: the executor yielded nothing for the whole limit, so the
            // watchdog cancelled its read, and the executor's own cancel path has stopped the
            // provider process. Reported as a failure, never as "Task cancelled.", with a fixed
            // message that carries the limit and nothing else. The session is kept: the next
            // message resumes it.
            var errorMsg = TurnStallTimeout.FailureMessage(_stall.Timeout);
            _logger.LogError(
                "Task #{TaskId} for chat {ChatId} stalled: no executor event for {Minutes} minutes; turn stopped (source={Source}, provider={Provider})",
                taskId, chatId, (int)_stall.Timeout.TotalMinutes, source, _agentConfig.Provider);
            await SendWithStatsAsync($"{Prefix()}{errorMsg}");
            _host.RaiseTaskCompleted(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId, CompletionKind.Failed);
            PublishTerminalError(chatId, taskId, _identity, ProtocolErrorCode.ExecutorError);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Task #{TaskId} cancelled for chat {ChatId}", taskId, chatId);
            await SendWithStatsAsync($"{Prefix()}Task cancelled.");
            _host.RaiseTaskCompleted(chatId, "Task cancelled.", relaySender, source, false, correlationId, relayTaskId, CompletionKind.Failed);

            // The chat-visible string and the callback payload are unchanged; only the reason is
            // new, and it was recorded by the canceller before Cts.Cancel().
            var cancelReason = _state.Get(taskId)?.CancelReason ?? TurnCancelReason.Unknown;
            PublishTerminal(chatId, taskId, ConversationEventKind.TurnCanceled, _identity,
                new TurnCanceledPayload { Reason = cancelReason, MergedSubmissionIds = MergedIds() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing task #{TaskId} for chat {ChatId}", taskId, chatId);
            var errorMsg = $"Error: {ex.Message}";
            await SendWithStatsAsync($"{Prefix()}{errorMsg}");
            if (isSessionTask)
                _sessions.ClearSession(chatId);
            _host.RaiseTaskCompleted(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId, CompletionKind.Failed);

            // `internal` in particular must never carry the exception message.
            PublishTerminalError(chatId, taskId, _identity, ProtocolErrorCode.Internal);
        }
        finally
        {
            _stall?.Dispose();
            await typingCts.CancelAsync();
            await typingTask;
        }
    }

    /// <summary>One event of the executor stream: what it says to the chat, to the relay buffer and to a client.</summary>
    private async Task HandleProgressAsync(AgentProgress progress, bool guardsThisEvent)
    {
        if (isSessionTask && progress.SessionId is not null)
            _sessions.SetSession(chatId, progress.SessionId);

        if (progress.FinalResult is not null)
        {
            _lastResult = progress.FinalResult;
            _allAssistantTexts.Add(progress.FinalResult);
        }

        if (progress.Stats is not null)
            _stats = progress.Stats;

        if (progress.EventType == "error")
            _lastError = progress.Summary;

        if (progress.IsErrorResult)
            _errorResult = true;

        if (progress.IsProcessExit)
            _processExitResult = true;

        if (progress.EventType == "warning" && progress.IsSignificant)
        {
            // User-facing warning (e.g. provider capability notice) — deliver immediately
            await _sink.SendTextByOriginAsync(chatId, progress.Summary, _origin);
            var (noticeText, noticeTruncated) = ProtocolSanitizer.SanitizeAndBound(
                progress.Summary, ProtocolLimits.MaxNoticeTextChars, _attachmentDir);
            PublishEvent(chatId, ConversationEventKind.TurnNotice, _identity,
                new TurnNoticePayload { Text = noticeText, Truncated = noticeTruncated ? true : null });
        }
        else if (progress.EventType == "recovered_answer")
        {
            // Stale answer from the prior turn preserved during drain — deliver
            // immediately so it reaches the user before the new turn's response.
            // It is an answer, so it is journaled like one (#394).
            // #406: right after a steered turn's short drain, only `user` segments can be
            // a steering answer; drop those and deliver the rest exactly as today.
            var recovered = guardsThisEvent ? WithoutSteeringSegments(progress, taskId) : progress.Summary;
            if (recovered is not null)
            {
                await _sink.SendReplyByOriginAsync(chatId, new AgentReply(recovered), _origin);
                var (recoveredText, recoveredTruncated) = ProtocolSanitizer.SanitizeAndBound(
                    recovered, ProtocolLimits.MaxNoticeTextChars, _attachmentDir);
                PublishEvent(chatId, ConversationEventKind.TurnRecoveredAnswer, _identity,
                    new TurnRecoveredAnswerPayload { Text = recoveredText, Truncated = recoveredTruncated ? true : null });
            }
        }
        else if (progress.IsSignificant && progress.ToolName is not null)
        {
            _significantUpdates++;
            _toolCalls.Add((ShortenToolName(progress.ToolName), TruncateArgs(progress.ToolArgs ?? "{}", _agentConfig.ToolArgsTruncateLength)));
            // Suppress progress messages for check-ins — they may end up IDLE
            // Also suppress when SuppressToolMessages is configured (e.g. for non-technical users)
            if (!_agentConfig.SuppressToolMessages && source is not (TaskSource.CheckIn or TaskSource.DebouncedGroupBatch) && _significantUpdates % 5 == 1)
            {
                var summaryText = progress.Summary;
                if (progress.Summary.StartsWith("Using") && progress.ToolArgs is { } rawArgs)
                {
                    var argsSnippet = TruncateArgs(rawArgs, _agentConfig.ToolArgsTruncateLength);
                    summaryText = $"{progress.Summary}({argsSnippet})";
                }
                var htmlPrefix = "";
                if (_agentConfig.PrefixMessages && _agentConfig.ShortName.Length > 0)
                {
                    var displayName = $"{char.ToUpperInvariant(_agentConfig.ShortName[0])}{_agentConfig.ShortName[1..]}";
                    htmlPrefix = $"<b>{displayName}:</b>\n";
                }
                var encoded = System.Net.WebUtility.HtmlEncode($"{Prefix()}... {summaryText}");
                await _sink.SendHtmlTextByOriginAsync(chatId, $"{htmlPrefix}<blockquote expandable>{encoded}</blockquote>", _origin);
            }
            _host.RaiseToolUse(chatId, progress.ToolName, progress.Summary);

            // Only the tool NAME leaves the runtime. The Telegram string built a few
            // lines above appends truncated tool ARGUMENTS, and reusing it here is
            // specifically prohibited — arguments routinely carry absolute paths and
            // secret-shaped values.
            PublishEvent(chatId, ConversationEventKind.TurnProgress, _identity,
                new TurnProgressPayload
                {
                    Activity = ProgressActivity.Tool,
                    ToolName = ProtocolSanitizer.BoundToolName(progress.ToolName),
                });
        }
    }

    /// <summary>
    /// After one executor read: under the turn's dispatch lock, either sets up the next executor
    /// turn (a resume after a process exit, or a merged continuation of messages that waited in
    /// the inbox) and returns true, or closes the turn to live delivery and returns false.
    /// </summary>
    private async Task<bool> FinishExecutorTurnAsync()
    {
        var completingTask = _state.Get(taskId);
        if (completingTask is not null)
            await completingTask.TurnDispatchLock.WaitAsync(ct);

        try
        {
            if (_processExitResult && completingTask is not null && completingTask.InjectedMessagesForResume.Count > 0)
            {
                var redeliver = completingTask.InjectedMessagesForResume.ToList();
                completingTask.InjectedMessagesForResume.Clear();
                foreach (var injected in redeliver)
                    _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.PossibleDuplicateAfterResume);

                var firstRedelivery = redeliver[0];
                foreach (var injected in redeliver.Skip(1))
                    completingTask.Inbox.Writer.TryWrite(injected);

                // A redelivery keeps the conversation and increments the attempt — this is
                // the ONLY producer of attempt > 1, and it is in-process only. Nothing here
                // claims durable replay.
                //
                // The identity deliberately stays the ORIGINAL submission's, with only
                // TurnId and Attempt moving. Re-pointing it at firstRedelivery.Identity
                // would drop the original submission from the turn entirely — it would
                // never terminate — while duplicating the injected id, which the injection
                // accept site already recorded in MergedSubmissionIds. The resumed turn
                // answers both submissions, so it must carry both exactly once.
                _identity = _identity
                    .WithTurn(Guid.NewGuid().ToString("N"))
                    .WithAttempt(_identity.Attempt + 1);
                completingTask.Identity = _identity;
                completingTask.TerminalPublished = false;
                PublishEvent(chatId, ConversationEventKind.TurnStarted, _identity, new TurnStartedPayload());

                completingTask.InjectionCount = 0;
                // The process is gone, so this redelivery starts on a new one: with its history.
                _currentTask = !_executor.IsProcessWarm && firstRedelivery.ColdStartTask is { } coldStartTask
                    ? coldStartTask
                    : firstRedelivery.Task;
                _currentImages = firstRedelivery.Images;
                _currentDocuments = firstRedelivery.Documents;
                _lastError = null;
                _errorResult = false;
                _processExitResult = false;
                return true;
            }

            // After turn completes, atomically drain the fallback inbox before
            // closing the live-delivery window. This prevents a message from
            // being enqueued between TryRead(false) and Closed=true.
            // Consecutive compatible messages are coalesced into a single merged
            // turn so a burst that was refused by the final-answer gate doesn't
            // produce one reply per message.
            if (_inboxReader is not null && _inboxReader.TryRead(out var firstFallback))
            {
                _logger.LogInformation("Task #{TaskId}: coalescing inbox fallback messages into merged turn", taskId);

                // Drain all waiting messages so we can merge compatible ones.
                var drained = new List<MidTurnMessage> { firstFallback };
                while (_inboxReader.TryRead(out var additional))
                    drained.Add(additional);

                // Build the first merged group using the same CanMerge / MaxParts
                // semantics as the global chat-level queue coalescing path.
                var firstPart = QueueIntake.CreateQueuedPart(drained[0].Task, drained[0].DisplayText, drained[0].IsSessionTask,
                    drained[0].Source, drained[0].RelaySender, drained[0].CorrelationId, drained[0].TaskId,
                    drained[0].Images, drained[0].Documents, drained[0].UserId, drained[0].ArrivedAt,
                    drained[0].Identity, coldStartTask: drained[0].ColdStartTask);
                var mergedEntry = new QueuedMessage(chatId, firstPart);

                // overflowStart marks where the first group ends; default to "all fit".
                var overflowStart = drained.Count;
                for (var i = 1; i < drained.Count; i++)
                {
                    var part = QueueIntake.CreateQueuedPart(drained[i].Task, drained[i].DisplayText, drained[i].IsSessionTask,
                        drained[i].Source, drained[i].RelaySender, drained[i].CorrelationId, drained[i].TaskId,
                        drained[i].Images, drained[i].Documents, drained[i].UserId, drained[i].ArrivedAt,
                        drained[i].Identity, coldStartTask: drained[i].ColdStartTask);
                    if (!mergedEntry.TryAppendPart(part))
                    {
                        // This part doesn't fit in the first group (different source or
                        // MaxParts reached). Everything from here is overflow for the next turn.
                        overflowStart = i;
                        break;
                    }
                    // N parts → N-1 increments, matching TryAppendToPendingQueue convention.
                    _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.MergedIntoQueue);
                }

                // Write overflow back to the Inbox — not a local variable — so the
                // existing DrainInboxToGlobalQueue teardown recovers them if the task
                // is cancelled between merged turns. If the Inbox write fails (task
                // already closed) or completingTask is null, fall through to the global
                // queue — the same fallback the single-message sibling in
                // EnqueueForTurnEndAsync uses when its TryWrite returns false.
                for (var i = overflowStart; i < drained.Count; i++)
                {
                    if (completingTask is null || !completingTask.Inbox.Writer.TryWrite(drained[i]))
                        _intake.EnqueueMessage(chatId, drained[i], notifyUser: false);
                }

                foreach (var pending in drained.Take(overflowStart))
                    if (pending.Priority == TaskPriority.PrimaryHuman && pending.TelegramMessageId > 0)
                        _primaryDedup.Dispatched(chatId, pending.TelegramMessageId);

                // Deliver the completed turn's result before starting the next merged turn.
                if (_lastResult is not null && !_errorResult
                    && !_lastResult.Trim().Equals("IDLE", StringComparison.OrdinalIgnoreCase))
                {
                    await SendWithStatsAsync($"{Prefix()}{_lastResult}", isReply: true);
                }
                // ...then any injected message Claude answered in a turn of its own, so the
                // merged continuation below does not start while that turn is still running.
                await DeliverInjectedTurnAnswersAsync(completingTask?.InjectionCount ?? 0);
                await DiscardSteeringTurnAnswersAsync();

                // ...and terminate it on the event path too, under the OUTGOING identity,
                // before the new turn is minted below. This turn really did finish and
                // really did answer its submissions; without this the client sees
                // submission.accepted for them and then nothing, because the next terminal
                // event belongs to a different turn and lists a different submission set.
                if (!ProtocolSanitizer.IsIdleMarker(_lastResult))
                {
                    var (continuationText, continuationTruncated) = ProtocolSanitizer.SanitizeAndBound(
                        _lastResult ?? "", ProtocolLimits.MaxFinalTextChars, _attachmentDir);
                    PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, _identity,
                        new TurnFinalPayload
                        {
                            Text = continuationText,
                            Completion = _errorResult ? TurnCompletion.Incomplete : TurnCompletion.Completed,
                            IsPartial = _errorResult,
                            Truncated = continuationTruncated,
                            MergedSubmissionIds = MergedIds(),
                        });
                }
                _lastResult = null;

                // The executor turn that just ended may have ended with its process: then the
                // continuation starts cold, and its first message has to bring the history with it.
                var payload = mergedEntry.BuildPayload(DateTimeOffset.Now);
                _currentTask = payload.TaskAtTurnStart(processCold: !_executor.IsProcessWarm);
                _currentImages = payload.Images;
                _currentDocuments = payload.Documents;

                // A continuation is a NEW turn answering a NEW set of submissions: mint a
                // fresh TurnId, keep the conversation, and adopt the merged entry's
                // submission ids so each coalesced part terminates on the client.
                _identity = (payload.Identity ?? _identity).WithTurn(Guid.NewGuid().ToString("N"));
                if (completingTask is not null)
                {
                    completingTask.Identity = _identity;
                    var continuationSubmission = _identity.SubmissionId;
                    completingTask.ReplaceMergedSubmissionIds((payload.MergedSubmissionIds ?? [])
                        .Where(mergedId => !string.Equals(mergedId, continuationSubmission, StringComparison.Ordinal)));
                    // A continuation turn has not published its terminal event yet, so the
                    // reaper must be armed again for it.
                    completingTask.TerminalPublished = false;
                }
                PublishEvent(chatId, ConversationEventKind.TurnStarted, _identity, new TurnStartedPayload());

                if (completingTask is not null)
                {
                    completingTask.InjectionCount = 0;
                    completingTask.SteerCount = 0;
                    completingTask.InjectedMessagesForResume.Clear();
                }
                // Reset per-turn state but accumulate texts and stats
                _lastError = null;
                _errorResult = false;
                _processExitResult = false;
                return true;
            }

            if (completingTask is not null)
            {
                _injectedIntoLastTurn = completingTask.InjectionCount;
                completingTask.InjectedMessagesForResume.Clear();
                completingTask.Closed = true;
            }
        }
        finally
        {
            completingTask?.TurnDispatchLock.Release();
        }

        return false;
    }

    /// <summary>The closed turn's result: the chat reply, the relay callback and the terminal event, in that order.</summary>
    private async Task DeliverOutcomeAsync()
    {
        // IDLE is an internal contract marker — never deliver it to chat,
        // regardless of which task source emitted it.
        if (_lastResult is not null && _lastResult.Trim().Equals("IDLE", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Result is IDLE-only, suppressing output for chat {ChatId} (source={Source})", chatId, source);
            await DiscardSteeringTurnAnswersAsync();
            // Relay/bridge callers have a delegate step waiting — fire OnTaskCompleted with
            // CompletionKind.Idle so the workflow can advance instead of hanging to retry exhaustion.
            if (source is TaskSource.Relay or TaskSource.Bridge)
                _host.RaiseTaskCompleted(chatId, "", relaySender, source, false, correlationId, relayTaskId, CompletionKind.Idle);

            // Publish for EVERY conversation, not only client-owned ones. This is not
            // cosmetic: the terminal event is what sets TerminalPublished, and without it the
            // reaper would emit turn_reaped on every Telegram idle check-in — a fabricated
            // failure signal on the single most common no-op path in the runtime.
            //
            // Nothing a Telegram user sees changes: the chat output stays suppressed, the
            // relay callback keeps its existing Idle behaviour, and the published event is
            // simply counted as not_routed. The literal string "IDLE" never reaches text.
            PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, _identity,
                new TurnFinalPayload
                {
                    Text = "",
                    Completion = TurnCompletion.Idle,
                    IsPartial = false,
                    Truncated = false,
                    MergedSubmissionIds = MergedIds(),
                });
            return;
        }

        // Check-in and debounced group batch with no output at all: suppress the empty reply
        if (source is TaskSource.CheckIn or TaskSource.DebouncedGroupBatch && _lastResult is null)
        {
            _logger.LogInformation("{Source}: no output, suppressing for chat {ChatId}", source, chatId);
            // Same reasoning as the IDLE branch above — a no-output DebouncedGroupBatch is the
            // other path that would otherwise reach the reaper with nothing published.
            PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, _identity,
                new TurnFinalPayload
                {
                    Text = "",
                    Completion = TurnCompletion.Idle,
                    IsPartial = false,
                    Truncated = false,
                    MergedSubmissionIds = MergedIds(),
                });
            return;
        }

        if (_lastResult is not null)
        {
            // IsErrorResult covers max-turns exhaustion, auth failures, RPC errors and
            // executor crashes alike — so the marker must not assert a specific cause.
            // It previously claimed "hit limit" for every one of them, which sends
            // anyone debugging a dead executor after the wrong problem.
            var marker = _errorResult ? " [incomplete — executor reported an error]" : "";
            // Send the final text to Telegram, but relay ALL assistant texts
            // so that agent addresses from intermediate turns aren't lost
            await SendWithStatsAsync($"{Prefix()}{_lastResult}{marker}", isReply: true);
            await DeliverInjectedTurnAnswersAsync(_injectedIntoLastTurn);
            await DiscardSteeringTurnAnswersAsync();
            var fullText = string.Join("\n", _allAssistantTexts);
            // errorResult covers max-turns exhaustion (IsErrorResult from executor) — use
            // Incomplete so the workflow continuation loop can retry, not Failed which abandons.
            _host.RaiseTaskCompleted(chatId, fullText, relaySender, source, _errorResult, correlationId, relayTaskId, _errorResult ? CompletionKind.Incomplete : CompletionKind.Completed);

            // turn.final carries the CHAT-VISIBLE text (lastResult), matching what the main
            // reply path delivers. The concatenated all-texts form above stays exclusive to
            // the relay callback — the two genuinely differ, and collapsing them into one
            // canonical string would silently change one of the two consumers.
            var (finalText, finalTruncated) = ProtocolSanitizer.SanitizeAndBound(
                _lastResult, ProtocolLimits.MaxFinalTextChars, _attachmentDir);
            PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, _identity,
                new TurnFinalPayload
                {
                    Text = finalText,
                    Completion = _errorResult ? TurnCompletion.Incomplete : TurnCompletion.Completed,
                    IsPartial = _errorResult,
                    Truncated = finalTruncated,
                    MergedSubmissionIds = MergedIds(),
                });
        }
        else if (_lastError is not null)
        {
            if (isSessionTask)
                _sessions.ClearSession(chatId);
            var errorMsg = $"Task failed: {_lastError}";
            await SendWithStatsAsync($"{Prefix()}{errorMsg}");
            await DiscardSteeringTurnAnswersAsync();
            _host.RaiseTaskCompleted(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId, CompletionKind.Failed);

            // The client event carries the FIXED message for the code, never lastError.
            // Provider text and executor output routinely contain absolute paths, session ids
            // and credentials. Telegram still shows the raw string — that is the owner's own
            // channel and is unchanged.
            PublishTerminalError(chatId, taskId, _identity, ProtocolErrorCode.ExecutorError);
        }
        else if (_errorResult)
        {
            // A result event flagged as an error but carrying no text: the turn failed
            // and produced nothing. Reporting "Done!" here is what let a dead executor
            // look healthy — the error flag must survive even with no output to show.
            var errorMsg = "Task failed: executor reported an error and produced no output";
            _logger.LogError("Task #{TaskId} for chat {ChatId}: {Error}", taskId, chatId, errorMsg);
            await SendWithStatsAsync($"{Prefix()}{errorMsg}");
            await DiscardSteeringTurnAnswersAsync();
            _host.RaiseTaskCompleted(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId, CompletionKind.Failed);
            PublishTerminalError(chatId, taskId, _identity, ProtocolErrorCode.ExecutorError);
        }
        else
        {
            await SendWithStatsAsync($"{Prefix()}Done! (no text output)");
            await DiscardSteeringTurnAnswersAsync();
            _host.RaiseTaskCompleted(chatId, "Done! (no text output)", relaySender, source, false, correlationId, relayTaskId, CompletionKind.Completed);
            PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, _identity,
                new TurnFinalPayload
                {
                    Text = "",
                    Completion = TurnCompletion.Completed,
                    IsPartial = false,
                    Truncated = false,
                    MergedSubmissionIds = MergedIds(),
                });
        }
    }

    // Every submission this turn answers: its own, plus anything coalesced or injected into it.
    // Deduplicated, because a submission that appears twice is as wrong as one that is missing —
    // a client correlating terminal events would count the same answer twice.
    private IReadOnlyList<string> MergedIds()
    {
        var running = _state.Get(taskId);
        if (running is null) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var id in ((string[])[_identity.SubmissionId, .. running.SnapshotMergedSubmissionIds()]))
        {
            if (!string.IsNullOrEmpty(id) && seen.Add(id))
                ids.Add(id);
        }
        return ids;
    }

    private string StatsSuffix()
    {
        if (!_agentConfig.ShowStats || _stats is null) return "";
        if (_toolCalls.Count > 0)
            _stats.ToolCalls = _toolCalls.Select(t => new Models.ToolCallEntry(t.Name, t.Args)).ToList();
        return $"\n{_stats.Format()}";
    }

    private async Task SendWithStatsAsync(string content, bool isReply = false)
    {
        // Guarantee: this must NEVER throw. Terminal-state callers rely on it to
        // finish so that OnTaskCompleted runs and the bridge/relay gets its answer.
        // A failed Telegram echo (e.g., bot not a member of the chat) is non-fatal —
        // the authoritative response goes out via OnTaskCompleted → relay.
        try
        {
            var statsText = StatsSuffix();
            var toolBlock = _stats?.FormatToolBlock() ?? "";
            var htmlPrefix = "";
            if (toolBlock.Length > 0 && _agentConfig.PrefixMessages && _agentConfig.ShortName.Length > 0)
            {
                var displayName = $"{char.ToUpperInvariant(_agentConfig.ShortName[0])}{_agentConfig.ShortName[1..]}";
                htmlPrefix = $"<b>{displayName}:</b>\n";
            }
            var reply = new AgentReply(content, statsText, toolBlock) { HtmlPrefix = htmlPrefix };

            // Only a turn's answer is journaled, and only its body (#394). The status lines that
            // share this footer ("Task failed", "Task cancelled.", "Done! (no text output)")
            // go out composed exactly the same way on the sends that never journal.
            if (isReply)
                await _sink.SendReplyByOriginAsync(chatId, reply, _origin);
            else
                await _sink.SendComposedByOriginAsync(chatId, reply, _origin);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendWithStatsAsync: failed to deliver Telegram message for chat {ChatId} (task #{TaskId}) — continuing", chatId, taskId);
        }
    }

    // #369: an injected message that reached Claude after it had begun its final answer runs
    // as its own turn with its own result, after the executor already returned on the first.
    // Deliver that answer here, right after the turn's own answer and before its terminal
    // event, instead of leaving it to surface out-of-band on the user's next message. Only
    // after an injection — a turn without one never waits.
    private async Task DeliverInjectedTurnAnswersAsync(int injectedCount)
    {
        if (injectedCount <= 0) return;
        using var injectedStall = WatchForStall(ct);
        try
        {
        await foreach (var extra in _executor.ReadInjectedTurnAnswersAsync(injectedCount, injectedStall.Token))
        {
            injectedStall.Touch();
            if (extra.FinalResult is not { Length: > 0 } text || ProtocolSanitizer.IsIdleMarker(text))
                continue;

            _allAssistantTexts.Add(text);
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.AnsweredAsSeparateTurn);
            _logger.LogInformation(
                "Task #{TaskId}: delivering the answer to an injected message that ran as its own turn", taskId);

            // The extra turn's own stats, and none of the first turn's tool calls.
            var (turnStats, turnToolCalls) = (_stats, _toolCalls.ToList());
            _stats = extra.Stats;
            _toolCalls.Clear();
            var marker = extra.IsErrorResult ? " [incomplete — executor reported an error]" : "";
            await SendWithStatsAsync($"{Prefix()}{text}{marker}", isReply: true);
            (_stats, _toolCalls) = (turnStats, turnToolCalls);

            var (extraText, extraTruncated) = ProtocolSanitizer.SanitizeAndBound(
                text, ProtocolLimits.MaxNoticeTextChars, _attachmentDir);
            PublishEvent(chatId, ConversationEventKind.TurnRecoveredAnswer, _identity,
                new TurnRecoveredAnswerPayload { Text = extraText, Truncated = extraTruncated ? true : null });
        }
        }
        catch (OperationCanceledException) when (injectedStall.Stalled)
        {
            // The turn's own answer is already delivered. Only this extra answer is given up on.
            _logger.LogError(
                "Task #{TaskId} for chat {ChatId}: no executor event for {Minutes} minutes while reading the answer to an injected message; stopped waiting for it",
                taskId, chatId, (int)injectedStall.Timeout.TotalMinutes);
        }
    }

    // #406: a steering copy that ran as its own Claude turn produced an answer that belongs to
    // nobody — not the workflow, not the human (who gets a reply turn), not the next task. Read
    // those turns under this turn's own origin and drop every answer. Only after a terminal
    // result with a live process: never on cancellation, an exception or process exit.
    private async Task DiscardSteeringTurnAnswersAsync()
    {
        var steerCount = _state.Get(taskId)?.SteerCount ?? 0;
        if (steerCount <= 0 || _processExitResult) return;
        var read = 0;
        using var steerStall = WatchForStall(ct);
        try
        {
            await foreach (var _ in _executor.ReadInjectedTurnAnswersAsync(steerCount, steerStall.Token))
            {
                steerStall.Touch();
                read++;
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.SteerAnswerDiscarded);
                _logger.LogInformation("Task #{TaskId}: discarded the answer to a steering message that ran as its own turn", taskId);
            }
        }
        catch (OperationCanceledException) when (steerStall.Stalled)
        {
            // The executor stopped its process when the read was cancelled, so no late answer
            // can follow: nothing to guard the next turn against.
            _logger.LogError(
                "Task #{TaskId} for chat {ChatId}: no executor event for {Minutes} minutes while discarding a steering answer; stopped waiting for it",
                taskId, chatId, (int)steerStall.Timeout.TotalMinutes);
            return;
        }
        // A steering own turn that did not start in time may still finish before the next send;
        // its text then arrives as that turn's leading recovered_answer, tagged `user`.
        if (read < steerCount) _host.LeaveSteeringResidue();
    }

    /// <summary>
    /// A leading recovered answer right after a steered turn's short drain (#406): drop only the
    /// segments a stdin-started turn produced: in that window they can only be steering answers. Null when
    /// nothing remains. Unsegmented text has unknown provenance and is delivered unchanged.
    /// </summary>
    private string? WithoutSteeringSegments(AgentProgress progress, int taskId)
    {
        if (progress.RecoveredSegments is not { } segments) return progress.Summary;
        var dropped = segments.Count(s => s.Origin == RecoveredSegment.User);
        if (dropped == 0) return progress.Summary;
        for (var i = 0; i < dropped; i++)
        {
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.SteerAnswerDiscarded);
            _logger.LogInformation("Task #{TaskId}: discarded a late steering answer from a recovered answer", taskId);
        }
        var kept = segments.Where(s => s.Origin != RecoveredSegment.User).Select(s => s.Text).ToList();
        return kept.Count > 0 ? string.Join("\n", kept) : null;
    }

    private async Task RunTypingLoopAsync(long chatId, CancellationToken ct, ConversationIdentity? identity = null)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _sink.SendTypingAsync(chatId, ct);
                PublishEvent(chatId, ConversationEventKind.TurnProgress, identity,
                    new TurnProgressPayload { Activity = ProgressActivity.Typing });
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
        catch { }
    }

    private static string ShortenToolName(string name)
    {
        if (!name.StartsWith("mcp__")) return name;
        var lastSep = name.LastIndexOf("__");
        return lastSep > 4 ? name[(lastSep + 2)..] : name;
    }

    private static string TruncateArgs(string args, int maxLength = 300) =>
        args.Length <= maxLength ? args : args[..maxLength] + "...";
}
