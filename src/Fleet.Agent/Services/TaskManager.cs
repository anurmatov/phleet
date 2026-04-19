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

    private readonly ConcurrentDictionary<long, ChatTaskState> _chatTasks = new();
    // taskId dedup: tracks bridge taskIds that are currently in-flight
    private readonly ConcurrentDictionary<string, bool> _activeTaskIds = new();

    // Global FIFO queue for messages that arrive while the agent is at capacity.
    private readonly ConcurrentQueue<QueuedMessage> _messageQueue = new();
    private const int MaxQueueDepth = 20;

    // user-level index: userId → list of (chatId, taskId) for cross-chat cancel
    private readonly ConcurrentDictionary<long, List<(long ChatId, int TaskId)>> _userTasks = new();

    private string _botUsername = "";

    /// <summary>Set by AgentTransport after construction to break circular DI.</summary>
    public IMessageSink Sink { get; set; } = null!;

    public TaskManager(
        IOptions<AgentOptions> agentConfig,
        IAgentExecutor executor,
        SessionManager sessions,
        ILogger<TaskManager> logger)
    {
        _agentConfig = agentConfig.Value;
        _executor = executor;
        _sessions = sessions;
        _logger = logger;
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

    /// <summary>
    /// Append a message to the running session task's inbox so it's delivered
    /// as additional context after the current turn completes.
    /// Returns true if enqueued, false if no suitable task found.
    /// </summary>
    public bool AppendToRunningTask(long chatId, string text)
    {
        var state = GetChatState(chatId);
        var sessionTask = state.Snapshot().FirstOrDefault(t => t.IsSessionTask);
        if (sessionTask is null) return false;
        sessionTask.Inbox.Writer.TryWrite(text);
        return true;
    }

    public void StartTask(long chatId, string task, string displayText, bool isSessionTask,
        TaskSource source = TaskSource.UserMessage,
        string? relaySender = null,
        string? correlationId = null,
        string? taskId = null,
        IReadOnlyList<MessageImage>? images = null,
        long userId = 0)
    {
        var state = GetChatState(chatId);

        // Dedup: ignore re-delivered bridge directives with the same taskId
        if (taskId is not null && !_activeTaskIds.TryAdd(taskId, true))
        {
            _logger.LogInformation("Duplicate taskId={TaskId} ignored (already in-flight)", taskId);
            return;
        }

        var totalRunning = _chatTasks.Values.Sum(s => s.Count);
        if (totalRunning >= _agentConfig.MaxConcurrentTasks)
        {
            // Undo the taskId reservation — we're not actually running it yet
            if (taskId is not null) _activeTaskIds.TryRemove(taskId, out _);

            // Check-ins silently skip when at capacity instead of queuing
            if (source == TaskSource.CheckIn)
            {
                _logger.LogDebug("Check-in skipped — max tasks reached globally ({Total}/{Max})", totalRunning, _agentConfig.MaxConcurrentTasks);
                return;
            }

            // Enqueue the message for processing after the current task completes
            if (_messageQueue.Count >= MaxQueueDepth)
            {
                _logger.LogWarning("Message queue full ({Max}) — dropping incoming task from chat {ChatId}", MaxQueueDepth, chatId);
                _ = Sink.SendTextAsync(chatId, $"Queue is full ({MaxQueueDepth} messages waiting). Please wait for tasks to complete.");
                if (source == TaskSource.Bridge && correlationId is not null)
                    OnTaskCompleted?.Invoke(chatId, "[status: failed]\nagent queue full", "bridge", source, true, correlationId, taskId);
                return;
            }

            var senderDisplay = relaySender ?? source.ToString().ToLowerInvariant();
            _messageQueue.Enqueue(new QueuedMessage(
                chatId, task, displayText, isSessionTask, source,
                relaySender, correlationId, taskId,
                images, userId,
                DateTimeOffset.UtcNow, senderDisplay));

            var queuePos = _messageQueue.Count;
            _logger.LogInformation("Message queued (position {Pos}) for chat {ChatId} — agent at capacity ({Total}/{Max})", queuePos, chatId, totalRunning, _agentConfig.MaxConcurrentTasks);
            _ = Sink.SendTextAsync(chatId, $"I'm busy right now — your message is queued (position {queuePos}). I'll get to it once my current task finishes.");
            OnStatusChanged?.Invoke();
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
                await ProcessTask(chatId, running.Id, task, displayText, isSessionTask, source, relaySender, correlationId, taskId, images, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in task #{TaskId} for chat {ChatId}", running.Id, chatId);
            }
            finally
            {
                state.Remove(running.Id);
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
            msg += $"\n\nRunning tasks ({totalCount}/{_agentConfig.MaxConcurrentTasks}):";
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
        IReadOnlyList<MessageImage>? images, CancellationToken ct)
    {
        var state = GetChatState(chatId);
        string Prefix() => state.Count > 1 ? $"[#{taskId}] " : "";

        string? lastResult = null;
        string? lastError = null;
        var significantUpdates = 0;
        List<string> allAssistantTexts = [];
        ExecutionStats? stats = null;
        var errorResult = false;
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

            while (true)
            {
                await foreach (var progress in _executor.ExecuteAsync(currentTask, currentImages, ct))
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

                    if (progress.IsSignificant && progress.ToolName is not null)
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

                // After turn completes, check if user sent additional context mid-task
                if (inboxReader is not null && inboxReader.TryRead(out var nextMessage))
                {
                    _logger.LogInformation("Task #{TaskId}: delivering queued message to executor", taskId);
                    currentTask = nextMessage;
                    currentImages = null;
                    // Reset per-turn state but accumulate texts and stats
                    lastError = null;
                    errorResult = false;
                    continue;
                }

                break;
            }

            // Check-in IDLE suppression: if the result is just "IDLE", suppress output entirely
            if (source == TaskSource.CheckIn
                && (lastResult is null
                    || lastResult.Trim().Equals("IDLE", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Check-in: IDLE, suppressing output for chat {ChatId}", chatId);
                return;
            }

            if (lastResult is not null)
            {
                var marker = errorResult ? " [incomplete — hit limit]" : "";
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
            while (_messageQueue.TryDequeue(out var item))
            {
                if (item.TaskId == bridgeTaskId)
                    found = true;
                else
                    retained.Add(item);
            }
            foreach (var item in retained)
                _messageQueue.Enqueue(item);
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

        _logger.LogInformation("CancelAll: all running tasks cancelled and queue cleared");
    }

    /// <summary>Dequeues one message and starts it if capacity is available.</summary>
    private void DrainQueue()
    {
        if (_messageQueue.IsEmpty) return;
        if (!_messageQueue.TryDequeue(out var queued)) return;

        _logger.LogInformation("Draining queued message for chat {ChatId} (source={Source})", queued.ChatId, queued.Source);
        _ = Sink.SendTextAsync(queued.ChatId, "Now processing your queued message...");
        OnStatusChanged?.Invoke();

        StartTask(queued.ChatId, queued.Task, queued.DisplayText, queued.IsSessionTask,
            queued.Source, queued.RelaySender, queued.CorrelationId, queued.TaskId,
            queued.Images, queued.UserId);
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
