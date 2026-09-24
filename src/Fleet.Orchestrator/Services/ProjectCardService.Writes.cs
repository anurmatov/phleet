using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>How a card or route operation ended. REST maps it to a status code, MCP to text.</summary>
public enum ProjectCardWriteStatus
{
    Saved,
    NotFound,
    Rejected,
    Conflict,
}

/// <summary>
/// Result of a card or route operation. <paramref name="MissingKeeps"/> / <paramref name="InvalidKeeps"/>
/// are set only on the gate rejections that name them; <paramref name="Route"/> only on a route add.
/// </summary>
public sealed record ProjectCardWriteResult(
    ProjectCardWriteStatus Status,
    string Message,
    int? Version = null,
    IReadOnlyList<string>? MissingKeeps = null,
    IReadOnlyList<string>? InvalidKeeps = null,
    ProjectContextRoute? Route = null)
{
    public bool Saved => Status == ProjectCardWriteStatus.Saved;
}

// DB operations shared by the REST handlers and the MCP tools, so the two surfaces cannot drift on
// a rule. Every write ends in ONE SaveChangesAsync, and every rejection returns before it, so a
// rejected request persists nothing.
public static partial class ProjectCardService
{
    /// <summary>
    /// Resolves a project context by name in C# over the loaded rows — an ordinal match first, then
    /// a unique <see cref="StringComparer.OrdinalIgnoreCase"/> match — never through DB collation,
    /// which differs between MySQL and the SQLite test host. Returns the tracked row, or null.
    /// </summary>
    public static async Task<ProjectContext?> FindContextAsync(
        OrchestratorDbContext db, string? name, CancellationToken ct = default)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return null;

        var rows = await db.ProjectContexts.AsNoTracking()
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var id = rows.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.Ordinal))?.Id;
        if (id is null)
        {
            var matches = rows.Where(r => string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
                id = matches[0].Id;
        }

        return id is null ? null : await db.ProjectContexts.FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>Content of the context's current full version, or empty when the row is missing.</summary>
    public static async Task<string> CurrentFullContentAsync(
        OrchestratorDbContext db, ProjectContext ctx, CancellationToken ct = default) =>
        await db.ProjectContextVersions.AsNoTracking()
            .Where(v => v.ProjectContextId == ctx.Id && v.VersionNumber == ctx.CurrentVersion)
            .Select(v => v.Content)
            .FirstOrDefaultAsync(ct) ?? "";

    /// <summary>
    /// State of the context's current card against <paramref name="fullContent"/> (the current full
    /// content), or null when the project has no card.
    /// </summary>
    public static async Task<ProjectCardState?> EvaluateCurrentAsync(
        OrchestratorDbContext db, ProjectContext ctx, string fullContent, CancellationToken ct = default)
    {
        if (ctx.CurrentCardVersion is not { } cardVersion)
            return null;

        var card = await db.ProjectContextCardVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.ProjectContextId == ctx.Id && v.VersionNumber == cardVersion, ct);

        return card is null
            ? null
            : Evaluate(card.VersionNumber, card.BasedOnFullVersion, card.Content, ctx.CurrentVersion, fullContent);
    }

    /// <summary>One line for an MCP result describing the card after a full write, e.g. <c>Card: v2 for full v1 — stale (full is v2)</c>.</summary>
    public static string DescribeCardLine(ProjectCardState state, int currentFullVersion)
    {
        var line = $"Card: v{state.CurrentVersion} for full v{state.BasedOnFullVersion}";
        line += state.Stale ? $" — stale (full is v{currentFullVersion})" : " — current";
        if (state.MissingKeeps.Count > 0)
            line += $" — missing keeps: {string.Join(", ", state.MissingKeeps)} (card-mode agents will fall back to full at the next provision)";
        return line;
    }

    /// <summary>
    /// Writes a new card version. Rejects (nothing saved) a blank card, a missing or out-of-range
    /// <paramref name="basedOnFullVersion"/>, any invalid keep candidate in the card, and a card
    /// that drops a valid keep marker of the CURRENT full content (the preservation gate).
    /// </summary>
    public static async Task<ProjectCardWriteResult> WriteCardAsync(
        OrchestratorDbContext db,
        string? name,
        string? content,
        int? basedOnFullVersion,
        string? reason,
        string? createdBy,
        CancellationToken ct = default)
    {
        var ctx = await FindContextAsync(db, name, ct);
        if (ctx is null)
            return new(ProjectCardWriteStatus.NotFound, $"Project context '{name}' not found");

        if (string.IsNullOrWhiteSpace(content))
            return new(ProjectCardWriteStatus.Rejected, "content is required and must not be blank");

        // No default: the author has to say which full version the card reflects, or staleness
        // would be computed from a number nobody chose.
        if (basedOnFullVersion is not { } basedOn)
            return new(ProjectCardWriteStatus.Rejected,
                $"basedOnFullVersion is required: the full context version (1..{ctx.CurrentVersion}) this card was written for");
        if (basedOn < 1 || basedOn > ctx.CurrentVersion)
            return new(ProjectCardWriteStatus.Rejected,
                $"basedOnFullVersion must be between 1 and {ctx.CurrentVersion} (the current full version of '{ctx.Name}'), got {basedOn}");

        var scan = KeepMarkerParser.Parse(content);
        if (scan.HasInvalid)
            return new(ProjectCardWriteStatus.Rejected,
                $"Card rejected, {KeepMarkerParser.DescribeInvalid(scan.Invalid)} Nothing was saved.",
                InvalidKeeps: scan.Invalid);

        var full = await CurrentFullContentAsync(db, ctx, ct);
        var missing = KeepMarkerParser.Missing(full, content);
        if (missing.Count > 0)
            return new(ProjectCardWriteStatus.Rejected,
                $"Card rejected: it is missing keep marker(s) that the current full context of '{ctx.Name}' " +
                $"(v{ctx.CurrentVersion}) carries: {string.Join(", ", missing)}. " +
                "Add each one to the card as <!-- keep:slug -->. Nothing was saved.",
                MissingKeeps: missing);

        var version = await AppendCardVersionAsync(db, ctx, content, basedOn, createdBy, reason, ct);
        var stale = IsStale(basedOn, ctx.CurrentVersion) ? $" — stale, full is v{ctx.CurrentVersion}" : "";
        return new(ProjectCardWriteStatus.Saved,
            $"Card for '{ctx.Name}' saved as v{version} (written for full v{basedOn}{stale})", version);
    }

    /// <summary>
    /// Rolls the card back by copying <c>Content</c> and <c>BasedOnFullVersion</c> of
    /// <paramref name="targetVersion"/> into a new version. Exempt from the preservation gate and
    /// the invalid-marker rule — it is the recovery path; provisioning still refuses to render a
    /// card that lacks a current keep marker. Invalid candidates are logged as a Warning.
    /// </summary>
    public static async Task<ProjectCardWriteResult> RollbackCardAsync(
        OrchestratorDbContext db, string? name, int targetVersion, ILogger logger, CancellationToken ct = default)
    {
        var ctx = await FindContextAsync(db, name, ct);
        if (ctx is null)
            return new(ProjectCardWriteStatus.NotFound, $"Project context '{name}' not found");

        var target = await db.ProjectContextCardVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.ProjectContextId == ctx.Id && v.VersionNumber == targetVersion, ct);
        if (target is null)
            return new(ProjectCardWriteStatus.NotFound, $"Card version {targetVersion} not found for project context '{ctx.Name}'");

        var version = await AppendCardVersionAsync(
            db, ctx, target.Content, target.BasedOnFullVersion, "rollback", $"rollback to card v{targetVersion}", ct);

        var message = $"Card for '{ctx.Name}' rolled back to v{targetVersion} content — saved as v{version} (written for full v{target.BasedOnFullVersion})";
        var invalid = KeepMarkerParser.Parse(target.Content).Invalid;
        if (invalid.Count > 0)
        {
            logger.LogWarning(
                "Card rollback for {Project} to v{Target} restored invalid keep marker candidate(s), ignored for missingKeeps: {Invalid}",
                ctx.Name, targetVersion, string.Join(" | ", invalid));
            message += $". Warning: the restored card has invalid keep marker(s), which are ignored: {string.Join(", ", invalid.Select(i => $"'{i}'"))}";
        }

        return new(ProjectCardWriteStatus.Saved, message, version);
    }

    /// <summary>Logs a Warning naming invalid keep candidates restored by a FULL context rollback.</summary>
    public static void WarnOnInvalidFullRollback(ILogger logger, string project, int targetVersion, string content)
    {
        var invalid = KeepMarkerParser.Parse(content).Invalid;
        if (invalid.Count > 0)
            logger.LogWarning(
                "Project context rollback for {Project} to v{Target} restored invalid keep marker candidate(s), ignored for missingKeeps: {Invalid}",
                project, targetVersion, string.Join(" | ", invalid));
    }

    private static async Task<int> AppendCardVersionAsync(
        OrchestratorDbContext db,
        ProjectContext ctx,
        string content,
        int basedOnFullVersion,
        string? createdBy,
        string? reason,
        CancellationToken ct)
    {
        var existing = await db.ProjectContextCardVersions
            .Where(v => v.ProjectContextId == ctx.Id)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(ct);

        var next = Math.Max(existing.Count == 0 ? 0 : existing[^1].VersionNumber, ctx.CurrentCardVersion ?? 0) + 1;

        db.ProjectContextCardVersions.Add(new ProjectContextCardVersion
        {
            ProjectContextId   = ctx.Id,
            VersionNumber      = next,
            Content            = content,
            BasedOnFullVersion = basedOnFullVersion,
            CreatedAt          = DateTime.UtcNow,
            CreatedBy          = createdBy,
            Reason             = reason,
        });
        ctx.CurrentCardVersion = next;

        // Oldest first, like full versions. The new row is not in `existing`, hence the + 1.
        var excess = existing.Count + 1 - MaxCardVersions;
        if (excess > 0)
            db.ProjectContextCardVersions.RemoveRange(existing.Take(excess));

        await db.SaveChangesAsync(ct);
        return next;
    }

    // ── Routes ────────────────────────────────────────────────────────────────

    /// <summary>The context's routes, ordered by kind then value (ordinal, in C#).</summary>
    public static async Task<List<ProjectContextRoute>> ListRoutesAsync(
        OrchestratorDbContext db, int projectContextId, CancellationToken ct = default)
    {
        var routes = await db.ProjectContextRoutes.AsNoTracking()
            .Where(r => r.ProjectContextId == projectContextId)
            .ToListAsync(ct);
        return routes
            .OrderBy(r => r.SignalKind, StringComparer.Ordinal)
            .ThenBy(r => r.SignalValue, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// Adds a route to the named context. The value is stored in the form
    /// <see cref="RouteSignalKind.Normalize"/> returns; the same (kind, value) on the same context
    /// is a <see cref="ProjectCardWriteStatus.Conflict"/>.
    /// </summary>
    public static async Task<ProjectCardWriteResult> AddRouteAsync(
        OrchestratorDbContext db, string? name, string? kind, string? value, string? createdBy, CancellationToken ct = default)
    {
        var ctx = await FindContextAsync(db, name, ct);
        if (ctx is null)
            return new(ProjectCardWriteStatus.NotFound, $"Project context '{name}' not found");

        var signalKind = kind?.Trim().ToLowerInvariant();
        var stored = RouteSignalKind.Normalize(signalKind, value, out var error);
        if (stored is null)
            return new(ProjectCardWriteStatus.Rejected, error ?? "invalid route");

        // Compared in C#: workflow values are exact ordinal, which DB collation may not honour.
        var existing = await ListRoutesAsync(db, ctx.Id, ct);
        if (existing.Any(r => r.SignalKind == signalKind && string.Equals(r.SignalValue, stored, StringComparison.Ordinal)))
            return Duplicate(ctx.Name, signalKind!, stored);

        var route = new ProjectContextRoute
        {
            ProjectContextId = ctx.Id,
            SignalKind       = signalKind!,
            SignalValue      = stored,
            CreatedAt        = DateTime.UtcNow,
            CreatedBy        = createdBy,
        };
        db.ProjectContextRoutes.Add(route);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent add of the same route, or a case-insensitive collation treating two
            // workflow values as one, trips the unique index. Re-queried rather than matched on a
            // provider error code, so a write that failed for any other reason is rethrown.
            db.ChangeTracker.Clear();
            var now = await ListRoutesAsync(db, ctx.Id, ct);
            if (!now.Any(r => r.SignalKind == signalKind && string.Equals(r.SignalValue, stored, StringComparison.OrdinalIgnoreCase)))
                throw;
            return Duplicate(ctx.Name, signalKind!, stored);
        }

        return new(ProjectCardWriteStatus.Saved, $"Route {signalKind}={stored} added to '{ctx.Name}' (id {route.Id})", Route: route);
    }

    /// <summary>
    /// Removes route <paramref name="routeId"/> from the named context. A route that belongs to a
    /// different context is <see cref="ProjectCardWriteStatus.NotFound"/>, never removed.
    /// </summary>
    public static async Task<ProjectCardWriteResult> RemoveRouteAsync(
        OrchestratorDbContext db, string? name, int routeId, CancellationToken ct = default)
    {
        var ctx = await FindContextAsync(db, name, ct);
        if (ctx is null)
            return new(ProjectCardWriteStatus.NotFound, $"Project context '{name}' not found");

        var route = await db.ProjectContextRoutes
            .FirstOrDefaultAsync(r => r.Id == routeId && r.ProjectContextId == ctx.Id, ct);
        if (route is null)
            return new(ProjectCardWriteStatus.NotFound, $"Route {routeId} not found on project context '{ctx.Name}'");

        db.ProjectContextRoutes.Remove(route);
        await db.SaveChangesAsync(ct);
        return new(ProjectCardWriteStatus.Saved, $"Route {route.SignalKind}={route.SignalValue} (id {routeId}) removed from '{ctx.Name}'");
    }

    private static ProjectCardWriteResult Duplicate(string project, string kind, string value) =>
        new(ProjectCardWriteStatus.Conflict, $"Route {kind}={value} already exists on project context '{project}'");
}
