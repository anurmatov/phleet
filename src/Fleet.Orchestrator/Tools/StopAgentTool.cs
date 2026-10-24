using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class StopAgentTool(IAgentRuntimeLifecycle lifecycle)
{
    [McpServerTool(Name = "stop_agent")]
    [Description("Stop an agent through its authoritative runtime lifecycle; blocked or unknown state never falls back to Docker.")]
    public async Task<string> StopAgentAsync(
        [Description("Agent name (e.g. agent1)")] string name)
    {
        var result = await lifecycle.StopAsync(name);
        return result.Success ? result.Message : $"{result.StatusCode}: {result.Message}";
    }
}
