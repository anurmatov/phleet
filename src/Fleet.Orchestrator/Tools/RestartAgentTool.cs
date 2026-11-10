using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class RestartAgentTool(AgentRegistry registry, DockerService docker, IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "restart_agent")]
    [Description("Restart a fleet agent's Docker container. Use with caution — the agent will be briefly unavailable.")]
    public async Task<string> RestartAgentAsync(
        [Description("Agent name (e.g. my-agent)")] string name)
    {
        // Prefer registry (live state), fall back to DB for container name (handles stopped/crashed agents)
        var containerName = registry.Get(name)?.ContainerName;
        if (containerName is null)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            var dbAgent = db is null ? null : await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
            containerName = dbAgent?.ContainerName;
        }

        if (containerName is null)
            return $"Agent '{name}' not found in registry or DB. Use list_agents to see available agents.";
        var ok = await docker.RestartContainerAsync(containerName);

        if (!ok)
            return $"Failed to restart container '{containerName}'. Docker socket may be unavailable — check orchestrator logs.";

        // Give Docker a moment before polling the new status
        await Task.Delay(TimeSpan.FromSeconds(3));
        var newDockerStatus = await docker.GetContainerStatusAsync(containerName);

        var sb = new StringBuilder();
        sb.AppendLine($"Container '{containerName}' restart requested successfully.");
        sb.AppendLine($"Docker status: {newDockerStatus ?? "unknown"}");
        sb.AppendLine($"Orchestrator will update agent status once the next heartbeat is received.");
        return sb.ToString();
    }
}
