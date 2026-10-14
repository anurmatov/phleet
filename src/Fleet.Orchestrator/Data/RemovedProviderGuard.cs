using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Data;

/// <summary>Refuse startup while any row still uses the removed provider, including disabled agents.</summary>
public static class RemovedProviderGuard
{
    public static async Task EnsureAsync(OrchestratorDbContext db, CancellationToken ct = default)
    {
        var names = await db.Agents.AsNoTracking()
            .Where(a => a.Provider == "pi").OrderBy(a => a.Name).Select(a => a.Name).ToListAsync(ct);
        if (names.Count != 0)
            throw new InvalidOperationException(
                $"The pi provider was removed; agents still configured for pi: {string.Join(", ", names)}. "
                + "Use the previous release to move every pi agent to claude or codex and reprovision before upgrading.");
    }
}
