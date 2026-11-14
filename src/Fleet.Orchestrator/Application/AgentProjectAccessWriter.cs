using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Application;

/// <summary>An agent's memory project access after a write, sorted.</summary>
public sealed record ProjectAccess(string AgentName, IReadOnlyList<string> Projects, bool Changed);

/// <summary>One access row with where it came from: an operator's grant or a project assignment.</summary>
public sealed record ProjectAccessRow(string Project, string Source);

/// <summary>
/// The one implementation of an operator's direct grant or revocation of memory project access.
/// The <c>/api/agents/{name}/project-access</c> routes and <c>manage_agent_project_access</c> are
/// adapters over it.
/// </summary>
/// <remarks>
/// Every row written here is <see cref="AgentProjectAccessSource.Manual"/>, so the assignment hook
/// (<see cref="AgentProjectAccessSync"/>) never revokes it. fleet-memory is told after the save;
/// when it could not be, the outcome carries <see cref="WriteOutcome{T}.NotDelivered"/> and the
/// previous access applies until it re-reads the list on its own timer.
/// </remarks>
public sealed class AgentProjectAccessWriter(IServiceScopeFactory scopes, ConfigService configService)
{
    /// <summary>The token that grants every project.</summary>
    public const string Wildcard = AgentProjectAccessSync.Wildcard;

    public static string NormalizeAgent(string agentName) => agentName.Trim().ToLowerInvariant();
    public static string NormalizeProject(string? project) => project?.Trim().ToLowerInvariant() ?? "";

    public async Task<IReadOnlyList<ProjectAccessRow>> ListAsync(string agentName, CancellationToken ct = default)
    {
        var agent = NormalizeAgent(agentName);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.AgentProjectAccess
            .Where(x => x.AgentName == agent)
            .OrderBy(x => x.Project)
            .Select(x => new ProjectAccessRow(x.Project, x.Source))
            .ToListAsync(ct);
    }

    /// <summary>Replaces the agent's whole access list with <paramref name="projects"/>.</summary>
    public async Task<WriteOutcome<ProjectAccess>> ReplaceAsync(
        string agentName, IReadOnlyList<string> projects, CancellationToken ct = default)
    {
        if (projects.Any(string.IsNullOrWhiteSpace))
            return WriteFaults.Invalid("project_empty", "project entries must not be empty or whitespace");

        var agent = NormalizeAgent(agentName);
        var normalized = projects.Select(NormalizeProject).Distinct().ToList();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var existing = await db.AgentProjectAccess.Where(x => x.AgentName == agent).ToListAsync(ct);
        db.AgentProjectAccess.RemoveRange(existing);
        // An explicit full list is an operator grant, so every row it writes is manual and the
        // assignment hook will not later revoke it.
        db.AgentProjectAccess.AddRange(normalized.Select(p => new AgentProjectAccess
        {
            AgentName = agent,
            Project = p,
            Source = AgentProjectAccessSource.Manual,
        }));
        await db.SaveChangesAsync(ct);

        return WriteOutcome<ProjectAccess>.Ok(new ProjectAccess(agent, normalized, Changed: true),
            await TellMemoryAsync($"the project access of agent '{agent}'", ct));
    }

    /// <summary>Grants one project (or <see cref="Wildcard"/>). Granting what is already granted changes nothing.</summary>
    public async Task<WriteOutcome<ProjectAccess>> GrantAsync(string agentName, string? project, CancellationToken ct = default)
    {
        var agent = NormalizeAgent(agentName);
        var name = NormalizeProject(project);
        if (name.Length == 0)
            return WriteFaults.Invalid("project_empty", "project must not be empty");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        if (await db.AgentProjectAccess.AnyAsync(x => x.AgentName == agent && x.Project == name, ct))
            return WriteOutcome<ProjectAccess>.Ok(new ProjectAccess(agent, await CurrentAsync(db, agent, ct), Changed: false));

        db.AgentProjectAccess.Add(new AgentProjectAccess
        {
            AgentName = agent,
            Project = name,
            Source = AgentProjectAccessSource.Manual,
        });
        await db.SaveChangesAsync(ct);

        var notDelivered = await TellMemoryAsync(
            name == Wildcard ? $"wildcard access for agent '{agent}'" : $"access to project '{name}' for agent '{agent}'", ct);
        return WriteOutcome<ProjectAccess>.Ok(new ProjectAccess(agent, await CurrentAsync(db, agent, ct), Changed: true), notDelivered);
    }

    /// <summary>Revokes one project (or <see cref="Wildcard"/>). A row that is not there is a not-found fault.</summary>
    public async Task<WriteOutcome<ProjectAccess>> RevokeAsync(string agentName, string? project, CancellationToken ct = default)
    {
        var agent = NormalizeAgent(agentName);
        var name = NormalizeProject(project);
        if (name.Length == 0)
            return WriteFaults.Invalid("project_empty", "project must not be empty");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var row = await db.AgentProjectAccess.FirstOrDefaultAsync(x => x.AgentName == agent && x.Project == name, ct);
        if (row is null)
            return WriteFaults.NotFound("project_access_not_found", $"No project-access entry '{name}' for agent '{agent}'");

        db.AgentProjectAccess.Remove(row);
        await db.SaveChangesAsync(ct);

        var notDelivered = await TellMemoryAsync(
            name == Wildcard ? $"the removal of wildcard access from agent '{agent}'" : $"the removal of project '{name}' from agent '{agent}'", ct);
        return WriteOutcome<ProjectAccess>.Ok(new ProjectAccess(agent, await CurrentAsync(db, agent, ct), Changed: true), notDelivered);
    }

    private async Task<string?> TellMemoryAsync(string change, CancellationToken ct) =>
        await configService.TryPublishAclChangedAsync(ct) ? null : LiveDelivery.AccessNotDelivered(change);

    private static async Task<IReadOnlyList<string>> CurrentAsync(OrchestratorDbContext db, string agent, CancellationToken ct) =>
        await db.AgentProjectAccess.Where(x => x.AgentName == agent).Select(x => x.Project).OrderBy(p => p).ToListAsync(ct);
}
