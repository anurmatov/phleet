using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class DeprovisionAgentTool(ContainerProvisioningService provisioning)
{
    [McpServerTool(Name = "deprovision_agent")]
    [Description("Stops and removes the Docker container for an agent. Fails if the container is not found. Data in the workspace volume is preserved.")]
    public async Task<string> DeprovisionAsync(
        [Description("Agent short name (e.g. my-agent)")] string agent_name)
    {
        var result = await provisioning.DeprovisionAsync(agent_name);
        return result.Success
            ? $"✓ {result.Message}"
            : $"✗ deprovision_agent failed: {result.Message}";
    }
}
