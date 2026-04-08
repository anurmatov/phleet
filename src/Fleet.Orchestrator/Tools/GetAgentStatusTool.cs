using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class GetAgentStatusTool(AgentRegistry registry, DockerService docker)
{
    [McpServerTool(Name = "get_agent_status")]
    [Description("Get detailed status for a specific fleet agent including container info from Docker.")]
    public async Task<string> GetAgentStatusAsync(
        [Description("Agent name (e.g. my-agent)")] string name)
    {
        var agent = registry.Get(name);
        if (agent is null)
            return $"Agent '{name}' not found in registry. Use list_agents to see registered agents.";

        var containerName = agent.ContainerName ?? name;
        var dockerStatus  = await docker.GetContainerStatusAsync(containerName);

        var age = DateTimeOffset.UtcNow - agent.LastSeen;
        var ageStr = age.TotalSeconds < 60
            ? $"{(int)age.TotalSeconds}s ago"
            : $"{(int)age.TotalMinutes}m ago";

        var sb = new StringBuilder();
        sb.AppendLine($"## Agent: {agent.AgentName}");
        sb.AppendLine();
        sb.AppendLine($"**Orchestrator status**: {agent.EffectiveStatus} (reported: {agent.ReportedStatus})");
        sb.AppendLine($"**Docker container**: {containerName} → {dockerStatus ?? "unknown (socket unavailable)"}");
        sb.AppendLine($"**Last seen**: {ageStr} ({agent.LastSeen:yyyy-MM-dd HH:mm:ss}Z)");
        sb.AppendLine($"**Registered at**: {agent.RegisteredAt:yyyy-MM-dd HH:mm:ss}Z");
        if (agent.Role         is not null) sb.AppendLine($"**Role**: {agent.Role}");
        if (agent.Model        is not null) sb.AppendLine($"**Model**: {agent.Model}");
        if (agent.Version      is not null) sb.AppendLine($"**Version**: {agent.Version}");
        if (agent.Endpoint     is not null) sb.AppendLine($"**Endpoint**: {agent.Endpoint}");
        if (agent.CurrentTask  is not null) sb.AppendLine($"**Current task**: {agent.CurrentTask}");
        if (agent.Capabilities is { Length: > 0 })
            sb.AppendLine($"**Capabilities**: {string.Join(", ", agent.Capabilities)}");

        return sb.ToString();
    }
}
