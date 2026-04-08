using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ManageAgentEnvRefsTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_agent_env_refs")]
    [Description("Add or remove an environment variable key reference for an agent. Env refs are secret key names read from the .env file at provision time (no actual values stored).")]
    public async Task<string> ManageAgentEnvRefsAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add' or 'remove'")] string action,
        [Description("Environment variable key name (e.g. TELEGRAM_BOT_TOKEN)")] string env_key_name)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.EnvRefs)
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        action = action.Trim().ToLowerInvariant();

        switch (action)
        {
            case "add":
            {
                if (agent.EnvRefs.Any(r => r.EnvKeyName.Equals(env_key_name, StringComparison.OrdinalIgnoreCase)))
                    return $"Env ref '{env_key_name}' already exists for agent '{agent_name}'.";

                agent.EnvRefs.Add(new AgentEnvRef { AgentId = agent.Id, EnvKeyName = env_key_name });
                await db.SaveChangesAsync();
                return $"Added env ref '{env_key_name}' to agent '{agent_name}'.";
            }

            case "remove":
            {
                var existing = agent.EnvRefs.FirstOrDefault(r => r.EnvKeyName.Equals(env_key_name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return $"Env ref '{env_key_name}' not found for agent '{agent_name}'.";

                db.AgentEnvRefs.Remove(existing);
                await db.SaveChangesAsync();
                return $"Removed env ref '{env_key_name}' from agent '{agent_name}'.";
            }

            default:
                return $"Unknown action '{action}'. Use 'add' or 'remove'.";
        }
    }
}
