using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class AgentLogsTool(AgentRegistry registry, DockerService docker)
{
    [McpServerTool(Name = "agent_logs")]
    [Description("Tail recent container logs for a fleet agent. Returns the last N lines (default 100, max 500).")]
    public async Task<string> GetAgentLogsAsync(
        [Description("Agent name (e.g. my-agent)")] string name,
        [Description("Number of log lines to tail (default 100, max 500)")] int tail = 100)
    {
        tail = Math.Clamp(tail, 1, 500);

        var agent = registry.Get(name);
        var containerName = agent?.ContainerName ?? name;

        var logs = await docker.GetContainerLogsAsync(containerName, tail);
        if (logs is null)
            return $"Could not fetch logs for agent '{name}' (container: {containerName}). Docker socket may be unavailable.";

        return $"## Logs: {name} (container: {containerName}, last {tail} lines)\n\n{logs}";
    }
}
