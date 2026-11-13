using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>The MCP adapter over <see cref="AgentLifecycle.RestartAsync"/>, which the REST restart route calls too.</summary>
[McpServerToolType]
public sealed class RestartAgentTool(AgentLifecycle lifecycle)
{
    [McpServerTool(Name = "restart_agent")]
    [Description("Restart a fleet agent's Docker container. Use with caution — the agent will be briefly unavailable.")]
    public async Task<string> RestartAgentAsync(
        [Description("Agent name (e.g. my-agent)")] string name)
    {
        var outcome = await lifecycle.RestartAsync(name);
        if (outcome.Fault is { } fault)
            return fault.Code == "agent_not_found"
                ? $"Agent '{name}' not found in registry or DB. Use list_agents to see available agents."
                : fault.Message.Replace(" — check orchestrator logs", ". Docker socket may be unavailable — check orchestrator logs.");

        var containerName = outcome.Value!.ContainerName;

        // Give Docker a moment before reading the new status. Presentation only: the restart is done.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var status = await lifecycle.DescribeContainerAsync(containerName);

        var sb = new StringBuilder();
        sb.AppendLine($"Container '{containerName}' restart requested successfully.");
        sb.AppendLine($"Docker status: {status ?? "unknown"}");
        sb.AppendLine($"Orchestrator will update agent status once the next heartbeat is received.");
        return sb.ToString();
    }
}
