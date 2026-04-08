using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class RepositoryTools(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "list_repositories")]
    [Description("List managed GitHub repositories. Active only by default; pass includeInactive=true to see all.")]
    public async Task<string> ListRepositoriesAsync(
        [Description("Include inactive (archived) repos. Default: false.")] bool includeInactive = false)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var query = db.Repositories.AsQueryable();
        if (!includeInactive) query = query.Where(r => r.IsActive);

        var repos = await query.OrderBy(r => r.Name).AsNoTracking().ToListAsync();

        if (repos.Count == 0)
            return includeInactive ? "No repositories found." : "No active repositories found.";

        var sb = new StringBuilder();
        sb.AppendLine("| Name | FullName | Active |");
        sb.AppendLine("|------|----------|--------|");
        foreach (var r in repos)
            sb.AppendLine($"| {r.Name} | {r.FullName} | {r.IsActive} |");
        return sb.ToString();
    }

    [McpServerTool(Name = "manage_repository")]
    [Description("Add, update, or remove a managed GitHub repository. action: add | update | remove")]
    public async Task<string> ManageRepositoryAsync(
        [Description("Action to perform: add | update | remove")] string action,
        [Description("Short name (e.g. 'fleet')")] string name,
        [Description("Full GitHub org/repo string (e.g. 'your-org/your-repo'). Required for add; optional for update.")] string? full_name = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        switch (action.ToLowerInvariant())
        {
            case "add":
                if (string.IsNullOrWhiteSpace(full_name))
                    return "full_name is required for add action.";
                var existing = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
                if (existing is not null)
                    return $"Repository '{name}' already exists (FullName: {existing.FullName}). Use action=update to change it.";
                db.Repositories.Add(new Repository
                {
                    Name = name.Trim(),
                    FullName = full_name.Trim(),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
                return $"Added repository '{name}' → {full_name}.";

            case "update":
                var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
                if (repo is null) return $"Repository '{name}' not found.";
                if (!string.IsNullOrWhiteSpace(full_name)) repo.FullName = full_name.Trim();
                repo.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return $"Updated repository '{name}' → {repo.FullName}.";

            case "remove":
                var toRemove = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
                if (toRemove is null) return $"Repository '{name}' not found.";
                db.Repositories.Remove(toRemove);
                await db.SaveChangesAsync();
                return $"Removed repository '{name}'.";

            default:
                return $"Unknown action '{action}'. Use: add | update | remove";
        }
    }
}
