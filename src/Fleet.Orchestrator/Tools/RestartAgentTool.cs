using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class RestartAgentTool(IAgentRuntimeLifecycle lifecycle)
{
    [McpServerTool(Name = "restart_agent")]
    [Description("Restart an agent through its authoritative runtime lifecycle; blocked or unknown state never falls back to Docker.")]
    public async Task<string> RestartAgentAsync(
        [Description("Agent name (e.g. agent1)")] string name)
    {
        var result = await lifecycle.RestartAsync(name);
        return result.Success ? result.Message : $"{result.StatusCode}: {result.Message}";
    }
}
