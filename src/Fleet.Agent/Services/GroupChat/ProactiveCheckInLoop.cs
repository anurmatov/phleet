using Fleet.Agent.Configuration;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.GroupChat;

/// <summary>
/// The timer that, every <c>Agent:ProactiveIntervalMinutes</c>, starts a check-in for each allowed
/// group that has messages since its last one. Starting the check-in itself (prompt, watermark,
/// pending images, dispatch) is the caller's, passed in as <paramref name="startGroupCheckIn"/>.
/// </summary>
internal sealed class ProactiveCheckInLoop(
    AgentOptions agentConfig,
    AllowlistHolder allowlist,
    IAgentExecutor executor,
    GroupChatHistoryStore history,
    Action<long, string, string, CancellationToken> startGroupCheckIn,
    ILogger logger)
{
    private readonly AgentOptions _agentConfig = agentConfig;
    private readonly AllowlistHolder _allowlist = allowlist;
    private readonly IAgentExecutor _executor = executor;
    private readonly GroupChatHistoryStore _history = history;
    private readonly Action<long, string, string, CancellationToken> _startGroupCheckIn = startGroupCheckIn;
    private readonly ILogger _logger = logger;

    // Iterates ALL groups (fixes single-group bug)
    internal async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(_agentConfig.ProactiveIntervalMinutes);
        _logger.LogInformation("Proactive loop started with interval {Interval}", interval);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await RunProactiveCheckIns(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Proactive check-in failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        _logger.LogInformation("Proactive loop stopped");
    }

    private Task RunProactiveCheckIns(CancellationToken ct)
    {
        // Iterate ALL allowed groups with new messages (fixes first-group-only bug)
        foreach (var groupId in _allowlist.GetAllowedGroupIds())
        {
            if (!_history.TryGetExisting(groupId, out var buffer) || !buffer.HasMessagesSinceLastCheck())
                continue;

            buffer.MarkChecked();

            var isAllMode = _agentConfig.GroupListenMode.Equals("all", StringComparison.OrdinalIgnoreCase);

            if (isAllMode && _executor.IsProcessWarm)
            {
                _startGroupCheckIn(groupId, "Proactive check-in", """
                    Review your memory and session context. Are there pending tasks,
                    follow-ups, or overdue items? If nothing needs attention: IDLE
                    """, ct);
            }
            else
            {
                var context = buffer.FormatContext();
                if (context.Length == 0)
                {
                    _logger.LogDebug("Proactive check-in: buffer empty after format for group {ChatId}, skipping", groupId);
                    continue;
                }

                _startGroupCheckIn(groupId, "Proactive check-in", $"""
                    Review the recent conversation and your memory. Track all tasks — ones
                    the CEO assigned to any agent, ones you delegated, and ones assigned to you.
                    Are there pending items, follow-ups, or overdue tasks? If nothing needs
                    attention, respond with just: IDLE
                    """, ct);
            }
        }

        return Task.CompletedTask;
    }
}
