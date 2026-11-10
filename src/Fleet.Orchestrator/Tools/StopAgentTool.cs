using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class StopAgentTool(AgentRegistry registry, DockerService docker)
{
    [McpServerTool(Name = "stop_agent")]
    [Description("Stop a fleet agent's Docker container gracefully (waits up to 10s before SIGKILL). Use with caution.")]
    public async Task<string> StopAgentAsync(
        [Description("Agent name (e.g. my-agent)")] string name)
    {
        var agent = registry.Get(name);
        if (agent is null)
            return $"Agent '{name}' not found in registry. Use list_agents to see registered agents.";

        var containerName = agent.ContainerName ?? name;
        var ok = await docker.StopContainerAsync(containerName);

        return ok
            ? $"Container '{containerName}' stopped successfully."
            : $"Failed to stop container '{containerName}'. Docker socket may be unavailable — check orchestrator logs.";
    }
}
