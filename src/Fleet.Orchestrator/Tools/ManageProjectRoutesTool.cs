using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>
/// Admin tool for project context routes (#347) — the deterministic signals that attach a
/// project's full context to a card-mode agent's turn. Served on the admin <c>/mcp</c> only and never
/// auto-granted. The rules are <see cref="ProjectCardService"/>'s, shared with the REST routes.
/// </summary>
[McpServerToolType]
public sealed class ManageProjectRoutesTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_project_routes")]
    [Description("List, add or remove the routes of a project context. A route attaches the project's full context to card-mode agents' turns carrying that signal: repo (owner/name, stored lower-case), workflow (exact workflow type), or chat (signed 64-bit chat id). Precedence at the agent is repo > workflow > chat. Agents pick route changes up on reprovision.")]
    public async Task<string> ManageProjectRoutesAsync(
        [Description("list | add | remove")] string action,
        [Description("Project name (e.g. 'my-project')")] string name,
        [Description("For add: repo | workflow | chat")] string? kind = null,
        [Description("For add: the signal value (repo 'owner/name', workflow type, or chat id)")] string? value = null,
        [Description("For remove: the route id (see list)")] int? id = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        switch (action?.Trim().ToLowerInvariant())
        {
            case "list":
            {
                var ctx = await ProjectCardService.FindContextAsync(db, name);
                if (ctx is null)
                    return $"Project context '{name}' not found.";

                var routes = await ProjectCardService.ListRoutesAsync(db, ctx.Id);
                if (routes.Count == 0)
                    return $"Project context '{ctx.Name}' has no routes.";

                var sb = new StringBuilder();
                sb.AppendLine($"## Routes: {ctx.Name} ({routes.Count})");
                foreach (var r in routes)
                    sb.AppendLine($"- [{r.Id}] {r.SignalKind}={r.SignalValue}");
                return sb.ToString();
            }

            case "add":
            {
                var result = await ProjectCardService.AddRouteAsync(db, name, kind, value, createdBy: "mcp");
                return result.Saved ? result.Message + ". Reprovision the agents assigned this project to apply." : result.Message;
            }

            case "remove":
            {
                if (id is not { } routeId)
                    return "id is required for remove (see action=list).";
                var result = await ProjectCardService.RemoveRouteAsync(db, name, routeId);
                return result.Saved ? result.Message + ". Reprovision the agents assigned this project to apply." : result.Message;
            }

            default:
                return $"Unknown action '{action}'. Use list, add or remove.";
        }
    }
}
