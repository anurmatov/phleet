using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Single source of truth for command dispatch.
/// Used by both MessageRouter (Telegram commands) and GroupBehavior (relay commands).
/// </summary>
public sealed class CommandDispatcher
{
    private readonly TaskManager _taskManager;
    private readonly IAgentExecutor _executor;
    private readonly AgentOptions _agentConfig;
    private readonly ILogger<CommandDispatcher> _logger;

    /// <summary>
    /// Outbound text destination. Constructor-injected via <see cref="MessageSinkHolder"/>
    /// instead of assigned by <c>AgentTransport</c> after construction (#277 D-1). Never null.
    /// </summary>
    private readonly IMessageSink _sink;

    public CommandDispatcher(
        TaskManager taskManager,
        IAgentExecutor executor,
        IOptions<AgentOptions> agentConfig,
        ILogger<CommandDispatcher> logger,
        IMessageSink? sink = null)
    {
        _taskManager = taskManager;
        _executor = executor;
        _agentConfig = agentConfig.Value;
        _logger = logger;
        _sink = sink ?? NullMessageSink.Instance;
    }

    /// <summary>
    /// Try to handle a command string. Returns true if it was a recognized command.
    /// </summary>
    public async Task<bool> TryHandleAsync(long chatId, string text)
    {
        if (text.Equals("/stop", StringComparison.OrdinalIgnoreCase))
        {
            await _taskManager.HandleStop(chatId);
            return true;
        }

        if (text.Equals("/reset", StringComparison.OrdinalIgnoreCase))
        {
            await _taskManager.HandleReset(chatId);
            return true;
        }

        if (text.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            await _taskManager.HandleStatus(chatId);
            return true;
        }

        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("/cancel ", StringComparison.OrdinalIgnoreCase))
        {
            var arg = text.Length > 7 ? text[8..].Trim() : "";
            // Relay commands default to "all" when no arg given
            if (string.IsNullOrEmpty(arg)) arg = "all";
            await _taskManager.HandleCancel(chatId, arg);
            return true;
        }

        if (text.Equals("/cancel_bg", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("/cancel_bg ", StringComparison.OrdinalIgnoreCase))
        {
            var arg = text.Length > 10 ? text[11..].Trim() : "";
            await HandleCancelBgAsync(chatId, arg);
            return true;
        }

        if (text.StartsWith("/run ", StringComparison.OrdinalIgnoreCase))
        {
            var command = text[5..].Trim();
            if (string.IsNullOrEmpty(command))
            {
                await _sink.SendTextAsync(chatId, "Usage: /run <command> (e.g. /run /compact)");
                return true;
            }

            await HandleRunCommand(chatId, command);
            return true;
        }

        return false;
    }

    private async Task HandleCancelBgAsync(long chatId, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            // No arg — list active background tasks
            var tasks = _taskManager.GetActiveBackgroundTasks();
            if (tasks.Count == 0)
            {
                await _sink.SendTextAsync(chatId, "No active background tasks.");
                return;
            }

            var msg = $"Active background tasks ({tasks.Count}):\n";
            foreach (var t in tasks)
            {
                var summary = t.Summary is not null ? $" — {TaskManager.TruncateText(t.Summary, 60)}" : "";
                msg += $"  [{t.TaskType}] {t.TaskId}{summary} ({t.ElapsedSeconds}s)\n";
            }
            msg += "\nUse /cancel_bg <taskId> to cancel a specific task.";
            await _sink.SendTextAsync(chatId, msg);
            return;
        }

        // Arg provided — cancel by task ID
        var cancelled = await _taskManager.CancelBackgroundTaskAsync(arg);
        if (cancelled)
            await _sink.SendTextAsync(chatId, $"Cancel requested for background task '{arg}'.");
        else
            await _sink.SendTextAsync(chatId, $"Background task '{arg}' not found. Use /cancel_bg to list active tasks.");
    }

    private async Task HandleRunCommand(long chatId, string command)
    {
        _logger.LogInformation("/run command received: {Command}", command);

        string? lastResult = null;
        Models.ExecutionStats? stats = null;

        await foreach (var progress in _executor.SendCommandAsync(command))
        {
            if (progress.FinalResult is not null)
                lastResult = progress.FinalResult;
            if (progress.Stats is not null)
                stats = progress.Stats;
            if (progress.EventType == "error")
            {
                await _sink.SendTextAsync(chatId, progress.Summary);
                return;
            }
        }

        var statsSuffix = (_agentConfig.ShowStats && stats is not null) ? $"\n{stats.Format()}" : "";
        var response = lastResult is not null
            ? $"{lastResult}{statsSuffix}"
            : $"Command completed (no output).{statsSuffix}";
        await _sink.SendTextAsync(chatId, response);
    }
}
