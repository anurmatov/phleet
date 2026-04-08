using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ReprovisionAgentTool(ContainerProvisioningService provisioning)
{
    [McpServerTool(Name = "reprovision_agent")]
    [Description("Stops, removes, and re-creates the Docker container for an agent from its current DB config. Use this after updating agent config (model, tools, memory). Workspace volume data is preserved.")]
    public async Task<string> ReprovisionAsync(
        [Description("Agent short name (e.g. my-agent)")] string agent_name)
    {
        var result = await provisioning.ReprovisionAsync(agent_name);
        return result.Success
            ? $"✓ {result.Message}"
            : $"✗ reprovision_agent failed: {result.Message}";
    }
}
