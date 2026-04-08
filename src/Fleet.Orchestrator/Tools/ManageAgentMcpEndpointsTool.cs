using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ManageAgentMcpEndpointsTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_agent_mcp_endpoints")]
    [Description("Add, update, or remove an MCP endpoint for an agent. Use action='add' to add a new endpoint, action='update' to change url/transport of an existing endpoint, action='remove' to delete an endpoint.")]
    public async Task<string> ManageAgentMcpEndpointsAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add', 'update', or 'remove'")] string action,
        [Description("MCP server name (e.g. fleet-memory, fleet-playwright)")] string mcp_name,
        [Description("URL of the MCP server. Required for add/update.")] string? url = null,
        [Description("Transport type (e.g. sse, http). Required for add; optional for update.")] string? transport_type = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.McpEndpoints)
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        action = action.Trim().ToLowerInvariant();

        switch (action)
        {
            case "add":
            {
                if (string.IsNullOrWhiteSpace(url))
                    return "url is required for action='add'.";
                if (string.IsNullOrWhiteSpace(transport_type))
                    return "transport_type is required for action='add'.";

                if (agent.McpEndpoints.Any(e => e.McpName.Equals(mcp_name, StringComparison.OrdinalIgnoreCase)))
                    return $"MCP endpoint '{mcp_name}' already exists for agent '{agent_name}'. Use action='update' to change it.";

                agent.McpEndpoints.Add(new AgentMcpEndpoint
                {
                    AgentId = agent.Id,
                    McpName = mcp_name,
                    Url = url,
                    TransportType = transport_type,
                });
                await db.SaveChangesAsync();
                return $"Added MCP endpoint '{mcp_name}' ({url}, {transport_type}) to agent '{agent_name}'.";
            }

            case "update":
            {
                var existing = agent.McpEndpoints.FirstOrDefault(e => e.McpName.Equals(mcp_name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return $"MCP endpoint '{mcp_name}' not found for agent '{agent_name}'. Use action='add' to create it.";

                var changed = new List<string>();
                if (url is not null && url != existing.Url) { changed.Add($"url: {existing.Url} → {url}"); existing.Url = url; }
                if (transport_type is not null && transport_type != existing.TransportType) { changed.Add($"transport_type: {existing.TransportType} → {transport_type}"); existing.TransportType = transport_type; }

                if (changed.Count == 0)
                    return $"No changes specified for MCP endpoint '{mcp_name}'.";

                await db.SaveChangesAsync();
                return $"Updated MCP endpoint '{mcp_name}' for agent '{agent_name}': {string.Join(", ", changed)}.";
            }

            case "remove":
            {
                var existing = agent.McpEndpoints.FirstOrDefault(e => e.McpName.Equals(mcp_name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return $"MCP endpoint '{mcp_name}' not found for agent '{agent_name}'.";

                db.AgentMcpEndpoints.Remove(existing);
                await db.SaveChangesAsync();
                return $"Removed MCP endpoint '{mcp_name}' from agent '{agent_name}'.";
            }

            default:
                return $"Unknown action '{action}'. Use 'add', 'update', or 'remove'.";
        }
    }
}
