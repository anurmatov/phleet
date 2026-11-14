using System.ComponentModel;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>The MCP adapter over <see cref="AgentLifecycle.StartAsync"/>, which the REST start route calls too.</summary>
[McpServerToolType]
public sealed class StartAgentTool(AgentLifecycle lifecycle)
{
    [McpServerTool(Name = "start_agent")]
    [Description("Start a fleet agent's Docker container. Use this to bring back a stopped agent.")]
    public async Task<string> StartAgentAsync(
        [Description("Agent name (e.g. my-agent)")] string name)
    {
        var outcome = await lifecycle.StartAsync(name);
        return outcome.Fault switch
        {
            null => $"Container '{outcome.Value!.ContainerName}' started successfully.",
            { Code: "agent_not_registered" } => $"Agent '{name}' not found in registry. Use list_agents to see registered agents.",
            var fault => DockerFailure(fault),
        };
    }

    /// <summary>The service's sentence, with the hint the tool has always added for an agent reading it.</summary>
    private static string DockerFailure(WriteFault fault) =>
        fault.Message.Replace(" — check orchestrator logs", ". Docker socket may be unavailable — check orchestrator logs.");
}
