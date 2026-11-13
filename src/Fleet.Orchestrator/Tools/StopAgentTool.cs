using System.ComponentModel;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>The MCP adapter over <see cref="AgentLifecycle.StopAsync"/>, which the REST stop route calls too.</summary>
[McpServerToolType]
public sealed class StopAgentTool(AgentLifecycle lifecycle)
{
    [McpServerTool(Name = "stop_agent")]
    [Description("Stop a fleet agent's Docker container gracefully (waits up to 10s before SIGKILL). Use with caution.")]
    public async Task<string> StopAgentAsync(
        [Description("Agent name (e.g. my-agent)")] string name)
    {
        var outcome = await lifecycle.StopAsync(name);
        return outcome.Fault switch
        {
            null => $"Container '{outcome.Value!.ContainerName}' stopped successfully.",
            { Code: "agent_not_registered" } => $"Agent '{name}' not found in registry. Use list_agents to see registered agents.",
            var fault => DockerFailure(fault),
        };
    }

    /// <summary>The service's sentence, with the hint the tool has always added for an agent reading it.</summary>
    private static string DockerFailure(WriteFault fault) =>
        fault.Message.Replace(" — check orchestrator logs", ". Docker socket may be unavailable — check orchestrator logs.");
}
