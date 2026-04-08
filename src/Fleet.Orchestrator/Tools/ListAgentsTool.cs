using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ListAgentsTool(AgentRegistry registry, IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "list_agents")]
    [Description("List all registered fleet agents with their current status, role, model, current task, and last heartbeat time.")]
    public async Task<string> ListAgents()
    {
        // Merge DB agent records so DB-only (not yet provisioned) agents are visible.
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is not null)
            {
                var dbAgents = await db.Agents.AsNoTracking().ToListAsync();
                foreach (var a in dbAgents)
                    registry.PreloadFromDbConfig(a); // no-op if already in registry
            }
        }
        catch { /* DB unavailable — use what's in registry */ }

        var agents = registry.GetAll();

        if (agents.Count == 0)
            return "No agents registered yet. Agents appear after their first heartbeat.";

        var sb = new StringBuilder();
        sb.AppendLine($"Fleet agents ({agents.Count} registered):");
        sb.AppendLine();

        foreach (var a in agents)
        {
            sb.AppendLine($"### {a.AgentName}");
            sb.AppendLine($"- Status: {a.EffectiveStatus}");
            if (a.Role  is not null) sb.AppendLine($"- Role: {a.Role}");
            if (a.Model is not null) sb.AppendLine($"- Model: {a.Model}");
            if (a.CurrentTask is not null) sb.AppendLine($"- Current task: {a.CurrentTask}");
            if (!a.IsDbOnly)
            {
                var age = DateTimeOffset.UtcNow - a.LastSeen;
                var ageStr = age.TotalSeconds < 60
                    ? $"{(int)age.TotalSeconds}s ago"
                    : age.TotalMinutes < 60
                        ? $"{(int)age.TotalMinutes}m ago"
                        : $"{(int)age.TotalHours}h ago";
                sb.AppendLine($"- Last seen: {ageStr} ({a.LastSeen:yyyy-MM-dd HH:mm:ss}Z)");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
