namespace Fleet.Orchestrator.Configuration;

/// <summary>
/// Opt-in, run-scoped epic approval grants (#436). Bound from section <c>EpicGrants</c>; the
/// reference compose maps <c>EpicGrants__Enabled</c>, <c>EpicGrants__MaxDays</c> and
/// <c>EpicGrants__DeniedRepos</c> from the <c>FLEET_EPIC_GRANTS_*</c> keys. Read at startup, so a
/// change needs an orchestrator restart.
/// </summary>
public sealed class EpicGrantOptions
{
    public const string Section = "EpicGrants";

    /// <summary>
    /// The kill switch. False (the default) refuses every decision with <c>disabled</c> and every
    /// create with 503. <c>true</c> with <see cref="MaxDays"/> ≤ 0 is a misconfiguration and is
    /// treated as disabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Longest allowed grant lifetime, in days from creation.</summary>
    public int MaxDays { get; set; } = 14;

    /// <summary>
    /// Comma-separated <c>owner/name</c> repos that can never be a grant target. Compared
    /// case-insensitively; wins over a target's <c>allowPublic</c>.
    /// </summary>
    public string DeniedRepos { get; set; } = "";

    /// <summary>Base URL for the unauthenticated repository visibility read.</summary>
    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary><see cref="DeniedRepos"/> parsed: trimmed, blanks dropped, case-insensitive.</summary>
    public IReadOnlySet<string> DeniedRepoSet() =>
        new HashSet<string>(
            (DeniedRepos ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
}
