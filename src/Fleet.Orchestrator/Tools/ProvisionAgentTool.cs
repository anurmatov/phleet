using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ProvisionAgentTool(ContainerProvisioningService provisioning)
{
    [McpServerTool(Name = "provision_agent")]
    [Description("Creates and starts a Docker container for an agent from its DB config. Fails if a container with that name already exists — use reprovision_agent to replace an existing one.")]
    public async Task<string> ProvisionAsync(
        [Description("Agent short name (e.g. my-agent)")] string agent_name)
    {
        var result = await provisioning.ProvisionAsync(agent_name);
        return result.Success
            ? $"✓ {result.Message}"
            : $"✗ provision_agent failed: {result.Message}";
    }
}
