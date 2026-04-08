using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class GetAgentConfigTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "get_agent_config")]
    [Description("Get full DB configuration for an agent: model, memory, tools, projects, MCP endpoints, instruction assignments, and env refs.")]
    public async Task<string> GetAgentConfigAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.Tools.Where(t => t.IsEnabled).OrderBy(t => t.ToolName))
            .Include(a => a.Projects.OrderBy(p => p.ProjectName))
            .Include(a => a.McpEndpoints.OrderBy(e => e.McpName))
            .Include(a => a.Instructions.OrderBy(i => i.LoadOrder))
                .ThenInclude(ai => ai.Instruction)
            .Include(a => a.EnvRefs.OrderBy(e => e.EnvKeyName))
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        var sb = new StringBuilder();
        sb.AppendLine($"## {agent.Name} ({agent.DisplayName})");
        sb.AppendLine($"- Role: {agent.Role}");
        sb.AppendLine($"- Model: {agent.Model}");
        sb.AppendLine($"- Memory limit: {agent.MemoryLimitMb}MB");
        sb.AppendLine($"- Container: {agent.ContainerName}");
        sb.AppendLine($"- Enabled: {agent.IsEnabled}");
        sb.AppendLine($"- Telegram send-only: {agent.TelegramSendOnly}");
        sb.AppendLine($"- Auto memory: {agent.AutoMemoryEnabled}");
        sb.AppendLine();

        sb.AppendLine("### Projects");
        if (agent.Projects.Count == 0) sb.AppendLine("(none)");
        else foreach (var p in agent.Projects) sb.AppendLine($"- {p.ProjectName}");
        sb.AppendLine();

        sb.AppendLine("### MCP Endpoints");
        if (agent.McpEndpoints.Count == 0) sb.AppendLine("(none)");
        else foreach (var e in agent.McpEndpoints) sb.AppendLine($"- {e.McpName}: {e.Url} ({e.TransportType})");
        sb.AppendLine();

        sb.AppendLine("### Instructions");
        if (agent.Instructions.Count == 0) sb.AppendLine("(none)");
        else foreach (var i in agent.Instructions) sb.AppendLine($"- [{i.LoadOrder}] {i.Instruction.Name} (v{i.Instruction.CurrentVersion})");
        sb.AppendLine();

        sb.AppendLine("### Tools");
        if (agent.Tools.Count == 0) sb.AppendLine("(none)");
        else foreach (var t in agent.Tools) sb.AppendLine($"- {t.ToolName}");
        sb.AppendLine();

        sb.AppendLine("### Env Refs");
        if (agent.EnvRefs.Count == 0) sb.AppendLine("(none)");
        else foreach (var e in agent.EnvRefs) sb.AppendLine($"- {e.EnvKeyName}");

        return sb.ToString();
    }
}
