using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>
/// Read and author the rows in <c>output_styles</c> (#317).
/// </summary>
/// <remarks>
/// Seeding is create-if-absent so an operator edit survives a redeploy, which means this tool and
/// the REST surface are the only ways an existing row ever changes. Both go through
/// <see cref="OutputStyleValidator"/> and <see cref="OutputStyleUsage"/>, so neither can accept a
/// body the other would refuse or disagree about whether a style is in use.
/// </remarks>
[McpServerToolType]
public sealed class ManageOutputStylesTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_output_styles")]
    [Description("Manage Claude Code output styles (an agent's chat tone and register). Actions: "
               + "'list' — names, descriptions and which agents use each; "
               + "'get' — one style with its full body; "
               + "'create' — add a style from a full style file (YAML frontmatter + body); "
               + "'update' — replace an existing style's body; "
               + "'delete' — remove a style, refused while any agent is assigned to it. "
               + "A style reaches an agent at provision time, so reprovision the assigned agents after an edit.")]
    public async Task<string> ManageOutputStylesAsync(
        [Description("Action: 'list', 'get', 'create', 'update', 'delete'")] string action,
        [Description("Style name (required for 'get', 'create', 'update', 'delete')")] string? name = null,
        [Description("Full style file for 'create'/'update': YAML frontmatter with name, description and optionally keep-coding-instructions, then the style body. The frontmatter name must equal the style name.")] string? body = null)
    {
        if (string.IsNullOrWhiteSpace(action))
            return "manage_output_styles: missing required parameter 'action'.";

        action = action.Trim().ToLowerInvariant();
        var styleName = name?.Trim() ?? "";

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        switch (action)
        {
            case "list":
            {
                var styles = await db.OutputStyles
                    .AsNoTracking()
                    .OrderBy(s => s.Name)
                    .Select(s => new { s.Name, s.Description })
                    .ToListAsync();

                if (styles.Count == 0)
                    return "No output styles exist.";

                var usage = await OutputStyleUsage.ByStyleAsync(db);
                var lines = styles.Select(s =>
                {
                    var agents = OutputStyleUsage.For(usage, s.Name);
                    var used = agents.Count == 0 ? "unused" : $"used by {string.Join(", ", agents)}";
                    return $"- {s.Name} — {s.Description ?? "(no description)"} [{used}]";
                });
                return $"Output styles ({styles.Count}):\n{string.Join("\n", lines)}";
            }

            case "get":
            {
                if (styleName.Length == 0)
                    return "manage_output_styles: 'get' action requires 'name' parameter.";

                var style = await db.OutputStyles.AsNoTracking().FirstOrDefaultAsync(s => s.Name == styleName);
                if (style is null)
                    return $"Output style '{styleName}' not found.";

                var agents = await OutputStyleUsage.AgentsUsingAsync(db, style.Name);
                var used = agents.Count == 0 ? "no agents" : string.Join(", ", agents);
                return $"Output style '{style.Name}'\nDescription: {style.Description ?? "(none)"}\n"
                     + $"Used by: {used}\n\n{style.Body}";
            }

            case "create":
            {
                var error = OutputStyleValidator.Validate(styleName, body);
                if (error is not null)
                    return $"manage_output_styles: {error}.";

                if (await db.OutputStyles.AnyAsync(s => s.Name == styleName))
                    return $"Output style '{styleName}' already exists — use action 'update' to change it.";

                db.OutputStyles.Add(new OutputStyle
                {
                    Name        = styleName,
                    Body        = body!,
                    Description = OutputStyleRenderer.ReadDescription(body!),
                });
                await db.SaveChangesAsync();

                return $"Created output style '{styleName}'. Assign it with "
                     + $"update_agent_config output_style={styleName}, then reprovision that agent.";
            }

            case "update":
            {
                if (styleName.Length == 0)
                    return "manage_output_styles: 'update' action requires 'name' parameter.";

                var style = await db.OutputStyles.FirstOrDefaultAsync(s => s.Name == styleName);
                if (style is null)
                    return $"Output style '{styleName}' not found — use action 'create' to add it.";

                // The name is not editable here: it is the value agents.OutputStyle holds, so
                // renaming the row orphans every agent pointing at it. Rename is create + reassign
                // + delete.
                var error = OutputStyleValidator.ValidateBody(style.Name, body);
                if (error is not null)
                    return $"manage_output_styles: {error}.";

                style.Body        = body!;
                style.Description = OutputStyleRenderer.ReadDescription(body!);
                await db.SaveChangesAsync();

                var agents = await OutputStyleUsage.AgentsUsingAsync(db, style.Name);
                return agents.Count == 0
                    ? $"Updated output style '{styleName}'. No agent is assigned to it."
                    : $"Updated output style '{styleName}'. The new body reaches an agent at provision "
                      + $"time — reprovision to apply: {string.Join(", ", agents)}.";
            }

            case "delete":
            {
                if (styleName.Length == 0)
                    return "manage_output_styles: 'delete' action requires 'name' parameter.";

                var style = await db.OutputStyles.FirstOrDefaultAsync(s => s.Name == styleName);
                if (style is null)
                    return $"Output style '{styleName}' not found.";

                // Deleting a style an agent still names leaves that agent writing an outputStyle
                // into settings.json that resolves to nothing, while system/init keeps reporting
                // the configured name — a silent degrade. Refuse and name the agents.
                var agents = await OutputStyleUsage.AgentsUsingAsync(db, style.Name);
                if (agents.Count > 0)
                    return $"Refusing to delete output style '{styleName}' — {agents.Count} agent(s) "
                         + $"still assigned: {string.Join(", ", agents)}. Clear the style on those agents first.";

                db.OutputStyles.Remove(style);
                await db.SaveChangesAsync();
                return $"Deleted output style '{styleName}'.";
            }

            default:
                return $"Unknown action '{action}'. Valid actions: list, get, create, update, delete.";
        }
    }
}
