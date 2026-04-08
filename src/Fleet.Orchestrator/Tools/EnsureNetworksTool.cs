using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class EnsureNetworksTool(ContainerProvisioningService provisioning)
{
    [McpServerTool(Name = "ensure_networks_exist")]
    [Description("Ensures all Docker networks referenced in agent_networks DB table exist as standalone bridge networks. Creates any that are missing. Safe to call multiple times — idempotent.")]
    public async Task<string> EnsureAsync()
    {
        var result = await provisioning.EnsureNetworksExistAsync();

        var sb = new StringBuilder();
        sb.AppendLine("## Network Ensure Result");
        sb.AppendLine();

        if (result.Ensured.Count > 0)
        {
            sb.AppendLine("### Ensured (created or already existed)");
            foreach (var n in result.Ensured)
                sb.AppendLine($"- {n}");
            sb.AppendLine();
        }

        if (result.Failed.Count > 0)
        {
            sb.AppendLine("### Failed");
            foreach (var n in result.Failed)
                sb.AppendLine($"- {n}");
            sb.AppendLine();
            sb.AppendLine("Check orchestrator logs for Docker API errors.");
        }
        else
        {
            sb.AppendLine("All networks are ready.");
        }

        return sb.ToString();
    }
}
