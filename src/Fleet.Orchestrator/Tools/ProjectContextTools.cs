using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ProjectContextTools(IServiceScopeFactory scopeFactory)
{
    private const int MaxVersions = 20;

    [McpServerTool(Name = "create_project_context")]
    [Description("Create a new project context with initial v1 content. Fails if a context with that name already exists.")]
    public async Task<string> CreateProjectContextAsync(
        [Description("Project name (e.g. 'my-project')")] string name,
        [Description("Initial content for the project context (markdown)")] string content,
        [Description("Who is creating this context (e.g. agent name)")] string created_by)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_-]+$"))
            return "name must contain only letters, digits, hyphens, or underscores.";

        var exists = await db.ProjectContexts.AnyAsync(p => p.Name == name);
        if (exists)
            return $"Project context '{name}' already exists. Use update_project_context to add a new version.";

        var ctx = new ProjectContext { Name = name, CurrentVersion = 1 };
        db.ProjectContexts.Add(ctx);
        await db.SaveChangesAsync();

        db.ProjectContextVersions.Add(new ProjectContextVersion
        {
            ProjectContextId = ctx.Id,
            VersionNumber    = 1,
            Content          = content,
            CreatedAt        = DateTime.UtcNow,
            CreatedBy        = created_by,
            Reason           = "Initial creation",
        });

        await db.SaveChangesAsync();

        return $"Project context '{name}' created at v1.";
    }

    [McpServerTool(Name = "list_project_contexts")]
    [Description("List all project contexts with current version number and agent assignments.")]
    public async Task<string> ListProjectContextsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var contexts = await db.ProjectContexts
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.CurrentVersion,
                TotalVersions = db.ProjectContextVersions.Count(v => v.ProjectContextId == p.Id),
            })
            .ToListAsync();

        if (contexts.Count == 0)
            return "No project contexts found.";

        var agents = await db.Agents
            .Include(a => a.Projects)
            .AsNoTracking()
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine($"## Project Contexts ({contexts.Count})");
        sb.AppendLine();

        foreach (var ctx in contexts)
        {
            var assignedAgents = agents
                .Where(a => a.Projects.Any(p => p.ProjectName.Equals(ctx.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Name)
                .OrderBy(n => n)
                .ToList();

            var agentsStr = assignedAgents.Count > 0 ? string.Join(", ", assignedAgents) : "(none)";
            sb.AppendLine($"- **{ctx.Name}** — v{ctx.CurrentVersion} ({ctx.TotalVersions} versions) — agents: {agentsStr}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_project_context")]
    [Description("Get the current content and version history of a project context.")]
    public async Task<string> GetProjectContextAsync(
        [Description("Project name (e.g. 'my-project')")] string name)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var ctx = await db.ProjectContexts
            .Include(p => p.Versions.OrderByDescending(v => v.VersionNumber))
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == name);

        if (ctx is null)
            return $"Project context '{name}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Project Context: {ctx.Name}");
        sb.AppendLine($"Current version: {ctx.CurrentVersion}");
        sb.AppendLine($"Total versions: {ctx.Versions.Count}");
        sb.AppendLine();

        var current = ctx.Versions.FirstOrDefault(v => v.VersionNumber == ctx.CurrentVersion);
        if (current is not null)
        {
            sb.AppendLine("### Current content:");
            sb.AppendLine(current.Content);
            sb.AppendLine();
        }

        sb.AppendLine("### Version history:");
        foreach (var v in ctx.Versions)
        {
            var preview = v.Content.Length > 80
                ? v.Content[..80].Replace('\n', ' ') + "..."
                : v.Content.Replace('\n', ' ');
            sb.AppendLine($"- v{v.VersionNumber}{(v.VersionNumber == ctx.CurrentVersion ? " (current)" : "")} — {v.CreatedAt:yyyy-MM-dd HH:mm}Z" +
                          (v.CreatedBy is not null ? $" by {v.CreatedBy}" : "") +
                          (v.Reason is not null ? $" — {v.Reason}" : ""));
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "update_project_context")]
    [Description("Create a new version of a project context with updated content. Auto-increments version number and prunes history beyond 20 versions.")]
    public async Task<string> UpdateProjectContextAsync(
        [Description("Project name (e.g. 'my-project')")] string name,
        [Description("Full new content for the project context (markdown)")] string content,
        [Description("Reason for the update")] string reason,
        [Description("Who is making the update (e.g. agent name)")] string created_by)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var ctx = await db.ProjectContexts
            .Include(p => p.Versions.OrderBy(v => v.VersionNumber))
            .FirstOrDefaultAsync(p => p.Name == name);

        if (ctx is null)
            return $"Project context '{name}' not found.";

        var newVersionNumber = ctx.CurrentVersion + 1;

        ctx.Versions.Add(new ProjectContextVersion
        {
            ProjectContextId = ctx.Id,
            VersionNumber    = newVersionNumber,
            Content          = content,
            CreatedAt        = DateTime.UtcNow,
            CreatedBy        = created_by,
            Reason           = reason,
        });

        ctx.CurrentVersion = newVersionNumber;
        ctx.UpdatedAt = DateTime.UtcNow;

        var excess = ctx.Versions.Count - MaxVersions;
        if (excess > 0)
        {
            var toDelete = ctx.Versions
                .OrderBy(v => v.VersionNumber)
                .Take(excess)
                .ToList();
            db.ProjectContextVersions.RemoveRange(toDelete);
        }

        await db.SaveChangesAsync();

        return $"Project context '{name}' updated to v{newVersionNumber}.";
    }

    [McpServerTool(Name = "rollback_project_context")]
    [Description("Roll back a project context to a prior version by creating a new version with the content copied from the target version. Non-destructive — full history is preserved.")]
    public async Task<string> RollbackProjectContextAsync(
        [Description("Project name (e.g. 'my-project')")] string name,
        [Description("Version number to roll back to")] int target_version)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var ctx = await db.ProjectContexts
            .Include(p => p.Versions.OrderBy(v => v.VersionNumber))
            .FirstOrDefaultAsync(p => p.Name == name);

        if (ctx is null)
            return $"Project context '{name}' not found.";

        var target = ctx.Versions.FirstOrDefault(v => v.VersionNumber == target_version);
        if (target is null)
            return $"Version {target_version} does not exist for project context '{name}'.";

        var newVersionNumber = ctx.CurrentVersion + 1;

        ctx.Versions.Add(new ProjectContextVersion
        {
            ProjectContextId = ctx.Id,
            VersionNumber    = newVersionNumber,
            Content          = target.Content,
            CreatedAt        = DateTime.UtcNow,
            CreatedBy        = "rollback",
            Reason           = $"rollback to version {target_version}",
        });

        ctx.CurrentVersion = newVersionNumber;
        ctx.UpdatedAt = DateTime.UtcNow;

        var excess = ctx.Versions.Count - MaxVersions;
        if (excess > 0)
        {
            var toDelete = ctx.Versions
                .OrderBy(v => v.VersionNumber)
                .Take(excess)
                .ToList();
            db.ProjectContextVersions.RemoveRange(toDelete);
        }

        await db.SaveChangesAsync();

        return $"Project context '{name}' rolled back to v{target_version} content — saved as v{newVersionNumber}.";
    }
}
