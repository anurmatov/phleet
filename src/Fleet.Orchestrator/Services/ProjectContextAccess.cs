using System.Text;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// <c>get_project_context</c> on <c>/mcp/context</c>: the card agents' fallback read (#347 D7).
/// </summary>
/// <remarks>
/// <para>
/// The ACL is the assignment and nothing else: a project is readable only when <c>agent_projects</c>
/// has a row for (agent, project), in any mode. <c>agent_project_access</c>, its wildcard and manual
/// rows are memory-ACL concepts and are deliberately never read — a second permission source would
/// drift from the assignments.
/// </para>
/// <para>
/// Names are compared in C# with <see cref="StringComparer.OrdinalIgnoreCase"/> on loaded rows, never
/// by database collation, which differs between MySQL and the SQLite test host.
/// </para>
/// <para>
/// Every denial returns the same text, so an unassigned project, a nonexistent one, an unknown
/// agent and a binding mismatch are indistinguishable to the caller — nothing is enumerable. A
/// database failure is NOT a denial: it says so, and logs at <c>Error</c>.
/// </para>
/// </remarks>
public sealed class ProjectContextAccess(
    OrchestratorDbContext db,
    ContextSessionRegistry sessions,
    ILogger<ProjectContextAccess> logger)
{
    public const string UnavailableText = "Project context lookup is temporarily unavailable.";

    public static string DeniedText(string project) => $"Project context '{project}' is not available to this caller.";

    /// <summary>D7: the agent holds an assignment for the project (any mode), ignoring case.</summary>
    public static bool IsAssigned(IEnumerable<string> assignedProjects, string project) =>
        assignedProjects.Any(p => string.Equals(p, project, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The loaded row named <paramref name="name"/>, ignoring case — an exact ordinal match first, so
    /// a (hand-made) case-variant duplicate can never shadow the row the name actually spells.
    /// </summary>
    public static T? MatchByName<T>(IReadOnlyCollection<T> rows, string name, Func<T, string> nameOf) where T : class =>
        rows.FirstOrDefault(r => string.Equals(nameOf(r), name, StringComparison.Ordinal))
        ?? rows.FirstOrDefault(r => string.Equals(nameOf(r), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Serves one fallback read for the request in <paramref name="http"/>, which must be on the
    /// context route. Identity is re-read from THIS request — the path, <c>?agent=</c> and
    /// <c>Mcp-Session-Id</c> — never from the session's origin.
    /// </summary>
    public async Task<string> ReadAsync(HttpContext http, string project, CancellationToken ct = default)
    {
        var agent = ContextMcpRoute.ReadAgent(http.Request);
        if (agent is null)
            return Deny("(invalid)", project, "unknown_agent");

        // Defense in depth behind ContextMcpSessionGuard: a request naming a session must name one
        // bound to this same agent. Without a session id this is the SDK's implicit session, which
        // the guard binds as the response starts.
        var sessionIds = ContextMcpRoute.ReadSessionIds(http.Request);
        if (sessionIds.Count > 0 && !(sessionIds.Count == 1 && string.IsNullOrEmpty(sessionIds[0])))
        {
            var sessionId = sessionIds.Count == 1 ? sessionIds[0]! : sessionIds.ToString();
            if (!sessions.TryGetAgent(sessionId, out var bound) || !string.Equals(bound, agent, StringComparison.Ordinal))
                return Deny(agent, project, "binding_mismatch");
        }

        try
        {
            var agentRow = (await db.Agents.AsNoTracking()
                    .Where(a => a.Name == agent)
                    .Select(a => new { a.Id, a.Name })
                    .ToListAsync(ct))
                .FirstOrDefault(a => string.Equals(a.Name, agent, StringComparison.Ordinal));
            if (agentRow is null)
                return Deny(agent, project, "unknown_agent");

            var assigned = await db.AgentProjects.AsNoTracking()
                .Where(p => p.AgentId == agentRow.Id)
                .Select(p => p.ProjectName)
                .ToListAsync(ct);
            if (!IsAssigned(assigned, project))
                return Deny(agent, project, "not_assigned");

            var contexts = await db.ProjectContexts.AsNoTracking()
                .Select(p => new { p.Id, p.Name, p.CurrentVersion })
                .ToListAsync(ct);
            var ctx = MatchByName(contexts, project, p => p.Name);
            if (ctx is null)
                return Deny(agent, project, "nonexistent");

            var content = await db.ProjectContextVersions.AsNoTracking()
                .Where(v => v.ProjectContextId == ctx.Id && v.VersionNumber == ctx.CurrentVersion)
                .Select(v => v.Content)
                .FirstOrDefaultAsync(ct);
            if (content is null)
                return Deny(agent, project, "nonexistent");

            logger.LogInformation(
                "ProjectContextFallback allowed agent={Agent} project={Project} reason={Reason} version={Version}",
                agent, ctx.Name, "assigned", ctx.CurrentVersion);

            var sb = new StringBuilder();
            sb.AppendLine($"## Project Context: {ctx.Name}");
            sb.AppendLine($"Current version: {ctx.CurrentVersion}");
            sb.AppendLine();
            sb.AppendLine(content);
            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "ProjectContextFallback unavailable agent={Agent} project={Project} reason={Reason} version={Version}",
                agent, project, "db_error", "-");
            return UnavailableText;
        }
    }

    private string Deny(string agent, string project, string reason)
    {
        logger.LogWarning(
            "ProjectContextFallback denied agent={Agent} project={Project} reason={Reason} version={Version}",
            agent, project, reason, "-");
        return DeniedText(project);
    }
}
