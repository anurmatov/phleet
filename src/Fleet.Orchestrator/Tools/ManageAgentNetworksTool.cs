using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ManageAgentNetworksTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_agent_networks")]
    [Description("Add or remove a Docker network for an agent. Use action='add' to attach a network, action='remove' to detach it.")]
    public async Task<string> ManageAgentNetworksAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add' or 'remove'")] string action,
        [Description("Docker network name (e.g. fleet-net)")] string network_name)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.Networks)
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        action = action.Trim().ToLowerInvariant();

        switch (action)
        {
            case "add":
            {
                if (agent.Networks.Any(n => n.NetworkName.Equals(network_name, StringComparison.OrdinalIgnoreCase)))
                    return $"Network '{network_name}' already assigned to agent '{agent_name}'.";

                agent.Networks.Add(new AgentNetwork { AgentId = agent.Id, NetworkName = network_name });
                await db.SaveChangesAsync();
                return $"Added network '{network_name}' to agent '{agent_name}'.";
            }

            case "remove":
            {
                var existing = agent.Networks.FirstOrDefault(n => n.NetworkName.Equals(network_name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return $"Network '{network_name}' not found for agent '{agent_name}'.";

                db.AgentNetworks.Remove(existing);
                await db.SaveChangesAsync();
                return $"Removed network '{network_name}' from agent '{agent_name}'.";
            }

            default:
                return $"Unknown action '{action}'. Use 'add' or 'remove'.";
        }
    }
}
