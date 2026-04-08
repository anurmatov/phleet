using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ListAgentConfigsTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "list_agent_configs")]
    [Description("List all agents in the DB with their model, role, and enabled status.")]
    public async Task<string> ListAgentConfigsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync();

        if (agents.Count == 0)
            return "No agents found in DB.";

        var sb = new StringBuilder();
        sb.AppendLine($"Agent configs ({agents.Count} total):");
        sb.AppendLine();

        foreach (var a in agents)
        {
            var status = a.IsEnabled ? "enabled" : "disabled";
            sb.AppendLine($"- **{a.Name}** ({a.DisplayName}) — role: {a.Role}, model: {a.Model}, memory: {a.MemoryLimitMb}MB, {status}");
        }

        return sb.ToString();
    }
}
