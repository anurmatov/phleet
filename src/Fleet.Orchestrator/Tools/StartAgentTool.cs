using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class StartAgentTool(AgentRegistry registry, DockerService docker)
{
    [McpServerTool(Name = "start_agent")]
    [Description("Start a fleet agent's Docker container. Use this to bring back a stopped agent.")]
    public async Task<string> StartAgentAsync(
        [Description("Agent name (e.g. my-agent)")] string name)
    {
        var agent = registry.Get(name);
        if (agent is null)
            return $"Agent '{name}' not found in registry. Use list_agents to see registered agents.";

        var containerName = agent.ContainerName ?? name;
        var ok = await docker.StartContainerAsync(containerName);

        return ok
            ? $"Container '{containerName}' started successfully. Wait for the next heartbeat to confirm the agent is online."
            : $"Failed to start container '{containerName}'. Docker socket may be unavailable — check orchestrator logs.";
    }
}
