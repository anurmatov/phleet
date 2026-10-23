using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class StartAgentTool(IAgentRuntimeLifecycle lifecycle)
{
    [McpServerTool(Name = "start_agent")]
    [Description("Start an agent through its authoritative runtime lifecycle; blocked or unknown state never falls back to Docker.")]
    public async Task<string> StartAgentAsync(
        [Description("Agent name (e.g. agent1)")] string name)
    {
        var result = await lifecycle.StartAsync(name);
        return result.Success ? result.Message : $"{result.StatusCode}: {result.Message}";
    }
}
