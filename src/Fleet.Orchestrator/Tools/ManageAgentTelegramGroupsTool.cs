using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ManageAgentTelegramGroupsTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_agent_telegram_groups")]
    [Description("Add or remove an allowed Telegram group ID for an agent. Only listed group IDs will be monitored by the agent.")]
    public async Task<string> ManageAgentTelegramGroupsAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add' or 'remove'")] string action,
        [Description("Telegram group ID (numeric, typically negative)")] long group_id)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.TelegramGroups)
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        action = action.Trim().ToLowerInvariant();

        switch (action)
        {
            case "add":
            {
                if (agent.TelegramGroups.Any(g => g.GroupId == group_id))
                    return $"Telegram group {group_id} already allowed for agent '{agent_name}'.";

                agent.TelegramGroups.Add(new AgentTelegramGroup { AgentId = agent.Id, GroupId = group_id });
                await db.SaveChangesAsync();
                return $"Added Telegram group {group_id} to agent '{agent_name}'.";
            }

            case "remove":
            {
                var existing = agent.TelegramGroups.FirstOrDefault(g => g.GroupId == group_id);
                if (existing is null)
                    return $"Telegram group {group_id} not found for agent '{agent_name}'.";

                db.AgentTelegramGroups.Remove(existing);
                await db.SaveChangesAsync();
                return $"Removed Telegram group {group_id} from agent '{agent_name}'.";
            }

            default:
                return $"Unknown action '{action}'. Use 'add' or 'remove'.";
        }
    }
}
