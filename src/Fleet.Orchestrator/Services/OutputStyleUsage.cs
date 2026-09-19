using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Which agents name a given output style (#317).
/// </summary>
/// <remarks>
/// <para>
/// <c>agents.OutputStyle</c> is deliberately not a foreign key — a name with no row must be
/// storable so provisioning can refuse it loudly — so "who uses this style" is a query, not a
/// navigation property. One place answers it, because the delete guard and the operator-facing
/// list must not be able to disagree about whether a style is in use.
/// </para>
/// </remarks>
public static class OutputStyleUsage
{
    /// <summary>Agent names by style name, each list ordered. Styles with no agents are absent.</summary>
    /// <remarks>
    /// Grouped case-insensitively to match what provisioning will actually resolve: it looks the
    /// style up with <c>s.Name == agent.OutputStyle</c>, which MySQL answers under a case-insensitive
    /// collation, and <c>output_styles.Name</c> is the primary key so two rows cannot differ by case
    /// alone. An agent that spelled the name differently is genuinely on the style — grouping
    /// ordinally here would show it as unused while the delete guard refused, and the two must not
    /// be able to disagree.
    /// </remarks>
    public static async Task<Dictionary<string, List<string>>> ByStyleAsync(
        OrchestratorDbContext db, CancellationToken ct = default)
    {
        var rows = await db.Agents
            .AsNoTracking()
            .Where(a => a.OutputStyle != null && a.OutputStyle != "")
            .Select(a => new { a.Name, Style = a.OutputStyle! })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Style, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Agent names assigned to one style, ordered. Empty when nothing references it.
    /// </summary>
    /// <remarks>
    /// Deliberately the same grouping <see cref="ByStyleAsync"/> performs rather than its own
    /// <c>WHERE</c>: a database predicate would answer under the provider's collation — case-
    /// insensitively on MySQL, case-sensitively on SQLite — so the delete guard's meaning would
    /// depend on which store it ran against, and a test would prove it on the wrong one.
    /// </remarks>
    public static async Task<List<string>> AgentsUsingAsync(
        OrchestratorDbContext db, string styleName, CancellationToken ct = default) =>
        For(await ByStyleAsync(db, ct), styleName);

    /// <summary>The agents for one style out of a <see cref="ByStyleAsync"/> map, never null.</summary>
    public static List<string> For(Dictionary<string, List<string>> usage, string styleName) =>
        usage.TryGetValue(styleName, out var agents) ? agents : [];
}
