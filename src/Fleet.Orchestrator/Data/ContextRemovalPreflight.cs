using System.Data.Common;
using System.Globalization;
using System.Text;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Data;

/// <summary>
/// Thrown by <see cref="ContextRemovalPreflight"/> when dropping the card schema would leave a project
/// without canonical text. Nothing has been migrated when it is thrown.
/// </summary>
public sealed class ContextRemovalBlockedException(string message) : Exception(message);

/// <summary>
/// The gate in front of <c>RemoveProjectContextCards</c> (#346), called by <see cref="DbSeeder"/> in
/// place of a bare <c>MigrateAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// It runs only when <c>AddProjectContextCards</c> is applied and <c>RemoveProjectContextCards</c> is
/// pending. A fresh database, or one already past the removal, migrates exactly as before.
/// </para>
/// <para>
/// It reads the card schema with raw read-only SQL, because those tables and columns are no longer in
/// the EF model, and loads contexts and versions through EF. It writes nothing. It blocks when a project
/// that has a card or a card assignment has no context row, no version row, or whitespace-only canonical
/// content: then no migration is applied and <see cref="ContextRemovalBlockedException"/> is thrown, so
/// the orchestrator exits before it can serve or reprovision anything. The canonical content is the
/// <c>CurrentVersion</c> row, else the latest — the rule provisioning uses.
/// </para>
/// <para>
/// The project-context soft limit only labels the report. It never blocks.
/// </para>
/// </remarks>
public static class ContextRemovalPreflight
{
    public const string CardsMigration = "20260924165651_AddProjectContextCards";
    public const string RemovalMigration = "20260925105427_RemoveProjectContextCards";

    /// <summary>The reserved #347 fallback server name and its one grant, deleted by the migration.</summary>
    public const string FleetContextServer = "fleet-context";
    public const string FleetContextGrant = "mcp__fleet-context__get_project_context";
    public const string FleetContextToolPrefix = "mcp__fleet-context__";

    public const string ReasonNoContext = "no project context";
    public const string ReasonNoVersions = "no versions";
    public const string ReasonEmptyContent = "empty canonical content";

    public const string NotAppliedMessage =
        "RemoveProjectContextCards not applied. The orchestrator will not start. " +
        "Redeploy the previous orchestrator image, fix the listed contexts, then deploy again.";

    /// <summary>
    /// Migrates the database, running the preflight first when <c>RemoveProjectContextCards</c> is the
    /// next step past the card schema.
    /// </summary>
    /// <exception cref="ContextRemovalBlockedException">The preflight blocked; nothing was migrated.</exception>
    public static async Task MigrateAsync(
        OrchestratorDbContext db, ILogger? logger, int projectContextLimitBytes, CancellationToken ct = default)
    {
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        var pending = await db.Database.GetPendingMigrationsAsync(ct);
        if (!applied.Contains(CardsMigration, StringComparer.Ordinal) || !pending.Contains(RemovalMigration, StringComparer.Ordinal))
        {
            await db.Database.MigrateAsync(ct);
            return;
        }

        var report = await ReadAsync(db, ct);

        if (report.Violations.Count > 0)
        {
            foreach (var v in report.Violations)
            {
                logger?.LogCritical("Context removal blocked: project={Project} agents={Agents} reason={Reason}",
                    v.Project, v.Agents.Count == 0 ? "none" : string.Join(",", v.Agents), v.Reason);
            }
            logger?.LogCritical("{Message}", NotAppliedMessage);
            throw new ContextRemovalBlockedException(
                $"Context removal preflight blocked on {report.Violations.Count} project(s) without canonical content " +
                $"({string.Join(", ", report.Violations.Select(v => v.Project))}). {NotAppliedMessage}");
        }

        logger?.LogInformation(
            "Context removal preflight: cardAssignments={CardAssignments} projectsWithCards={ProjectsWithCards} routes={Routes} " +
            "fleetContextEndpointRows={EndpointRows} fleetContextToolRows={ToolRows} result=pass",
            report.Assignments.Count, report.ProjectsWithCards, report.Routes, report.EndpointRows, report.ToolRows);

        foreach (var a in report.Assignments)
        {
            var delta = a.FullBytes - (a.CardBytes ?? 0);
            logger?.LogInformation(
                "Context removal preflight: agent={Agent} project={Project} fullVersion={FullVersion} fullBytes={FullBytes} " +
                "cardBytes={CardBytes} residentDelta={ResidentDelta} overProjectContextLimit={OverLimit}",
                a.Agent, a.Project, a.FullVersion,
                PromptSizePolicy.Format(a.FullBytes),
                a.CardBytes is { } cardBytes ? PromptSizePolicy.Format(cardBytes) : "none",
                (delta >= 0 ? "+" : "") + PromptSizePolicy.Format(delta),
                projectContextLimitBytes > 0 && a.FullBytes > projectContextLimitBytes ? "yes" : "no");
        }

        if (report.Mentions.Count > 0)
        {
            logger?.LogWarning(
                "Context removal preflight: still mention {Prefix} (remove the reference; the tool is gone): {Names}",
                FleetContextToolPrefix, string.Join(", ", report.Mentions));
        }

        await db.Database.MigrateAsync(ct);
    }

    internal sealed record Violation(string Project, IReadOnlyList<string> Agents, string Reason);

    internal sealed record CardAssignment(
        string Agent, string Project, int FullVersion, int FullBytes, int? CardBytes);

    internal sealed record Report(
        IReadOnlyList<CardAssignment> Assignments,
        int ProjectsWithCards,
        long Routes,
        int EndpointRows,
        int ToolRows,
        IReadOnlyList<string> Mentions,
        IReadOnlyList<Violation> Violations);

    private static async Task<Report> ReadAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        var cardAssignments = (await QueryAsync(db, """
                SELECT a.Name, ap.ProjectName FROM agent_projects ap JOIN agents a ON a.Id = ap.AgentId
                WHERE ap.ContextMode = 'card' ORDER BY a.Name, ap.ProjectName
                """, ct))
            .Select(r => (Agent: (string)r[0]!, Project: (string)r[1]!))
            .ToList();

        // Every project that has a card, with the byte length of its current card (null when
        // CurrentCardVersion points at no row). LENGTH counts bytes.
        var cards = (await QueryAsync(db, """
                SELECT pc.Name, LENGTH(cur.Content) FROM project_contexts pc
                LEFT JOIN project_context_card_versions cur
                  ON cur.ProjectContextId = pc.Id AND cur.VersionNumber = pc.CurrentCardVersion
                WHERE EXISTS (SELECT 1 FROM project_context_card_versions cv WHERE cv.ProjectContextId = pc.Id)
                """, ct))
            .Select(r => (Project: (string)r[0]!, Bytes: r[1] is null ? (int?)null : Convert.ToInt32(r[1], CultureInfo.InvariantCulture)))
            .ToList();

        var routes = Convert.ToInt64((await QueryAsync(db, "SELECT COUNT(*) FROM project_context_routes", ct))[0][0],
            CultureInfo.InvariantCulture);

        var endpointRows = await db.AgentMcpEndpoints.AsNoTracking().CountAsync(e => e.McpName == FleetContextServer, ct);
        var toolRows = await db.AgentTools.AsNoTracking().CountAsync(t => t.ToolName == FleetContextGrant, ct);

        var contexts = await db.ProjectContexts
            .Include(p => p.Versions)
            .AsNoTracking()
            .ToListAsync(ct);

        // Group by the context each name resolves to (or the name itself when none does), matching
        // names OrdinalIgnoreCase as provisioning does.
        var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var projects = new List<(string Key, ProjectContext? Context)>();
        foreach (var name in cards.Select(c => c.Project).Concat(cardAssignments.Select(a => a.Project)))
        {
            if (keyOf.ContainsKey(name)) continue;
            var ctx = ProjectNameMatch.MatchByName(contexts, name, c => c.Name);
            var key = ctx?.Name
                ?? projects.FirstOrDefault(p => p.Context is null && string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key
                ?? name;
            keyOf[name] = key;
            if (!projects.Any(p => string.Equals(p.Key, key, StringComparison.Ordinal)))
                projects.Add((key, ctx));
        }

        var violations = new List<Violation>();
        var canonical = new Dictionary<string, ProjectContextVersion>(StringComparer.Ordinal);
        foreach (var (key, ctx) in projects)
        {
            var agents = cardAssignments
                .Where(a => keyOf[a.Project] == key)
                .Select(a => a.Agent)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();

            var version = ctx?.Versions.FirstOrDefault(v => v.VersionNumber == ctx.CurrentVersion)
                       ?? ctx?.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();

            if (ctx is null) violations.Add(new Violation(key, agents, ReasonNoContext));
            else if (version is null) violations.Add(new Violation(key, agents, ReasonNoVersions));
            else if (string.IsNullOrWhiteSpace(version.Content)) violations.Add(new Violation(key, agents, ReasonEmptyContent));
            else canonical[key] = version;
        }

        var assignments = new List<CardAssignment>();
        foreach (var a in cardAssignments)
        {
            var key = keyOf[a.Project];
            if (!canonical.TryGetValue(key, out var version)) continue;
            var card = cards.FirstOrDefault(c => keyOf[c.Project] == key);
            assignments.Add(new CardAssignment(
                a.Agent, a.Project, version.VersionNumber, Encoding.UTF8.GetByteCount(version.Content), card.Bytes));
        }

        return new Report(
            assignments, cards.Count, routes, endpointRows, toolRows,
            await MentionsAsync(db, ct), violations);
    }

    /// <summary>Current instruction versions and active workflow definitions naming the removed tool, by name.</summary>
    private static async Task<List<string>> MentionsAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        var instructions = await db.Instructions.AsNoTracking()
            .Select(i => new
            {
                i.Name,
                Content = i.Versions.Where(v => v.VersionNumber == i.CurrentVersion).Select(v => v.Content).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var workflows = await db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.IsActive)
            .Select(w => new { w.Name, w.Definition })
            .ToListAsync(ct);

        return instructions
            .Where(i => i.Content?.Contains(FleetContextToolPrefix, StringComparison.Ordinal) == true)
            .Select(i => $"instruction {i.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .Concat(workflows
                .Where(w => w.Definition.Contains(FleetContextToolPrefix, StringComparison.Ordinal))
                .Select(w => $"workflow {w.Name}")
                .OrderBy(n => n, StringComparer.Ordinal))
            .ToList();
    }

    private static async Task<List<object?[]>> QueryAsync(OrchestratorDbContext db, string sql, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
