using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
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
            // #455: applied only to provider pi; another provider keeps the stored value but ignores it.
            var piImages = (a.PiImageInput ? "on" : "off")
                + (a.PiImageInput && !PiLocalModel.IsPi(a.Provider) ? " (ignored: pi only)" : "");
            sb.AppendLine($"- **{a.Name}** ({a.DisplayName}) — role: {a.Role}, model: {a.Model}, memory: {a.MemoryLimitMb}MB, warmup: {a.WarmupTimeoutSeconds}s, pi image input: {piImages}, {status}");
        }

        return sb.ToString();
    }
}
