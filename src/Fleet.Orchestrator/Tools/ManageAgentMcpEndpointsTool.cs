using System.ComponentModel;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ManageAgentMcpEndpointsTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_agent_mcp_endpoints")]
    [Description("Add, update, or remove an MCP endpoint for an agent. Use action='add' to add a new endpoint, action='update' to change url/transport/header mappings of an existing endpoint, action='remove' to delete an endpoint. Changes take effect on reprovision.")]
    public async Task<string> ManageAgentMcpEndpointsAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add', 'update', or 'remove'")] string action,
        [Description("MCP server name (e.g. fleet-memory, fleet-playwright)")] string mcp_name,
        [Description("URL of the MCP server. Required for add/update.")] string? url = null,
        [Description("Transport type (e.g. sse, http). Required for add; optional for update.")] string? transport_type = null,
        [Description("Optional headers sent to this endpoint, each { \"headerName\": \"X-Example-Key\", \"envRefName\": \"EXAMPLE_KEY\" }: the header carries the value of that Env Ref, which the agent must already hold. Names only, never a value. Transport http on claude and codex only. Omit to keep the current mappings; [] clears them.")] McpHeaderEnvRef?[]? header_env_refs = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.McpEndpoints)
            .Include(a => a.EnvRefs)
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

                // #593: only references the agent already holds; this tool never adds one.
                IReadOnlyList<McpHeaderEnvRef?> mappings = header_env_refs ?? [];
                if (McpHeaderMappings.Check(mcp_name, transport_type, agent.Provider, mappings, [], HeldRefs(agent)) is { } addFault)
                    return $"{addFault.Message} Nothing was changed.";

                agent.McpEndpoints.Add(new AgentMcpEndpoint
                {
                    AgentId = agent.Id,
                    McpName = mcp_name,
                    Url = url,
                    TransportType = transport_type,
                    HeaderEnvRefsJson = McpHeaderMappings.Serialize([.. mappings.Select(m => m!)]),
                });
                await db.SaveChangesAsync();
                return $"Added MCP endpoint '{mcp_name}' ({url}, {transport_type}{Headers(mappings)}) to agent '{agent_name}'.";
            }

            case "update":
            {
                var existing = agent.McpEndpoints.FirstOrDefault(e => e.McpName.Equals(mcp_name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return $"MCP endpoint '{mcp_name}' not found for agent '{agent_name}'. Use action='add' to create it.";

                // #593: omitted keeps the stored mappings, which must then be readable; a supplied list replaces them.
                if (!McpHeaderMappings.TryRead(existing, out var stored, out var storedFault) && header_env_refs is null)
                    return $"{storedFault} Nothing was changed.";

                var changed = new List<string>();
                if (url is not null && url != existing.Url) { changed.Add($"url: {existing.Url} → {url}"); existing.Url = url; }
                if (transport_type is not null && transport_type != existing.TransportType) { changed.Add($"transport_type: {existing.TransportType} → {transport_type}"); existing.TransportType = transport_type; }

                IReadOnlyList<McpHeaderEnvRef?> resulting = (IReadOnlyList<McpHeaderEnvRef?>?)header_env_refs ?? stored;
                if (McpHeaderMappings.Check(existing.McpName, existing.TransportType, agent.Provider, resulting, stored, HeldRefs(agent)) is { } updateFault)
                    return $"{updateFault.Message} Nothing was changed.";
                if (header_env_refs is not null)
                {
                    var json = McpHeaderMappings.Serialize([.. header_env_refs.Select(m => m!)]);
                    if (json != existing.HeaderEnvRefsJson)
                    {
                        changed.Add($"header_env_refs: [{Names(stored)}] → [{Names(header_env_refs)}]");
                        existing.HeaderEnvRefsJson = json;
                    }
                }

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

    private static List<string> HeldRefs(Agent agent) => [.. agent.EnvRefs.Select(r => r.EnvKeyName)];

    private static string Names(IEnumerable<McpHeaderEnvRef?> mappings) =>
        string.Join(", ", mappings.Select(m => $"{m!.HeaderName} ← {m.EnvRefName}"));

    private static string Headers(IReadOnlyList<McpHeaderEnvRef?> mappings) =>
        mappings.Count == 0 ? "" : $", headers [{Names(mappings)}]";
}
