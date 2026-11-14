using System.Collections.Concurrent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

public sealed class TaskManager
{
    private readonly AgentOptions _agentConfig;
    private readonly IAgentExecutor _executor;
    private readonly SessionManager _sessions;
    private readonly ILogger<TaskManager> _logger;
    private readonly InjectionOutcomeCounter _injectionCounter;

    private const int MaxMidTurnInjectionsPerTurn = 3;

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
    private const int MaxQueuedPartsPerEntry = QueuedMessage.MaxParts;

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

    private enum PendingQueueResult
    {
        NoPending,
        Merged,
        EnqueueFresh,
    }

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

    /// <summary>The clock the stall watchdog runs on. A parameter so a test can move it.</summary>
    private readonly TimeProvider _time;

    /// <summary>
    /// Starts the silence limit for one read of the executor's event stream. See
    /// <see cref="TurnStallWatch"/>; <c>Agent:TurnStallTimeoutMinutes</c> = 0 disables it.
    /// </summary>
    private TurnStallWatch WatchForStall(CancellationToken turnToken) =>
        new(TimeSpan.FromMinutes(_agentConfig.TurnStallTimeoutMinutes), turnToken, _time);

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
        _time = timeProvider ?? TimeProvider.System;
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
        bool steeringEligible = false, string? executionId = null)
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
                priority: priority, telegramMessageId: telegramMessageId, steeringEligible: steeringEligible, executionId: executionId);
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
        bool steeringEligible = false, string? executionId = null)
    {
        var dispatch = StartTask(chatId, task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
            images, documents, userId, identity, priority, telegramMessageId, steeringEligible, executionId);
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
        bool steeringEligible = false, string? executionId = null)
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
                    images, documents, userId, DateTimeOffset.UtcNow, identity, priority, telegramMessageId);
                if (priority == TaskPriority.PrimaryHuman && telegramMessageId > 0)
                {
                    lock (runningSession.PrimaryMessageIds) runningSession.PrimaryMessageIds.Add(telegramMessageId);
                }
                return await DeliverMidTurnMessageAsync(chatId, runningSession, message);
            }

            if (source == TaskSource.CheckIn)
            {
                Release(taskId, reservation);
                var message = new MidTurnMessage(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
                    images, documents, userId, DateTimeOffset.UtcNow, identity, priority, telegramMessageId);
                return await DeferUntilTurnEndAsync(chatId, runningSession, message, notifyUser: false);
            }

            if (source is TaskSource.Relay or TaskSource.Bridge)
                _logger.LogInformation("Not injecting {Source} task into running conversational turn for chat {ChatId}; using normal capacity path", source, chatId);
        }

        var queuedPart = CreateQueuedPart(task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, documents, userId, identity: identity, priority: priority, telegramMessageId: telegramMessageId);
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

            pendingResult = TryAppendToPendingQueue(chatId, queuedPart);
            if (pendingResult == PendingQueueResult.Merged)
            {
                ReportDisposition(chatId, identity, TaskDispatchOutcome.Queued);
            }
            else if (pendingResult == PendingQueueResult.EnqueueFresh && source != TaskSource.CheckIn)
            {
                pendingNotify = source != TaskSource.DebouncedGroupBatch && BusyNoticeApplies(chatId, source);
                pendingEnqueued = _messageQueue.TryEnqueue(chatId, queuedPart, out pendingEntry, out pendingPosition);
                if (pendingEnqueued)
                {
                    if (BusyNoticeDue(chatId, pendingNotify, deferNotice: steer)) pendingEntry!.BusyNoticeSent = true;
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
            if (steer) await SteerRunningTurnAsync(chatId, userId, queuedPart, images, documents);
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
            AfterEnqueue(pendingEnqueued, chatId, queuedPart, pendingNotify, completeBridgeOnDrop: true,
                pendingEntry, pendingPosition, deferNotice: steer);
            if (!pendingEnqueued)
            {
                // Release the reservation only when enqueue failed — a queued task keeps it.
                Release(taskId, reservation);
                return ReportDisposition(chatId, identity, TaskDispatchOutcome.QueueFull);
            }
            if (steer)
                await SteerAndNotifyAsync(chatId, userId, queuedPart, images, documents, pendingEntry!, pendingPosition,
                    pendingNotify && !(_agentConfig.SuppressToolMessages && chatId < 0));
            return TaskDispatchOutcome.Queued;
        }

        // Each agent has one persistent executor process and the executor's send lock is
        // held for a full turn. A second "concurrent" turn would just block behind that
        // lock while bypassing queue notices and coalescing, so the runtime is explicitly
        // one-at-a-time until executors can actually interleave turns.
        var start = new TurnStart(chatId, task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
            images, documents, userId, identity, MergedSubmissionIds: null,
            PrimaryMessageIds: priority == TaskPriority.PrimaryHuman && telegramMessageId > 0 ? [telegramMessageId] : []);
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
                    notifyUser = source != TaskSource.DebouncedGroupBatch && BusyNoticeApplies(chatId, source);
                    // Enqueued in the same hold as the busy decision: the turn that made the agent
                    // busy cannot hand over before this entry is in the queue.
                    enqueued = _messageQueue.TryEnqueue(chatId, queuedPart, out entry, out position);
                    if (enqueued)
                    {
                        if (BusyNoticeDue(chatId, notifyUser, deferNotice: steer)) entry!.BusyNoticeSent = true;
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

            AfterEnqueue(enqueued, chatId, queuedPart, notifyUser, completeBridgeOnDrop: true, entry, position, deferNotice: steer);
            if (!enqueued)
            {
                // Release the reservation only when enqueue failed — a queued task keeps it.
                Release(taskId, reservation);
                return ReportDisposition(chatId, identity, TaskDispatchOutcome.QueueFull);
            }
            if (steer)
                await SteerAndNotifyAsync(chatId, userId, queuedPart, images, documents, entry!, position,
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

    /// <summary>Everything a turn is started with, whether it runs at once or comes off the queue.</summary>
    private sealed record TurnStart(
        long ChatId, string Task, string DisplayText, bool IsSessionTask, TaskSource Source,
        string? RelaySender, string? CorrelationId, string? TaskId,
        IReadOnlyList<MessageImage>? Images, IReadOnlyList<MessageDocument>? Documents, long UserId,
        ConversationIdentity Identity, IReadOnlyList<string>? MergedSubmissionIds, IReadOnlyList<long> PrimaryMessageIds);

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
        try
        {
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
                await ProcessTask(chatId, running.Id, start.Task, start.DisplayText, start.IsSessionTask, source, start.RelaySender,
                    start.CorrelationId, taskId, start.Images, start.Documents, turnIdentity, cts.Token);
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
                    DrainInboxToGlobalQueue(chatId, running);
                    lock (running.PrimaryMessageIds)
                        foreach (var key in running.PrimaryMessageIds)
                            if (!_messageQueue.Snapshot().Any(e => e.ChatId == chatId && e.Parts.Any(p => KeysOfPart(p).Contains(key))))
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

    public async Task HandleStop(long chatId)
    {
        _logger.LogWarning("/stop received in chat {ChatId} — cancelling all tasks and clearing all sessions", chatId);

        foreach (var (_, state) in _chatTasks)
        {
            foreach (var t in state.Snapshot())
            {
                try { await t.Cts.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
        }

        _sessions.ClearAllSessions();
        await _executor.StopProcessAsync();
        await _sink.SendTextAsync(chatId, "halted");
    }

    public async Task HandleReset(long chatId)
    {
        var state = GetChatState(chatId);
        if (state.Count > 0)
        {
            await _sink.SendTextAsync(chatId, "Can't reset while tasks are running. Use /cancel all first.");
            return;
        }

        await _executor.StopProcessAsync();
        _sessions.ClearSession(chatId);
        await _sink.SendTextAsync(chatId, "Session cleared. Send a new task to start fresh.");
    }

    public async Task HandleStatus(long chatId)
    {
        var session = _sessions.GetSession(chatId) is not null ? "active" : "none";
        var buildCommit = Environment.GetEnvironmentVariable("FLEET_BUILD_COMMIT") ?? "unknown";
        var msg = $"Agent: {_agentConfig.Name}\nRole: {_agentConfig.Role}\nBuild: {buildCommit}\nProjects: {string.Join(", ", _agentConfig.Projects)}\nSession: {session}";

        var allChatTasks = GetAllRunningTasks();
        var totalCount = allChatTasks.Sum(x => x.Tasks.Count);

        if (totalCount == 0)
        {
            msg += "\nStatus: idle";
        }
        else
        {
            msg += $"\n\nRunning tasks ({totalCount}/1):";
            foreach (var (cid, tasks) in allChatTasks)
            {
                var chatLabel = cid == chatId ? "this chat" : $"chat {cid}";
                foreach (var t in tasks)
                {
                    var elapsed = DateTimeOffset.UtcNow - t.StartedAt;
                    var label = t.IsSessionTask ? " (session)" : "";
                    msg += $"\n  [#{t.Id}] {TruncateText(t.Description, 60)}{label} ({(int)elapsed.TotalSeconds}s) [{chatLabel}]";
                }
            }
        }

        // Background subagent tasks
        var bgTasks = _executor.GetActiveBackgroundTasks();
        if (bgTasks.Count > 0)
        {
            msg += $"\n\nBackground subagent tasks ({bgTasks.Count}):";
            foreach (var bt in bgTasks)
            {
                var summary = bt.Summary is not null ? $" — {TruncateText(bt.Summary, 60)}" : "";
                msg += $"\n  [{bt.TaskType}] {TruncateText(bt.Description, 60)}{summary} ({bt.ElapsedSeconds}s)";
            }
        }

        await _sink.SendTextAsync(chatId, msg);
    }

    public async Task HandleCancel(long chatId, string arg, long userId = 0,
        TurnCancelReason reason = TurnCancelReason.User)
    {
        var state = GetChatState(chatId);
        var tasks = state.Snapshot();

        // The reason is recorded on the RunningTask BEFORE Cts.Cancel(), because the catch block
        // in ProcessTask is the only place that can report it and by then the canceller is gone.
        void Mark(RunningTask t) => t.CancelReason = reason;

        if (tasks.Count == 0)
        {
            // Fall back to user-level index: find tasks the user started in other chats
            if (userId != 0 && _userTasks.TryGetValue(userId, out var userEntries))
            {
                List<(long ChatId, int TaskId, RunningTask Task)> crossChatTasks;
                lock (userEntries)
                {
                    crossChatTasks = userEntries
                        .Select(e => (e.ChatId, e.TaskId, Task: GetChatState(e.ChatId).Get(e.TaskId)))
                        .Where(e => e.Task is not null)
                        .Select(e => (e.ChatId, e.TaskId, e.Task!))
                        .ToList();
                }

                if (crossChatTasks.Count == 0)
                {
                    await _sink.SendTextAsync(chatId, "No active tasks to cancel.");
                    return;
                }

                if (crossChatTasks.Count == 1 && (arg == "" || arg.Equals("all", StringComparison.OrdinalIgnoreCase)))
                {
                    var (originChatId, _, t) = crossChatTasks[0];
                    Mark(t);
                    try { await t.Cts.CancelAsync(); } catch (ObjectDisposedException) { }
                    await _sink.SendTextAsync(chatId, $"Cancelling task from chat {originChatId}...");
                    if (originChatId != chatId)
                        await _sink.SendTextAsync(originChatId, "Task cancelled by user from another chat.");
                    return;
                }

                if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (originChatId, _, t) in crossChatTasks)
                    {
                        Mark(t);
                        try { await t.Cts.CancelAsync(); } catch (ObjectDisposedException) { }
                        if (originChatId != chatId)
                            await _sink.SendTextAsync(originChatId, "Task cancelled by user from another chat.");
                    }
                    await _sink.SendTextAsync(chatId, $"Cancelling {crossChatTasks.Count} task(s) from other chats...");
                    return;
                }

                // List them for the user to pick
                var crossChatList = "Your active tasks are in other chats. Use /cancel all or specify:\n";
                foreach (var (originChatId, tid, t) in crossChatTasks)
                {
                    var elapsed = DateTimeOffset.UtcNow - t.StartedAt;
                    crossChatList += $"  [chat {originChatId} #{tid}] {TruncateText(t.Description, 60)} ({(int)elapsed.TotalSeconds}s)\n";
                }
                crossChatList += "\nUse /cancel all to cancel all.";
                await _sink.SendTextAsync(chatId, crossChatList);
                return;
            }

            await _sink.SendTextAsync(chatId, "No active tasks to cancel.");
            return;
        }

        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var t in tasks)
            {
                Mark(t);
                try { await t.Cts.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
            await _sink.SendTextAsync(chatId, $"Cancelling all {tasks.Count} task(s)...");
            return;
        }

        if (int.TryParse(arg, out var id))
        {
            var task = state.Get(id);
            if (task is null)
            {
                await _sink.SendTextAsync(chatId, $"No task with ID #{id}.");
                return;
            }
            Mark(task);
            try { await task.Cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
            await _sink.SendTextAsync(chatId, $"Cancelling task [#{id}]...");
            return;
        }

        if (tasks.Count == 1)
        {
            var t = tasks[0];
            Mark(t);
            try { await t.Cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
            await _sink.SendTextAsync(chatId, "Cancelling the current task...");
            return;
        }

        var list = "Multiple tasks running. Specify which to cancel:\n";
        foreach (var t in tasks)
        {
            var elapsed = DateTimeOffset.UtcNow - t.StartedAt;
            list += $"  [#{t.Id}] {TruncateText(t.Description, 60)} ({(int)elapsed.TotalSeconds}s)\n";
        }
        list += "\nUse /cancel <id> or /cancel all";
        await _sink.SendTextAsync(chatId, list);
    }

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

    private static TurnCompletion MapCompletion(CompletionKind kind) => kind switch
    {
        CompletionKind.Completed => TurnCompletion.Completed,
        CompletionKind.Incomplete => TurnCompletion.Incomplete,
        CompletionKind.Idle => TurnCompletion.Idle,
        _ => TurnCompletion.Failed,
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

    private async Task ProcessTask(long chatId, int taskId, string task, string displayText,
        bool isSessionTask, TaskSource source, string? relaySender, string? correlationId, string? relayTaskId,
        IReadOnlyList<MessageImage>? images, IReadOnlyList<MessageDocument>? documents,
        ConversationIdentity identity, CancellationToken ct)
    {
        var state = GetChatState(chatId);

        // Every submission this turn answers: its own, plus anything coalesced or injected into it.
        // Deduplicated, because a submission that appears twice is as wrong as one that is missing —
        // a client correlating terminal events would count the same answer twice.
        IReadOnlyList<string> MergedIds()
        {
            var running = state.Get(taskId);
            if (running is null) return [];

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ids = new List<string>();
            foreach (var id in ((string[])[identity.SubmissionId, .. running.SnapshotMergedSubmissionIds()]))
            {
                if (!string.IsNullOrEmpty(id) && seen.Add(id))
                    ids.Add(id);
            }
            return ids;
        }

        var attachmentDir = _telegramAttachmentDir;
        string Prefix() => state.Count > 1 ? $"[#{taskId}] " : "";
        var origin = OriginOf(source);

        string? lastResult = null;
        string? lastError = null;
        var significantUpdates = 0;
        List<string> allAssistantTexts = [];
        ExecutionStats? stats = null;
        var errorResult = false;
        var processExitResult = false;
        var toolCalls = new List<(string Name, string Args)>();

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingTask = RunTypingLoopAsync(chatId, typingCts.Token, identity);

        string StatsSuffix()
        {
            if (!_agentConfig.ShowStats || stats is null) return "";
            if (toolCalls.Count > 0)
                stats.ToolCalls = toolCalls.Select(t => new Models.ToolCallEntry(t.Name, t.Args)).ToList();
            return $"\n{stats.Format()}";
        }

        async Task SendWithStatsAsync(string content, bool isReply = false)
        {
            // Guarantee: this must NEVER throw. Terminal-state callers rely on it to
            // finish so that OnTaskCompleted runs and the bridge/relay gets its answer.
            // A failed Telegram echo (e.g., bot not a member of the chat) is non-fatal —
            // the authoritative response goes out via OnTaskCompleted → relay.
            try
            {
                var statsText = StatsSuffix();
                var toolBlock = stats?.FormatToolBlock() ?? "";
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
                    await _sink.SendReplyByOriginAsync(chatId, reply, origin);
                else
                    await _sink.SendComposedByOriginAsync(chatId, reply, origin);
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
        async Task DeliverInjectedTurnAnswersAsync(int injectedCount)
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

                allAssistantTexts.Add(text);
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.AnsweredAsSeparateTurn);
                _logger.LogInformation(
                    "Task #{TaskId}: delivering the answer to an injected message that ran as its own turn", taskId);

                // The extra turn's own stats, and none of the first turn's tool calls.
                var (turnStats, turnToolCalls) = (stats, toolCalls.ToList());
                stats = extra.Stats;
                toolCalls.Clear();
                var marker = extra.IsErrorResult ? " [incomplete — executor reported an error]" : "";
                await SendWithStatsAsync($"{Prefix()}{text}{marker}", isReply: true);
                (stats, toolCalls) = (turnStats, turnToolCalls);

                var (extraText, extraTruncated) = ProtocolSanitizer.SanitizeAndBound(
                    text, ProtocolLimits.MaxNoticeTextChars, attachmentDir);
                PublishEvent(chatId, ConversationEventKind.TurnRecoveredAnswer, identity,
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
        async Task DiscardSteeringTurnAnswersAsync()
        {
            var steerCount = state.Get(taskId)?.SteerCount ?? 0;
            if (steerCount <= 0 || processExitResult) return;
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
            if (read < steerCount) Volatile.Write(ref _steeringResidue, 1);
        }

        // Grab the inbox for this task to receive mid-execution messages
        var inboxReader = state.Get(taskId)?.Inbox.Reader;

        // The silence limit for the executor read in flight; a fresh one for every read.
        TurnStallWatch? stall = null;

        try
        {
            var currentTask = task;
            IReadOnlyList<MessageImage>? currentImages = images;
            IReadOnlyList<MessageDocument>? currentDocuments = documents;
            var injectedIntoLastTurn = 0;

            while (true)
            {
                // #406: this executor turn consumes the late-result guard whatever its first event is.
                var steeringResidue = Interlocked.Exchange(ref _steeringResidue, 0) == 1;
                var firstEvent = true;
                stall?.Dispose();
                stall = WatchForStall(ct);
                await foreach (var progress in _executor.ExecuteAsync(currentTask, currentImages, currentDocuments, stall.Token))
                {
                    stall.Touch();
                    var guardsThisEvent = steeringResidue && firstEvent;
                    firstEvent = false;
                    // Generic provider recovery has no original conversation/submission owner.
                    // Segment tags describe provenance, not ownership. Suppress before even a
                    // recovered FinalResult can enter this client's completion text or sinks.
                    // Scoped injected answers use DeliverInjectedTurnAnswersAsync instead.
                    if (progress.EventType == "recovered_answer"
                        && identity.ChannelId == ConversationSouthAdapter.ClientChannel)
                    {
                        _injectionCounter.Increment(_agentConfig.Provider,
                            InjectionOutcomeCounter.UnownedClientRecoverySuppressed);
                        continue;
                    }

                    if (isSessionTask && progress.SessionId is not null)
                        _sessions.SetSession(chatId, progress.SessionId);

                    if (progress.FinalResult is not null)
                    {
                        lastResult = progress.FinalResult;
                        allAssistantTexts.Add(progress.FinalResult);
                    }

                    if (progress.Stats is not null)
                        stats = progress.Stats;

                    if (progress.EventType == "error")
                        lastError = progress.Summary;

                    if (progress.IsErrorResult)
                        errorResult = true;

                    if (progress.IsProcessExit)
                        processExitResult = true;

                    if (progress.EventType == "warning" && progress.IsSignificant)
                    {
                        // User-facing warning (e.g. provider capability notice) — deliver immediately
                        await _sink.SendTextByOriginAsync(chatId, progress.Summary, origin);
                        var (noticeText, noticeTruncated) = ProtocolSanitizer.SanitizeAndBound(
                            progress.Summary, ProtocolLimits.MaxNoticeTextChars, attachmentDir);
                        PublishEvent(chatId, ConversationEventKind.TurnNotice, identity,
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
                            await _sink.SendReplyByOriginAsync(chatId, new AgentReply(recovered), origin);
                            var (recoveredText, recoveredTruncated) = ProtocolSanitizer.SanitizeAndBound(
                                recovered, ProtocolLimits.MaxNoticeTextChars, attachmentDir);
                            PublishEvent(chatId, ConversationEventKind.TurnRecoveredAnswer, identity,
                                new TurnRecoveredAnswerPayload { Text = recoveredText, Truncated = recoveredTruncated ? true : null });
                        }
                    }
                    else if (progress.IsSignificant && progress.ToolName is not null)
                    {
                        significantUpdates++;
                        toolCalls.Add((ShortenToolName(progress.ToolName), TruncateArgs(progress.ToolArgs ?? "{}", _agentConfig.ToolArgsTruncateLength)));
                        // Suppress progress messages for check-ins — they may end up IDLE
                        // Also suppress when SuppressToolMessages is configured (e.g. for non-technical users)
                        if (!_agentConfig.SuppressToolMessages && source is not (TaskSource.CheckIn or TaskSource.DebouncedGroupBatch) && significantUpdates % 5 == 1)
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
                            await _sink.SendHtmlTextByOriginAsync(chatId, $"{htmlPrefix}<blockquote expandable>{encoded}</blockquote>", origin);
                        }
                        OnToolUse?.Invoke(chatId, progress.ToolName, progress.Summary);

                        // Only the tool NAME leaves the runtime. The Telegram string built a few
                        // lines above appends truncated tool ARGUMENTS, and reusing it here is
                        // specifically prohibited — arguments routinely carry absolute paths and
                        // secret-shaped values.
                        PublishEvent(chatId, ConversationEventKind.TurnProgress, identity,
                            new TurnProgressPayload
                            {
                                Activity = ProgressActivity.Tool,
                                ToolName = ProtocolSanitizer.BoundToolName(progress.ToolName),
                            });
                    }
                }

                // The read is over. The watch must not outlive it: a timer that ran out later, while
                // the answer is being delivered, would make an unrelated cancellation look like a stall.
                stall.Dispose();
                stall = null;

                var completingTask = state.Get(taskId);
                if (completingTask is not null)
                    await completingTask.TurnDispatchLock.WaitAsync(ct);

                try
                {
                    if (processExitResult && completingTask is not null && completingTask.InjectedMessagesForResume.Count > 0)
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
                        identity = identity
                            .WithTurn(Guid.NewGuid().ToString("N"))
                            .WithAttempt(identity.Attempt + 1);
                        completingTask.Identity = identity;
                        completingTask.TerminalPublished = false;
                        PublishEvent(chatId, ConversationEventKind.TurnStarted, identity, new TurnStartedPayload());

                        completingTask.InjectionCount = 0;
                        currentTask = firstRedelivery.Task;
                        currentImages = firstRedelivery.Images;
                        currentDocuments = firstRedelivery.Documents;
                        lastError = null;
                        errorResult = false;
                        processExitResult = false;
                        continue;
                    }

                    // After turn completes, atomically drain the fallback inbox before
                    // closing the live-delivery window. This prevents a message from
                    // being enqueued between TryRead(false) and Closed=true.
                    // Consecutive compatible messages are coalesced into a single merged
                    // turn so a burst that was refused by the final-answer gate doesn't
                    // produce one reply per message.
                    if (inboxReader is not null && inboxReader.TryRead(out var firstFallback))
                    {
                        _logger.LogInformation("Task #{TaskId}: coalescing inbox fallback messages into merged turn", taskId);

                        // Drain all waiting messages so we can merge compatible ones.
                        var drained = new List<MidTurnMessage> { firstFallback };
                        while (inboxReader.TryRead(out var additional))
                            drained.Add(additional);

                        // Build the first merged group using the same CanMerge / MaxParts
                        // semantics as the global chat-level queue coalescing path.
                        var firstPart = CreateQueuedPart(drained[0].Task, drained[0].DisplayText, drained[0].IsSessionTask,
                            drained[0].Source, drained[0].RelaySender, drained[0].CorrelationId, drained[0].TaskId,
                            drained[0].Images, drained[0].Documents, drained[0].UserId, drained[0].ArrivedAt,
                            drained[0].Identity);
                        var mergedEntry = new QueuedMessage(chatId, firstPart);

                        // overflowStart marks where the first group ends; default to "all fit".
                        var overflowStart = drained.Count;
                        for (var i = 1; i < drained.Count; i++)
                        {
                            var part = CreateQueuedPart(drained[i].Task, drained[i].DisplayText, drained[i].IsSessionTask,
                                drained[i].Source, drained[i].RelaySender, drained[i].CorrelationId, drained[i].TaskId,
                                drained[i].Images, drained[i].Documents, drained[i].UserId, drained[i].ArrivedAt,
                                drained[i].Identity);
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
                                EnqueueMessage(chatId, drained[i], notifyUser: false);
                        }

                        foreach (var pending in drained.Take(overflowStart))
                            if (pending.Priority == TaskPriority.PrimaryHuman && pending.TelegramMessageId > 0)
                                _primaryDedup.Dispatched(chatId, pending.TelegramMessageId);

                        // Deliver the completed turn's result before starting the next merged turn.
                        if (lastResult is not null && !errorResult
                            && !lastResult.Trim().Equals("IDLE", StringComparison.OrdinalIgnoreCase))
                        {
                            await SendWithStatsAsync($"{Prefix()}{lastResult}", isReply: true);
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
                        if (!ProtocolSanitizer.IsIdleMarker(lastResult))
                        {
                            var (continuationText, continuationTruncated) = ProtocolSanitizer.SanitizeAndBound(
                                lastResult ?? "", ProtocolLimits.MaxFinalTextChars, attachmentDir);
                            PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, identity,
                                new TurnFinalPayload
                                {
                                    Text = continuationText,
                                    Completion = errorResult ? TurnCompletion.Incomplete : TurnCompletion.Completed,
                                    IsPartial = errorResult,
                                    Truncated = continuationTruncated,
                                    MergedSubmissionIds = MergedIds(),
                                });
                        }
                        lastResult = null;

                        var payload = mergedEntry.BuildPayload(DateTimeOffset.Now);
                        currentTask = payload.Task;
                        currentImages = payload.Images;
                        currentDocuments = payload.Documents;

                        // A continuation is a NEW turn answering a NEW set of submissions: mint a
                        // fresh TurnId, keep the conversation, and adopt the merged entry's
                        // submission ids so each coalesced part terminates on the client.
                        identity = (payload.Identity ?? identity).WithTurn(Guid.NewGuid().ToString("N"));
                        if (completingTask is not null)
                        {
                            completingTask.Identity = identity;
                            var continuationSubmission = identity.SubmissionId;
                            completingTask.ReplaceMergedSubmissionIds((payload.MergedSubmissionIds ?? [])
                                .Where(mergedId => !string.Equals(mergedId, continuationSubmission, StringComparison.Ordinal)));
                            // A continuation turn has not published its terminal event yet, so the
                            // reaper must be armed again for it.
                            completingTask.TerminalPublished = false;
                        }
                        PublishEvent(chatId, ConversationEventKind.TurnStarted, identity, new TurnStartedPayload());

                        if (completingTask is not null)
                        {
                            completingTask.InjectionCount = 0;
                            completingTask.SteerCount = 0;
                            completingTask.InjectedMessagesForResume.Clear();
                        }
                        // Reset per-turn state but accumulate texts and stats
                        lastError = null;
                        errorResult = false;
                        processExitResult = false;
                        continue;
                    }

                    if (completingTask is not null)
                    {
                        injectedIntoLastTurn = completingTask.InjectionCount;
                        completingTask.InjectedMessagesForResume.Clear();
                        completingTask.Closed = true;
                    }
                }
                finally
                {
                    completingTask?.TurnDispatchLock.Release();
                }

                break;
            }

            // IDLE is an internal contract marker — never deliver it to chat,
            // regardless of which task source emitted it.
            if (lastResult is not null && lastResult.Trim().Equals("IDLE", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Result is IDLE-only, suppressing output for chat {ChatId} (source={Source})", chatId, source);
                await DiscardSteeringTurnAnswersAsync();
                // Relay/bridge callers have a delegate step waiting — fire OnTaskCompleted with
                // CompletionKind.Idle so the workflow can advance instead of hanging to retry exhaustion.
                if (source is TaskSource.Relay or TaskSource.Bridge)
                    OnTaskCompleted?.Invoke(chatId, "", relaySender, source, false, correlationId, relayTaskId, CompletionKind.Idle);

                // Publish for EVERY conversation, not only client-owned ones. This is not
                // cosmetic: the terminal event is what sets TerminalPublished, and without it the
                // reaper would emit turn_reaped on every Telegram idle check-in — a fabricated
                // failure signal on the single most common no-op path in the runtime.
                //
                // Nothing a Telegram user sees changes: the chat output stays suppressed, the
                // relay callback keeps its existing Idle behaviour, and the published event is
                // simply counted as not_routed. The literal string "IDLE" never reaches text.
                PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, identity,
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
            if (source is TaskSource.CheckIn or TaskSource.DebouncedGroupBatch && lastResult is null)
            {
                _logger.LogInformation("{Source}: no output, suppressing for chat {ChatId}", source, chatId);
                // Same reasoning as the IDLE branch above — a no-output DebouncedGroupBatch is the
                // other path that would otherwise reach the reaper with nothing published.
                PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, identity,
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

            if (lastResult is not null)
            {
                // IsErrorResult covers max-turns exhaustion, auth failures, RPC errors and
                // executor crashes alike — so the marker must not assert a specific cause.
                // It previously claimed "hit limit" for every one of them, which sends
                // anyone debugging a dead executor after the wrong problem.
                var marker = errorResult ? " [incomplete — executor reported an error]" : "";
                // Send the final text to Telegram, but relay ALL assistant texts
                // so that agent addresses from intermediate turns aren't lost
                await SendWithStatsAsync($"{Prefix()}{lastResult}{marker}", isReply: true);
                await DeliverInjectedTurnAnswersAsync(injectedIntoLastTurn);
                await DiscardSteeringTurnAnswersAsync();
                var fullText = string.Join("\n", allAssistantTexts);
                // errorResult covers max-turns exhaustion (IsErrorResult from executor) — use
                // Incomplete so the workflow continuation loop can retry, not Failed which abandons.
                OnTaskCompleted?.Invoke(chatId, fullText, relaySender, source, errorResult, correlationId, relayTaskId, errorResult ? CompletionKind.Incomplete : CompletionKind.Completed);

                // turn.final carries the CHAT-VISIBLE text (lastResult), matching what the main
                // reply path delivers. The concatenated all-texts form above stays exclusive to
                // the relay callback — the two genuinely differ, and collapsing them into one
                // canonical string would silently change one of the two consumers.
                var (finalText, finalTruncated) = ProtocolSanitizer.SanitizeAndBound(
                    lastResult, ProtocolLimits.MaxFinalTextChars, attachmentDir);
                PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, identity,
                    new TurnFinalPayload
                    {
                        Text = finalText,
                        Completion = errorResult ? TurnCompletion.Incomplete : TurnCompletion.Completed,
                        IsPartial = errorResult,
                        Truncated = finalTruncated,
                        MergedSubmissionIds = MergedIds(),
                    });
            }
            else if (lastError is not null)
            {
                if (isSessionTask)
                    _sessions.ClearSession(chatId);
                var errorMsg = $"Task failed: {lastError}";
                await SendWithStatsAsync($"{Prefix()}{errorMsg}");
                await DiscardSteeringTurnAnswersAsync();
                OnTaskCompleted?.Invoke(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId, CompletionKind.Failed);

                // The client event carries the FIXED message for the code, never lastError.
                // Provider text and executor output routinely contain absolute paths, session ids
                // and credentials. Telegram still shows the raw string — that is the owner's own
                // channel and is unchanged.
                PublishTerminalError(chatId, taskId, identity, ProtocolErrorCode.ExecutorError);
            }
            else if (errorResult)
            {
                // A result event flagged as an error but carrying no text: the turn failed
                // and produced nothing. Reporting "Done!" here is what let a dead executor
                // look healthy — the error flag must survive even with no output to show.
                var errorMsg = "Task failed: executor reported an error and produced no output";
                _logger.LogError("Task #{TaskId} for chat {ChatId}: {Error}", taskId, chatId, errorMsg);
                await SendWithStatsAsync($"{Prefix()}{errorMsg}");
                await DiscardSteeringTurnAnswersAsync();
                OnTaskCompleted?.Invoke(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId, CompletionKind.Failed);
                PublishTerminalError(chatId, taskId, identity, ProtocolErrorCode.ExecutorError);
            }
            else
            {
                await SendWithStatsAsync($"{Prefix()}Done! (no text output)");
                await DiscardSteeringTurnAnswersAsync();
                OnTaskCompleted?.Invoke(chatId, "Done! (no text output)", relaySender, source, false, correlationId, relayTaskId, CompletionKind.Completed);
                PublishTerminal(chatId, taskId, ConversationEventKind.TurnFinal, identity,
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
        catch (OperationCanceledException) when (stall is { Stalled: true })
        {
            // Nobody cancelled this turn: the executor yielded nothing for the whole limit, so the
            // watchdog cancelled its read, and the executor's own cancel path has stopped the
            // provider process. Reported as a failure, never as "Task cancelled.", with a fixed
            // message that carries the limit and nothing else. The session is kept: the next
            // message resumes it.
            var errorMsg = TurnStallTimeout.FailureMessage(stall.Timeout);
            _logger.LogError(
                "Task #{TaskId} for chat {ChatId} stalled: no executor event for {Minutes} minutes; turn stopped (source={Source}, provider={Provider})",
                taskId, chatId, (int)stall.Timeout.TotalMinutes, source, _agentConfig.Provider);
            await SendWithStatsAsync($"{Prefix()}{errorMsg}");
            OnTaskCompleted?.Invoke(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId, CompletionKind.Failed);
            PublishTerminalError(chatId, taskId, identity, ProtocolErrorCode.ExecutorError);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Task #{TaskId} cancelled for chat {ChatId}", taskId, chatId);
            await SendWithStatsAsync($"{Prefix()}Task cancelled.");
            OnTaskCompleted?.Invoke(chatId, "Task cancelled.", relaySender, source, false, correlationId, relayTaskId, CompletionKind.Failed);

            // The chat-visible string and the callback payload are unchanged; only the reason is
            // new, and it was recorded by the canceller before Cts.Cancel().
            var cancelReason = state.Get(taskId)?.CancelReason ?? TurnCancelReason.Unknown;
            PublishTerminal(chatId, taskId, ConversationEventKind.TurnCanceled, identity,
                new TurnCanceledPayload { Reason = cancelReason, MergedSubmissionIds = MergedIds() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing task #{TaskId} for chat {ChatId}", taskId, chatId);
            var errorMsg = $"Error: {ex.Message}";
            await SendWithStatsAsync($"{Prefix()}{errorMsg}");
            if (isSessionTask)
                _sessions.ClearSession(chatId);
            OnTaskCompleted?.Invoke(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId, CompletionKind.Failed);

            // `internal` in particular must never carry the exception message.
            PublishTerminalError(chatId, taskId, identity, ProtocolErrorCode.Internal);
        }
        finally
        {
            stall?.Dispose();
            await typingCts.CancelAsync();
            await typingTask;
        }
    }


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
    private async Task SteerRunningTurnAsync(long chatId, long userId, QueuedMessagePart part,
        IReadOnlyList<MessageImage>? images, IReadOnlyList<MessageDocument>? documents)
    {
        SteerAttemptStartingForTest?.Invoke();
        if (!TryGetSteerableTurn(out var running)) return;
        string outcome;
        await running.TurnDispatchLock.WaitAsync();
        try
        {
            if (running.Closed) outcome = "closed";
            else if (running.SteeringOwner is { } owner && owner != (chatId, userId)) outcome = "refused_other_human";
            else if (running.SteerCount >= MaxMidTurnInjectionsPerTurn) outcome = "cap";
            else if (ExecutorBusyElsewhere) outcome = "executor_busy_elsewhere";
            else
            {
                var attachments = (images?.Count ?? 0) + (documents?.Count ?? 0);
                var result = await _executor.TryInjectMessageAsync(FormatSteeringMessage(part.Task, attachments),
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
    private async Task<TaskDispatchOutcome> DeliverMidTurnMessageAsync(long chatId, RunningTask running, MidTurnMessage message)
    {
        DispatchCheckpointForTest?.Invoke("mid_turn_delivery");
        await running.TurnDispatchLock.WaitAsync();
        try
        {
            var outcome = await DeliverMidTurnUnderLockAsync(chatId, running, message);
            ReportDisposition(chatId, message.Identity, outcome);
            // The closed turn may already have handed over, on an empty queue. Then nothing is
            // left to start this entry, so try here; it does nothing while any turn, that closed
            // one included, is still in the running set.
            if (outcome == TaskDispatchOutcome.Queued && running.Closed) DrainQueue();
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
                var enqueued = EnqueueMessage(chatId, message, notifyUser: BusyNoticeApplies(chatId, message.Source));
                if (enqueued) _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
                return enqueued ? TaskDispatchOutcome.Queued : TaskDispatchOutcome.QueueFull;
            }

            if (running.InjectionCount >= MaxMidTurnInjectionsPerTurn)
            {
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
                return await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: false);
            }

            if (ExecutorBusyElsewhere)
            {
                // The executor's live turn may be the warmup ping or a /run command, not this one.
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
                _logger.LogInformation(
                    "Mid-turn injection skipped for chat {ChatId}: the executor is running a turn started outside the task queue; queued for turn-end delivery",
                    chatId);
                return await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: false);
            }

            var result = await _executor.TryInjectMessageAsync(FormatInjectedMessage(message.Task),
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
                notifyUser: result.Status == MidTurnInjectionStatus.Failed && BusyNoticeApplies(chatId, message.Source));
        }
        catch (Exception ex)
        {
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.FailedThenQueued);
            _logger.LogWarning(ex, "Mid-turn injection failed for chat {ChatId}; queued for turn-end delivery", chatId);
            return await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: BusyNoticeApplies(chatId, message.Source));
        }
    }


    private async Task<TaskDispatchOutcome> DeferUntilTurnEndAsync(long chatId, RunningTask running, MidTurnMessage message, bool notifyUser)
    {
        await running.TurnDispatchLock.WaitAsync();
        try
        {
            // Check-ins are not conversational corrections, so do not fold them into
            // the current response chain. Queue them to start only after the current
            // task has sent its own terminal response and DrainQueue runs.
            var enqueued = EnqueueMessage(chatId, message, notifyUser);
            if (enqueued) _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
            var outcome = enqueued ? TaskDispatchOutcome.Queued : TaskDispatchOutcome.QueueFull;
            // Accepted before any drain, for the reason given on DeliverMidTurnMessageAsync.
            ReportDisposition(chatId, message.Identity, outcome);
            // Same as a message that found its turn closed: that turn's drain may already be behind us.
            if (enqueued && running.Closed) DrainQueue();
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

            var relayPart = CreateQueuedPart(message.Task, message.DisplayText, message.IsSessionTask, message.Source,
                message.RelaySender, message.CorrelationId, message.TaskId, message.Images, message.Documents,
                message.UserId, message.ArrivedAt, message.Identity, message.Priority, message.TelegramMessageId);
            var enqueued = EnqueueFreshMessage(chatId, relayPart, notifyUser, completeBridgeOnDrop: true);
            return enqueued ? TaskDispatchOutcome.Queued : TaskDispatchOutcome.QueueFull;
        }

        if (!running.Inbox.Writer.TryWrite(message))
        {
            var enqueued = EnqueueMessage(chatId, message, notifyUser);
            return enqueued ? TaskDispatchOutcome.Queued : TaskDispatchOutcome.QueueFull;
        }

        if (notifyUser && !(_agentConfig.SuppressToolMessages && chatId < 0))
        {
            try
            {
                await _sink.SendTextByOriginAsync(chatId, "I'm busy right now — your message is queued. I'll get to it once my current turn finishes.", OriginOf(message.Source));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send mid-turn queue notice for chat {ChatId}", chatId);
            }
        }
        OnStatusChanged?.Invoke();
        return TaskDispatchOutcome.Queued;
    }

    private void DrainInboxToGlobalQueue(long chatId, RunningTask running)
    {
        while (running.Inbox.Reader.TryRead(out var pending))
        {
            if (EnqueueMessage(chatId, pending, notifyUser: false))
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
        }
    }

    private bool EnqueueMessage(long chatId, MidTurnMessage message, bool notifyUser)
    {
        var part = CreateQueuedPart(message.Task, message.DisplayText, message.IsSessionTask, message.Source,
            message.RelaySender, message.CorrelationId, message.TaskId, message.Images, message.Documents, message.UserId,
            message.ArrivedAt, message.Identity, message.Priority, message.TelegramMessageId);

        var pendingResult = TryAppendToPendingQueue(chatId, part);
        if (pendingResult == PendingQueueResult.Merged)
        {
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.MergedIntoQueue);
            OnStatusChanged?.Invoke();
            return true;
        }

        return EnqueueFreshMessage(chatId, part, notifyUser, completeBridgeOnDrop: false);
    }

    private static IEnumerable<long> KeysOfPart(QueuedMessagePart part) =>
        (part.PrimaryMessageIds ?? []).Concat(part.Priority == TaskPriority.PrimaryHuman && part.TelegramMessageId > 0 ? [part.TelegramMessageId] : []);

    private QueuedMessagePart CreateQueuedPart(string task, string displayText, bool isSessionTask, TaskSource source,
        string? relaySender, string? correlationId, string? taskId, IReadOnlyList<MessageImage>? images,
        IReadOnlyList<MessageDocument>? documents, long userId, DateTimeOffset? arrivedAt = null,
        ConversationIdentity? identity = null, TaskPriority priority = TaskPriority.Routine, long telegramMessageId = 0)
    {
        var senderDisplay = relaySender ?? source.ToString().ToLowerInvariant();
        var arrival = arrivedAt?.ToLocalTime() ?? DateTimeOffset.Now;
        return new QueuedMessagePart(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
            images, documents, userId, arrival, senderDisplay, identity, priority, telegramMessageId);
    }

    private PendingQueueResult TryAppendToPendingQueue(long chatId, QueuedMessagePart part)
    {
        if (_messageQueue.TryAppend(chatId, part)) return PendingQueueResult.Merged;
        return _messageQueue.HasPending(chatId) ? PendingQueueResult.EnqueueFresh : PendingQueueResult.NoPending;
    }

    private bool EnqueueFreshMessage(long chatId, QueuedMessagePart part, bool notifyUser, bool completeBridgeOnDrop) =>
        EnqueueFreshMessage(chatId, part, notifyUser, completeBridgeOnDrop, out _, out _);

    /// <summary>
    /// Enqueues outside the admission gate, for callers that already know a turn is running or a
    /// handoff is in flight: a turn-end fallback or an overflow under the running turn's dispatch
    /// lock, or a message that found a pending entry it could not join. A caller that cannot know
    /// that (the turn it holds may already have gone) follows up with <see cref="DrainQueue"/>.
    /// </summary>
    private bool EnqueueFreshMessage(long chatId, QueuedMessagePart part, bool notifyUser, bool completeBridgeOnDrop,
        out QueuedMessage? queued, out int queuePos, bool deferNotice = false)
    {
        var enqueued = _messageQueue.TryEnqueue(chatId, part, out queued, out queuePos);
        return AfterEnqueue(enqueued, chatId, part, notifyUser, completeBridgeOnDrop, queued, queuePos, deferNotice);
    }

    private bool BusyNoticeDue(long chatId, bool notifyUser, bool deferNotice) =>
        !deferNotice && notifyUser && !(_agentConfig.SuppressToolMessages && chatId < 0);

    /// <summary>Logs, notices and callbacks for an enqueue attempt that has already been decided.</summary>
    private bool AfterEnqueue(bool enqueued, long chatId, QueuedMessagePart part, bool notifyUser, bool completeBridgeOnDrop,
        QueuedMessage? queued, int queuePos, bool deferNotice)
    {
        if (!enqueued)
        {
            _logger.LogWarning("Message queue full ({Max}) — dropping incoming task from chat {ChatId}", MaxQueueDepth, chatId);
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DroppedAtQueueCap);
            // DebouncedGroupBatch drops silently — the notice is noise for automated checks.
            if (part.Source != TaskSource.DebouncedGroupBatch)
                _ = _sink.SendTextByOriginAsync(chatId, $"Queue is full ({MaxQueueDepth} messages waiting). Please wait for tasks to complete.", OriginOf(part.Source));
            // Production relay passes taskId but no correlationId — gate on either so relay
            // completions are not dead code when the source has only a taskId.
            if (completeBridgeOnDrop && part.Source is TaskSource.Bridge or TaskSource.Relay
                    && (part.CorrelationId is not null || part.TaskId is not null))
                OnTaskCompleted?.Invoke(chatId, "agent queue full",
                    part.Source == TaskSource.Bridge ? "bridge" : part.RelaySender,
                    part.Source, false, part.CorrelationId, part.TaskId, CompletionKind.Failed);
            return false;
        }

        _logger.LogInformation("Message queued (position {Pos}) for chat {ChatId}; queue entries={EntryCount}, max parts per entry={MaxParts}",
            queuePos, chatId, _messageQueue.Count, MaxQueuedPartsPerEntry);
        if (BusyNoticeDue(chatId, notifyUser, deferNotice))
        {
            queued!.BusyNoticeSent = true;
            _ = _sink.SendTextByOriginAsync(chatId, $"I'm busy right now — your message is queued (position {queuePos}). I'll get to it once my current task finishes.", OriginOf(part.Source));
        }
        OnStatusChanged?.Invoke();
        return true;
    }

    private async Task SteerAndNotifyAsync(long chatId, long userId, QueuedMessagePart part,
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
            SteeringNoticePendingForTest?.Invoke();
            if (noticeDue) await SendSteeringQueueNoticeAsync(entry, chatId, position, part.Source);
        }
    }

    private async Task SendSteeringQueueNoticeAsync(QueuedMessage entry, long chatId, int position, TaskSource source)
    {
        var acquired = false;
        Task? send = null;
        try
        {
            NoticeLockWaitThrowsForTest?.Invoke();
            await entry.QueueDispatchLock.WaitAsync();
            acquired = true;
            if (entry.Claimed)
                TryLog(LogLevel.Information, null, "Steering notice skipped: queue entry already dispatched");
            else
            {
                var text = entry.FirstPart.Steered ? SteeringDeliveredNotice
                    : $"I'm busy right now — your message is queued (position {position}). I'll get to it once my current task finishes.";
                entry.BusyNoticeSent = true;
                send = _sink.SendTextByOriginAsync(chatId, text, OriginOf(source));
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

    /// <summary>
    /// Whether a message from <paramref name="chatId"/> that has to wait gets the busy notice (#369).
    /// It does only when the agent is busy on work that did not come from this chat: a turn or queued
    /// entry for another chat, or a Relay/Bridge directive. Waiting behind the chat's own turn,
    /// including its injected-answer read window, is silent. Relay/Bridge arrivals keep the notice.
    /// </summary>
    private bool BusyNoticeApplies(long chatId, TaskSource source)
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

    private void RemovePendingIndexIfCurrent(QueuedMessage queued) => _messageQueue.RemovePendingIfCurrent(queued);

    internal static string FormatInjectedMessage(string original) =>
        """
        [NEW MESSAGE — arrived while you were still working on the previous
        instruction. This is not a tool result. Decide whether to finish your
        current step, adjust your plan, or stop and address this first.]

        """ + original;

    /// <summary>Delivery receipt only; does not claim the model read or followed the message.</summary>
    internal const string SteeringDeliveredNotice = "message delivered to my current task, reply pending here.";

    /// <summary>Fixed header of a steering copy (#406). <see cref="FormatInjectedMessage"/> is not applied.</summary>
    internal const string SteeringHeader =
        """
        [HUMAN MESSAGE — a person wrote to you
        while you are doing workflow work. You
        may adjust or stop the current work
        because of it. Do not reply to the
        person here: this turn's final answer
        still goes only to the workflow. After
        this turn ends you get a separate turn
        in that person's chat to reply.]
        """;

    /// <summary>Prefix each steered part gets when its queued entry is drained into the reply turn.</summary>
    internal const string SteeredPartPrefix =
        "[You already saw this message during workflow work. That turn has ended. Reply to the person now.]";

    /// <summary>The steering header, then the already-formatted human prompt. Text only: media waits for the reply turn.</summary>
    internal static string FormatSteeringMessage(string prompt, int attachments) =>
        SteeringHeader + "\n\n" + prompt
        + (attachments > 0 ? $"\n\n({attachments} attachments will be available in the reply turn.)" : "");

    /// <summary>A copy of the entry with each steered part prefixed; the entry itself when none is.</summary>
    private static QueuedMessage WithSteeredPrefix(QueuedMessage queued)
    {
        var parts = queued.Parts;
        if (!parts.Any(p => p.Steered)) return queued;
        static QueuedMessagePart Prefixed(QueuedMessagePart p) =>
            p.Steered ? p with { Task = SteeredPartPrefix + "\n\n" + p.Task } : p;
        var rebuilt = new QueuedMessage(queued.ChatId, Prefixed(parts[0]));
        foreach (var part in parts.Skip(1))
            if (!rebuilt.TryAppendPart(Prefixed(part))) return queued;
        return rebuilt;
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

    private ChatTaskState GetChatState(long chatId) =>
        _chatTasks.GetOrAdd(chatId, _ => new ChatTaskState());

    private List<(long ChatId, List<RunningTask> Tasks)> GetAllRunningTasks() =>
        _chatTasks
            .Select(kv => (kv.Key, kv.Value.Snapshot()))
            .Where(x => x.Item2.Count > 0)
            .OrderBy(x => x.Key)
            .ToList();

    internal static string TruncateText(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static string ShortenToolName(string name)
    {
        if (!name.StartsWith("mcp__")) return name;
        var lastSep = name.LastIndexOf("__");
        return lastSep > 4 ? name[(lastSep + 2)..] : name;
    }

    private static string TruncateArgs(string args, int maxLength = 300) =>
        args.Length <= maxLength ? args : args[..maxLength] + "...";

    internal static string FormatRelativeTime(TimeSpan remaining)
    {
        if (remaining.TotalMinutes < 1)
            return $"~{(int)remaining.TotalSeconds}s";
        if (remaining.TotalHours < 1)
            return $"~{(int)remaining.TotalMinutes}m";
        if (remaining.TotalHours < 24)
            return $"~{remaining.TotalHours:0.#}h";
        if (remaining.TotalDays < 2)
            return "tomorrow";
        return $"in {(int)remaining.TotalDays} days";
    }

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
                    foreach (var key in KeysOfPart(part)) _primaryDedup.Complete(item.ChatId, key);
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
                foreach (var key in KeysOfPart(part)) _primaryDedup.Complete(item.ChatId, key);

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
                foreach (var key in KeysOfPart(part)) _primaryDedup.Complete(msg.ChatId, key);
            }

        // Cancel all running tasks (their finally blocks hand over, find an empty queue and
        // leave the agent idle).
        //
        // Reason is `operator`: this method's only caller is the operator HTTP cancel endpoint.
        // Host stop does NOT reach here — ProcessTask runs on the per-task CancellationTokenSource
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
                        var payload = WithSteeredPrefix(queued).BuildPayload(DateTimeOffset.Now);
                        start = new TurnStart(queued.ChatId, payload.Task, payload.DisplayText, payload.IsSessionTask,
                            payload.Source, payload.RelaySender, payload.CorrelationId, payload.TaskId,
                            payload.Images, payload.Documents, payload.UserId,
                            payload.Identity ?? SynthesizeIdentity(queued.ChatId, payload.Source),
                            payload.MergedSubmissionIds,
                            queued.Parts.SelectMany(KeysOfPart).Distinct().ToArray());
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
                foreach (var key in KeysOfPart(part)) _primaryDedup.Complete(queued.ChatId, key);
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
    public (string Status, string? CurrentTask, string? CurrentTaskId) GetOrchestratorStatus()
    {
        var allTasks = GetAllRunningTasks();
        if (allTasks.Count == 0 || allTasks.All(x => x.Tasks.Count == 0))
            return ("idle", null, null);

        var first = allTasks.SelectMany(x => x.Tasks).OrderBy(t => t.StartedAt).FirstOrDefault();
        return ("busy",
            first is not null ? TruncateText(first.Description, 500) : null,
            first?.BridgeTaskId);
    }

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
}
