using System.Globalization;
using System.Text.RegularExpressions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// The signals one intake site can offer (#347 "Routing: resolved at intake"). Each site fills only
/// the kinds the spec table gives it: relay/bridge supply <see cref="Repo"/> and
/// <see cref="Workflow"/> but NEVER <see cref="Chat"/> (the relay chat id is a posting target, not
/// a topic); Telegram messages and check-ins supply only <see cref="Chat"/>.
/// </summary>
public sealed record ProjectContextSignals(string? Repo = null, string? Workflow = null, long? Chat = null);

/// <summary>A project a winning level matched but did not request, and why.</summary>
public sealed record ProjectContextSkip(string Project, string Reason)
{
    public const string FullMode = "full_mode";
    public const string RouteTooBroad = "route_too_broad";

    public override string ToString() => $"{Project}:{Reason}";
}

/// <summary>What <see cref="ProjectContextRouter.Resolve(ProjectContextSignals, ProjectContextRoutingTable)"/> decided, and why.</summary>
/// <param name="SignalKind">The winning level, or null when no level matched.</param>
/// <param name="SignalValue">The winning signal's value (normalised), or null.</param>
/// <param name="Matched">Every assigned project the winning level matched, any mode.</param>
/// <param name="Requests">What the message will carry — at most <see cref="ProjectContextRouter.MaxRequests"/>.</param>
/// <param name="Skipped">Matched projects that were not requested, with the reason.</param>
public sealed record ProjectContextRouteResult(
    string? SignalKind,
    string? SignalValue,
    IReadOnlyList<string> Matched,
    IReadOnlyList<ContextAttachmentRequest> Requests,
    IReadOnlyList<ProjectContextSkip> Skipped)
{
    public static ProjectContextRouteResult NoMatch { get; } = new(null, null, [], [], []);

    public bool TooBroad => Skipped.Any(s => s.Reason == ProjectContextSkip.RouteTooBroad);
}

/// <summary>
/// The validated, immutable form of <see cref="ProjectContextRoutingOptions"/>: routes already
/// filtered to assigned projects and normalised for comparison.
/// </summary>
public sealed class ProjectContextRoutingTable
{
    public const string KindRepo = "repo";
    public const string KindWorkflow = "workflow";
    public const string KindChat = "chat";

    /// <summary>Same grammar the orchestrator validates route writes with and temporal validates Repo with.</summary>
    internal static readonly Regex RepoPattern = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    private ProjectContextRoutingTable(
        IReadOnlyList<(string Kind, string Value, string Project)> routes,
        IReadOnlyDictionary<string, int> cardVersions)
    {
        Routes = routes;
        CardVersions = cardVersions;
    }

    /// <summary>
    /// Routes for ASSIGNED projects only, with the value normalised (repo lower-case, chat as its
    /// invariant decimal) and the project in the assignment's own casing.
    /// </summary>
    public IReadOnlyList<(string Kind, string Value, string Project)> Routes { get; }

    /// <summary>Effective-card projects and the full version each one is attached at (OrdinalIgnoreCase keys).</summary>
    public IReadOnlyDictionary<string, int> CardVersions { get; }

    /// <summary>
    /// Validates the block and builds the table. Returns false with a reason naming the first fault;
    /// the caller disables routing entirely rather than route on a partially understood block.
    /// </summary>
    public static bool TryCreate(
        ProjectContextRoutingOptions options,
        IReadOnlyList<string> assignedProjects,
        out ProjectContextRoutingTable table,
        out string? error)
    {
        table = null!;
        error = null;

        // Assignment names as the agent knows them (PromptBuilder reads projects/<name>/ with this
        // exact casing), looked up case-insensitively (D1).
        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assignedProjects)
        {
            if (!string.IsNullOrWhiteSpace(name))
                assigned.TryAdd(name.Trim(), name.Trim());
        }

        var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (project, raw) in options.FullVersions ?? [])
        {
            if (string.IsNullOrWhiteSpace(project))
                return Fail("fullVersions has a blank project name", out error);
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
                return Fail($"fullVersions['{project}'] = '{raw}' is not a positive integer", out error);
            versions[project.Trim()] = version;
        }

        var cardVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in options.CardProjects ?? [])
        {
            if (string.IsNullOrWhiteSpace(project))
                return Fail("cardProjects has a blank entry", out error);
            if (!IsSafeProjectName(project.Trim()))
                return Fail($"cardProjects entry '{project}' is not a valid project directory name", out error);
            if (!versions.TryGetValue(project.Trim(), out var version))
                return Fail($"card project '{project}' has no fullVersions entry", out error);

            // A card project that is not assigned is never routed to (step 1 below), so it is
            // dropped here rather than treated as malformed.
            if (assigned.TryGetValue(project.Trim(), out var canonical))
                cardVersions[canonical] = version;
        }

        var routes = new List<(string Kind, string Value, string Project)>();
        var index = 0;
        foreach (var route in options.Routes ?? [])
        {
            var at = $"routes[{index++}]";
            if (route is null)
                return Fail($"{at} is empty", out error);
            if (string.IsNullOrWhiteSpace(route.Project))
                return Fail($"{at} has a blank project", out error);

            var kind = (route.Kind ?? "").Trim().ToLowerInvariant();
            var value = (route.Value ?? "").Trim();
            string normalized;
            switch (kind)
            {
                case KindRepo:
                    if (!RepoPattern.IsMatch(value))
                        return Fail($"{at} repo value '{route.Value}' is not owner/name", out error);
                    normalized = value.ToLowerInvariant();
                    break;
                case KindWorkflow:
                    if (value.Length == 0 || value.Length > 256)
                        return Fail($"{at} workflow value is blank or longer than 256", out error);
                    normalized = value;
                    break;
                case KindChat:
                    if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chatId))
                        return Fail($"{at} chat value '{route.Value}' is not a 64-bit integer", out error);
                    normalized = chatId.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    return Fail($"{at} has unknown kind '{route.Kind}'", out error);
            }

            // Step 1 of resolution: only routes for projects the agent is assigned, any mode.
            if (assigned.TryGetValue(route.Project.Trim(), out var canonicalProject))
                routes.Add((kind, normalized, canonicalProject));
        }

        table = new ProjectContextRoutingTable(routes, cardVersions);
        return true;

        static bool Fail(string message, out string? error)
        {
            error = message;
            return false;
        }
    }

    /// <summary>A project name is used as a directory under <c>projects/</c>, so it must be one segment.</summary>
    internal static bool IsSafeProjectName(string project) =>
        project.Length > 0
        && project is not "." and not ".."
        && project.IndexOfAny(['/', '\\', ':']) < 0
        && project.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

/// <summary>
/// Resolves a message's project-context requests at intake (#347 D4). The decision is a pure
/// function of the signals, the assigned projects and the routing block — no model judgement, no
/// network — and its result travels with the message.
/// </summary>
/// <remarks>
/// <para>Resolution:</para>
/// <list type="number">
/// <item>Only routes for assigned projects count (any mode, <c>OrdinalIgnoreCase</c>).</item>
/// <item><c>repo</c>, then <c>workflow</c>, then <c>chat</c>. The first level with at least one
/// match wins and lower levels are never consulted — even when every match at the winning level is
/// effective-full. Levels are never merged.</item>
/// <item>Matches are kept only when they are in <c>cardProjects</c> (a full assignment is already
/// resident), ordered by ordinal-ignore-case project name.</item>
/// <item>More than <see cref="MaxRequests"/> → nothing, and <c>route_too_broad</c>.</item>
/// </list>
/// </remarks>
public sealed class ProjectContextRouter
{
    public const int MaxRequests = 3;

    private readonly ProjectContextRoutingTable? _table;
    private readonly ILogger<ProjectContextRouter> _logger;

    public ProjectContextRouter(IOptions<AgentOptions> options, ILogger<ProjectContextRouter> logger)
    {
        _logger = logger;
        var agent = options.Value;

        // Absent → disabled with no log: this is every all-full agent, and its behaviour must be
        // exactly what it was before cards existed.
        if (agent.ProjectContextRouting is not { } routing)
            return;

        if (ProjectContextRoutingTable.TryCreate(routing, agent.Projects, out var table, out var error))
        {
            _table = table;
            _logger.LogInformation(
                "ProjectContextRouting enabled cardProjects={CardProjects} routes={RouteCount}",
                string.Join(",", table.CardVersions.Keys.Order(StringComparer.OrdinalIgnoreCase)), table.Routes.Count);
        }
        else
        {
            // One Warning, once: the router is a singleton and validation runs only here.
            _logger.LogWarning(
                "ProjectContextRouting is malformed ({Error}) — router disabled; card projects are served by the resident card and the get_project_context fallback only",
                error);
        }
    }

    /// <summary>False when the block is absent or malformed. Every request list is then empty.</summary>
    public bool IsEnabled => _table is not null;

    /// <summary>
    /// Relay / bridge intake (#347 D9): the repo comes from the structured <c>RelayMessage.Repo</c>,
    /// the workflow from a LEADING <c>[fleet-wf:&lt;Type&gt;:&lt;Id&gt;]</c> line. The relay's chat
    /// id is deliberately not a parameter.
    /// </summary>
    public IReadOnlyList<ContextAttachmentRequest> ResolveRelay(string? repo, string? text)
    {
        if (_table is null)
            return [];

        string? validRepo = null;
        if (!string.IsNullOrWhiteSpace(repo))
        {
            if (ProjectContextRoutingTable.RepoPattern.IsMatch(repo.Trim()))
                validRepo = repo.Trim();
            else
                _logger.LogWarning("ProjectContextRoute ignoring malformed relay Repo '{Repo}' (expected owner/name)", Truncate(repo, 120));
        }

        return Resolve(new ProjectContextSignals(Repo: validRepo, Workflow: ParseWorkflowType(text)));
    }

    /// <summary>Telegram message and check-in intake: the chat is the only signal.</summary>
    public IReadOnlyList<ContextAttachmentRequest> ResolveChat(long chatId) =>
        _table is null || chatId == 0
            ? []
            : Resolve(new ProjectContextSignals(Chat: chatId));

    /// <summary>Resolves and logs one intake decision.</summary>
    public IReadOnlyList<ContextAttachmentRequest> Resolve(ProjectContextSignals signals)
    {
        if (_table is null)
            return [];

        var result = Resolve(signals, _table);
        if (result.SignalKind is null)
            return [];

        var skipped = result.Skipped.Count == 0 ? "" : string.Join(",", result.Skipped);
        if (result.TooBroad)
        {
            _logger.LogWarning(
                "ProjectContextRoute route_too_broad signal={SignalKind}:{SignalValue} matched={Matched} requested= skipped={Skipped} (more than {Max} card projects)",
                result.SignalKind, result.SignalValue, string.Join(",", result.Matched), skipped, MaxRequests);
        }
        else
        {
            _logger.LogInformation(
                "ProjectContextRoute signal={SignalKind}:{SignalValue} matched={Matched} requested={Requested} skipped={Skipped}",
                result.SignalKind, result.SignalValue, string.Join(",", result.Matched),
                string.Join(",", result.Requests.Select(r => r.Project)), skipped);
        }

        return result.Requests;
    }

    /// <summary>The pure resolution function. No logging, no I/O.</summary>
    public static ProjectContextRouteResult Resolve(ProjectContextSignals signals, ProjectContextRoutingTable table)
    {
        var levels = new (string Kind, string? Value)[]
        {
            (ProjectContextRoutingTable.KindRepo, signals.Repo?.Trim().ToLowerInvariant()),
            (ProjectContextRoutingTable.KindWorkflow, signals.Workflow),
            (ProjectContextRoutingTable.KindChat, signals.Chat?.ToString(CultureInfo.InvariantCulture)),
        };

        foreach (var (kind, value) in levels)
        {
            if (string.IsNullOrEmpty(value))
                continue;

            var matched = table.Routes
                .Where(r => r.Kind == kind && string.Equals(r.Value, value, StringComparison.Ordinal))
                .Select(r => r.Project)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Fall through ONLY when this level matched nothing at all.
            if (matched.Count == 0)
                continue;

            var skipped = new List<ProjectContextSkip>();
            var cards = new List<ContextAttachmentRequest>();
            foreach (var project in matched)
            {
                if (table.CardVersions.TryGetValue(project, out var version))
                    cards.Add(new ContextAttachmentRequest(project, version, kind));
                else
                    skipped.Add(new ProjectContextSkip(project, ProjectContextSkip.FullMode));
            }

            if (cards.Count > MaxRequests)
            {
                skipped.AddRange(cards.Select(c => new ProjectContextSkip(c.Project, ProjectContextSkip.RouteTooBroad)));
                cards.Clear();
            }

            // This level won. Even when every match was full mode (nothing requested), the lower
            // levels are not consulted: precedence decides which signal identifies the turn, and a
            // full-mode winner means the context is already resident.
            return new ProjectContextRouteResult(kind, value, matched, cards, skipped);
        }

        return ProjectContextRouteResult.NoMatch;
    }

    private static readonly Regex WorkflowTagLine = new(
        @"\A\[fleet-wf:(?<type>[^:\]\r\n]+):[^\]\r\n]*\][ \t]*(?:\r?\n|\z)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The workflow type from a LEADING <c>[fleet-wf:&lt;Type&gt;:&lt;Id&gt;]</c> line, as the temporal
    /// bridge prepends it to every directive. A tag anywhere else in the text is not a signal.
    /// </summary>
    internal static string? ParseWorkflowType(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var match = WorkflowTagLine.Match(text);
        return match.Success ? match.Groups["type"].Value : null;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
