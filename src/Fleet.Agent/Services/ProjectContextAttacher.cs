using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>One ledger entry: a project's full context at one version.</summary>
public readonly record struct ProjectContextKey(string Project, int FullVersion)
{
    public override string ToString() => Project;
}

/// <summary>
/// The result of one <see cref="ProjectContextAttacher.Render"/>: the text to put in front of the
/// executor input, and the keys it attached, which become ledger entries only if the executor
/// accepts the prompt.
/// </summary>
public sealed class ProjectContextRender
{
    public static ProjectContextRender Empty { get; } = new("", [], [], [], 0);

    internal ProjectContextRender(
        string prefix,
        IReadOnlyList<ProjectContextKey> pendingKeys,
        IReadOnlyList<ProjectContextKey> suppressed,
        IReadOnlyList<ProjectContextKey> missing,
        long generation)
    {
        Prefix = prefix;
        PendingKeys = pendingKeys;
        Suppressed = suppressed;
        Missing = missing;
        Generation = generation;
    }

    /// <summary>Executor-input-only text. Never displayText, never Telegram, never an event.</summary>
    public string Prefix { get; }

    /// <summary>Rendered keys, marked on acceptance.</summary>
    public IReadOnlyList<ProjectContextKey> PendingKeys { get; }

    /// <summary>Requested keys already in the ledger (<c>already_attached</c>).</summary>
    public IReadOnlyList<ProjectContextKey> Suppressed { get; }

    /// <summary>Requested keys whose <c>full.md</c> could not be read (<c>full_file_missing</c>).</summary>
    public IReadOnlyList<ProjectContextKey> Missing { get; }

    /// <summary>The ledger generation the render was decided against. A mark into a newer one is dropped.</summary>
    internal long Generation { get; }

    /// <summary>True when the delivery carried at least one request — i.e. when it is worth a log line.</summary>
    public bool HadRequests => PendingKeys.Count + Suppressed.Count + Missing.Count > 0;

    /// <summary>Prefix + the text the executor would otherwise have received.</summary>
    public string Apply(string executorInput) => Prefix.Length == 0 ? executorInput : Prefix + executorInput;
}

/// <summary>
/// Renders routed project contexts at delivery and keeps the per-session ledger that suppresses a
/// repeat (#347 D5, "Attachment: rendered at delivery, marked on acceptance").
/// </summary>
/// <remarks>
/// <para><b>Render</b> dedupes the requests by (project, fullVersion), drops those already in the
/// ledger and reads <c>projects/&lt;p&gt;/full.md</c>. A missing or unreadable file yields no block
/// for that project and nothing to mark; the turn proceeds.</para>
/// <para><b>Ledger reset</b> happens at render time when the executor is not warm, when the
/// ledger is bound to a session id and <c>LastSessionId</c> differs, or when
/// <c>CompactionEpoch</c> moved. An unbound ledger binds to <c>LastSessionId</c> at the first
/// render where the process is warm.</para>
/// <para><b>Marking</b> happens only on acceptance — <c>prompt_accepted</c> for a turn,
/// <c>MidTurnInjectionStatus.Injected</c> for an injection — and never at intake or render. A
/// delivery that fails before acceptance marks nothing, so the next routed delivery renders again.
/// One lock guards the ledger; the worst race is a duplicate attachment, never a missing one: a mark
/// whose render was decided against an older ledger generation is dropped rather than written into
/// a ledger that was reset (cold start, session change, compaction) after it.</para>
/// <para>Known cost: a claude process that restarts and resumes a prior session gets a second copy,
/// because the ledger is in memory. That is safe and accepted.</para>
/// </remarks>
public sealed class ProjectContextAttacher
{
    public const string PathTurn = "turn";
    public const string PathInject = "inject";
    public const string PathQueue = "queue";
    public const string PathInbox = "inbox";
    public const string PathResume = "resume";

    private readonly IAgentExecutor _executor;
    private readonly ILogger<ProjectContextAttacher> _logger;

    private readonly Lock _lock = new();
    private readonly HashSet<ProjectContextKey> _ledger = new(KeyComparer.Instance);
    private string? _boundSessionId;
    private int _ledgerEpoch;
    private long _generation;
    private long _promptAcceptedMissing;

    public ProjectContextAttacher(IAgentExecutor executor, ILogger<ProjectContextAttacher> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Root the generated <c>projects/</c> tree is read from — the same root <see cref="PromptBuilder"/>
    /// reads <c>context.md</c> from. Overridable for tests only.
    /// </summary>
    internal string ContentRoot { get; init; } = AppContext.BaseDirectory;

    /// <summary>Deliveries that rendered a block and ended without the executor accepting the prompt.</summary>
    public long PromptAcceptedMissingCount => Interlocked.Read(ref _promptAcceptedMissing);

    /// <summary>Snapshot of the ledger, for tests and diagnostics.</summary>
    internal IReadOnlyList<ProjectContextKey> LedgerSnapshot()
    {
        lock (_lock) return [.. _ledger];
    }

    /// <summary>
    /// Renders the attachment for one delivery. Null or empty requests return
    /// <see cref="ProjectContextRender.Empty"/> without touching the ledger, so an agent with no
    /// routing block behaves exactly as it did before cards existed.
    /// </summary>
    public ProjectContextRender Render(IReadOnlyList<ContextAttachmentRequest>? requests)
    {
        if (requests is not { Count: > 0 })
            return ProjectContextRender.Empty;

        var wanted = new List<ProjectContextKey>();
        foreach (var request in ContextAttachmentRequest.Union([requests]) ?? [])
            wanted.Add(new ProjectContextKey(request.Project, request.FullVersion));

        List<ProjectContextKey> candidates;
        List<ProjectContextKey> suppressed;
        long generation;
        lock (_lock)
        {
            ResetIfStaleLocked();
            generation = _generation;
            candidates = wanted.Where(k => !_ledger.Contains(k)).ToList();
            suppressed = wanted.Where(k => _ledger.Contains(k)).ToList();
        }

        // File reads happen outside the lock. Two deliveries racing here can both render the same
        // project — a duplicate, which is the accepted failure direction.
        var prefix = new System.Text.StringBuilder();
        var pending = new List<ProjectContextKey>();
        var missing = new List<ProjectContextKey>();
        foreach (var key in candidates)
        {
            var content = TryReadFull(key);
            if (content is null)
            {
                missing.Add(key);
                continue;
            }

            pending.Add(key);
            prefix.Append("[project context: ").Append(key.Project)
                .Append(" · full v").Append(key.FullVersion).Append(" · attached for this turn]\n");
            prefix.Append(content);
            if (!content.EndsWith('\n'))
                prefix.Append('\n');
            prefix.Append("[end project context: ").Append(key.Project).Append("]\n\n");
        }

        return new ProjectContextRender(prefix.ToString(), pending, suppressed, missing, generation);
    }

    /// <summary>
    /// Marks the render's keys as attached. Call only once the executor accepted the prompt.
    /// Idempotent; a render decided against an older ledger generation marks nothing.
    /// </summary>
    public void MarkAccepted(ProjectContextRender render)
    {
        if (render.PendingKeys.Count == 0)
            return;

        lock (_lock)
        {
            if (render.Generation != _generation)
            {
                _logger.LogDebug(
                    "ProjectContextAttach mark dropped: ledger reset since render (render gen {Render}, ledger gen {Ledger}); next routed delivery renders again",
                    render.Generation, _generation);
                return;
            }

            foreach (var key in render.PendingKeys)
                _ledger.Add(key);
        }
    }

    /// <summary>
    /// The one <c>ProjectContextAttach</c> line per delivery that carried requests, plus the
    /// <c>prompt_accepted_missing</c> count when a rendered block was never accepted.
    /// </summary>
    public void LogDelivery(ProjectContextRender render, string path, bool accepted)
    {
        if (!render.HadRequests)
            return;

        _logger.LogInformation(
            "ProjectContextAttach path={Path} rendered={Rendered} suppressed={Suppressed} accepted={Accepted}",
            path,
            string.Join(",", render.PendingKeys),
            string.Join(",", render.Suppressed.Select(k => $"{k.Project}:already_attached")),
            accepted ? "true" : "false");

        if (!accepted && render.PendingKeys.Count > 0 && path != PathInject)
        {
            // A turn whose executor never said prompt_accepted: nothing was marked, so the next
            // routed delivery attaches again. A duplicate, never a gap — but a count that keeps
            // rising on every turn means the executor lost the event.
            var total = Interlocked.Increment(ref _promptAcceptedMissing);
            _logger.LogWarning(
                "prompt_accepted_missing path={Path} pending={Pending} total={Total}",
                path, string.Join(",", render.PendingKeys), total);
        }
    }

    private void ResetIfStaleLocked()
    {
        var warm = _executor.IsProcessWarm;
        var sessionId = _executor.LastSessionId;
        var epoch = _executor.CompactionEpoch;

        string? reason = null;
        if (!warm)
            reason = "cold";
        else if (_boundSessionId is not null && !string.Equals(_boundSessionId, sessionId, StringComparison.Ordinal))
            reason = "session_changed";
        else if (epoch != _ledgerEpoch)
            reason = "compaction";

        if (reason is not null)
        {
            if (_ledger.Count > 0)
            {
                // Cold is routine for a never-warm executor (gemini clears before every render), so
                // it is Debug; a session change or a compaction is worth seeing at Information.
                _logger.Log(reason == "cold" ? LogLevel.Debug : LogLevel.Information,
                    "ProjectContextAttach ledger cleared reason={Reason} entries={Entries}",
                    reason, string.Join(",", _ledger));
            }
            _ledger.Clear();
            _boundSessionId = null;
            _ledgerEpoch = epoch;
            _generation++;
        }

        if (warm && _boundSessionId is null)
            _boundSessionId = sessionId;
    }

    private string? TryReadFull(ProjectContextKey key)
    {
        var path = ProjectContextRoutingTable.IsSafeProjectName(key.Project)
            ? Path.Combine(ContentRoot, "projects", key.Project, "full.md")
            : null;

        try
        {
            if (path is null)
                throw new IOException($"'{key.Project}' is not a valid project directory name");
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(
                "full_file_missing project={Project} fullVersion={FullVersion} path={Path}: {Error} — no attachment, nothing marked",
                key.Project, key.FullVersion, path, ex.Message);
            return null;
        }
    }

    private sealed class KeyComparer : IEqualityComparer<ProjectContextKey>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals(ProjectContextKey x, ProjectContextKey y) =>
            x.FullVersion == y.FullVersion && string.Equals(x.Project, y.Project, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ProjectContextKey key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Project), key.FullVersion);
    }
}
