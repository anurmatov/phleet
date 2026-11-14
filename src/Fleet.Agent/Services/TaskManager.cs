using System.Collections.Concurrent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services.Dispatch;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Admission and coordination for the agent's one-turn-at-a-time runtime: it decides whether an
/// arriving message runs, joins the running turn, or waits, and it hands the queue's next entry
/// to a turn when one ends. The turn itself, the commands over running turns, mid-turn delivery
/// and queue notices live in <c>Services/Dispatch</c>; this type owns the admission gate, the
/// running set and the one place that publishes conversation events.
/// </summary>
public sealed class TaskManager
{
    private readonly AgentOptions _agentConfig;
    private readonly IAgentExecutor _executor;
    private readonly SessionManager _sessions;
    private readonly ILogger<TaskManager> _logger;
    private readonly InjectionOutcomeCounter _injectionCounter;

    private readonly ConcurrentDictionary<long, ChatTaskState> _chatTasks = new();
    // taskId dedup: tracks bridge taskIds that are currently in-flight
    private readonly ConcurrentDictionary<string, bool> _activeTaskIds = new();

    private readonly DispatchQueue _messageQueue;
    private readonly PrimaryHumanDedup _primaryDedup = new();
    private readonly QueueLaneCounter _queueCounter;
    private const int MaxQueueDepth = DispatchQueue.MaxQueueDepth;

    /// <summary>
    /// The one gate for "is a turn running?". An admission decides under it and, in the same hold,
    /// either registers its turn or puts its message in the queue; a finishing turn leaves the
    /// running set under it and the next queued entry is claimed under it. Without it two
    /// admissions could both find the agent idle, and an arrival could start between a turn
    /// leaving and the queue being drained, ahead of everything already waiting.
    /// </summary>
    /// <remarks>
    /// Lock order, outermost first: <c>RunningTask.TurnDispatchLock</c> → <c>_admissionGate</c> →
    /// <c>lock (RunningTask.PrimaryMessageIds)</c> → <c>DispatchQueue._gate</c> →
    /// <c>QueuedMessage.QueueDispatchLock</c>. Only state changes happen under the gate: no await,
    /// no sink send, no subscriber callback, and never a turn. The one exception is
    /// <c>submission.accepted</c> for a message that was queued, which is published in the same hold
    /// as the enqueue (see <see cref="ReportDisposition"/>): publication is a non-blocking hand-off to
    /// the event pump, and outside the hold the entry could be claimed and its turn started first.
    /// </remarks>
    private readonly Lock _admissionGate = new();

    /// <summary>
    /// Handoffs in flight, guarded by <see cref="_admissionGate"/>: a turn that has left the running
    /// set and has not drained the queue yet, or a claimed queue entry that has not registered its
    /// turn yet. While it is above zero the agent is busy even though no turn is registered.
    /// </summary>
    private int _dispatchReservations;

    /// <summary>
    /// Executor turns in flight that this manager did not start: the warmup ping and a
    /// <c>/run</c> command. They go straight to the executor, whose own lock keeps them from
    /// overlapping a task's turn, so a registered turn may still be waiting for that lock while
    /// one of them runs. A mid-turn injection made then would be written into the ping or the
    /// command, and its answer would come out there, where nobody delivers it.
    /// </summary>
    private int _executorSideUses;

    internal bool ExecutorBusyElsewhere => Volatile.Read(ref _executorSideUses) > 0;

    /// <summary>
    /// Marks an executor turn started outside this manager, for as long as the returned handle is
    /// held. While any is held, messages for a running turn are not injected into it; they wait
    /// for its end like any message that could not be injected.
    /// </summary>
    public IDisposable BeginExecutorSideUse()
    {
        Interlocked.Increment(ref _executorSideUses);
        return new ExecutorSideUse(this);
    }

    private sealed class ExecutorSideUse(TaskManager owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref owner._executorSideUses);
        }
    }

    // user-level index: userId → list of (chatId, taskId) for cross-chat cancel
    private readonly ConcurrentDictionary<long, List<(long ChatId, int TaskId)>> _userTasks = new();

    private string _botUsername = "";

    /// <summary>
    /// Agent-level late-result guard (#406): 1 only after a steered turn ended with a terminal
    /// result and its drain read fewer own-turn answers than steering copies. The next executor
    /// turn consumes it and, in a leading <c>recovered_answer</c>, drops only <c>user</c> segments.
    /// </summary>
    private int _steeringResidue;
    internal bool SteeringResidueForTest => Volatile.Read(ref _steeringResidue) == 1;

    /// <summary>
    /// Outbound text destination. Constructor-injected via <see cref="MessageSinkHolder"/> rather
    /// than assigned after construction by <c>AgentTransport</c>: as a settable property it was
    /// <c>null!</c> until the transport existed, so omitting the transport turned every send into
    /// a <see cref="NullReferenceException"/> (#277 D-1). Never null.
    /// </summary>
    private readonly IMessageSink _sink;

    /// <summary>
    /// Raises <see cref="OnTaskCompleted"/> directly. A C# event can only be raised by its
    /// declaring type, and since #277 D-2 the subscribers are separate components, so a test that
    /// wants to exercise one of them without standing up an executor turn needs this hook.
    /// </summary>
    internal void RaiseTaskCompletedForTest(
        long chatId, string result, string? relaySender = null,
        TaskSource source = TaskSource.UserMessage, bool isPartial = false,
        string? correlationId = null, string? taskId = null,
        CompletionKind kind = CompletionKind.Completed) =>
        OnTaskCompleted?.Invoke(chatId, result, relaySender, source, isPartial, correlationId, taskId, kind);

    internal Action? SteeringNoticePendingForTest { get; set; }
    internal Action? SteerAttemptStartingForTest { get; set; }
    internal Action? NoticeLockWaitThrowsForTest { get; set; }
    internal int ActiveTaskIdCountForTest => _activeTaskIds.Count;
    internal int PrimaryDedupCountForTest => _primaryDedup.Count;

    internal Action? QueueEntryClaimedForTest { get; set; }
    internal Action<string>? DispatchCheckpointForTest { get; set; }
    internal Action? QueueEntryDequeuedForBridgeCancelForTest { get; set; }

    /// <summary>
    /// Publishes conversation events alongside — never instead of — the existing sink and
    /// callback calls. Optional so every existing construction site (and every existing test)
    /// compiles unchanged; when absent, publication is a no-op and behaviour is exactly as before.
    /// </summary>
    private readonly IConversationEventPublisher? _events;

    /// <summary>
    /// Configured attachment directory, redacted from every client-bound text field. Optional so
    /// existing construction sites are unchanged; when absent, marker stripping still applies.
    /// </summary>
    private readonly string? _telegramAttachmentDir;

    private readonly TurnBindingPublisher? _turnBindings;

    private readonly QueueIntake _intake;
    private readonly MidTurnDelivery _midTurn;
    private readonly TaskCommands _commands;
    private readonly TurnRunDependencies _turnDependencies;

    public TaskManager(
        IOptions<AgentOptions> agentConfig,
        IAgentExecutor executor,
        SessionManager sessions,
        ILogger<TaskManager> logger,
        InjectionOutcomeCounter? injectionCounter = null,
        IConversationEventPublisher? events = null,
        IOptions<TelegramOptions>? telegramConfig = null,
        ConversationEventCounters? counters = null,
        IMessageSink? sink = null,
        TurnBindingPublisher? turnBindings = null,
        QueueLaneCounter? queueCounter = null,
        TimeProvider? timeProvider = null)
    {
        _queueCounter = queueCounter ?? new();
        _messageQueue = new DispatchQueue(_queueCounter);
        _turnBindings = turnBindings;
        _agentConfig = agentConfig.Value;
        _executor = executor;
        _sessions = sessions;
        _logger = logger;
        _injectionCounter = injectionCounter ?? new InjectionOutcomeCounter();
        _events = events;
        _telegramAttachmentDir = telegramConfig?.Value.AttachmentDir;
        _sink = sink ?? NullMessageSink.Instance;
        // Constructor-injected rather than a settable property. As a property it was simply never
        // set anywhere in production, so conversation_submissions_total and
        // turn_outcome_unknown_total never incremented — the counters existed and measured nothing.
        _counters = counters;

        var host = new DispatchHost(this);
        _intake = new QueueIntake(_messageQueue, _chatTasks, _agentConfig, _injectionCounter, _sink, _logger, host);
        _midTurn = new MidTurnDelivery(_chatTasks, _agentConfig, _executor, _injectionCounter, _primaryDedup, _intake,
            _sink, _logger, host);
        _commands = new TaskCommands(_chatTasks, _userTasks, _agentConfig, _executor, _sessions, _sink, _logger, host);
        _turnDependencies = new TurnRunDependencies(_agentConfig, _executor, _sessions, _logger, _injectionCounter,
            _sink, _primaryDedup, _intake, host, _telegramAttachmentDir, timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// True when at least one handler is attached to <see cref="OnTaskCompleted"/>.
    ///
    /// NOT read by the startup wiring guard any more. Since #277 D-2 split the single handler
    /// into <c>RelayCompletionPublisher</c> and <c>CompletionContextBuffer</c>, "any subscriber is
    /// attached" is satisfied by the buffer alone while every relay answer vanishes, so
    /// <c>RuntimeWiringService</c> checks each component by identity instead (#277 MUST NOT 21).
    /// Retained as a diagnostic.
    /// </summary>
    public bool HasCompletionSubscriber => OnTaskCompleted is not null;

    /// <summary>Returns a snapshot of active background subagent tasks from the executor.</summary>
    public IReadOnlyCollection<Models.BackgroundTaskInfo> GetActiveBackgroundTasks() =>
        _executor.GetActiveBackgroundTasks();

    /// <summary>
    /// Request cancellation of a specific background subagent task by ID.
    /// Returns false if the task ID is not found in the active set.
    /// </summary>
    public Task<bool> CancelBackgroundTaskAsync(string taskId) =>
        _executor.CancelBackgroundTaskAsync(taskId);

    public void SetBotUsername(string username) => _botUsername = username;

    /// <summary>A single real Telegram human turn, never a steered relay/client turn.</summary>
    public bool TryGetCurrentHumanTurn(long privateChatId, out RunningTask? turn)
    {
        turn = null;
        var active = _chatTasks.SelectMany(pair => pair.Value.Snapshot().Select(t => (Chat: pair.Key, Task: t)))
            .Where(pair => !pair.Task.Closed && !pair.Task.Cts.IsCancellationRequested).ToArray();
        if (active.Length != 1) return false;
        var candidate = active[0];
        if (candidate.Chat != privateChatId || candidate.Task.UserId != privateChatId
            || candidate.Task.Source is not (TaskSource.UserMessage or TaskSource.NewCommand)
            || candidate.Task.Identity?.ChannelId != ChannelIds.Telegram) return false;
        turn = candidate.Task;
        return true;
    }

    public bool HasRunningTasks(long chatId) => GetChatState(chatId).Count > 0;

    public async Task<TaskDispatchOutcome> StartTask(long chatId, string task, string displayText, bool isSessionTask,
        TaskSource source = TaskSource.UserMessage,
        string? relaySender = null,
        string? correlationId = null,
        string? taskId = null,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        long userId = 0,
        ConversationIdentity? identity = null,
        TaskPriority priority = TaskPriority.Routine, long telegramMessageId = 0,
        bool steeringEligible = false,
        string? coldStartTask = null)
    {
        if (source is not (TaskSource.UserMessage or TaskSource.NewCommand)) priority = TaskPriority.Routine;
        var keyed = priority == TaskPriority.PrimaryHuman && telegramMessageId > 0;
        if (keyed && !_primaryDedup.TryReserve(chatId, telegramMessageId, out var duplicate))
        {
            if (duplicate) _queueCounter.Increment("primary_duplicate_dropped");
            else
                await _sink.SendTextByOriginAsync(chatId, $"Queue is full ({MaxQueueDepth} messages waiting). Please wait for tasks to complete.", OriginOf(source));
            return ReportDisposition(chatId, identity ?? SynthesizeIdentity(chatId, source),
                duplicate ? TaskDispatchOutcome.Dropped : TaskDispatchOutcome.QueueFull);
        }
        try
        {
            var outcome = await StartTaskCore(chatId, task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, documents, userId, skipPendingQueueCheck: false, identity: identity,
                priority: priority, telegramMessageId: telegramMessageId, steeringEligible: steeringEligible,
                coldStartTask: coldStartTask);
            if (keyed && outcome is TaskDispatchOutcome.Dropped or TaskDispatchOutcome.QueueFull) _primaryDedup.Complete(chatId, telegramMessageId);
            return outcome;
        }
        catch { if (keyed) _primaryDedup.Complete(chatId, telegramMessageId); throw; }
    }


    /// <summary>
    /// <see cref="StartTask"/> for a caller that does not wait for the outcome: a transport
    /// callback, a relay handler, a timer. The dispatch is still observed. If it throws, the
    /// message is gone, and discarding the task meant it went without a line in the log.
    /// </summary>
    public void StartTaskDetached(long chatId, string task, string displayText, bool isSessionTask,
        TaskSource source = TaskSource.UserMessage,
        string? relaySender = null,
        string? correlationId = null,
        string? taskId = null,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        long userId = 0,
        ConversationIdentity? identity = null,
        TaskPriority priority = TaskPriority.Routine, long telegramMessageId = 0,
        bool steeringEligible = false,
        string? coldStartTask = null)
    {
        var dispatch = StartTask(chatId, task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
            images, documents, userId, identity, priority, telegramMessageId, steeringEligible, coldStartTask);
        if (dispatch.IsCompletedSuccessfully) return;
        _ = ObserveDetachedStartAsync(dispatch, chatId, source, taskId, correlationId is not null);
    }

    private async Task ObserveDetachedStartAsync(Task<TaskDispatchOutcome> dispatch, long chatId, TaskSource source,
        string? taskId, bool hasCorrelationId)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Identifiers only. The prompt and the display text can carry anything a person or
            // another agent wrote, and neither belongs in a log.
            TryLog(LogLevel.Error, ex,
                "Task dispatch failed and the message was dropped (chat={ChatId}, source={Source}, taskId={TaskId}, hasCorrelationId={HasCorrelationId})",
                chatId, source, taskId ?? "none", hasCorrelationId);
        }
    }

    private async Task<TaskDispatchOutcome> StartTaskCore(long chatId, string task, string displayText, bool isSessionTask,
        TaskSource source = TaskSource.UserMessage,
        string? relaySender = null,
        string? correlationId = null,
        string? taskId = null,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        long userId = 0,
        bool skipPendingQueueCheck = false,
        bool skipDedupReservationAcquire = false,
        ConversationIdentity? identity = null,
        IReadOnlyList<string>? mergedSubmissionIds = null,
        TaskPriority priority = TaskPriority.Routine, long telegramMessageId = 0,
        IReadOnlyList<long>? primaryMessageIds = null,
        bool steeringEligible = false,
        DispatchReservation? reservation = null,
        string? coldStartTask = null)
    {
        // A null identity means a caller that predates the seam — synthesize one from the runtime
        // key so every dispatch decision has something to report against, and every existing call
        // site keeps compiling and behaving unchanged.
        identity ??= SynthesizeIdentity(chatId, source);

        var state = GetChatState(chatId);

        // Dedup: ignore re-delivered bridge directives with the same taskId.
        // DrainQueue passes skipDedupReservationAcquire=true because the reservation was
        // already acquired when the task entered the queue and is still held.
        if (!skipDedupReservationAcquire && taskId is not null && !_activeTaskIds.TryAdd(taskId, true))
        {
            _logger.LogInformation("Duplicate taskId={TaskId} ignored (already in-flight)", taskId);
            return ReportDisposition(chatId, identity, TaskDispatchOutcome.Dropped);
        }

        if (source is TaskSource.UserMessage or TaskSource.DebouncedGroupBatch && isSessionTask
            && state.Snapshot().Any(t => t.IsSessionTask
                && t.Source is TaskSource.Relay or TaskSource.Bridge or TaskSource.CheckIn))
        {
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.NotInjectedWorkflowTurn);
        }

        if (TryGetRunningSessionTask(state, out var runningSession))
        {
            // UserMessage and DebouncedGroupBatch are both injection-eligible for same-chat turns
            if ((source == TaskSource.UserMessage || source == TaskSource.DebouncedGroupBatch) && isSessionTask)
            {
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                var message = new MidTurnMessage(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
                    images, documents, userId, DateTimeOffset.UtcNow, identity, priority, telegramMessageId, coldStartTask);
                if (priority == TaskPriority.PrimaryHuman && telegramMessageId > 0)
                {
                    lock (runningSession.PrimaryMessageIds) runningSession.PrimaryMessageIds.Add(telegramMessageId);
                }
                return await _midTurn.DeliverMidTurnMessageAsync(chatId, runningSession, message);
            }

            if (source == TaskSource.CheckIn)
            {
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                var message = new MidTurnMessage(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
                    images, documents, userId, DateTimeOffset.UtcNow, identity, priority, telegramMessageId, coldStartTask);
                return await _midTurn.DeferUntilTurnEndAsync(chatId, runningSession, message, notifyUser: false);
            }

            if (source is TaskSource.Relay or TaskSource.Bridge)
                _logger.LogInformation("Not injecting {Source} task into running conversational turn for chat {ChatId}; using normal capacity path", source, chatId);
        }

        var queuedPart = QueueIntake.CreateQueuedPart(task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, documents, userId, identity: identity, priority: priority, telegramMessageId: telegramMessageId, coldStartTask: coldStartTask) with { PrimaryMessageIds = primaryMessageIds };
        // #406: only the router's verified-human flag makes a message eligible to steer a running
        // workflow turn, and only after it took today's queue path and was Queued.
        var steer = steeringEligible && source == TaskSource.UserMessage;
        if (!skipPendingQueueCheck)
        {
            // Under the gate, with the acceptance published in the same hold: a claim of the pending
            // entry is made under the gate too, so the entry cannot become a turn, and that turn
            // cannot start emitting, before this submission has been accepted as queued.
            PendingQueueResult pendingResult;
            var pendingNotify = false;
            var pendingEnqueued = false;
            QueuedMessage? pendingEntry = null;
            var pendingPosition = 0;
            lock (_admissionGate)
            {
                pendingResult = _intake.TryAppendToPendingQueue(chatId, queuedPart);
                if (pendingResult == PendingQueueResult.Merged)
                {
                    ReportDisposition(chatId, identity, TaskDispatchOutcome.Queued);
                }
                else if (pendingResult == PendingQueueResult.EnqueueFresh && source != TaskSource.CheckIn)
                {
                    pendingNotify = source != TaskSource.DebouncedGroupBatch && _intake.BusyNoticeApplies(chatId, source);
                    pendingEnqueued = _messageQueue.TryEnqueue(chatId, queuedPart, out pendingEntry, out pendingPosition);
                    if (pendingEnqueued)
                    {
                        if (_intake.BusyNoticeDue(chatId, pendingNotify, deferNotice: steer)) pendingEntry!.BusyNoticeSent = true;
                        ReportDisposition(chatId, identity, TaskDispatchOutcome.Queued);
                    }
                }
            }

            if (pendingResult == PendingQueueResult.Merged)
            {
                // Keep the taskId reservation — it covers the task's lifetime in the queue.
                // DrainQueue will call StartTaskCore with skipDedupReservationAcquire=true,
                // and the reservation is released by the finally block in Task.Run.
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.MergedIntoQueue);
                OnStatusChanged?.Invoke();
                if (steer) await _midTurn.SteerRunningTurnAsync(chatId, userId, queuedPart, images, documents);
                return TaskDispatchOutcome.Queued;
            }

            if (pendingResult == PendingQueueResult.EnqueueFresh)
            {
                if (source == TaskSource.CheckIn)
                {
                    // Check-ins are dropped rather than queued behind user work.
                    if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                    _logger.LogDebug("Check-in skipped — agent already has a pending queued entry for chat {ChatId}", chatId);
                    return ReportDisposition(chatId, identity, TaskDispatchOutcome.Dropped);
                }
                _intake.AfterEnqueue(pendingEnqueued, chatId, queuedPart, pendingNotify, completeBridgeOnDrop: true,
                    pendingEntry, pendingPosition, deferNotice: steer);
                if (!pendingEnqueued)
                {
                    // Release the reservation only when enqueue failed — a queued task keeps it.
                    if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                    return ReportDisposition(chatId, identity, TaskDispatchOutcome.QueueFull);
                }
                if (steer)
                    await _midTurn.SteerAndNotifyAsync(chatId, userId, queuedPart, images, documents, pendingEntry!, pendingPosition,
                        pendingNotify && !(_agentConfig.SuppressToolMessages && chatId < 0));
                return TaskDispatchOutcome.Queued;
            }
        }

        // Each agent has one persistent executor process and the executor's send lock is
        // held for a full turn. A second "concurrent" turn would just block behind that
        // lock while bypassing queue notices and coalescing, so the runtime is explicitly
        // one-at-a-time until executors can actually interleave turns.
        RunningTask? running = null;
        CancellationTokenSource? cts = null;
        var turnIdentity = identity;
        var busy = false;
        var enqueued = false;
        var notifyUser = false;
        QueuedMessage? entry = null;
        var position = 0;
        try
        {
            lock (_admissionGate)
            {
                // A claimed queue entry gives its reservation back in the step that registers it.
                reservation?.Release();
                busy = _dispatchReservations > 0 || AnyTurnRunning();
                if (!busy)
                {
                    DispatchCheckpointForTest?.Invoke("admission_idle");
                    cts = new CancellationTokenSource();
                    running = state.Add(displayText, cts, isSessionTask, userId, bridgeTaskId: taskId, source: source);

                    // TurnId is minted ONCE, here at registration, and lives on the RunningTask. Every event
                    // this turn emits derives from this identity via `with` rather than rebuilding it.
                    turnIdentity = identity.WithTurn(Guid.NewGuid().ToString("N"));
                    running.Identity = turnIdentity;
                    var keys = primaryMessageIds ?? (priority == TaskPriority.PrimaryHuman && telegramMessageId > 0 ? [telegramMessageId] : []);
                    foreach (var key in keys)
                    {
                        lock (running.PrimaryMessageIds) running.PrimaryMessageIds.Add(key);
                        _primaryDedup.Dispatched(chatId, key);
                    }
                    if (mergedSubmissionIds is { Count: > 0 })
                    {
                        // A coalesced entry is ONE turn answering SEVERAL submissions. Without this list the
                        // merged submissions would never terminate on the client.
                        running.ReplaceMergedSubmissionIds(
                            mergedSubmissionIds.Where(id => !string.Equals(id, identity.SubmissionId, StringComparison.Ordinal)));
                    }
                }
                else if (source != TaskSource.CheckIn)
                {
                    // DebouncedGroupBatch (different-chat) and all other sources go to the global FIFO.
                    // DebouncedGroupBatch must NOT use DeferUntilTurnEndAsync — that is reserved for
                    // CheckIn. The FIFO preserves ordering with other queued work.
                    // DebouncedGroupBatch is silent: sending "I'm busy" for automated group checks is noise.
                    // So is a chat message queued behind that chat's own turn (#369).
                    notifyUser = source != TaskSource.DebouncedGroupBatch && _intake.BusyNoticeApplies(chatId, source);
                    // Enqueued in the same hold as the busy decision: the turn or handoff that made
                    // the agent busy cannot drain the queue before this entry is in it.
                    enqueued = _messageQueue.TryEnqueue(chatId, queuedPart, out entry, out position);
                    if (enqueued)
                    {
                        if (_intake.BusyNoticeDue(chatId, notifyUser, deferNotice: steer)) entry!.BusyNoticeSent = true;
                        // Accepted as queued before the gate is released: once it is, the entry can
                        // be claimed and its turn can start, and that must come after this.
                        ReportDisposition(chatId, identity, TaskDispatchOutcome.Queued);
                    }
                }
            }
        }
        catch
        {
            AbandonRegistration();
            throw;
        }

        // Releases what a failed registration holds, so neither the task id nor a half-registered
        // turn outlives the exception: a turn left in the running set would keep the agent busy forever.
        void AbandonRegistration()
        {
            if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
            if (running is null) return;
            lock (_admissionGate) state.Remove(running.Id);
            cts?.Dispose();
            DrainQueue(reserved: false);
        }

        if (busy)
        {
            // Check-ins silently skip when at capacity instead of queuing
            if (source == TaskSource.CheckIn)
            {
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                _logger.LogDebug("Check-in skipped — agent already has a running task");
                return ReportDisposition(chatId, identity, TaskDispatchOutcome.Dropped);
            }

            _intake.AfterEnqueue(enqueued, chatId, queuedPart, notifyUser, completeBridgeOnDrop: true, entry, position, deferNotice: steer);
            if (!enqueued)
            {
                // Release the reservation only when enqueue failed — a queued task keeps it.
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                return ReportDisposition(chatId, identity, TaskDispatchOutcome.QueueFull);
            }
            if (steer)
                await _midTurn.SteerAndNotifyAsync(chatId, userId, queuedPart, images, documents, entry!, position,
                    notifyUser && !(_agentConfig.SuppressToolMessages && chatId < 0));
            // Already reported, under the gate.
            return TaskDispatchOutcome.Queued;
        }

        // Exception safety: if anything between here and Task.Run fires throws, release the
        // reservation so the same taskId can be retried. The Task.Run finally block handles
        // the release once the task has been handed off successfully.
        var launched = false;
        try
        {
        PublishEvent(chatId, ConversationEventKind.TurnStarted, turnIdentity, new TurnStartedPayload());

        // Register in user-level index for cross-chat cancel
        if (userId != 0)
        {
            var userList = _userTasks.GetOrAdd(userId, _ => []);
            lock (userList) userList.Add((chatId, running!.Id));
        }

        // Notify orchestrator immediately that agent is now busy
        OnStatusChanged?.Invoke();

        // Accepted BEFORE the turn's task exists. A client sees turn.started, then
        // submission.accepted, then the turn's own events, and relies on that order. Published
        // after Task.Run, the acceptance raced the turn: on a busy machine the turn's first
        // progress events could be out before it.
        ReportDisposition(chatId, turnIdentity, TaskDispatchOutcome.Ran);

        // Which prompt the turn needs is decided here, where it starts, not where the message
        // arrived: a process restarted in between holds no conversation, and the prompt built for
        // a warm one carries none. An entry from the queue was already decided when it was claimed.
        if (coldStartTask is not null && !_executor.IsProcessWarm)
            task = coldStartTask;

        launched = true;
        var registered = running!;
        var turnCts = cts!;
        _ = Task.Run(async () =>
        {
            var running = registered;
            var cts = turnCts;
            var bindingSequence = _turnBindings?.BeginTurn(chatId, source);
            try
            {
                await new TurnRun(_turnDependencies, chatId, running.Id, task, isSessionTask, source, relaySender,
                    correlationId, taskId, images, documents, turnIdentity, cts.Token).RunAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in task #{TaskId} for chat {ChatId}", running.Id, chatId);
            }
            finally
            {
                // From the moment the turn leaves the running set it holds a dispatch reservation,
                // and the drain in the outer finally gives it back on every path: a reservation
                // that leaked would keep the agent busy, and its queue growing, forever.
                var reserved = false;
                try
                {
                if (bindingSequence is { } sequence) _turnBindings?.EndTurn(sequence);
                await running.TurnDispatchLock.WaitAsync();
                try
                {
                    running.Closed = true;
                    _intake.DrainInboxToGlobalQueue(chatId, running);
                    lock (_admissionGate)
                    {
                        state.Remove(running.Id);
                        _dispatchReservations++;
                        reserved = true;
                    }
                    lock (running.PrimaryMessageIds)
                        foreach (var key in running.PrimaryMessageIds)
                            if (!_messageQueue.Snapshot().Any(e => e.ChatId == chatId && e.Parts.Any(p => QueueIntake.KeysOfPart(p).Contains(key))))
                                _primaryDedup.Complete(chatId, key);
                }
                finally
                {
                    running.TurnDispatchLock.Release();
                }

                // The reaper (D10.1). This finally already runs on every exit path — normal
                // completion, cancellation and unhandled exception — so it is the one place that
                // can guarantee a submission terminates.
                //
                // It fires ONLY on a genuine gap. Every terminal path sets TerminalPublished,
                // including the idle and no-output branches, which is why those branches publish
                // for Telegram-owned conversations too: without that, the most common no-op turn
                // in the runtime — a Telegram idle check-in — would manufacture a
                // turn_reaped event on every single tick.
                //
                // Publication here is a non-blocking TryWrite into the per-conversation terminal
                // outbox, never the shared progress channel, so a full queue can neither block
                // nor throw out of a turn's teardown.
                if (!running.TerminalPublished && running.Identity is not null)
                {
                    _counters?.OutcomeUnknown(OutcomeUnknownReason.TurnReaped.ToString());
                    PublishEvent(chatId, ConversationEventKind.TurnOutcomeUnknown, running.Identity,
                        new TurnOutcomeUnknownPayload { Reason = OutcomeUnknownReason.TurnReaped });
                    running.TerminalPublished = true;
                }
                // Release taskId dedup slot so the agent can accept a re-send of the same task
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                // Remove from user-level index
                if (userId != 0 && _userTasks.TryGetValue(userId, out var userList))
                {
                    lock (userList) userList.RemoveAll(e => e.ChatId == chatId && e.TaskId == running.Id);
                }
                cts.Dispose();
                // Notify orchestrator immediately that agent is now idle (or next queued task starts)
                OnStatusChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    TryLog(LogLevel.Error, ex, "Teardown of task #{TaskId} for chat {ChatId} failed part-way", running.Id, chatId);
                }
                finally
                {
                    if (!reserved)
                    {
                        // Teardown failed before the turn left the running set. It leaves now: a
                        // finished turn that stayed listed would refuse every later message.
                        lock (_admissionGate)
                        {
                            state.Remove(running.Id);
                            _dispatchReservations++;
                        }
                    }
                    // Drain one message from the queue, or give the reservation back if it is empty.
                    DrainQueue(reserved: true);
                }
            }
        });

        return TaskDispatchOutcome.Ran;
        } // end exception-safety try
        catch
        {
            // Release unconditionally: skipDedupReservationAcquire governs acquisition, not release.
            // On drain re-entry (skipDedupReservationAcquire=true) an exception here means the task
            // was dequeued but never ran — without TryRemove the taskId leaks permanently.
            if (launched)
            {
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
            }
            else
            {
                AbandonRegistration();
            }
            throw;
        }
    }

    private bool AnyTurnRunning() => _chatTasks.Values.Any(s => s.Count > 0);

    /// <summary>
    /// One in-flight handoff, given back exactly once: by the admission that registers the claimed
    /// entry's turn, or, if that never happens, when the start attempt has finished.
    /// </summary>
    private sealed class DispatchReservation(TaskManager owner)
    {
        private int _released;

        /// <summary>Gives the reservation back. Returns false when it already was.</summary>
        public bool Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1) return false;
            lock (owner._admissionGate) owner._dispatchReservations--;
            return true;
        }
    }

    public Task HandleStop(long chatId) => _commands.HandleStop(chatId);

    public Task HandleReset(long chatId) => _commands.HandleReset(chatId);

    public Task HandleStatus(long chatId) => _commands.HandleStatus(chatId);

    public Task HandleCancel(long chatId, string arg, long userId = 0,
        TurnCancelReason reason = TurnCancelReason.User) => _commands.HandleCancel(chatId, arg, userId, reason);

    /// <summary>Holds a message for the end of the running turn. See <see cref="MidTurnDelivery"/>.</summary>
    internal Task<TaskDispatchOutcome> EnqueueForTurnEndAsync(long chatId, RunningTask running, MidTurnMessage message, bool notifyUser) =>
        _midTurn.EnqueueForTurnEndAsync(chatId, running, message, notifyUser);

    /// <summary>Delivery receipt only; does not claim the model read or followed the message.</summary>
    internal const string SteeringDeliveredNotice = MidTurnText.SteeringDeliveredNotice;

    /// <summary>Fixed header of a steering copy (#406).</summary>
    internal const string SteeringHeader = MidTurnText.SteeringHeader;

    /// <summary>Prefix each steered part gets when its queued entry is drained into the reply turn.</summary>
    internal const string SteeredPartPrefix = MidTurnText.SteeredPartPrefix;

    // --- Conversation event seam (additive; never replaces a sink or callback call) ---

    /// <summary>
    /// Build a routing identity for a caller that supplied none.
    ///
    /// Relay and bridge work is stamped with the <c>relay</c> channel so it can never be routed
    /// to a client adapter; everything else is <c>telegram</c>. The conversation id is the runtime
    /// key rendered as a string, which is what lets the bus reverse-resolve it.
    /// </summary>
    private static ConversationIdentity SynthesizeIdentity(long chatId, TaskSource source)
    {
        var channelId = source is TaskSource.Relay or TaskSource.Bridge
            ? ChannelIds.Relay
            : ChannelIds.Telegram;

        return new ConversationIdentity
        {
            PrincipalId = "",
            Role = PrincipalRole.Owner,
            ChannelId = channelId,
            ConversationId = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SubmissionId = Guid.NewGuid().ToString("N"),
            Attempt = 1,
        };
    }

    private void PublishEvent<TPayload>(long chatId, string kind, ConversationIdentity? identity, TPayload? payload)
        where TPayload : class
    {
        if (_events is null || identity is null) return;
        try
        {
            _events.Publish(chatId, kind, identity, payload);
        }
        catch (Exception ex)
        {
            // Publication must never reach a turn. The bus already swallows its own failures;
            // this is the last line of defence against a contract defect faulting the executor.
            _logger.LogWarning(ex, "Conversation event publish failed for kind={Kind}", kind);
        }
    }

    /// <summary>
    /// Publish a terminal event and record that the turn HAS terminated, so the reaper does not
    /// also fire. <see cref="RunningTask.TerminalPublished"/> is set whether or not the event was
    /// accepted into a queue: a dropped terminal event is already counted at the bus, and turning
    /// that into a second, contradictory terminal event would be worse than the drop.
    /// </summary>
    private void PublishTerminal<TPayload>(long chatId, int taskId, string kind, ConversationIdentity? identity, TPayload? payload)
        where TPayload : class
    {
        var running = GetChatState(chatId).Get(taskId);
        if (running is not null)
            running.TerminalPublished = true;

        if (kind == ConversationEventKind.TurnOutcomeUnknown && payload is TurnOutcomeUnknownPayload unknown)
            _counters?.OutcomeUnknown(unknown.Reason.ToString());

        PublishEvent(chatId, kind, identity, payload);
    }

    /// <summary>
    /// Publish a terminal error. The message is ALWAYS the fixed constant for the code — runtime
    /// text, provider text and exception messages stay server-side (Constraint 4).
    /// </summary>
    private void PublishTerminalError(long chatId, int taskId, ConversationIdentity? identity, ProtocolErrorCode code) =>
        PublishTerminal(chatId, taskId, ConversationEventKind.TurnError, identity,
            new TurnErrorPayload { Code = code, Message = ProtocolErrors.MessageFor(code) });

    /// <summary>Counter sink for submission dispositions and unknown outcomes (D20).</summary>
    private readonly ConversationEventCounters? _counters;

    private TaskDispatchOutcome ReportDisposition(long chatId, ConversationIdentity? identity, TaskDispatchOutcome outcome, int? queuePosition = null)
    {
        _counters?.Submission(outcome.ToString());
        PublishEvent(chatId, ConversationEventKind.SubmissionAccepted, identity,
            new SubmissionAcceptedPayload
            {
                Disposition = MapDisposition(outcome),
                QueuePosition = queuePosition,
            });
        return outcome;
    }

    private static SubmissionDisposition MapDisposition(TaskDispatchOutcome outcome) => outcome switch
    {
        TaskDispatchOutcome.Ran => SubmissionDisposition.Ran,
        TaskDispatchOutcome.Injected => SubmissionDisposition.Injected,
        TaskDispatchOutcome.Queued => SubmissionDisposition.Queued,
        TaskDispatchOutcome.QueueFull => SubmissionDisposition.QueueFull,
        _ => SubmissionDisposition.Dropped,
    };

    // --- Private ---

    /// <summary>
    /// A reply to another agent's directive or to a workflow is operational traffic: the journal
    /// never records it, even in a chat a human can see.
    /// </summary>
    internal static OutboundOrigin OriginOf(TaskSource source) => source switch
    {
        TaskSource.Relay => OutboundOrigin.Relay,
        TaskSource.Bridge => OutboundOrigin.Bridge,
        _ => OutboundOrigin.Human,
    };


    private static bool TryGetRunningSessionTask(ChatTaskState state, out RunningTask running)
    {
        // State is already scoped by the runtime chat key. Never deliver a human correction
        // into a workflow, check-in or /new turn, even when it uses a conversational session.
        // /new arrivals remain queued, as do messages arriving during a /new turn.
        running = state.Snapshot().FirstOrDefault(t => t.IsSessionTask
            && t.Source is TaskSource.UserMessage or TaskSource.DebouncedGroupBatch)!;
        return running is not null;
    }

    private void TryLog(LogLevel level, Exception? ex, string template, params object?[] args)
    {
        try { _logger.Log(level, ex, template, args); }
        catch { /* Best effort only: no logging, notice retry or state change. */ }
    }

    private void RemovePendingIndexIfCurrent(QueuedMessage queued) => _messageQueue.RemovePendingIfCurrent(queued);

    private ChatTaskState GetChatState(long chatId) =>
        _chatTasks.GetOrAdd(chatId, _ => new ChatTaskState());

    internal static string TruncateText(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>
    /// Cancels the running task with the given bridge taskId (from a Temporal delegation).
    /// Also removes the task from the queue if it hasn't started yet.
    /// Returns false if no task with that taskId is found.
    /// </summary>
    public async Task<bool> CancelByBridgeTaskIdAsync(string bridgeTaskId)
    {
        var found = false;

        // Check running tasks in all chats
        foreach (var (_, state) in _chatTasks)
        {
            foreach (var t in state.Snapshot())
            {
                if (t.BridgeTaskId == bridgeTaskId)
                {
                    t.CancelReason = TurnCancelReason.Bridge;
                    try { await t.Cts.CancelAsync(); }
                    catch (ObjectDisposedException) { }
                    found = true;
                }
            }
        }

        if (!found)
        {
            foreach (var item in _messageQueue.RemoveByTaskId(bridgeTaskId, QueueEntryDequeuedForBridgeCancelForTest))
            {
                found = true;
                foreach (var part in item.Parts)
                {
                    if (part.TaskId is not null) _activeTaskIds.TryRemove(part.TaskId, out _);
                    foreach (var key in QueueIntake.KeysOfPart(part)) _primaryDedup.Complete(item.ChatId, key);
                }
            }
        }

        if (found)
            _logger.LogInformation("CancelByBridgeTaskId: cancelled task bridgeTaskId={BridgeTaskId}", bridgeTaskId);
        else
            // Benign race: cancel arrived before the task was registered, or after it
            // already completed. The workflow has its answer (or never will); nothing to do.
            _logger.LogInformation("CancelByBridgeTaskId: no active task for bridgeTaskId={BridgeTaskId} (already done or not yet registered)", bridgeTaskId);

        return found;
    }

    /// <summary>
    /// Cancels all running tasks and clears the pending queue.
    /// Used by the HTTP cancel endpoint invoked from the orchestrator dashboard.
    /// </summary>
    public async Task CancelAllAsync()
    {
        // Drain the queue FIRST so that when cancelling running tasks triggers DrainQueue,
        // there is nothing left to dequeue and no queued reservation is accidentally promoted
        // to a running reservation before we release it.
        foreach (var msg in _messageQueue.Clear())
            foreach (var part in msg.Parts)
            {
                if (part.TaskId is not null) _activeTaskIds.TryRemove(part.TaskId, out _);
                foreach (var key in QueueIntake.KeysOfPart(part)) _primaryDedup.Complete(msg.ChatId, key);
            }

        // Cancel all running tasks (their finally blocks will call DrainQueue, which now finds
        // an empty queue and returns immediately).
        //
        // Reason is `operator`: this method's only caller is the operator HTTP cancel endpoint.
        // Host stop does NOT reach here — a turn runs on the per-task CancellationTokenSource
        // created at registration, which is linked to no host token — which is exactly why there
        // is no `shutdown` cancel reason to ship.
        foreach (var (_, state) in _chatTasks)
        {
            foreach (var t in state.Snapshot())
            {
                t.CancelReason = TurnCancelReason.Operator;
                try { await t.Cts.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
        }

        _logger.LogInformation("CancelAll: all running tasks cancelled and queue cleared");
    }

    /// <summary>
    /// Claims the next queued entry and starts its turn, unless a turn is running or another
    /// handoff is in flight.
    /// </summary>
    /// <param name="reserved">
    /// True when the caller is a finishing turn that already holds a dispatch reservation; the
    /// reservation passes to the claimed entry, or is given back when the queue is empty.
    /// </param>
    private void DrainQueue(bool reserved)
    {
        QueuedMessage? queued;
        lock (_admissionGate)
        {
            if (!reserved)
            {
                if (_dispatchReservations > 0 || AnyTurnRunning()) return;
                _dispatchReservations++;
            }

            if (!_messageQueue.TryDequeue(out queued))
            {
                _dispatchReservations--;
                return;
            }
        }

        // Held until the claimed entry's turn is registered, so no arrival sees an idle agent in between.
        var reservation = new DispatchReservation(this);
        try
        {
            QueueEntryClaimedForTest?.Invoke();

            // The provider process may have been restarted while this entry waited. Its first
            // message was built for the process that was there when it arrived; a cold one gets
            // the rendering that carries the history.
            var payload = MidTurnText.WithSteeredPrefix(queued).BuildPayload(DateTimeOffset.Now,
                processCold: !_executor.IsProcessWarm);

            _logger.LogInformation("Queue dispatch lane={lane} reason={reason} chat={chat} parts={parts} waitedMs={waitedMs}",
                queued.Priority == TaskPriority.PrimaryHuman ? "priority" : "routine", "queue_drain",
                queued.ChatId, queued.PartCount, Math.Max(0, (DateTimeOffset.Now - queued.QueuedAt).TotalMilliseconds));
            if (queued.BusyNoticeSent)
                _ = _sink.SendTextByOriginAsync(queued.ChatId, "Now processing your queued message...", OriginOf(queued.Source));
            OnStatusChanged?.Invoke();

            // Identity and the merged-submission list must survive the queue. Without them the turn
            // runs under a freshly synthesized identity, so the submission that produced
            // submission.accepted{queued} never receives a terminal event and a client waits forever.
            var start = StartTaskCore(queued.ChatId, payload.Task, payload.DisplayText, payload.IsSessionTask,
                payload.Source, payload.RelaySender, payload.CorrelationId, payload.TaskId,
                payload.Images, payload.Documents, payload.UserId,
                skipPendingQueueCheck: true, skipDedupReservationAcquire: true,
                identity: payload.Identity, mergedSubmissionIds: payload.MergedSubmissionIds,
                priority: queued.Priority,
                primaryMessageIds: queued.Parts.SelectMany(QueueIntake.KeysOfPart).Distinct().ToArray(),
                reservation: reservation);

            RemovePendingIndexIfCurrent(queued);
            _ = ObserveDrainedStartAsync(start, reservation, queued);
        }
        catch (Exception ex)
        {
            // The entry was claimed and cannot start. It is gone, so release what it held and move
            // on to the next one instead of leaving the agent busy with nothing running.
            AbandonClaimedEntry(queued, reservation, ex);
        }
    }

    private async Task ObserveDrainedStartAsync(Task<TaskDispatchOutcome> start, DispatchReservation reservation, QueuedMessage queued)
    {
        try
        {
            await start;
        }
        catch (Exception ex)
        {
            AbandonClaimedEntry(queued, reservation, ex);
            return;
        }

        // A start that returned without registering a turn still holds the reservation.
        if (reservation.Release())
            DrainQueue(reserved: false);
    }

    private void AbandonClaimedEntry(QueuedMessage queued, DispatchReservation reservation, Exception ex)
    {
        TryLog(LogLevel.Error, ex, "Queued entry for chat {ChatId} ({Source}, {Parts} part(s)) could not be started and was dropped",
            queued.ChatId, queued.Source, queued.PartCount);
        foreach (var part in queued.Parts)
        {
            if (part.TaskId is not null) _activeTaskIds.TryRemove(part.TaskId, out _);
            foreach (var key in QueueIntake.KeysOfPart(part)) _primaryDedup.Complete(queued.ChatId, key);
        }
        _messageQueue.RemovePendingIfCurrent(queued);
        if (reservation.Release())
            DrainQueue(reserved: false);
    }

    /// <summary>Returns a snapshot of the current queue for heartbeat/status reporting.</summary>
    public IReadOnlyList<QueuedMessage> GetQueueSnapshot() => _messageQueue.Snapshot();
    public int PriorityQueuedCount => _messageQueue.PriorityCount;

    /// <summary>
    /// Returns the current agent status for orchestrator heartbeats.
    /// </summary>
    public (string Status, string? CurrentTask, string? CurrentTaskId) GetOrchestratorStatus() =>
        _commands.GetOrchestratorStatus();

    /// <summary>
    /// Raised when a task completes with a result.
    /// Parameters: chatId, result, relaySender (null if Telegram-originated), source, isPartial (hit max-turns/error), correlationId, taskId, kind.
    /// </summary>
    public event Action<long, string, string?, TaskSource, bool, string?, string?, CompletionKind>? OnTaskCompleted;

    /// <summary>
    /// Raised for each significant tool-use event during task execution.
    /// Parameters: chatId, toolName, description.
    /// </summary>
    public event Action<long, string, string>? OnToolUse;

    /// <summary>
    /// Raised immediately when agent state changes (task started or completed).
    /// Allows the orchestrator heartbeat to publish without waiting for the next timer tick.
    /// </summary>
    public event Action? OnStatusChanged;

    /// <summary>
    /// The private side of this manager, for the parts in <c>Services/Dispatch</c>. A nested type so
    /// that nothing here has to become more visible than it was.
    /// </summary>
    private sealed class DispatchHost(TaskManager owner) : IDispatchHost
    {
        public ChatTaskState GetChatState(long chatId) => owner.GetChatState(chatId);

        public void PublishEvent<TPayload>(long chatId, string kind, ConversationIdentity? identity, TPayload? payload)
            where TPayload : class => owner.PublishEvent(chatId, kind, identity, payload);

        public void PublishTerminal<TPayload>(long chatId, int taskId, string kind, ConversationIdentity? identity, TPayload? payload)
            where TPayload : class => owner.PublishTerminal(chatId, taskId, kind, identity, payload);

        public void PublishTerminalError(long chatId, int taskId, ConversationIdentity? identity, ProtocolErrorCode code) =>
            owner.PublishTerminalError(chatId, taskId, identity, code);

        public TaskDispatchOutcome ReportDisposition(long chatId, ConversationIdentity? identity, TaskDispatchOutcome outcome, int? queuePosition = null) =>
            owner.ReportDisposition(chatId, identity, outcome, queuePosition);

        public void DrainQueue(bool reserved) => owner.DrainQueue(reserved);

        public void RaiseTaskCompleted(long chatId, string result, string? relaySender, TaskSource source, bool isPartial,
            string? correlationId, string? taskId, CompletionKind kind) =>
            owner.OnTaskCompleted?.Invoke(chatId, result, relaySender, source, isPartial, correlationId, taskId, kind);

        public void RaiseToolUse(long chatId, string toolName, string description) =>
            owner.OnToolUse?.Invoke(chatId, toolName, description);

        public void RaiseStatusChanged() => owner.OnStatusChanged?.Invoke();

        public bool ExecutorBusyElsewhere => owner.ExecutorBusyElsewhere;

        public bool TakeSteeringResidue() => Interlocked.Exchange(ref owner._steeringResidue, 0) == 1;

        public void LeaveSteeringResidue() => Volatile.Write(ref owner._steeringResidue, 1);

        public Action? SteerAttemptStartingForTest => owner.SteerAttemptStartingForTest;
        public Action<string>? DispatchCheckpointForTest => owner.DispatchCheckpointForTest;
        public Action? SteeringNoticePendingForTest => owner.SteeringNoticePendingForTest;
        public Action? NoticeLockWaitThrowsForTest => owner.NoticeLockWaitThrowsForTest;
    }
}
