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
/// to a turn when one ends (<c>HandOver</c>). The turn itself, the commands over running turns, mid-turn delivery
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
    // A directive's hold on its task id, from the moment it arrives until its task has ended. It
    // carries the execution id the directive named, null when it named none (see
    // CancelBridgeExecutionAsync), so this is where the id is kept with the task.
    private readonly ConcurrentDictionary<string, TaskIdReservation> _activeTaskIds = new();

    /// <summary>
    /// One directive's hold on a task id. It is taken before the directive is admitted, so for a
    /// moment it exists while the task is neither queued nor running.
    /// </summary>
    private sealed class TaskIdReservation(string? executionId)
    {
        public string? ExecutionId { get; } = executionId;

        // Set under _admissionGate at registration. A closed predecessor can still be in
        // _chatTasks after releasing the id, so task-id equality alone does not prove ownership.
        public RunningTask? AdmittedTurn;

        /// <summary>
        /// Set by a cancel that named this execution and found it reserved but not yet admitted.
        /// Read and written under <see cref="_admissionGate"/> only, which is also where a
        /// directive is admitted: so either the cancel is first and the directive is never queued
        /// or run, or the admission is first and the cancel finds the task queued or running.
        /// </summary>
        public bool CancelledBeforeAdmission;
    }

    /// <summary>
    /// Lets go of a reservation, if it is still the one held under the task id. A cancel can have
    /// removed it already, and a later execution may hold the task id by now; that one is not
    /// this caller's to release.
    /// </summary>
    private void Release(string? taskId, TaskIdReservation? reservation)
    {
        if (taskId is null || reservation is null) return;
        _activeTaskIds.TryRemove(new KeyValuePair<string, TaskIdReservation>(taskId, reservation));
    }

    private readonly DispatchQueue _messageQueue;
    private readonly PrimaryHumanDedup _primaryDedup = new();
    private readonly QueueLaneCounter _queueCounter;
    private const int MaxQueueDepth = DispatchQueue.MaxQueueDepth;

    /// <summary>
    /// The one gate for "is a turn running?". The agent is busy exactly when a turn is registered
    /// in <see cref="_chatTasks"/>, and every change to that goes through here:
    /// <list type="bullet">
    /// <item>an admission decides under the gate and, in the same hold, either registers its turn
    /// or puts its message in the queue;</item>
    /// <item>a finishing turn leaves the running set and the next queued entry's turn is registered
    /// in one hold (<see cref="HandOver"/>).</item>
    /// </list>
    /// So two admissions cannot both find the agent idle, an arrival cannot start ahead of work
    /// already queued, and there is no moment at which nothing is registered while the slot is
    /// still taken: what <see cref="HasRunningTasks"/>, <see cref="GetOrchestratorStatus"/> and
    /// <c>/status</c> report is what an admission decides on.
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
        bool steeringEligible = false, string? executionId = null,
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
            var outcome = await StartTaskCore(chatId, task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, documents, userId, identity: identity,
                priority: priority, telegramMessageId: telegramMessageId, steeringEligible: steeringEligible, executionId: executionId,
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
        bool steeringEligible = false, string? executionId = null,
        string? coldStartTask = null)
    {
        var dispatch = StartTask(chatId, task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
            images, documents, userId, identity, priority, telegramMessageId, steeringEligible, executionId, coldStartTask);
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
        ConversationIdentity? identity = null,
        TaskPriority priority = TaskPriority.Routine, long telegramMessageId = 0,
        bool steeringEligible = false, string? executionId = null,
        string? coldStartTask = null)
    {
        // A null identity means a caller that predates the seam — synthesize one from the runtime
        // key so every dispatch decision has something to report against, and every existing call
        // site keeps compiling and behaving unchanged.
        identity ??= SynthesizeIdentity(chatId, source);

        var state = GetChatState(chatId);

        // Dedup: ignore re-delivered bridge directives with the same taskId. A queued entry keeps
        // the reservation it took here until its own turn ends; HandOver starts it without coming
        // back through this method.
        var reservation = taskId is null ? null : new TaskIdReservation(string.IsNullOrEmpty(executionId) ? null : executionId);
        if (taskId is not null && !_activeTaskIds.TryAdd(taskId, reservation!))
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
                Release(taskId, reservation);
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
                Release(taskId, reservation);
                var message = new MidTurnMessage(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
                    images, documents, userId, DateTimeOffset.UtcNow, identity, priority, telegramMessageId, coldStartTask);
                return await _midTurn.DeferUntilTurnEndAsync(chatId, runningSession, message, notifyUser: false);
            }

            if (source is TaskSource.Relay or TaskSource.Bridge)
                _logger.LogInformation("Not injecting {Source} task into running conversational turn for chat {ChatId}; using normal capacity path", source, chatId);
        }

        var queuedPart = QueueIntake.CreateQueuedPart(task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, documents, userId, identity: identity, priority: priority, telegramMessageId: telegramMessageId, coldStartTask: coldStartTask);
        // #406: only the router's verified-human flag makes a message eligible to steer a running
        // workflow turn, and only after it took today's queue path and was Queued.
        var steer = steeringEligible && source == TaskSource.UserMessage;

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
            // A cancel that named this execution got here first. Nothing is queued for it.
            if (reservation is { CancelledBeforeAdmission: true })
                return CancelledBeforeAdmission(chatId, identity);

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
            // Keep the taskId reservation — it covers the task's lifetime in the queue, and
            // is released by the teardown of the turn that HandOver starts for the entry.
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
                Release(taskId, reservation);
                _logger.LogDebug("Check-in skipped — agent already has a pending queued entry for chat {ChatId}", chatId);
                return ReportDisposition(chatId, identity, TaskDispatchOutcome.Dropped);
            }
            _intake.AfterEnqueue(pendingEnqueued, chatId, queuedPart, pendingNotify, completeBridgeOnDrop: true,
                pendingEntry, pendingPosition, deferNotice: steer);
            if (!pendingEnqueued)
            {
                // Release the reservation only when enqueue failed — a queued task keeps it.
                Release(taskId, reservation);
                return ReportDisposition(chatId, identity, TaskDispatchOutcome.QueueFull);
            }
            if (steer)
                await _midTurn.SteerAndNotifyAsync(chatId, userId, queuedPart, images, documents, pendingEntry!, pendingPosition,
                    pendingNotify && !(_agentConfig.SuppressToolMessages && chatId < 0));
            return TaskDispatchOutcome.Queued;
        }

        // Each agent has one persistent executor process and the executor's send lock is
        // held for a full turn. A second "concurrent" turn would just block behind that
        // lock while bypassing queue notices and coalescing, so the runtime is explicitly
        // one-at-a-time until executors can actually interleave turns.
        var start = new TurnStart(chatId, task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
            images, documents, userId, identity, MergedSubmissionIds: null,
            PrimaryMessageIds: priority == TaskPriority.PrimaryHuman && telegramMessageId > 0 ? [telegramMessageId] : [],
            ColdStartTask: coldStartTask);
        RegisteredTurn? registered = null;
        var busy = false;
        var enqueued = false;
        var notifyUser = false;
        QueuedMessage? entry = null;
        var position = 0;
        try
        {
            lock (_admissionGate)
            {
                // The same check as at the first hold: the cancel may have come between the two.
                if (reservation is { CancelledBeforeAdmission: true })
                    return CancelledBeforeAdmission(chatId, identity);

                busy = AnyTurnRunning();
                if (!busy)
                {
                    DispatchCheckpointForTest?.Invoke("admission_idle");
                    registered = RegisterTurnLocked(start);
                }
                else if (source != TaskSource.CheckIn)
                {
                    // DebouncedGroupBatch (different-chat) and all other sources go to the global FIFO.
                    // DebouncedGroupBatch must NOT use DeferUntilTurnEndAsync — that is reserved for
                    // CheckIn. The FIFO preserves ordering with other queued work.
                    // DebouncedGroupBatch is silent: sending "I'm busy" for automated group checks is noise.
                    // So is a chat message queued behind that chat's own turn (#369).
                    notifyUser = source != TaskSource.DebouncedGroupBatch && _intake.BusyNoticeApplies(chatId, source);
                    // Enqueued in the same hold as the busy decision: the turn that made the agent
                    // busy cannot hand over before this entry is in the queue.
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
            Release(taskId, reservation);
            throw;
        }

        if (busy)
        {
            // Check-ins silently skip when at capacity instead of queuing
            if (source == TaskSource.CheckIn)
            {
                Release(taskId, reservation);
                _logger.LogDebug("Check-in skipped — agent already has a running task");
                return ReportDisposition(chatId, identity, TaskDispatchOutcome.Dropped);
            }

            _intake.AfterEnqueue(enqueued, chatId, queuedPart, notifyUser, completeBridgeOnDrop: true, entry, position, deferNotice: steer);
            if (!enqueued)
            {
                // Release the reservation only when enqueue failed — a queued task keeps it.
                Release(taskId, reservation);
                return ReportDisposition(chatId, identity, TaskDispatchOutcome.QueueFull);
            }
            if (steer)
                await _midTurn.SteerAndNotifyAsync(chatId, userId, queuedPart, images, documents, entry!, position,
                    notifyUser && !(_agentConfig.SuppressToolMessages && chatId < 0));
            // Already reported, under the gate.
            return TaskDispatchOutcome.Queued;
        }

        LaunchTurn(start, registered!);
        return TaskDispatchOutcome.Ran;
    }

    /// <summary>
    /// The end of a directive whose execution was cancelled before it was admitted. Called with
    /// <see cref="_admissionGate"/> held, in place of queueing or registering it. Nothing is
    /// published for it: no queue notice, no completion, no relay answer. The cancel has already
    /// answered <c>cancelled</c> and released the task id, and whoever sent the directive asked
    /// for exactly this. The dispatch is reported as dropped, the way a repeat is.
    /// </summary>
    private TaskDispatchOutcome CancelledBeforeAdmission(long chatId, ConversationIdentity identity) =>
        // Nothing else here: the gate is held. The cancel logs what it did.
        ReportDisposition(chatId, identity, TaskDispatchOutcome.Dropped);

    private bool AnyTurnRunning() => _chatTasks.Values.Any(s => s.Count > 0);

    /// <summary>
    /// Everything a turn is started with, whether it runs at once or comes off the queue.
    /// <c>ColdStartTask</c> is <c>Task</c> as a provider process that holds no conversation needs
    /// it; which of the two the turn gets is decided in <see cref="LaunchTurn"/>.
    /// </summary>
    private sealed record TurnStart(
        long ChatId, string Task, string DisplayText, bool IsSessionTask, TaskSource Source,
        string? RelaySender, string? CorrelationId, string? TaskId,
        IReadOnlyList<MessageImage>? Images, IReadOnlyList<MessageDocument>? Documents, long UserId,
        ConversationIdentity Identity, IReadOnlyList<string>? MergedSubmissionIds, IReadOnlyList<long> PrimaryMessageIds,
        string? ColdStartTask = null);

    /// <summary>A turn that is in the running set. Its task may not have been started yet.</summary>
    private sealed record RegisteredTurn(ChatTaskState State, RunningTask Running, CancellationTokenSource Cts, ConversationIdentity Identity);

    /// <summary>
    /// Puts a turn in the running set. Called with <see cref="_admissionGate"/> held and only when
    /// no turn is running. From here on the agent is busy for every observer and every admission.
    /// </summary>
    private RegisteredTurn RegisterTurnLocked(TurnStart start)
    {
        var state = GetChatState(start.ChatId);
        var cts = new CancellationTokenSource();
        var running = state.Add(start.DisplayText, cts, start.IsSessionTask, start.UserId, bridgeTaskId: start.TaskId, source: start.Source);
        try
        {
            // TurnId is minted ONCE, here at registration, and lives on the RunningTask. Every event
            // this turn emits derives from this identity via `with` rather than rebuilding it.
            var turnIdentity = start.Identity.WithTurn(Guid.NewGuid().ToString("N"));
            running.Identity = turnIdentity;
            foreach (var key in start.PrimaryMessageIds)
            {
                lock (running.PrimaryMessageIds) running.PrimaryMessageIds.Add(key);
                _primaryDedup.Dispatched(start.ChatId, key);
            }
            if (start.MergedSubmissionIds is { Count: > 0 })
            {
                // A coalesced entry is ONE turn answering SEVERAL submissions. Without this list the
                // merged submissions would never terminate on the client.
                running.ReplaceMergedSubmissionIds(
                    start.MergedSubmissionIds.Where(id => !string.Equals(id, start.Identity.SubmissionId, StringComparison.Ordinal)));
            }
            if (start.TaskId is not null && _activeTaskIds.TryGetValue(start.TaskId, out var reservation))
                reservation.AdmittedTurn = running;
            return new RegisteredTurn(state, running, cts, turnIdentity);
        }
        catch
        {
            // A half-registered turn left in the running set would keep the agent busy forever.
            state.Remove(running.Id);
            cts.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Announces a registered turn and starts its task. If anything here throws before the task
    /// exists, the turn leaves the running set again and the queue is offered the slot.
    /// </summary>
    private void LaunchTurn(TurnStart start, RegisteredTurn registered)
    {
        var (state, running, cts, turnIdentity) = registered;
        var (chatId, taskId, userId, source) = (start.ChatId, start.TaskId, start.UserId, start.Source);
        string task;
        try
        {
            // Which prompt the turn needs is decided here, where its task starts, not where the
            // message arrived: a provider process restarted while the message waited holds no
            // conversation, and the prompt built for a warm one carries none.
            task = start.ColdStartTask is not null && !_executor.IsProcessWarm ? start.ColdStartTask : start.Task;

            PublishEvent(chatId, ConversationEventKind.TurnStarted, turnIdentity, new TurnStartedPayload());

            // Register in user-level index for cross-chat cancel
            if (userId != 0)
            {
                var userList = _userTasks.GetOrAdd(userId, _ => []);
                lock (userList) userList.Add((chatId, running.Id));
            }

            // Notify orchestrator immediately that agent is now busy
            OnStatusChanged?.Invoke();

            // Accepted BEFORE the turn's task exists. A client sees turn.started, then
            // submission.accepted, then the turn's own events, and relies on that order. Published
            // after Task.Run, the acceptance raced the turn: on a busy machine the turn's first
            // progress events could be out before it.
            ReportDisposition(chatId, turnIdentity, TaskDispatchOutcome.Ran);

            // Only now may a message for this chat be delivered into the turn. Until its task
            // exists the executor has not been given the turn, so an injection would go nowhere
            // the turn could answer from; such a message takes the queue path instead.
            running.Launched = true;
        }
        catch
        {
            // Release unconditionally: an exception here means the turn was registered but never
            // ran — without TryRemove the taskId leaks permanently.
            if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
            cts.Dispose();
            HandOver(state, running.Id);
            throw;
        }

        _ = Task.Run(async () =>
        {
            var bindingSequence = _turnBindings?.BeginTurn(chatId, source);
            try
            {
                await new TurnRun(_turnDependencies, chatId, running.Id, task, start.IsSessionTask, source, start.RelaySender,
                    start.CorrelationId, taskId, start.Images, start.Documents, turnIdentity, cts.Token).RunAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in task #{TaskId} for chat {ChatId}", running.Id, chatId);
            }
            finally
            {
                try
                {
                if (bindingSequence is { } sequence) _turnBindings?.EndTurn(sequence);
                await running.TurnDispatchLock.WaitAsync();
                try
                {
                    // Closed, but still in the running set: the turn keeps counting as running
                    // until HandOver below takes it out and, in the same step, either starts the
                    // next queued entry or leaves the agent idle.
                    running.Closed = true;
                    _intake.DrainInboxToGlobalQueue(chatId, running);
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
                }
                catch (Exception ex)
                {
                    TryLog(LogLevel.Error, ex, "Teardown of task #{TaskId} for chat {ChatId} failed part-way", running.Id, chatId);
                }
                finally
                {
                    // On every path, including a teardown that threw: a finished turn that stayed in
                    // the running set would refuse every later message.
                    HandOver(state, running.Id);
                }
            }
        });
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
        // Nor into a turn whose task has not been started yet (RunningTask.Launched): a message
        // that arrives in that instant takes the queue path, as it did before the turn registered.
        running = state.Snapshot().FirstOrDefault(t => t.IsSessionTask && t.Launched
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
    /// The execution id kept with the task that holds this task id, null when it has none or
    /// nothing holds the id. Read by <see cref="RelayCompletionPublisher"/> while the task's
    /// completion is being raised, which is before the task lets go of its task id, so that the
    /// answer says which execution it is from.
    /// </summary>
    public string? ExecutionIdOf(string taskId) =>
        _activeTaskIds.TryGetValue(taskId, out var reservation) ? reservation.ExecutionId : null;

    /// <summary>
    /// Cancels the task with the given bridge taskId, but only if it is the execution the cancel
    /// names. A workflow reuses a task id when it runs a step again, so a cancel issued for one
    /// attempt can arrive when the id belongs to the next; <see cref="CancelByBridgeTaskIdAsync"/>
    /// would stop that one. A durable delegation's directive carries an execution id, kept with
    /// the task for as long as it is queued or running, and its cancel carries the same id.
    ///
    /// <para>
    /// Nothing is stopped unless the ids are equal: not a task that has finished or never arrived
    /// (<see cref="BridgeCancelOutcome.NotFound"/>), not a task that is now another execution's
    /// (<see cref="BridgeCancelOutcome.OtherExecution"/>), and not a task whose directive carried
    /// no id (<see cref="BridgeCancelOutcome.NoExecutionId"/>): that one was started by the
    /// in-process delegation path or by a bridge from before the id, so it cannot be the
    /// execution a cancel WITH an id was issued for. Every answer is final for the caller.
    /// </para>
    ///
    /// <para>
    /// The decision and the choice of what to stop are made in one hold of the admission gate. No
    /// task is queued or registered while it is held, and a task is queued or registered only
    /// while its own execution holds the reservation, so what is picked here belongs to the
    /// execution that was just compared. The reservation records its own admitted turn: a closed
    /// predecessor still awaiting handover does not count as this reservation's admitted work.
    /// </para>
    ///
    /// <para>
    /// A directive holds its task id slightly before it is admitted. A cancel that arrives in that
    /// moment finds the reservation and nothing under it. It does not answer that nothing was
    /// there: it marks the reservation, and the directive, which is admitted under the same gate,
    /// finds the mark and is neither queued nor run. Exactly one of the two gets the gate first,
    /// so the execution is either cancelled before it is admitted or admitted and cancelled where
    /// it is found. <see cref="CancelByBridgeTaskIdAsync"/> is not changed: a cancel without an
    /// execution id still misses a task in that moment, as it always did.
    /// </para>
    /// </summary>
    public async Task<BridgeCancelOutcome> CancelBridgeExecutionAsync(string bridgeTaskId, string executionId)
    {
        var running = new List<RunningTask>();
        IReadOnlyList<QueuedMessage> queued = [];
        BridgeCancelOutcome? refused = null;
        var beforeAdmission = false;

        lock (_admissionGate)
        {
            if (!_activeTaskIds.TryGetValue(bridgeTaskId, out var held))
                refused = BridgeCancelOutcome.NotFound;
            else if (held.ExecutionId is null)
                refused = BridgeCancelOutcome.NoExecutionId;
            else if (!string.Equals(held.ExecutionId, executionId, StringComparison.Ordinal))
                refused = BridgeCancelOutcome.OtherExecution;
            else
            {
                if (held.AdmittedTurn is { } admitted) running.Add(admitted);

                queued = _messageQueue.RemoveByTaskId(bridgeTaskId);
                foreach (var item in queued)
                    foreach (var part in item.Parts)
                        if (part.TaskId is not null) _activeTaskIds.TryRemove(part.TaskId, out _);

                if (running.Count == 0 && queued.Count == 0)
                {
                    // Reserved and not admitted yet: its directive is on its way to this gate.
                    // It finds this mark there and stops, so it is cancelled as surely as a task
                    // found in the queue. The task id is released now, for whoever is next.
                    held.CancelledBeforeAdmission = true;
                    Release(bridgeTaskId, held);
                    beforeAdmission = true;
                }
            }
        }

        if (refused is { } outcome)
        {
            _logger.LogInformation(
                "CancelBridgeExecution: nothing cancelled for bridgeTaskId={BridgeTaskId}, executionId={ExecutionId}: {Outcome}",
                bridgeTaskId, executionId, outcome);
            return outcome;
        }

        foreach (var t in running)
        {
            t.CancelReason = TurnCancelReason.Bridge;
            try { await t.Cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        foreach (var item in queued)
            foreach (var part in item.Parts)
                foreach (var key in QueueIntake.KeysOfPart(part)) _primaryDedup.Complete(item.ChatId, key);

        _logger.LogInformation(
            "CancelBridgeExecution: cancelled bridgeTaskId={BridgeTaskId}, executionId={ExecutionId} ({Where})",
            bridgeTaskId, executionId, beforeAdmission ? "before it was admitted" : running.Count > 0 ? "running" : "queued");
        return BridgeCancelOutcome.Cancelled;
    }

    /// <summary>
    /// Cancels all running tasks and clears the pending queue.
    /// Used by the HTTP cancel endpoint invoked from the orchestrator dashboard.
    /// </summary>
    public async Task CancelAllAsync()
    {
        // Drain the queue FIRST so that when cancelling running tasks triggers their hand-over,
        // there is nothing left to dequeue and no queued task id reservation is accidentally
        // promoted to a running one before we release it.
        foreach (var msg in _messageQueue.Clear())
            foreach (var part in msg.Parts)
            {
                if (part.TaskId is not null) _activeTaskIds.TryRemove(part.TaskId, out _);
                foreach (var key in QueueIntake.KeysOfPart(part)) _primaryDedup.Complete(msg.ChatId, key);
            }

        // Cancel all running tasks (their finally blocks hand over, find an empty queue and
        // leave the agent idle).
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
    /// Starts the next queued entry if no turn is running. For an enqueue that could not know
    /// whether a turn was still there to hand over to it.
    /// </summary>
    private void DrainQueue() => HandOver(finishedState: null, finishedTaskId: 0);

    /// <summary>
    /// The one step in which the slot changes hands: the finished turn (if any) leaves the running
    /// set and, in the same hold of the gate, the next queued entry's turn is registered. Either a
    /// turn is registered when the gate is released, or the agent is idle and the next admission
    /// runs at once. No newcomer can get in between, and no observer can see the agent idle while
    /// queued work is about to start.
    /// </summary>
    private void HandOver(ChatTaskState? finishedState, int finishedTaskId)
    {
        while (true)
        {
            QueuedMessage? queued = null;
            TurnStart? start = null;
            RegisteredTurn? registered = null;
            Exception? fault = null;
            var turnLeft = finishedState is not null;
            lock (_admissionGate)
            {
                finishedState?.Remove(finishedTaskId);
                finishedState = null;
                if (!AnyTurnRunning() && _messageQueue.TryDequeue(out queued))
                {
                    try
                    {
                        // Identity and the merged-submission list must survive the queue. Without them the turn
                        // runs under a freshly synthesized identity, so the submission that produced
                        // submission.accepted{queued} never receives a terminal event and a client waits forever.
                        var payload = MidTurnText.WithSteeredPrefix(queued).BuildPayload(DateTimeOffset.Now);
                        start = new TurnStart(queued.ChatId, payload.Task, payload.DisplayText, payload.IsSessionTask,
                            payload.Source, payload.RelaySender, payload.CorrelationId, payload.TaskId,
                            payload.Images, payload.Documents, payload.UserId,
                            payload.Identity ?? SynthesizeIdentity(queued.ChatId, payload.Source),
                            payload.MergedSubmissionIds,
                            queued.Parts.SelectMany(QueueIntake.KeysOfPart).Distinct().ToArray(),
                            payload.ColdStartTask);
                        registered = RegisterTurnLocked(start);
                    }
                    catch (Exception ex)
                    {
                        fault = ex;
                    }
                }
            }

            if (queued is null)
            {
                // Notify orchestrator immediately that agent is now idle. Only when a turn actually
                // left: a drain attempt that found nothing to do has changed nothing.
                if (turnLeft)
                {
                    try { OnStatusChanged?.Invoke(); }
                    catch (Exception ex) { TryLog(LogLevel.Error, ex, "A status subscriber threw after a turn ended"); }
                }
                return;
            }

            if (fault is null)
            {
                try
                {
                    QueueEntryClaimedForTest?.Invoke();

                    _logger.LogInformation("Queue dispatch lane={lane} reason={reason} chat={chat} parts={parts} waitedMs={waitedMs}",
                        queued.Priority == TaskPriority.PrimaryHuman ? "priority" : "routine", "queue_drain",
                        queued.ChatId, queued.PartCount, Math.Max(0, (DateTimeOffset.Now - queued.QueuedAt).TotalMilliseconds));
                    if (queued.BusyNoticeSent)
                        _ = _sink.SendTextByOriginAsync(queued.ChatId, "Now processing your queued message...", OriginOf(queued.Source));

                    LaunchTurn(start!, registered!);
                    RemovePendingIndexIfCurrent(queued);
                    return;
                }
                catch (Exception ex)
                {
                    fault = ex;
                    // LaunchTurn takes its own turn out of the running set when it fails; a failure
                    // before it (the hook, the log line) leaves that to us. Both are safe to repeat.
                    lock (_admissionGate) registered!.State.Remove(registered.Running.Id);
                    registered.Cts.Dispose();
                }
            }

            // The entry was claimed and cannot start. It is gone, so release what it held and go
            // round again for the next one instead of leaving queued work with nothing to start it.
            TryLog(LogLevel.Error, fault, "Queued entry for chat {ChatId} ({Source}, {Parts} part(s)) could not be started and was dropped",
                queued.ChatId, queued.Source, queued.PartCount);
            foreach (var part in queued.Parts)
            {
                if (part.TaskId is not null) _activeTaskIds.TryRemove(part.TaskId, out _);
                foreach (var key in QueueIntake.KeysOfPart(part)) _primaryDedup.Complete(queued.ChatId, key);
            }
            _messageQueue.RemovePendingIfCurrent(queued);
        }
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

        public void DrainQueue() => owner.DrainQueue();

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
