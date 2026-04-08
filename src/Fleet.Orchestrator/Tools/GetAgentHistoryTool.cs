using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class GetAgentHistoryTool(TaskHistoryStore taskHistory)
{
    [McpServerTool(Name = "get_agent_history")]
    [Description("Get the recent task history for a fleet agent (last 100 completed tasks).")]
    public string GetAgentHistory(
        [Description("Agent name (e.g. my-agent)")] string name)
    {
        var records = taskHistory.GetHistory(name);
        if (records.Count == 0)
            return $"No completed tasks found for agent '{name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Task history: {name} ({records.Count} records)");
        sb.AppendLine();
        foreach (var r in records)
        {
            var duration = r.DurationSeconds < 60
                ? $"{(int)r.DurationSeconds}s"
                : $"{(int)(r.DurationSeconds / 60)}m {(int)(r.DurationSeconds % 60)}s";
            sb.AppendLine($"- [{r.StartedAt:HH:mm:ss}Z] ({duration}) {r.TaskText}");
        }
        return sb.ToString();
    }
}
