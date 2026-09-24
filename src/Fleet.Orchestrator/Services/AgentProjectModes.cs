using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Per-assignment context modes (<see cref="AgentProject.ContextMode"/>), shared by
/// <c>PUT /api/agents/{name}/config</c> and <c>update_agent_config</c> so both surfaces apply the
/// same rules. Project names compare <see cref="StringComparer.OrdinalIgnoreCase"/> in C#, matching
/// the assignment code, never through DB collation.
/// </summary>
public static class AgentProjectModes
{
    /// <summary>
    /// The whole assignment step of a config update, on a tracked <paramref name="agent"/> with its
    /// <c>Projects</c> loaded: <paramref name="projects"/> (when given) replaces the list via
    /// <see cref="Replace"/>, then <paramref name="modes"/> (when given) is validated and applied to
    /// the RESULTING assignments via <see cref="ApplyAsync"/>. Returns an error, or null. Nothing is
    /// saved here: on an error the caller returns without <c>SaveChanges</c>, so nothing persists.
    /// </summary>
    public static async Task<string?> UpdateAssignmentsAsync(
        OrchestratorDbContext db,
        Agent agent,
        IEnumerable<string>? projects,
        IEnumerable<KeyValuePair<string, string>>? modes,
        List<string>? modeChanges = null,
        CancellationToken ct = default)
    {
        if (projects is not null)
        {
            var replaced = Replace(agent.Id, agent.Projects, projects);
            db.AgentProjects.RemoveRange(agent.Projects);
            agent.Projects = replaced;
        }

        if (modes is null)
            return null;

        if (!TryNormalize(modes, out var normalized, out var error))
            return error;

        return await ApplyAsync(db, agent.Projects, normalized, modeChanges, ct);
    }

    /// <summary>
    /// Replace-all for an agent's project list that PRESERVES <c>ContextMode</c> for every name that
    /// remains (case-insensitive match); new names start as <see cref="ProjectContextMode.Full"/>.
    /// Resetting modes here would flip every card assignment back to full whenever one project is
    /// added. The caller removes the old rows and assigns the returned list.
    /// </summary>
    public static List<AgentProject> Replace(int agentId, IEnumerable<AgentProject> current, IEnumerable<string> projectNames)
    {
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in current)
            previous.TryAdd(p.ProjectName, p.ContextMode);

        return projectNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new AgentProject
            {
                AgentId     = agentId,
                ProjectName = name,
                ContextMode = previous.TryGetValue(name, out var mode) && ProjectContextMode.IsValid(mode)
                    ? mode
                    : ProjectContextMode.Full,
            })
            .ToList();
    }

    /// <summary>
    /// Parses the MCP form <c>"p1=card,p2=full"</c>. Blank entries are skipped; every other entry
    /// must be <c>name=mode</c>.
    /// </summary>
    public static bool TryParse(string? spec, out Dictionary<string, string> modes, out string? error)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var entry in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0 || eq == entry.Length - 1)
            {
                modes = new(StringComparer.OrdinalIgnoreCase);
                error = $"project_modes entry '{entry}' must be name=mode (e.g. project-a=card,project-b=full)";
                return false;
            }
            pairs.Add(new(entry[..eq].Trim(), entry[(eq + 1)..].Trim()));
        }
        return TryNormalize(pairs, out modes, out error);
    }

    /// <summary>
    /// Validates raw <c>name → mode</c> pairs: non-blank names, each at most once (case-insensitive),
    /// values <c>full</c> or <c>card</c>. Returns a case-insensitive map of lower-cased modes.
    /// </summary>
    public static bool TryNormalize(
        IEnumerable<KeyValuePair<string, string>>? raw, out Dictionary<string, string> modes, out string? error)
    {
        modes = new(StringComparer.OrdinalIgnoreCase);
        error = null;
        foreach (var (rawName, rawMode) in raw ?? [])
        {
            var name = rawName?.Trim() ?? "";
            if (name.Length == 0)
            {
                error = "project mode keys must be non-blank project names";
                return false;
            }

            var mode = rawMode?.Trim().ToLowerInvariant();
            if (!ProjectContextMode.IsValid(mode))
            {
                error = $"mode for project '{name}' must be '{ProjectContextMode.Full}' or '{ProjectContextMode.Card}', got '{rawMode}'";
                return false;
            }

            if (!modes.TryAdd(name, mode!))
            {
                error = $"project '{name}' is listed more than once in the project modes";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Applies <paramref name="modes"/> to <paramref name="assignments"/> (the agent's RESULTING
    /// assignments). Every key must name one of them; <c>card</c> requires the project to have a
    /// card (<c>CurrentCardVersion</c> not null). Everything is validated before anything is set, and
    /// nothing is saved here — on an error the caller returns without saving. Each effective change is
    /// appended to <paramref name="changes"/> as <c>name: old → new</c>.
    /// </summary>
    public static async Task<string?> ApplyAsync(
        OrchestratorDbContext db,
        IReadOnlyCollection<AgentProject> assignments,
        IReadOnlyDictionary<string, string> modes,
        List<string>? changes = null,
        CancellationToken ct = default)
    {
        if (modes.Count == 0)
            return null;

        var targets = new List<(AgentProject Assignment, string Mode)>();
        foreach (var (name, mode) in modes)
        {
            var assignment = assignments.FirstOrDefault(a => string.Equals(a.ProjectName, name, StringComparison.OrdinalIgnoreCase));
            if (assignment is null)
            {
                var assigned = assignments.Count == 0 ? "(none)" : string.Join(", ", assignments.Select(a => a.ProjectName));
                return $"project mode given for '{name}', which is not assigned to this agent (assigned: {assigned})";
            }
            targets.Add((assignment, mode));
        }

        if (targets.Any(t => t.Mode == ProjectContextMode.Card))
        {
            var contexts = await db.ProjectContexts.AsNoTracking()
                .Select(p => new { p.Name, p.CurrentCardVersion })
                .ToListAsync(ct);

            foreach (var (assignment, _) in targets.Where(t => t.Mode == ProjectContextMode.Card))
            {
                var hasCard = contexts.Any(c =>
                    string.Equals(c.Name, assignment.ProjectName, StringComparison.OrdinalIgnoreCase) &&
                    c.CurrentCardVersion is not null);
                if (!hasCard)
                    return $"project '{assignment.ProjectName}' has no card, so it cannot be set to card mode. " +
                           $"Write one first (update_project_card, or POST /api/project-contexts/{assignment.ProjectName}/card/versions).";
            }
        }

        foreach (var (assignment, mode) in targets)
        {
            if (assignment.ContextMode == mode)
                continue;
            changes?.Add($"{assignment.ProjectName}: {assignment.ContextMode} → {mode}");
            assignment.ContextMode = mode;
        }
        return null;
    }
}
