using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ManageAgentTelegramUsersTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_agent_telegram_users")]
    [Description("Add or remove an allowed Telegram user ID for an agent. Only listed user IDs can send DMs to the agent's bot.")]
    public async Task<string> ManageAgentTelegramUsersAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add' or 'remove'")] string action,
        [Description("Telegram user ID (numeric)")] long user_id)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.TelegramUsers)
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        action = action.Trim().ToLowerInvariant();

        switch (action)
        {
            case "add":
            {
                if (agent.TelegramUsers.Any(u => u.UserId == user_id))
                    return $"Telegram user {user_id} already allowed for agent '{agent_name}'.";

                agent.TelegramUsers.Add(new AgentTelegramUser { AgentId = agent.Id, UserId = user_id });
                await db.SaveChangesAsync();
                return $"Added Telegram user {user_id} to agent '{agent_name}'.";
            }

            case "remove":
            {
                var existing = agent.TelegramUsers.FirstOrDefault(u => u.UserId == user_id);
                if (existing is null)
                    return $"Telegram user {user_id} not found for agent '{agent_name}'.";

                db.AgentTelegramUsers.Remove(existing);
                await db.SaveChangesAsync();
                return $"Removed Telegram user {user_id} from agent '{agent_name}'.";
            }

            default:
                return $"Unknown action '{action}'. Use 'add' or 'remove'.";
        }
    }
}
