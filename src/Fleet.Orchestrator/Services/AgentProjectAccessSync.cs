using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Keeps <c>agent_project_access</c> in step with an agent's project assignments.
///
/// Assigning a project to an agent is what grants it memory read access; nothing else has to be
/// remembered. The provenance column is what makes the reverse direction safe: this hook writes
/// and removes <see cref="AgentProjectAccessSource.Assignment"/> rows only, so an
/// <see cref="AgentProjectAccessSource.Manual"/> grant an operator added by hand survives an
/// unassignment. A wildcard <c>*</c> row is never touched in either direction — it is an operator
/// grant with no assignment behind it.
/// </summary>
public static class AgentProjectAccessSync
{
    /// <summary>The wildcard project token, which this hook never creates or removes.</summary>
    public const string Wildcard = "*";

    /// <summary>
    /// Stages the add/remove set needed to make the agent's <c>assignment</c>-sourced rows match
    /// <paramref name="assignedProjects"/>. Does NOT save — the caller owns the transaction.
    /// Returns true when anything was staged, which is also the signal that peers need telling.
    /// </summary>
    public static async Task<bool> StageAssignmentsAsync(
        OrchestratorDbContext db,
        string agentName,
        IEnumerable<string> assignedProjects,
        CancellationToken ct = default)
    {
        var name = Normalize(agentName);
        if (string.IsNullOrEmpty(name)) return false;

        // The wildcard is never assignment-derived, so an assignment literally named "*" is
        // dropped rather than allowed to manufacture a cross-project grant.
        var desired = new HashSet<string>(
            assignedProjects
                .Select(Normalize)
                .Where(p => p.Length > 0 && p != Wildcard),
            StringComparer.Ordinal);

        var existing = await db.AgentProjectAccess
            .Where(x => x.AgentName == name)
            .ToListAsync(ct);

        var changed = false;

        // Grant: only for projects with no row at all. An existing manual row is left as manual so
        // a later unassignment cannot remove it; an existing assignment row is already correct.
        var haveRows = new HashSet<string>(existing.Select(x => x.Project), StringComparer.Ordinal);
        foreach (var project in desired)
        {
            if (haveRows.Contains(project)) continue;
            db.AgentProjectAccess.Add(new AgentProjectAccess
            {
                AgentName = name,
                Project = project,
                Source = AgentProjectAccessSource.Assignment,
            });
            changed = true;
        }

        // Revoke: assignment-sourced rows whose project is no longer assigned. Manual rows and the
        // wildcard are deliberately excluded.
        foreach (var row in existing)
        {
            if (row.Project == Wildcard) continue;
            if (!string.Equals(row.Source, AgentProjectAccessSource.Assignment, StringComparison.Ordinal)) continue;
            if (desired.Contains(row.Project)) continue;
            db.AgentProjectAccess.Remove(row);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies <see cref="StageAssignmentsAsync"/>, saves, and — only when something actually
    /// changed — broadcasts <c>config.changed</c> so fleet-memory refreshes its ACL cache now
    /// rather than on its five-minute timer. Without the broadcast a freshly assigned agent reads
    /// 403 for up to five minutes, which is indistinguishable at the agent from a missing row.
    /// </summary>
    public static async Task<bool> SyncAndBroadcastAsync(
        OrchestratorDbContext db,
        IAclChangeNotifier notifier,
        string agentName,
        IEnumerable<string> assignedProjects,
        CancellationToken ct = default)
    {
        if (!await StageAssignmentsAsync(db, agentName, assignedProjects, ct))
            return false;

        await db.SaveChangesAsync(ct);
        await notifier.PublishAclChangedAsync(ct);
        return true;
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";
}
