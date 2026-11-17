using Fleet.Orchestrator.Data;
using Fleet.Shared.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// What each agent may call on the fleet's own MCP servers, read from the agents' stored tools.
/// The orchestrator's own MCP endpoint asks it directly; the other servers read the whole map from
/// <c>GET /internal/agent-mcp-grants</c>.
/// </summary>
/// <remarks>
/// The stored tools are the authority, not what the running container was provisioned with: a
/// tool taken away is refused by the servers at once, before the agent is reprovisioned.
/// </remarks>
public sealed class DbAgentGrantSource(IServiceScopeFactory scopes, IConfiguration config) : IAgentGrantSource
{
    public async ValueTask<AgentGrantAnswer> GrantsOfAsync(string agent, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<OrchestratorDbContext>() is not { } db)
                return AgentGrantAnswer.Unknown;

            var row = await db.Agents.Include(a => a.Tools).AsNoTracking().FirstOrDefaultAsync(a => a.Name == agent, ct);
            // No such agent is an answer: it is granted nothing.
            return new AgentGrantAnswer(true, row is null ? [] : AgentConfigFiles.EffectiveMcpGrants(row, CtoAgentSetting.Resolve(config)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // The database could not answer. Not the same as "granted nothing", and never "granted".
            return AgentGrantAnswer.Unknown;
        }
    }

    /// <summary>Every agent's grants, by name. Null when there is no database to read them from.</summary>
    public async Task<Dictionary<string, List<string>>?> AllAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<OrchestratorDbContext>() is not { } db)
            return null;

        var cto = CtoAgentSetting.Resolve(config);
        var agents = await db.Agents.Include(a => a.Tools).AsNoTracking().ToListAsync(ct);
        return agents.ToDictionary(a => a.Name, a => AgentConfigFiles.EffectiveMcpGrants(a, cto), StringComparer.Ordinal);
    }
}
