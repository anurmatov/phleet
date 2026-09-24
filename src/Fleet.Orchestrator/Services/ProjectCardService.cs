namespace Fleet.Orchestrator.Services;

/// <summary>Staleness and keep-marker state of a project's current card against its current full context.</summary>
/// <param name="CurrentVersion">Current card version number.</param>
/// <param name="BasedOnFullVersion">Full context version the current card was written for.</param>
/// <param name="Stale"><c>BasedOnFullVersion &lt; CurrentFullVersion</c>.</param>
/// <param name="MissingKeeps">Valid keep slugs of the current full content absent from the card.</param>
/// <param name="InvalidKeeps">Invalid keep candidates in the current card (only reachable via rollback).</param>
public sealed record ProjectCardState(
    int CurrentVersion,
    int BasedOnFullVersion,
    bool Stale,
    IReadOnlyList<string> MissingKeeps,
    IReadOnlyList<string> InvalidKeeps);

/// <summary>
/// Card rules shared by REST, MCP and provisioning: staleness, <c>missingKeeps</c> and the
/// generated footer. Keep markers are always read through <see cref="KeepMarkerParser"/>.
/// </summary>
public static partial class ProjectCardService
{
    /// <summary>Card versions kept per project, matching full context versions.</summary>
    public const int MaxCardVersions = 20;

    public static bool IsStale(int basedOnFullVersion, int currentFullVersion) =>
        basedOnFullVersion < currentFullVersion;

    public static ProjectCardState Evaluate(
        int cardVersion, int basedOnFullVersion, string cardContent, int currentFullVersion, string fullContent) =>
        new(
            cardVersion,
            basedOnFullVersion,
            IsStale(basedOnFullVersion, currentFullVersion),
            KeepMarkerParser.Missing(fullContent, cardContent),
            KeepMarkerParser.Parse(cardContent).Invalid);

    /// <summary>
    /// The generated footer appended to a resident card. Never authored — the numbers come from the
    /// rows, so a card cannot claim a full version it was not written for.
    /// </summary>
    public static string RenderFooter(string project, int cardVersion, int basedOnFullVersion, int currentFullVersion)
    {
        var stale = IsStale(basedOnFullVersion, currentFullVersion) ? " · may be stale" : "";
        return
            $"[project card: {project} · card v{cardVersion} · written for full v{basedOnFullVersion} · full is v{currentFullVersion}{stale}]\n" +
            $"The full {project} context is attached to turns routed to this project.\n" +
            $"On any other turn that needs it, call get_project_context with name \"{project}\".";
    }

    /// <summary>The resident <c>context.md</c> body for an effective card assignment: the card, a blank line, the footer.</summary>
    public static string RenderResidentCard(
        string project, string cardContent, int cardVersion, int basedOnFullVersion, int currentFullVersion) =>
        cardContent.TrimEnd() + "\n\n" +
        RenderFooter(project, cardVersion, basedOnFullVersion, currentFullVersion) + "\n";
}
