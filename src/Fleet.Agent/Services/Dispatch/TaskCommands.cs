using System.Collections.Concurrent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.Dispatch;

/// <summary>
/// The operator's commands over running turns (<c>/stop</c>, <c>/reset</c>, <c>/status</c>,
/// <c>/cancel</c>) and the status the orchestrator heartbeat reports. They read the running set
/// and cancel turns through each turn's own token; none of them starts or queues work.
/// </summary>
internal sealed class TaskCommands(
    ConcurrentDictionary<long, ChatTaskState> chatTasks,
    ConcurrentDictionary<long, List<(long ChatId, int TaskId)>> userTasks,
    AgentOptions agentConfig,
    IAgentExecutor executor,
    SessionManager sessions,
    IMessageSink sink,
    ILogger logger,
    IDispatchHost host)
{
    private readonly ConcurrentDictionary<long, ChatTaskState> _chatTasks = chatTasks;
    private readonly ConcurrentDictionary<long, List<(long ChatId, int TaskId)>> _userTasks = userTasks;
    private readonly AgentOptions _agentConfig = agentConfig;
    private readonly IAgentExecutor _executor = executor;
    private readonly SessionManager _sessions = sessions;
    private readonly IMessageSink _sink = sink;
    private readonly ILogger _logger = logger;
    private readonly IDispatchHost _host = host;

    internal async Task HandleStop(long chatId)
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

    internal async Task HandleReset(long chatId)
    {
        var state = _host.GetChatState(chatId);
        if (state.Count > 0)
        {
            await _sink.SendTextAsync(chatId, "Can't reset while tasks are running. Use /cancel all first.");
            return;
        }

        await _executor.StopProcessAsync();
        _sessions.ClearSession(chatId);
        await _sink.SendTextAsync(chatId, "Session cleared. Send a new task to start fresh.");
    }

    internal async Task HandleStatus(long chatId)
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
                    msg += $"\n  [#{t.Id}] {TaskManager.TruncateText(t.Description, 60)}{label} ({(int)elapsed.TotalSeconds}s) [{chatLabel}]";
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
                var summary = bt.Summary is not null ? $" — {TaskManager.TruncateText(bt.Summary, 60)}" : "";
                msg += $"\n  [{bt.TaskType}] {TaskManager.TruncateText(bt.Description, 60)}{summary} ({bt.ElapsedSeconds}s)";
            }
        }

        await _sink.SendTextAsync(chatId, msg);
    }

    internal async Task HandleCancel(long chatId, string arg, long userId = 0,
        TurnCancelReason reason = TurnCancelReason.User)
    {
        var state = _host.GetChatState(chatId);
        var tasks = state.Snapshot();

        // The reason is recorded on the RunningTask BEFORE Cts.Cancel(), because the catch block
        // in TurnRun.RunAsync is the only place that can report it and by then the canceller is gone.
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
                        .Select(e => (e.ChatId, e.TaskId, Task: _host.GetChatState(e.ChatId).Get(e.TaskId)))
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
                    crossChatList += $"  [chat {originChatId} #{tid}] {TaskManager.TruncateText(t.Description, 60)} ({(int)elapsed.TotalSeconds}s)\n";
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
            list += $"  [#{t.Id}] {TaskManager.TruncateText(t.Description, 60)} ({(int)elapsed.TotalSeconds}s)\n";
        }
        list += "\nUse /cancel <id> or /cancel all";
        await _sink.SendTextAsync(chatId, list);
    }

    private List<(long ChatId, List<RunningTask> Tasks)> GetAllRunningTasks() =>
        _chatTasks
            .Select(kv => (kv.Key, kv.Value.Snapshot()))
            .Where(x => x.Item2.Count > 0)
            .OrderBy(x => x.Key)
            .ToList();

    /// <summary>
    /// Returns the current agent status for orchestrator heartbeats.
    /// </summary>
    internal (string Status, string? CurrentTask, string? CurrentTaskId) GetOrchestratorStatus()
    {
        var allTasks = GetAllRunningTasks();
        if (allTasks.Count == 0 || allTasks.All(x => x.Tasks.Count == 0))
            return ("idle", null, null);

        var first = allTasks.SelectMany(x => x.Tasks).OrderBy(t => t.StartedAt).FirstOrDefault();
        return ("busy",
            first is not null ? TaskManager.TruncateText(first.Description, 500) : null,
            first?.BridgeTaskId);
    }
}
