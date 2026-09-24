using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>
/// Admin tools for project cards (#347). Served on the admin <c>/mcp</c> only and never
/// auto-granted. The rules are <see cref="ProjectCardService"/>'s, shared with the REST routes.
/// </summary>
[McpServerToolType]
public sealed class ProjectCardTools(IServiceScopeFactory scopeFactory, ILogger<ProjectCardTools> logger)
{
    [McpServerTool(Name = "get_project_card")]
    [Description("Get a project's card: current content, the full version it was written for, staleness, missing and invalid keep markers, and card version history.")]
    public async Task<string> GetProjectCardAsync(
        [Description("Project name (e.g. 'my-project')")] string name)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var ctx = await ProjectCardService.FindContextAsync(db, name);
        if (ctx is null)
            return $"Project context '{name}' not found.";

        var versions = await db.ProjectContextCardVersions
            .AsNoTracking()
            .Where(v => v.ProjectContextId == ctx.Id)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync();

        var current = ctx.CurrentCardVersion is { } cv ? versions.FirstOrDefault(v => v.VersionNumber == cv) : null;
        if (current is null)
            return $"Project context '{ctx.Name}' has no card (full is v{ctx.CurrentVersion}). Write one with update_project_card.";

        var full = await ProjectCardService.CurrentFullContentAsync(db, ctx);
        var state = ProjectCardService.Evaluate(
            current.VersionNumber, current.BasedOnFullVersion, current.Content, ctx.CurrentVersion, full);

        var sb = new StringBuilder();
        sb.AppendLine($"## Project Card: {ctx.Name}");
        sb.AppendLine($"Card version: {state.CurrentVersion}");
        sb.AppendLine($"Written for full version: {state.BasedOnFullVersion} (full is v{ctx.CurrentVersion}{(state.Stale ? ", stale" : "")})");
        sb.AppendLine($"Missing keeps: {(state.MissingKeeps.Count == 0 ? "(none)" : string.Join(", ", state.MissingKeeps))}");
        sb.AppendLine($"Invalid keeps: {(state.InvalidKeeps.Count == 0 ? "(none)" : string.Join(", ", state.InvalidKeeps.Select(i => $"'{i}'")))}");
        sb.AppendLine();
        sb.AppendLine("### Current content:");
        sb.AppendLine(current.Content);
        sb.AppendLine();
        sb.AppendLine("### Version history:");
        foreach (var v in versions)
        {
            sb.AppendLine($"- v{v.VersionNumber}{(v.VersionNumber == state.CurrentVersion ? " (current)" : "")} for full v{v.BasedOnFullVersion} — {v.CreatedAt:yyyy-MM-dd HH:mm}Z" +
                          (v.CreatedBy is not null ? $" by {v.CreatedBy}" : "") +
                          (v.Reason is not null ? $" — {v.Reason}" : ""));
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "update_project_card")]
    [Description("Write a new version of a project's card — the compact text resident for card-mode assignments. The card must carry every <!-- keep:slug --> marker of the CURRENT full context, and may not contain an invalid keep marker; otherwise nothing is saved. Card-mode agents pick it up on reprovision.")]
    public async Task<string> UpdateProjectCardAsync(
        [Description("Project name (e.g. 'my-project')")] string name,
        [Description("Full card content (markdown)")] string content,
        [Description("The full context version this card was written for, 1..current. No default: state which full version the card reflects.")] int based_on_full_version,
        [Description("Reason for the update")] string reason,
        [Description("Who is making the update (e.g. agent name)")] string created_by)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var result = await ProjectCardService.WriteCardAsync(db, name, content, based_on_full_version, reason, created_by);
        return ToText(result);
    }

    [McpServerTool(Name = "rollback_project_card")]
    [Description("Roll a project's card back to a prior version by saving that version's content and full-version reference as a new version. Exempt from the keep-marker gate (recovery path); provisioning still renders full for an agent whose card lacks a current keep marker.")]
    public async Task<string> RollbackProjectCardAsync(
        [Description("Project name (e.g. 'my-project')")] string name,
        [Description("Card version number to roll back to")] int target_version)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var result = await ProjectCardService.RollbackCardAsync(db, name, target_version, logger);
        return ToText(result);
    }

    private static string ToText(ProjectCardWriteResult result) =>
        result.Saved ? result.Message + ". Reprovision the card-mode agents to apply." : result.Message;
}
