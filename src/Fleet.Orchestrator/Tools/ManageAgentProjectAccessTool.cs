using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ManageAgentProjectAccessTool(IServiceScopeFactory scopeFactory, ConfigService configService)
{
    [McpServerTool(Name = "manage_agent_project_access")]
    [Description("Manage memory project access for an agent. Actions: 'list' — show current projects; 'add' — grant access to a project; 'remove' — revoke access to a project; 'set_wildcard' — grant access to all projects (*); 'clear_wildcard' — remove wildcard, leaving only explicit project rows.")]
    public async Task<string> ManageAgentProjectAccessAsync(
        [Description("Agent name (e.g. acto, adev)")] string agent_name,
        [Description("Action: 'list', 'add', 'remove', 'set_wildcard', 'clear_wildcard'")] string action,
        [Description("Project name (required for 'add' and 'remove' actions)")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(agent_name))
            return "manage_agent_project_access: missing required parameter 'agent_name'.";

        action = action.Trim().ToLowerInvariant();
        var agentName = agent_name.Trim().ToLowerInvariant();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        switch (action)
        {
            case "list":
            {
                var rows = await db.AgentProjectAccess
                    .Where(x => x.AgentName == agentName)
                    .Select(x => x.Project)
                    .OrderBy(p => p)
                    .ToListAsync();

                if (rows.Count == 0)
                    return $"Agent '{agentName}' has no memory project access entries.";

                var isWildcard = rows.Contains("*");
                var projects = string.Join(", ", rows);
                return isWildcard
                    ? $"Agent '{agentName}' has wildcard access (*) — can read all memory projects. Rows: {projects}"
                    : $"Agent '{agentName}' project access: {projects}";
            }

            case "add":
            {
                var proj = project?.Trim().ToLowerInvariant() ?? "";
                if (string.IsNullOrEmpty(proj))
                    return "manage_agent_project_access: 'add' action requires 'project' parameter.";

                var exists = await db.AgentProjectAccess
                    .AnyAsync(x => x.AgentName == agentName && x.Project == proj);
                if (exists)
                    return $"Agent '{agentName}' already has access to project '{proj}'.";

                db.AgentProjectAccess.Add(new AgentProjectAccess { AgentName = agentName, Project = proj });
                await db.SaveChangesAsync();
                await configService.ReloadAsync();

                var all = await db.AgentProjectAccess
                    .Where(x => x.AgentName == agentName)
                    .Select(x => x.Project)
                    .OrderBy(p => p)
                    .ToListAsync();
                return $"Added project '{proj}' to agent '{agentName}'. Current access: {string.Join(", ", all)}";
            }

            case "remove":
            {
                var proj = project?.Trim().ToLowerInvariant() ?? "";
                if (string.IsNullOrEmpty(proj))
                    return "manage_agent_project_access: 'remove' action requires 'project' parameter.";

                var row = await db.AgentProjectAccess
                    .FirstOrDefaultAsync(x => x.AgentName == agentName && x.Project == proj);
                if (row is null)
                    return $"Agent '{agentName}' does not have access to project '{proj}'.";

                db.AgentProjectAccess.Remove(row);
                await db.SaveChangesAsync();
                await configService.ReloadAsync();

                var all = await db.AgentProjectAccess
                    .Where(x => x.AgentName == agentName)
                    .Select(x => x.Project)
                    .OrderBy(p => p)
                    .ToListAsync();
                var remaining = all.Count > 0 ? string.Join(", ", all) : "(none)";
                return $"Removed project '{proj}' from agent '{agentName}'. Remaining access: {remaining}";
            }

            case "set_wildcard":
            {
                var exists = await db.AgentProjectAccess
                    .AnyAsync(x => x.AgentName == agentName && x.Project == "*");
                if (!exists)
                {
                    db.AgentProjectAccess.Add(new AgentProjectAccess { AgentName = agentName, Project = "*" });
                    await db.SaveChangesAsync();
                    await configService.ReloadAsync();
                }
                return $"Agent '{agentName}' now has wildcard access (*) — can read all memory projects.";
            }

            case "clear_wildcard":
            {
                var row = await db.AgentProjectAccess
                    .FirstOrDefaultAsync(x => x.AgentName == agentName && x.Project == "*");
                if (row is null)
                    return $"Agent '{agentName}' does not have a wildcard entry — nothing to clear.";

                db.AgentProjectAccess.Remove(row);
                await db.SaveChangesAsync();
                await configService.ReloadAsync();

                var remaining = await db.AgentProjectAccess
                    .Where(x => x.AgentName == agentName)
                    .Select(x => x.Project)
                    .OrderBy(p => p)
                    .ToListAsync();
                var remainingStr = remaining.Count > 0 ? string.Join(", ", remaining) : "(none)";
                return $"Cleared wildcard from agent '{agentName}'. Explicit project access: {remainingStr}";
            }

            default:
                return $"Unknown action '{action}'. Valid actions: list, add, remove, set_wildcard, clear_wildcard.";
        }
    }
}
