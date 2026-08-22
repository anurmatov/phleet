using System.Collections.Concurrent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
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
    private readonly ConcurrentDictionary<string, bool> _activeTaskIds = new();

    // Global FIFO queue for messages that arrive while the agent is at capacity.
    private readonly ConcurrentQueue<QueuedMessage> _messageQueue = new();
    private readonly ConcurrentDictionary<long, QueuedMessage> _pendingQueueByChat = new();
    private readonly object _pendingQueueIndexLock = new();
    // MaxQueueDepth caps queue entries. MaxQueuedPartsPerEntry caps merged user messages
    // inside one entry; overflowing the parts cap creates a new entry, not a drop.
    private const int MaxQueueDepth = 20;
    private const int MaxQueuedPartsPerEntry = QueuedMessage.MaxParts;

    // user-level index: userId → list of (chatId, taskId) for cross-chat cancel
    private readonly ConcurrentDictionary<long, List<(long ChatId, int TaskId)>> _userTasks = new();

    private string _botUsername = "";

    private enum PendingQueueResult
    {
        NoPending,
        Merged,
        EnqueueFresh,
    }

    /// <summary>Set by AgentTransport after construction to break circular DI.</summary>
    public IMessageSink Sink { get; set; } = null!;

    internal Action? QueueEntryClaimedForTest { get; set; }
    internal Action? QueueEntryDequeuedForBridgeCancelForTest { get; set; }

    public TaskManager(
        IOptions<AgentOptions> agentConfig,
        IAgentExecutor executor,
        SessionManager sessions,
        ILogger<TaskManager> logger,
        InjectionOutcomeCounter? injectionCounter = null)
    {
        _agentConfig = agentConfig.Value;
        _executor = executor;
        _sessions = sessions;
        _logger = logger;
        _injectionCounter = injectionCounter ?? new InjectionOutcomeCounter();
    }

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

    public bool HasRunningTasks(long chatId) => GetChatState(chatId).Count > 0;

    public void StartTask(long chatId, string task, string displayText, bool isSessionTask,
        TaskSource source = TaskSource.UserMessage,
        string? relaySender = null,
        string? correlationId = null,
        string? taskId = null,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        long userId = 0) =>
        StartTaskCore(chatId, task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, documents, userId, skipPendingQueueCheck: false);

    private void StartTaskCore(long chatId, string task, string displayText, bool isSessionTask,
        TaskSource source = TaskSource.UserMessage,
        string? relaySender = null,
        string? correlationId = null,
        string? taskId = null,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        long userId = 0,
        bool skipPendingQueueCheck = false)
    {
        var state = GetChatState(chatId);

        // Dedup: ignore re-delivered bridge directives with the same taskId
        if (taskId is not null && !_activeTaskIds.TryAdd(taskId, true))
        {
            _logger.LogInformation("Duplicate taskId={TaskId} ignored (already in-flight)", taskId);
            return;
        }

        if (TryGetRunningSessionTask(state, out var runningSession))
        {
            if (source == TaskSource.UserMessage && isSessionTask)
            {
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                var message = new MidTurnMessage(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
                    images, documents, userId, DateTimeOffset.UtcNow);
                _ = DeliverMidTurnMessageAsync(chatId, runningSession, message);
                return;
            }

            if (source == TaskSource.CheckIn)
            {
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                var message = new MidTurnMessage(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
                    images, documents, userId, DateTimeOffset.UtcNow);
                _ = DeferUntilTurnEndAsync(chatId, runningSession, message, notifyUser: false);
                return;
            }

            if (source is TaskSource.Relay or TaskSource.Bridge)
                _logger.LogInformation("Not injecting {Source} task into running conversational turn for chat {ChatId}; using normal capacity path", source, chatId);
        }

        var queuedPart = CreateQueuedPart(task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, documents, userId);
        if (!skipPendingQueueCheck)
        {
            var pendingResult = TryAppendToPendingQueue(chatId, queuedPart);
            if (pendingResult == PendingQueueResult.Merged)
            {
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.MergedIntoQueue);
                OnStatusChanged?.Invoke();
                return;
            }

            if (pendingResult == PendingQueueResult.EnqueueFresh)
            {
                if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);
                if (source == TaskSource.CheckIn)
                {
                    // Preserve the existing capacity behavior: check-ins that cannot
                    // attach to a running turn are dropped, not queued behind user work.
                    _logger.LogDebug("Check-in skipped — agent already has a pending queued entry for chat {ChatId}", chatId);
                    return;
                }
                EnqueueFreshMessage(chatId, queuedPart, notifyUser: source != TaskSource.CheckIn, completeBridgeOnDrop: true);
                return;
            }
        }

        // Each agent has one persistent executor process and the executor's send lock is
        // held for a full turn. A second "concurrent" turn would just block behind that
        // lock while bypassing queue notices and coalescing, so the runtime is explicitly
        // one-at-a-time until executors can actually interleave turns.
        var hasRunningTask = _chatTasks.Values.Any(s => s.Count > 0);
        if (hasRunningTask)
        {
            // Undo the taskId reservation — we're not actually running it yet
            if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);

            // Check-ins silently skip when at capacity instead of queuing
            if (source == TaskSource.CheckIn)
            {
                _logger.LogDebug("Check-in skipped — agent already has a running task");
                return;
            }

            EnqueueFreshMessage(chatId, queuedPart, notifyUser: true, completeBridgeOnDrop: true);
            return;
        }

        var cts = new CancellationTokenSource();
        var running = state.Add(displayText, cts, isSessionTask, userId, bridgeTaskId: taskId);

        // Register in user-level index for cross-chat cancel
        if (userId != 0)
        {
            var userList = _userTasks.GetOrAdd(userId, _ => []);
            lock (userList) userList.Add((chatId, running.Id));
        }

        // Notify orchestrator immediately that agent is now busy
        OnStatusChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessTask(chatId, running.Id, task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, documents, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in task #{TaskId} for chat {ChatId}", running.Id, chatId);
            }
            finally
            {
                await running.TurnDispatchLock.WaitAsync();
                try
                {
                    running.Closed = true;
                    DrainInboxToGlobalQueue(chatId, running);
                    state.Remove(running.Id);
                }
                finally
                {
                    running.TurnDispatchLock.Release();
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
                // Drain one message from the queue — fire-and-forget via StartTask
                DrainQueue();
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
        await Sink.SendTextAsync(chatId, "halted");
    }

    public async Task HandleReset(long chatId)
    {
        var state = GetChatState(chatId);
        if (state.Count > 0)
        {
            await Sink.SendTextAsync(chatId, "Can't reset while tasks are running. Use /cancel all first.");
            return;
        }

        await _executor.StopProcessAsync();
        _sessions.ClearSession(chatId);
        await Sink.SendTextAsync(chatId, "Session cleared. Send a new task to start fresh.");
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

        await Sink.SendTextAsync(chatId, msg);
    }

    public async Task HandleCancel(long chatId, string arg, long userId = 0)
    {
        var state = GetChatState(chatId);
        var tasks = state.Snapshot();

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
                    await Sink.SendTextAsync(chatId, "No active tasks to cancel.");
                    return;
                }

                if (crossChatTasks.Count == 1 && (arg == "" || arg.Equals("all", StringComparison.OrdinalIgnoreCase)))
                {
                    var (originChatId, _, t) = crossChatTasks[0];
                    try { await t.Cts.CancelAsync(); } catch (ObjectDisposedException) { }
                    await Sink.SendTextAsync(chatId, $"Cancelling task from chat {originChatId}...");
                    if (originChatId != chatId)
                        await Sink.SendTextAsync(originChatId, "Task cancelled by user from another chat.");
                    return;
                }

                if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (originChatId, _, t) in crossChatTasks)
                    {
                        try { await t.Cts.CancelAsync(); } catch (ObjectDisposedException) { }
                        if (originChatId != chatId)
                            await Sink.SendTextAsync(originChatId, "Task cancelled by user from another chat.");
                    }
                    await Sink.SendTextAsync(chatId, $"Cancelling {crossChatTasks.Count} task(s) from other chats...");
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
                await Sink.SendTextAsync(chatId, crossChatList);
                return;
            }

            await Sink.SendTextAsync(chatId, "No active tasks to cancel.");
            return;
        }

        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var t in tasks)
            {
                try { await t.Cts.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
            await Sink.SendTextAsync(chatId, $"Cancelling all {tasks.Count} task(s)...");
            return;
        }

        if (int.TryParse(arg, out var id))
        {
            var task = state.Get(id);
            if (task is null)
            {
                await Sink.SendTextAsync(chatId, $"No task with ID #{id}.");
                return;
            }
            try { await task.Cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
            await Sink.SendTextAsync(chatId, $"Cancelling task [#{id}]...");
            return;
        }

        if (tasks.Count == 1)
        {
            var t = tasks[0];
            try { await t.Cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
            await Sink.SendTextAsync(chatId, "Cancelling the current task...");
            return;
        }

        var list = "Multiple tasks running. Specify which to cancel:\n";
        foreach (var t in tasks)
        {
            var elapsed = DateTimeOffset.UtcNow - t.StartedAt;
            list += $"  [#{t.Id}] {TruncateText(t.Description, 60)} ({(int)elapsed.TotalSeconds}s)\n";
        }
        list += "\nUse /cancel <id> or /cancel all";
        await Sink.SendTextAsync(chatId, list);
    }

    // --- Private ---

    private async Task ProcessTask(long chatId, int taskId, string task, string displayText,
        bool isSessionTask, TaskSource source, string? relaySender, string? correlationId, string? relayTaskId,
        IReadOnlyList<MessageImage>? images, IReadOnlyList<MessageDocument>? documents, CancellationToken ct)
    {
        var state = GetChatState(chatId);
        string Prefix() => state.Count > 1 ? $"[#{taskId}] " : "";

        string? lastResult = null;
        string? lastError = null;
        var significantUpdates = 0;
        List<string> allAssistantTexts = [];
        ExecutionStats? stats = null;
        var errorResult = false;
        var processExitResult = false;
        var toolCalls = new List<(string Name, string Args)>();

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingTask = RunTypingLoopAsync(chatId, typingCts.Token);

        string StatsSuffix()
        {
            if (!_agentConfig.ShowStats || stats is null) return "";
            if (toolCalls.Count > 0)
                stats.ToolCalls = toolCalls.Select(t => new Models.ToolCallEntry(t.Name, t.Args)).ToList();
            return $"\n{stats.Format()}";
        }

        async Task SendWithStatsAsync(string content)
        {
            // Guarantee: this must NEVER throw. Terminal-state callers rely on it to
            // finish so that OnTaskCompleted runs and the bridge/relay gets its answer.
            // A failed Telegram echo (e.g., bot not a member of the chat) is non-fatal —
            // the authoritative response goes out via OnTaskCompleted → relay.
            try
            {
                var statsText = StatsSuffix();
                var toolBlock = stats?.FormatToolBlock() ?? "";
                if (toolBlock.Length > 0)
                {
                    var encoded = System.Net.WebUtility.HtmlEncode(content);
                    var htmlPrefix = "";
                    if (_agentConfig.PrefixMessages && _agentConfig.ShortName.Length > 0)
                    {
                        var displayName = $"{char.ToUpperInvariant(_agentConfig.ShortName[0])}{_agentConfig.ShortName[1..]}";
                        htmlPrefix = $"<b>{displayName}:</b>\n";
                    }
                    await Sink.SendHtmlTextAsync(chatId, $"{htmlPrefix}{encoded}{statsText}{toolBlock}");
                }
                else
                {
                    await Sink.SendTextAsync(chatId, $"{content}{statsText}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SendWithStatsAsync: failed to deliver Telegram message for chat {ChatId} (task #{TaskId}) — continuing", chatId, taskId);
            }
        }

        // Grab the inbox for this task to receive mid-execution messages
        var inboxReader = state.Get(taskId)?.Inbox.Reader;

        try
        {
            var currentTask = task;
            IReadOnlyList<MessageImage>? currentImages = images;
            IReadOnlyList<MessageDocument>? currentDocuments = documents;

            while (true)
            {
                await foreach (var progress in _executor.ExecuteAsync(currentTask, currentImages, currentDocuments, ct))
                {
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
                        await Sink.SendTextAsync(chatId, progress.Summary);
                    }
                    else if (progress.IsSignificant && progress.ToolName is not null)
                    {
                        significantUpdates++;
                        toolCalls.Add((ShortenToolName(progress.ToolName), TruncateArgs(progress.ToolArgs ?? "{}", _agentConfig.ToolArgsTruncateLength)));
                        // Suppress progress messages for check-ins — they may end up IDLE
                        // Also suppress when SuppressToolMessages is configured (e.g. for non-technical users)
                        if (!_agentConfig.SuppressToolMessages && source != TaskSource.CheckIn && significantUpdates % 5 == 1)
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
                            await Sink.SendHtmlTextAsync(chatId, $"{htmlPrefix}<blockquote expandable>{encoded}</blockquote>");
                        }
                        OnToolUse?.Invoke(chatId, progress.ToolName, progress.Summary);
                    }
                }

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

                        completingTask.InjectionCount = 0;
                        currentTask = firstRedelivery.Task;
                        currentImages = firstRedelivery.Images;
                        currentDocuments = firstRedelivery.Documents;
                        lastError = null;
                        errorResult = false;
                        processExitResult = false;
                        continue;
                    }

                    // After turn completes, atomically check the fallback inbox before
                    // closing the live-delivery window. This prevents a message from
                    // being enqueued between TryRead(false) and Closed=true.
                    if (inboxReader is not null && inboxReader.TryRead(out var nextMessage))
                    {
                        _logger.LogInformation("Task #{TaskId}: delivering queued mid-turn fallback message to executor", taskId);
                        currentTask = nextMessage.Task;
                        currentImages = nextMessage.Images;
                        currentDocuments = nextMessage.Documents;
                        if (completingTask is not null)
                        {
                            completingTask.InjectionCount = 0;
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
                return;
            }

            // Check-in with no output at all: suppress the empty reply
            if (source == TaskSource.CheckIn && lastResult is null)
            {
                _logger.LogInformation("Check-in: no output, suppressing for chat {ChatId}", chatId);
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
                await SendWithStatsAsync($"{Prefix()}{lastResult}{marker}");
                var fullText = string.Join("\n", allAssistantTexts);
                OnTaskCompleted?.Invoke(chatId, fullText, relaySender, source, errorResult, correlationId, relayTaskId);
            }
            else if (lastError is not null)
            {
                if (isSessionTask)
                    _sessions.ClearSession(chatId);
                var errorMsg = $"Task failed: {lastError}";
                await SendWithStatsAsync($"{Prefix()}{errorMsg}");
                OnTaskCompleted?.Invoke(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId);
            }
            else if (errorResult)
            {
                // A result event flagged as an error but carrying no text: the turn failed
                // and produced nothing. Reporting "Done!" here is what let a dead executor
                // look healthy — the error flag must survive even with no output to show.
                var errorMsg = "Task failed: executor reported an error and produced no output";
                _logger.LogError("Task #{TaskId} for chat {ChatId}: {Error}", taskId, chatId, errorMsg);
                await SendWithStatsAsync($"{Prefix()}{errorMsg}");
                OnTaskCompleted?.Invoke(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId);
            }
            else
            {
                await SendWithStatsAsync($"{Prefix()}Done! (no text output)");
                OnTaskCompleted?.Invoke(chatId, "Done! (no text output)", relaySender, source, false, correlationId, relayTaskId);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Task #{TaskId} cancelled for chat {ChatId}", taskId, chatId);
            await SendWithStatsAsync($"{Prefix()}Task cancelled.");
            OnTaskCompleted?.Invoke(chatId, "Task cancelled.", relaySender, source, false, correlationId, relayTaskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing task #{TaskId} for chat {ChatId}", taskId, chatId);
            var errorMsg = $"Error: {ex.Message}";
            await SendWithStatsAsync($"{Prefix()}{errorMsg}");
            if (isSessionTask)
                _sessions.ClearSession(chatId);
            OnTaskCompleted?.Invoke(chatId, errorMsg, relaySender, source, true, correlationId, relayTaskId);
        }
        finally
        {
            await typingCts.CancelAsync();
            await typingTask;
        }
    }


    private static bool TryGetRunningSessionTask(ChatTaskState state, out RunningTask running)
    {
        running = state.Snapshot().FirstOrDefault(t => t.IsSessionTask)!;
        return running is not null;
    }

    private async Task DeliverMidTurnMessageAsync(long chatId, RunningTask running, MidTurnMessage message)
    {
        await running.TurnDispatchLock.WaitAsync();
        try
        {
            if (running.Closed)
            {
                if (EnqueueMessage(chatId, message, notifyUser: true))
                    _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
                return;
            }

            if (running.InjectionCount >= MaxMidTurnInjectionsPerTurn)
            {
                await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: true);
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
                return;
            }

            var result = await _executor.TryInjectMessageAsync(FormatInjectedMessage(message.Task),
                message.Images, message.Documents, running.Cts.Token);

            if (result.Status == MidTurnInjectionStatus.Injected)
            {
                running.InjectionCount++;
                running.InjectedMessagesForResume.Add(message);
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.Injected);
                _logger.LogInformation("Injected mid-turn message into running task #{TaskId} for chat {ChatId}", running.Id, chatId);
                return;
            }

            await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: true);
            var outcome = result.Status == MidTurnInjectionStatus.Failed
                ? InjectionOutcomeCounter.FailedThenQueued
                : InjectionOutcomeCounter.DegradedToQueue;
            _injectionCounter.Increment(_agentConfig.Provider, outcome);
            _logger.LogInformation("Mid-turn injection unavailable for chat {ChatId} (status={Status}, error={Error}); queued for turn-end delivery",
                chatId, result.Status, result.Error);
        }
        catch (Exception ex)
        {
            await EnqueueForTurnEndAsync(chatId, running, message, notifyUser: true);
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.FailedThenQueued);
            _logger.LogWarning(ex, "Mid-turn injection failed for chat {ChatId}; queued for turn-end delivery", chatId);
        }
        finally
        {
            running.TurnDispatchLock.Release();
        }
    }


    private async Task DeferUntilTurnEndAsync(long chatId, RunningTask running, MidTurnMessage message, bool notifyUser)
    {
        await running.TurnDispatchLock.WaitAsync();
        try
        {
            // Check-ins are not conversational corrections, so do not fold them into
            // the current response chain. Queue them to start only after the current
            // task has sent its own terminal response and DrainQueue runs.
            if (EnqueueMessage(chatId, message, notifyUser))
                _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DegradedToQueue);
        }
        finally
        {
            running.TurnDispatchLock.Release();
        }
    }

    private async Task EnqueueForTurnEndAsync(long chatId, RunningTask running, MidTurnMessage message, bool notifyUser)
    {
        if (!running.Inbox.Writer.TryWrite(message))
        {
            EnqueueMessage(chatId, message, notifyUser);
            return;
        }

        if (notifyUser && !(_agentConfig.SuppressToolMessages && chatId < 0))
        {
            try
            {
                await Sink.SendTextAsync(chatId, "I'm busy right now — your message is queued. I'll get to it once my current turn finishes.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send mid-turn queue notice for chat {ChatId}", chatId);
            }
        }
        OnStatusChanged?.Invoke();
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
            message.ArrivedAt);

        var pendingResult = TryAppendToPendingQueue(chatId, part);
        if (pendingResult == PendingQueueResult.Merged)
        {
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.MergedIntoQueue);
            OnStatusChanged?.Invoke();
            return true;
        }

        return EnqueueFreshMessage(chatId, part, notifyUser, completeBridgeOnDrop: false);
    }

    private QueuedMessagePart CreateQueuedPart(string task, string displayText, bool isSessionTask, TaskSource source,
        string? relaySender, string? correlationId, string? taskId, IReadOnlyList<MessageImage>? images,
        IReadOnlyList<MessageDocument>? documents, long userId, DateTimeOffset? arrivedAt = null)
    {
        var senderDisplay = relaySender ?? source.ToString().ToLowerInvariant();
        var arrival = arrivedAt?.ToLocalTime() ?? DateTimeOffset.Now;
        return new QueuedMessagePart(task, displayText, isSessionTask, source, relaySender, correlationId, taskId,
            images, documents, userId, arrival, senderDisplay);
    }

    private PendingQueueResult TryAppendToPendingQueue(long chatId, QueuedMessagePart part)
    {
        QueuedMessage? pending;
        lock (_pendingQueueIndexLock)
        {
            if (!_pendingQueueByChat.TryGetValue(chatId, out pending))
                return PendingQueueResult.NoPending;
        }

        pending.QueueDispatchLock.Wait();
        try
        {
            if (pending.Claimed)
                return PendingQueueResult.EnqueueFresh;

            if (!pending.TryAppendPart(part))
                return PendingQueueResult.EnqueueFresh;

            _logger.LogInformation("Merged queued message into pending entry for chat {ChatId} ({Count}/{MaxParts} parts)",
                chatId, pending.PartCount, MaxQueuedPartsPerEntry);
            return PendingQueueResult.Merged;
        }
        finally
        {
            pending.QueueDispatchLock.Release();
        }
    }

    private bool EnqueueFreshMessage(long chatId, QueuedMessagePart part, bool notifyUser, bool completeBridgeOnDrop)
    {
        if (_messageQueue.Count >= MaxQueueDepth)
        {
            _logger.LogWarning("Message queue full ({Max}) — dropping incoming task from chat {ChatId}", MaxQueueDepth, chatId);
            _injectionCounter.Increment(_agentConfig.Provider, InjectionOutcomeCounter.DroppedAtQueueCap);
            _ = Sink.SendTextAsync(chatId, $"Queue is full ({MaxQueueDepth} messages waiting). Please wait for tasks to complete.");
            if (completeBridgeOnDrop && part.Source == TaskSource.Bridge && part.CorrelationId is not null)
                OnTaskCompleted?.Invoke(chatId, "[status: failed]\nagent queue full", "bridge", part.Source, true, part.CorrelationId, part.TaskId);
            return false;
        }

        var queued = new QueuedMessage(chatId, part);
        _messageQueue.Enqueue(queued);
        if (part.Source == TaskSource.UserMessage)
        {
            lock (_pendingQueueIndexLock)
                _pendingQueueByChat[chatId] = queued;
        }

        var queuePos = _messageQueue.Count;
        _logger.LogInformation("Message queued (position {Pos}) for chat {ChatId}; queue entries={EntryCount}, max parts per entry={MaxParts}",
            queuePos, chatId, _messageQueue.Count, MaxQueuedPartsPerEntry);
        if (notifyUser && !(_agentConfig.SuppressToolMessages && chatId < 0))
            _ = Sink.SendTextAsync(chatId, $"I'm busy right now — your message is queued (position {queuePos}). I'll get to it once my current task finishes.");
        OnStatusChanged?.Invoke();
        return true;
    }

    private void RemovePendingIndexIfCurrent(QueuedMessage queued)
    {
        lock (_pendingQueueIndexLock)
        {
            if (_pendingQueueByChat.TryGetValue(queued.ChatId, out var current) && ReferenceEquals(current, queued))
                _pendingQueueByChat.TryRemove(queued.ChatId, out _);
        }
    }

    private void RebuildPendingQueueIndex()
    {
        lock (_pendingQueueIndexLock)
        {
            // Rebuild is a rare cancellation cleanup. Holding the index lock makes it
            // atomic against fresh enqueue/index writes; a concurrent merge that already
            // captured an entry still completes under that entry's QueueDispatchLock.
            _pendingQueueByChat.Clear();
            foreach (var queued in _messageQueue)
            {
                if (queued.Source == TaskSource.UserMessage && !queued.Claimed)
                    _pendingQueueByChat[queued.ChatId] = queued;
            }
        }
    }

    internal static string FormatInjectedMessage(string original) =>
        """
        [NEW MESSAGE — arrived while you were still working on the previous
        instruction. This is not a tool result. Decide whether to finish your
        current step, adjust your plan, or stop and address this first.]

        """ + original;

    private async Task RunTypingLoopAsync(long chatId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Sink.SendTypingAsync(chatId, ct);
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
                    try { await t.Cts.CancelAsync(); }
                    catch (ObjectDisposedException) { }
                    found = true;
                }
            }
        }

        if (!found)
        {
            // Check the pending queue — drain and re-enqueue non-matching items
            var retained = new List<QueuedMessage>();
            try
            {
                while (_messageQueue.TryDequeue(out var item))
                {
                    retained.Add(item);
                    QueueEntryDequeuedForBridgeCancelForTest?.Invoke();
                    if (item.ContainsTaskId(bridgeTaskId))
                    {
                        found = true;
                        retained.RemoveAt(retained.Count - 1);
                    }
                }
            }
            finally
            {
                foreach (var item in retained)
                    _messageQueue.Enqueue(item);
                RebuildPendingQueueIndex();
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
        // Cancel all running tasks
        foreach (var (_, state) in _chatTasks)
        {
            foreach (var t in state.Snapshot())
            {
                try { await t.Cts.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
        }

        // Clear the pending queue
        while (_messageQueue.TryDequeue(out _)) { }
        lock (_pendingQueueIndexLock)
            _pendingQueueByChat.Clear();

        _logger.LogInformation("CancelAll: all running tasks cancelled and queue cleared");
    }

    /// <summary>Dequeues one message and starts it if capacity is available.</summary>
    private void DrainQueue()
    {
        if (_messageQueue.IsEmpty) return;
        if (!_messageQueue.TryDequeue(out var queued)) return;

        queued.QueueDispatchLock.Wait();
        try
        {
            if (queued.Claimed) return;
            queued.Claimed = true;
        }
        finally
        {
            queued.QueueDispatchLock.Release();
        }

        QueueEntryClaimedForTest?.Invoke();

        var payload = queued.BuildPayload(DateTimeOffset.Now);

        _logger.LogInformation("Draining queued message for chat {ChatId} (source={Source}, parts={Parts})",
            queued.ChatId, queued.Source, queued.PartCount);
        if (!(_agentConfig.SuppressToolMessages && queued.ChatId < 0))
            _ = Sink.SendTextAsync(queued.ChatId, "Now processing your queued message...");
        OnStatusChanged?.Invoke();

        StartTaskCore(queued.ChatId, payload.Task, payload.DisplayText, payload.IsSessionTask,
            payload.Source, payload.RelaySender, payload.CorrelationId, payload.TaskId,
            payload.Images, payload.Documents, payload.UserId, skipPendingQueueCheck: true);

        RemovePendingIndexIfCurrent(queued);
    }

    /// <summary>Returns a snapshot of the current queue for heartbeat/status reporting.</summary>
    public IReadOnlyList<QueuedMessage> GetQueueSnapshot() => [.. _messageQueue];

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
    /// Parameters: chatId, result, relaySender (null if Telegram-originated), source, isPartial (hit max-turns/error), correlationId, taskId.
    /// </summary>
    public event Action<long, string, string?, TaskSource, bool, string?, string?>? OnTaskCompleted;

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
