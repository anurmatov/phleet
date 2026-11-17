using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services.McpTools;

/// <summary>The tools of every MCP server an agent is configured with, as the servers list them.</summary>
/// <param name="AlwaysOn">Tools every agent has whether or not they are granted.</param>
public sealed record McpToolDiscoveryResult(
    string Agent, IReadOnlyList<string> AlwaysOn, IReadOnlyList<McpServerTools> Servers);

/// <summary>
/// Asks each of an agent's saved MCP endpoints for its tools, so the dashboard offers them to pick
/// rather than to type. Saved endpoints only: an address typed into the dialog is not asked until
/// it is saved, so this never sends a request to an address the configuration does not hold.
/// </summary>
public sealed class McpToolDiscovery(
    IServiceScopeFactory scopes, IConfiguration config, JournalTokenService journalTokens, IMcpToolLister lister)
{
    /// <summary>Null when there is no such agent.</summary>
    public async Task<McpToolDiscoveryResult?> DiscoverAsync(string agentName, CancellationToken ct)
    {
        Agent? agent;
        List<SecretBinding> bindings;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            agent = await db.Agents
                .Include(a => a.McpEndpoints)
                .Include(a => a.EnvRefs)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Name == agentName, ct);
            if (agent is null)
                return null;
            bindings = await db.SecretBindings.AsNoTracking().ToListAsync(ct);
        }

        // The env file is read only when a header needs a value from it.
        var needsEnv = agent.McpEndpoints.Any(e => e.HeaderEnvRefsJson is not null);
        IReadOnlyDictionary<string, string> envValues = needsEnv
            ? EnvFile.Read(ProvisioningOptions.From(config).EnvFilePath)
            : new Dictionary<string, string>();

        var plan = McpToolPlan.Build(
            agent, envValues, bindings,
            AgentConfigFiles.NormalizeFleetMemoryMcpUrl(config["FleetMemory:McpUrl"]),
            () => journalTokens.DescribeKeyFault() is null
                ? journalTokens.Mint(JournalTokenService.PurposeRead, agent.Name)
                : null,
            Fleet.Shared.Mcp.AgentIdentitySettings.From(config).ServiceToken(OrchestratorMcp.ServerName));

        var answers = await Task.WhenAll(plan.Select(entry =>
            entry.Target is { } target ? lister.ListAsync(target, ct) : Task.FromResult(entry.NotAsked!)));

        return new McpToolDiscoveryResult(agent.Name, HarnessDefaults.AgentDefaultAllowedTools, answers);
    }
}
