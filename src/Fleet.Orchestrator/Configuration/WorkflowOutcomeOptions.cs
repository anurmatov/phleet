using System.Globalization;

namespace Fleet.Orchestrator.Configuration;

/// <summary>
/// Workflow failure accountability (#476). Bound from section <c>WorkflowOutcomes</c>; the
/// reference compose maps each <c>WorkflowOutcomes__*</c> key from a <c>FLEET_WORKFLOW_OUTCOMES_*</c>
/// key. Startup-only: a change needs an approved orchestrator restart. Invalid values make the
/// feature unavailable; nothing is clamped.
/// </summary>
public sealed class WorkflowOutcomeOptions
{
    public const string Section = "WorkflowOutcomes";

    /// <summary>The kill switch. False (the default) starts no observer, batcher or intake.</summary>
    public bool Enabled { get; set; }

    /// <summary>Required operator CSV of namespaces to observe. A missing or unreachable namespace is degraded coverage, never a filter.</summary>
    public string Namespaces { get; set; } = "";

    /// <summary>Known nonterminal run Describe interval, 5..300 seconds.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>Recent (24 h) window re-scan interval, 10..3600 seconds.</summary>
    public int RecentScanSeconds { get; set; } = 60;

    /// <summary>Full retention sweep target, 60..86400 seconds.</summary>
    public int FullScanSeconds { get; set; } = 300;

    /// <summary>Failure coalescing window, 5..30 seconds.</summary>
    public int BatchSeconds { get; set; } = 30;

    /// <summary>Maximum failure members per batch, 1..20.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Read-only ownership policy (ownership.json v1). Required when enabled.</summary>
    public string OwnersFile { get; set; } = "";

    /// <summary>Read-only classifier policy (classifiers.json v1). Required when enabled.</summary>
    public string ClassifiersFile { get; set; } = "";

    /// <summary>Owned, persisted 0700 directory for data-protection keys. Required when enabled; never an ephemeral default.</summary>
    public string KeyringDir { get; set; } = "";

    /// <summary>Name of the environment variable that holds the coordinator-only scoped token. Must not name the admin/config token.</summary>
    public string CoordinatorTokenKey { get; set; } = "";

    /// <summary>Current coordinator credential version. Rotation increments it; the old token stops authenticating.</summary>
    public int CoordinatorCredentialVersion { get; set; } = 1;

    /// <summary>Ownership generation. Owner change increments it; a stale generation is 409.</summary>
    public long CoordinatorGeneration { get; set; } = 1;

    /// <summary>
    /// Exact internal HTTP(S) origin the coordinator runtime uses for the inbox (no userinfo, path,
    /// query or fragment). Written only into the configured coordinator's generated config.
    /// </summary>
    public string CoordinatorBaseUrl { get; set; } = "";

    public int MaxConcurrentReads { get; set; } = 4;
    public int RpcTimeoutSeconds { get; set; } = 10;
    public int ListPageSize { get; set; } = 100;
    public int HistoryPageSize { get; set; } = 100;
    public int MaxHistoryPagesPerSlice { get; set; } = 200;

    /// <summary>Unacknowledged failure attention threshold.</summary>
    public int UnackedAttentionMinutes { get; set; } = 15;

    /// <summary>Unresolved-cause incident threshold.</summary>
    public int CauseUnresolvedAlertSeconds { get; set; } = 300;

    /// <summary>Retention of resolved failure rows. Changing it requires approval.</summary>
    public int ResolvedRetentionDays { get; set; } = 90;

    /// <summary>The parsed namespace list: trimmed, blanks dropped, ordinal-distinct, order kept.</summary>
    public IReadOnlyList<string> NamespaceList() =>
        (Namespaces ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Every configuration fault, empty when valid. Only meaningful when <see cref="Enabled"/>;
    /// a disabled feature is reported as disabled, not misconfigured.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (NamespaceList().Count == 0) errors.Add("WorkflowOutcomes:Namespaces is required");
        Range(nameof(PollSeconds), PollSeconds, 5, 300);
        Range(nameof(RecentScanSeconds), RecentScanSeconds, 10, 3600);
        Range(nameof(FullScanSeconds), FullScanSeconds, 60, 86400);
        Range(nameof(BatchSeconds), BatchSeconds, 5, 30);
        Range(nameof(BatchSize), BatchSize, 1, 20);
        Range(nameof(MaxConcurrentReads), MaxConcurrentReads, 1, 16);
        Range(nameof(RpcTimeoutSeconds), RpcTimeoutSeconds, 1, 60);
        Range(nameof(ListPageSize), ListPageSize, 1, 1000);
        Range(nameof(HistoryPageSize), HistoryPageSize, 1, 1000);
        Range(nameof(MaxHistoryPagesPerSlice), MaxHistoryPagesPerSlice, 1, 10000);
        Range(nameof(UnackedAttentionMinutes), UnackedAttentionMinutes, 1, 1440);
        Range(nameof(CauseUnresolvedAlertSeconds), CauseUnresolvedAlertSeconds, 30, 86400);
        Range(nameof(ResolvedRetentionDays), ResolvedRetentionDays, 1, 3650);
        if (CoordinatorCredentialVersion < 1) errors.Add("WorkflowOutcomes:CoordinatorCredentialVersion must be >= 1");
        if (CoordinatorGeneration < 1) errors.Add("WorkflowOutcomes:CoordinatorGeneration must be >= 1");
        if (string.IsNullOrWhiteSpace(OwnersFile)) errors.Add("WorkflowOutcomes:OwnersFile is required");
        if (string.IsNullOrWhiteSpace(ClassifiersFile)) errors.Add("WorkflowOutcomes:ClassifiersFile is required");
        if (string.IsNullOrWhiteSpace(KeyringDir)) errors.Add("WorkflowOutcomes:KeyringDir is required");
        if (!IsOrigin(CoordinatorBaseUrl)) errors.Add("WorkflowOutcomes:CoordinatorBaseUrl must be an http(s) origin without userinfo, path, query or fragment");
        if (string.IsNullOrWhiteSpace(CoordinatorTokenKey)) errors.Add("WorkflowOutcomes:CoordinatorTokenKey is required");
        else if (ForbiddenTokenKeys.Contains(CoordinatorTokenKey.Trim(), StringComparer.OrdinalIgnoreCase))
            errors.Add("WorkflowOutcomes:CoordinatorTokenKey must not name the admin or config token");
        return errors;

        void Range(string name, int value, int min, int max)
        {
            if (value < min || value > max)
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"WorkflowOutcomes:{name} must be {min}..{max} (got {value})"));
        }
    }

    /// <summary>True for an absolute http(s) origin with no userinfo, path, query or fragment.</summary>
    public static bool IsOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/" && value!.TrimEnd('/').IndexOf('/', uri.Scheme.Length + 3) < 0
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>Environment names the coordinator token may never reuse.</summary>
    public static readonly IReadOnlyList<string> ForbiddenTokenKeys =
        ["ORCHESTRATOR_AUTH_TOKEN", "ORCHESTRATOR_CONFIG_TOKEN", "Orchestrator__AuthToken", "Orchestrator__ConfigToken"];
}
