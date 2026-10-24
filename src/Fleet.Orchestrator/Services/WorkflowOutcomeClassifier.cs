using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

// Workflow failure accountability (#476): failure classification and ownership. Both policies are
// operator-owned, read-only files loaded once at startup with strict validation; any fault is a
// startup failure, never a partially applied policy. Classification uses typed history facts and
// pinned loaded-definition hashes only — no text matching, no sentiment, no current DB definitions.

/// <summary>A policy file that is missing, unreadable or invalid. The observer must not start.</summary>
public sealed class WorkflowOutcomePolicyException(string message) : Exception(message);

/// <summary>Terminal event type names exactly as Temporal records them.</summary>
public static class WorkflowOutcomeTerminalTypes
{
    public const string Completed = "WorkflowExecutionCompleted";
    public const string Failed = "WorkflowExecutionFailed";
    public const string TimedOut = "WorkflowExecutionTimedOut";
    public const string Canceled = "WorkflowExecutionCanceled";
    public const string Terminated = "WorkflowExecutionTerminated";
    public const string ContinuedAsNew = "WorkflowExecutionContinuedAsNew";

    /// <summary>The only terminal types a classifier rule may name; Failed/TimedOut need no rule.</summary>
    public static readonly IReadOnlyList<string> RuleTypes = [Completed, Canceled, Terminated];
}

public static class WorkflowOutcomeEvidenceKinds
{
    /// <summary>RFC 6901 pointer into <c>{"eventType","attributes","result"}</c> of the terminal event.</summary>
    public const string TerminalEvent = "terminal_event";

    /// <summary>
    /// RFC 6901 pointer into the typed result of the LAST scheduled DelegateToAgent whose taskId is
    /// <c>&lt;workflowId&gt;/&lt;exitBranch&gt;</c>: the decoded AgentTaskResult when the activity
    /// completed, or <c>{"activityFailure":{"eventType","failureType"}}</c> built from its typed
    /// ActivityTaskFailed/TimedOut/Canceled close event. An open delegate never matches.
    /// </summary>
    public const string DelegateResult = "delegate_result";

    /// <summary>A DelegateToAgent with taskId <c>&lt;workflowId&gt;/&lt;exitBranch&gt;</c> was scheduled: the branch ran.</summary>
    public const string FailureBranch = "failure_branch";

    public static readonly IReadOnlyList<string> All = [TerminalEvent, DelegateResult, FailureBranch];
}

/// <summary>A JSON scalar from a rule's <c>equals</c>.</summary>
public sealed record WorkflowOutcomeScalar(JsonValueKind Kind, string Raw)
{
    public bool Matches(JsonElement value)
    {
        switch (Kind)
        {
            case JsonValueKind.String:
                return value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), Raw, StringComparison.Ordinal);
            case JsonValueKind.True:
            case JsonValueKind.False:
                return value.ValueKind == Kind;
            case JsonValueKind.Number:
                return value.ValueKind == JsonValueKind.Number
                    && decimal.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected)
                    && value.TryGetDecimal(out var actual) && actual == expected;
            default:
                return false;
        }
    }
}

public sealed record WorkflowOutcomeClassifierRule(
    string Id,
    string WorkflowType,
    string SourceCommit,
    string? DefinitionHash,
    IReadOnlyList<string> TerminalEventTypes,
    string EvidenceKind,
    string? Path,
    WorkflowOutcomeScalar? EqualsValue,
    string? ExitBranch,
    string Classification,
    string ReasonCode);

/// <summary>classifiers.json v1, validated.</summary>
public sealed class WorkflowOutcomeClassifierPolicy
{
    public required string Hash { get; init; }
    public required IReadOnlyList<WorkflowOutcomeClassifierRule> Rules { get; init; }

    public static readonly WorkflowOutcomeClassifierPolicy Empty = new() { Hash = WorkflowOutcomeHashes.Sha256Hex("{\"schemaVersion\":1,\"rules\":[]}"), Rules = [] };

    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex TypePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,254}$", RegexOptions.CultureInvariant);
    private static readonly Regex StepPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static readonly string[] RootKeys = ["schemaVersion", "rules"];
    private static readonly string[] RuleKeys = ["id", "workflowType", "sourceCommit", "definitionHash", "terminalEventTypes", "evidence", "classification", "reasonCode"];
    private static readonly string[] RuleRequired = ["id", "workflowType", "sourceCommit", "terminalEventTypes", "evidence", "classification", "reasonCode"];
    private static readonly string[] EvidenceKeys = ["kind", "path", "equals", "exitBranch"];

    /// <summary>Strict parse: unknown or duplicate keys, duplicate ids/rules, catch-alls and invalid values all throw.</summary>
    public static WorkflowOutcomeClassifierPolicy Parse(byte[] bytes)
    {
        using var doc = PolicyJson.Parse(bytes, "classifiers");
        var root = doc.RootElement;
        PolicyJson.ExactKeys(root, RootKeys, RootKeys, "classifiers");
        if (!PolicyJson.IsInt(root.GetProperty("schemaVersion"), 1))
            throw new WorkflowOutcomePolicyException("classifiers: schemaVersion must be 1");
        var rulesElement = root.GetProperty("rules");
        if (rulesElement.ValueKind != JsonValueKind.Array) throw new WorkflowOutcomePolicyException("classifiers: rules must be an array");

        var rules = new List<WorkflowOutcomeClassifierRule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var semantic = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var r in rulesElement.EnumerateArray())
        {
            var at = "classifiers: rules[" + index++ + "]";
            PolicyJson.ExactKeys(r, RuleKeys, RuleRequired, at);
            var id = PolicyJson.String(r, "id", at);
            if (!IdPattern.IsMatch(id)) throw new WorkflowOutcomePolicyException(at + ": invalid id");
            if (!ids.Add(id)) throw new WorkflowOutcomePolicyException(at + ": duplicate id '" + id + "'");
            at = "classifiers: rule '" + id + "'";

            var type = PolicyJson.String(r, "workflowType", at);
            // No catch-all: one exact workflow type per rule.
            if (!TypePattern.IsMatch(type)) throw new WorkflowOutcomePolicyException(at + ": workflowType must be one exact type name");
            var commit = PolicyJson.String(r, "sourceCommit", at);
            if (!CommitPattern.IsMatch(commit)) throw new WorkflowOutcomePolicyException(at + ": sourceCommit must be a 40-hex commit");
            var hash = PolicyJson.OptionalString(r, "definitionHash", at);
            if (hash is not null && !HashPattern.IsMatch(hash)) throw new WorkflowOutcomePolicyException(at + ": definitionHash must be 64 lowercase hex");

            var terminalElement = r.GetProperty("terminalEventTypes");
            if (terminalElement.ValueKind != JsonValueKind.Array || terminalElement.GetArrayLength() == 0)
                throw new WorkflowOutcomePolicyException(at + ": terminalEventTypes must be a non-empty array");
            var terminals = new List<string>();
            foreach (var t in terminalElement.EnumerateArray())
            {
                var value = t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (value is null || !WorkflowOutcomeTerminalTypes.RuleTypes.Contains(value, StringComparer.Ordinal))
                    throw new WorkflowOutcomePolicyException(at + ": terminalEventTypes may only name WorkflowExecutionCompleted, WorkflowExecutionCanceled or WorkflowExecutionTerminated");
                if (terminals.Contains(value, StringComparer.Ordinal)) throw new WorkflowOutcomePolicyException(at + ": duplicate terminal event type");
                terminals.Add(value);
            }
            // A Completed rule with a definitionHash covers only runs that loaded exactly that
            // definition; one without it is pinned by workflowType + sourceCommit and covers only
            // runs whose complete history proves no LoadWorkflowDefinition (compiled workflows).

            var classification = PolicyJson.String(r, "classification", at);
            var reason = PolicyJson.String(r, "reasonCode", at);
            foreach (var terminal in terminals)
                if (!AllowedOutcome(terminal, classification, reason))
                    throw new WorkflowOutcomePolicyException(at + ": classification/reasonCode not allowed for " + terminal);

            var evidence = r.GetProperty("evidence");
            PolicyJson.ExactKeys(evidence, EvidenceKeys, ["kind"], at + ".evidence");
            var kind = PolicyJson.String(evidence, "kind", at + ".evidence");
            if (!WorkflowOutcomeEvidenceKinds.All.Contains(kind, StringComparer.Ordinal))
                throw new WorkflowOutcomePolicyException(at + ": evidence.kind must be terminal_event, delegate_result or failure_branch");
            var path = PolicyJson.OptionalString(evidence, "path", at + ".evidence");
            var exitBranch = PolicyJson.OptionalString(evidence, "exitBranch", at + ".evidence");
            WorkflowOutcomeScalar? equals = null;
            if (evidence.TryGetProperty("equals", out var eq) && eq.ValueKind != JsonValueKind.Null)
            {
                equals = eq.ValueKind switch
                {
                    JsonValueKind.String => new WorkflowOutcomeScalar(JsonValueKind.String, eq.GetString()!),
                    JsonValueKind.Number => new WorkflowOutcomeScalar(JsonValueKind.Number, eq.GetRawText()),
                    JsonValueKind.True or JsonValueKind.False => new WorkflowOutcomeScalar(eq.ValueKind, eq.GetRawText()),
                    _ => throw new WorkflowOutcomePolicyException(at + ": evidence.equals must be a string, number or boolean"),
                };
            }

            switch (kind)
            {
                case WorkflowOutcomeEvidenceKinds.TerminalEvent:
                    if (path is null || equals is null || exitBranch is not null)
                        throw new WorkflowOutcomePolicyException(at + ": terminal_event requires path and equals and no exitBranch");
                    // Terminate attributes (reason/details/identity) are caller free text, never a cause.
                    if (terminals.Contains(WorkflowOutcomeTerminalTypes.Terminated))
                        throw new WorkflowOutcomePolicyException(at + ": terminal_event evidence cannot classify WorkflowExecutionTerminated");
                    break;
                case WorkflowOutcomeEvidenceKinds.DelegateResult:
                    if (path is null || equals is null || exitBranch is null)
                        throw new WorkflowOutcomePolicyException(at + ": delegate_result requires path, equals and exitBranch");
                    break;
                default:
                    if (exitBranch is null || path is not null || equals is not null)
                        throw new WorkflowOutcomePolicyException(at + ": failure_branch requires exitBranch only");
                    break;
            }
            if (path is not null && !WorkflowOutcomeJsonPointer.IsValid(path))
                throw new WorkflowOutcomePolicyException(at + ": evidence.path must be an RFC 6901 JSON pointer");
            if (exitBranch is not null && !StepPattern.IsMatch(exitBranch))
                throw new WorkflowOutcomePolicyException(at + ": evidence.exitBranch must be one exact step name");

            foreach (var terminal in terminals)
            {
                var key = string.Join('\u001f', type, hash ?? "*", terminal, kind, path ?? "", equals?.Kind.ToString() ?? "", equals?.Raw ?? "", exitBranch ?? "");
                if (!semantic.Add(key)) throw new WorkflowOutcomePolicyException(at + ": duplicates the evidence of another rule");
            }

            rules.Add(new WorkflowOutcomeClassifierRule(id, type, commit, hash, terminals, kind, path, equals, exitBranch, classification, reason));
        }

        return new WorkflowOutcomeClassifierPolicy { Hash = WorkflowOutcomeHashes.Sha256Hex(bytes), Rules = rules };
    }

    public IEnumerable<WorkflowOutcomeClassifierRule> RulesFor(string workflowType, string terminalType) =>
        Rules.Where(r => r.WorkflowType == workflowType && r.TerminalEventTypes.Contains(terminalType, StringComparer.Ordinal));

    private static bool AllowedOutcome(string terminal, string classification, string reason) => classification switch
    {
        WorkflowOutcomeClassifications.IntentionalNonfailure => reason == WorkflowOutcomeReasonCodes.DeliberateDisposition,
        WorkflowOutcomeClassifications.ConfirmedFailure => terminal switch
        {
            WorkflowOutcomeTerminalTypes.Completed => WorkflowOutcomeReasonCodes.IsBusiness(reason),
            WorkflowOutcomeTerminalTypes.Canceled => reason == WorkflowOutcomeReasonCodes.FailureCausedCancel,
            WorkflowOutcomeTerminalTypes.Terminated => reason == WorkflowOutcomeReasonCodes.FailureCausedTerminate,
            _ => false,
        },
        _ => false,
    };
}

/// <summary>ownership.json v1, validated against the active ProjectContext names.</summary>
public sealed class WorkflowOutcomeOwnershipPolicy
{
    public required string Hash { get; init; }
    public required string DefaultCoordinator { get; init; }

    /// <summary>Key: namespace + "\u001f" + normalized owner/name.</summary>
    public required IReadOnlyDictionary<string, string> RepoProjects { get; init; }
    public required IReadOnlyDictionary<string, string> NamespaceProjects { get; init; }

    /// <summary>Key: namespace + "\u001f" + project.</summary>
    public required IReadOnlyDictionary<string, string> Rules { get; init; }

    private static readonly Regex AgentPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex NamespacePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly string[] RootKeys = ["schemaVersion", "defaultCoordinator", "repoProjects", "namespaceProjects", "rules"];

    /// <param name="bytes">Exact file bytes; the policy hash is over them.</param>
    /// <param name="projects">Every ProjectContext (name, isActive) from the DB.</param>
    public static WorkflowOutcomeOwnershipPolicy Parse(byte[] bytes, IReadOnlyCollection<(string Name, bool IsActive)> projects)
    {
        using var doc = PolicyJson.Parse(bytes, "ownership");
        var root = doc.RootElement;
        PolicyJson.ExactKeys(root, RootKeys, RootKeys, "ownership");
        if (!PolicyJson.IsInt(root.GetProperty("schemaVersion"), 1))
            throw new WorkflowOutcomePolicyException("ownership: schemaVersion must be 1");
        var defaultCoordinator = PolicyJson.String(root, "defaultCoordinator", "ownership");
        if (!AgentPattern.IsMatch(defaultCoordinator)) throw new WorkflowOutcomePolicyException("ownership: invalid defaultCoordinator");

        var active = projects.Where(p => p.IsActive).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var known = projects.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var repoProjects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (item, at) in Items(root, "repoProjects"))
        {
            PolicyJson.ExactKeys(item, ["namespace", "repo", "project"], ["namespace", "repo", "project"], at);
            var ns = Namespace(item, at);
            var rawRepo = item.GetProperty("repo");
            var repo = (rawRepo.ValueKind == JsonValueKind.String ? WorkflowOutcomeOwnership.NormalizeRepo(rawRepo.GetString()) : null)
                ?? throw new WorkflowOutcomePolicyException(at + ": repo must be owner/name");
            var project = Project(item, at, active, known);
            if (!repoProjects.TryAdd(ns + "\u001f" + repo, project))
                throw new WorkflowOutcomePolicyException(at + ": duplicate namespace+repo");
        }

        var namespaceProjects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (item, at) in Items(root, "namespaceProjects"))
        {
            PolicyJson.ExactKeys(item, ["namespace", "project"], ["namespace", "project"], at);
            var ns = Namespace(item, at);
            var project = Project(item, at, active, known);
            if (!namespaceProjects.TryAdd(ns, project))
                throw new WorkflowOutcomePolicyException(at + ": duplicate namespace");
        }

        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (item, at) in Items(root, "rules"))
        {
            PolicyJson.ExactKeys(item, ["namespace", "project", "coordinator"], ["namespace", "project", "coordinator"], at);
            var ns = Namespace(item, at);
            var project = Project(item, at, active, known);
            var coordinator = PolicyJson.String(item, "coordinator", at);
            if (!AgentPattern.IsMatch(coordinator)) throw new WorkflowOutcomePolicyException(at + ": invalid coordinator");
            if (!rules.TryAdd(ns + "\u001f" + project, coordinator))
                throw new WorkflowOutcomePolicyException(at + ": duplicate namespace+project");
        }

        return new WorkflowOutcomeOwnershipPolicy
        {
            Hash = WorkflowOutcomeHashes.Sha256Hex(bytes),
            DefaultCoordinator = defaultCoordinator,
            RepoProjects = repoProjects,
            NamespaceProjects = namespaceProjects,
            Rules = rules,
        };

        static IEnumerable<(JsonElement Item, string At)> Items(JsonElement root, string name)
        {
            var array = root.GetProperty(name);
            if (array.ValueKind != JsonValueKind.Array) throw new WorkflowOutcomePolicyException("ownership: " + name + " must be an array");
            var i = 0;
            foreach (var item in array.EnumerateArray()) yield return (item, "ownership: " + name + "[" + i++ + "]");
        }

        static string Namespace(JsonElement item, string at)
        {
            var ns = PolicyJson.String(item, "namespace", at);
            if (!NamespacePattern.IsMatch(ns)) throw new WorkflowOutcomePolicyException(at + ": invalid namespace");
            return ns;
        }

        static string Project(JsonElement item, string at, HashSet<string> active, HashSet<string> known)
        {
            var project = PolicyJson.String(item, "project", at);
            if (!known.Contains(project)) throw new WorkflowOutcomePolicyException(at + ": unknown project '" + project + "'");
            if (!active.Contains(project)) throw new WorkflowOutcomePolicyException(at + ": inactive project '" + project + "'");
            return project;
        }
    }
}

public sealed record WorkflowOutcomePolicies(WorkflowOutcomeClassifierPolicy Classifiers, WorkflowOutcomeOwnershipPolicy Ownership)
{
    /// <summary>Recorded at enrollment (fits the 200-char checkpoint column).</summary>
    public string Fingerprint => "classifiers:" + Classifiers.Hash + ";ownership:" + Ownership.Hash;
}

/// <summary>Loads both policy files once (startup) and keeps the validated result.</summary>
public sealed class WorkflowOutcomePolicyLoader(
    IOptions<WorkflowOutcomeOptions> options,
    IServiceScopeFactory scopes,
    ILogger<WorkflowOutcomePolicyLoader> logger)
{
    private WorkflowOutcomePolicies? _current;

    /// <summary>The loaded policies; throws when <see cref="LoadAsync"/> has not succeeded.</summary>
    public WorkflowOutcomePolicies Current => _current ?? throw new InvalidOperationException("workflow outcome policies not loaded");

    public async Task<WorkflowOutcomePolicies> LoadAsync(CancellationToken ct)
    {
        if (_current is not null) return _current;
        var opts = options.Value;
        var classifierBytes = await ReadAsync(opts.ClassifiersFile, "WorkflowOutcomes:ClassifiersFile", ct);
        var ownerBytes = await ReadAsync(opts.OwnersFile, "WorkflowOutcomes:OwnersFile", ct);

        List<(string Name, bool IsActive)> projects;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>()
                ?? throw new WorkflowOutcomePolicyException("ownership: database unavailable, project names cannot be validated");
            projects = (await db.ProjectContexts.AsNoTracking().Select(p => new { p.Name, p.IsActive }).ToListAsync(ct))
                .Select(p => (p.Name, p.IsActive)).ToList();
        }

        var policies = new WorkflowOutcomePolicies(
            WorkflowOutcomeClassifierPolicy.Parse(classifierBytes),
            WorkflowOutcomeOwnershipPolicy.Parse(ownerBytes, projects));

        var observed = opts.NamespaceList().ToHashSet(StringComparer.Ordinal);
        var unobserved = policies.Ownership.RepoProjects.Keys.Select(k => k.Split('\u001f')[0])
            .Concat(policies.Ownership.NamespaceProjects.Keys)
            .Concat(policies.Ownership.Rules.Keys.Select(k => k.Split('\u001f')[0]))
            .Where(ns => !observed.Contains(ns)).Distinct(StringComparer.Ordinal).ToArray();
        if (unobserved.Length > 0)
            logger.LogWarning("workflow_outcome_policy_unobserved_namespaces count={Count}", unobserved.Length);

        logger.LogInformation("workflow_outcome_policies_loaded classifiers={ClassifierHash} rules={Rules} ownership={OwnershipHash}",
            policies.Classifiers.Hash, policies.Classifiers.Rules.Count, policies.Ownership.Hash);
        _current = policies;
        return policies;
    }

    private static async Task<byte[]> ReadAsync(string path, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new WorkflowOutcomePolicyException(key + " is required");
        try { return await File.ReadAllBytesAsync(path, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing file bind mount becomes a directory; both are unreadable, never "no policy".
            throw new WorkflowOutcomePolicyException(key + " is not a readable file (" + ex.GetType().Name + ")");
        }
    }
}

// ─── Classification ─────────────────────────────────────────────────────────

/// <summary>
/// One classification of one exact run. <see cref="GapReason"/> non-null means the run is a
/// coverage gap (classifier/history evidence missing), visible in health, never a failure claim.
/// </summary>
public sealed record WorkflowOutcomeVerdict(
    string Classification,
    string ReasonCode,
    string EvidenceState,
    string? ClassifierId,
    IReadOnlyList<string> CauseRefs,
    IReadOnlyList<string> SourceTaskRefs,
    string? TaskOwner,
    string? GapReason,
    string? ClassifierHash = null);

/// <summary>A change to a <c>successor_pending</c> predecessor once its successor is classified.</summary>
public sealed record WorkflowOutcomePredecessorChange(string Classification, string ReasonCode, string EvidenceState, bool ShareIncident);

public static class WorkflowOutcomeClassifier
{
    /// <summary>
    /// Pure and deterministic: the same facts and policy always give the same verdict. Implements
    /// the §1 table: Failed/TimedOut are failures regardless of result; Canceled/Terminated are
    /// failures or deliberate only through an exact rule, otherwise cause_unresolved; Completed is a
    /// business failure only through an exact loaded-hash rule, otherwise not a failure.
    /// </summary>
    public static WorkflowOutcomeVerdict Classify(WorkflowOutcomeRunFacts facts, WorkflowOutcomeClassifierPolicy policy) =>
        // The policy hash is stored with the verdict so a pending row is re-read only after the
        // policy actually changes, not on every cycle.
        ClassifyUnderPolicy(facts, policy) with { ClassifierHash = policy.Hash };

    private static WorkflowOutcomeVerdict ClassifyUnderPolicy(WorkflowOutcomeRunFacts facts, WorkflowOutcomeClassifierPolicy policy)
    {
        var t = facts.Terminal;
        if (!facts.HistoryComplete || t is null)
            return Pending(WorkflowOutcomeReasonCodes.HistoryIncomplete, WorkflowOutcomeEvidenceStates.Pending,
                t is null ? [] : [EventRef(t.EventId)], "history_incomplete");

        var terminalRef = EventRef(t.EventId);
        var owner = TaskOwner(facts, null);
        switch (t.EventType)
        {
            case WorkflowOutcomeTerminalTypes.Failed:
            case WorkflowOutcomeTerminalTypes.TimedOut:
            {
                var retrying = t.RetryState == "InProgress" && t.NewExecutionRunId is not null;
                return new WorkflowOutcomeVerdict(
                    WorkflowOutcomeClassifications.ConfirmedFailure,
                    t.EventType == WorkflowOutcomeTerminalTypes.Failed ? WorkflowOutcomeReasonCodes.WorkflowFailed : WorkflowOutcomeReasonCodes.WorkflowTimedOut,
                    retrying ? WorkflowOutcomeEvidenceStates.SuccessorPending : WorkflowOutcomeEvidenceStates.Complete,
                    null, CauseRefs(terminalRef, owner), TaskRefs(owner), owner.Delegate?.Target, null);
            }

            case WorkflowOutcomeTerminalTypes.ContinuedAsNew:
                // Retries and cron runs after a failure close with Failed/TimedOut plus a new run id;
                // a continue-as-new transition itself is never a failure.
                return NonFailure(WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeReasonCodes.ContinuedAsNew,
                    WorkflowOutcomeEvidenceStates.Complete, terminalRef);

            case WorkflowOutcomeTerminalTypes.Canceled:
            case WorkflowOutcomeTerminalTypes.Terminated:
                return ClassifyAbort(facts, policy, t, terminalRef);

            case WorkflowOutcomeTerminalTypes.Completed:
                return ClassifyCompleted(facts, policy, t, terminalRef);

            default:
                return Pending(WorkflowOutcomeReasonCodes.HistoryIncomplete, WorkflowOutcomeEvidenceStates.Unverified, [terminalRef], "unknown_terminal_event");
        }
    }

    private static WorkflowOutcomeVerdict ClassifyAbort(WorkflowOutcomeRunFacts facts, WorkflowOutcomeClassifierPolicy policy,
        WorkflowOutcomeTerminal t, string terminalRef)
    {
        // Only exact rules can attribute an abort; reason/identity strings never can.
        var rules = policy.RulesFor(facts.WorkflowType, t.EventType)
            .Where(r => r.DefinitionHash is null || r.DefinitionHash == facts.DefinitionHash).ToArray();
        var (matches, undecidable) = Evaluate(rules, facts, t);
        var outcomes = matches.Select(m => (m.Rule.Classification, m.Rule.ReasonCode)).Distinct().ToArray();
        var evidenceState = undecidable ? WorkflowOutcomeEvidenceStates.Quarantined : WorkflowOutcomeEvidenceStates.Complete;
        if (outcomes.Length == 0)
            return new WorkflowOutcomeVerdict(WorkflowOutcomeClassifications.CauseUnresolved, WorkflowOutcomeReasonCodes.AbortCauseUnknown,
                evidenceState, null, [terminalRef], TaskRefs(TaskOwner(facts, null)), TaskOwner(facts, null).Delegate?.Target, null);
        if (outcomes.Length > 1 || undecidable)
            return new WorkflowOutcomeVerdict(WorkflowOutcomeClassifications.CauseUnresolved, WorkflowOutcomeReasonCodes.AbortCauseConflicting,
                evidenceState, null, [terminalRef, .. matches.SelectMany(m => m.Refs)], TaskRefs(TaskOwner(facts, null)), TaskOwner(facts, null).Delegate?.Target, null);
        return FromMatch(matches[0], facts, terminalRef);
    }

    private static WorkflowOutcomeVerdict ClassifyCompleted(WorkflowOutcomeRunFacts facts, WorkflowOutcomeClassifierPolicy policy,
        WorkflowOutcomeTerminal t, string terminalRef)
    {
        var typeRules = policy.RulesFor(facts.WorkflowType, t.EventType).ToArray();
        // No rules for this type: its business outcome is not covered. Never an assumed success and
        // never a failure claim; the run itself carries no failure evidence, so it is nonfailure
        // bookkeeping with the normal TTL while the durable per-type gap stays open.
        if (typeRules.Length == 0)
            return Pending(WorkflowOutcomeReasonCodes.ClassifierUncoveredType, WorkflowOutcomeEvidenceStates.Complete, [terminalRef], UncoveredTypeGap);

        {
            // A run that loaded a definition is covered only by rules pinned to that exact hash. A
            // compiled run — complete history with no LoadWorkflowDefinition scheduled at all — only
            // by rules pinned by workflowType + sourceCommit without a hash. Neither stands in for the
            // other, and an unreadable or unresolved definition is never treated as compiled; any
            // other snapshot is a visible gap, not an assumed success.
            var compiled = facts.HistoryComplete && !facts.DefinitionScheduled;
            var pinned = compiled
                ? typeRules.Where(r => r.DefinitionHash is null).ToArray()
                : typeRules.Where(r => facts.DefinitionHash is not null && r.DefinitionHash == facts.DefinitionHash).ToArray();
            if (pinned.Length == 0)
                return Pending(WorkflowOutcomeReasonCodes.ClassifierMissing,
                    facts.DefinitionUnsupported ? WorkflowOutcomeEvidenceStates.Quarantined : WorkflowOutcomeEvidenceStates.Complete,
                    [terminalRef], "classifier_missing");

            var (matches, undecidable) = Evaluate(pinned, facts, t);
            var outcomes = matches.Select(m => (m.Rule.Classification, m.Rule.ReasonCode)).Distinct().ToArray();
            if (undecidable)
                return Pending(WorkflowOutcomeReasonCodes.ClassifierMissing, WorkflowOutcomeEvidenceStates.Quarantined, [terminalRef], "evidence_unreadable");
            if (outcomes.Length > 1)
                return Pending(WorkflowOutcomeReasonCodes.ClassifierMissing, WorkflowOutcomeEvidenceStates.Complete,
                    [terminalRef, .. matches.SelectMany(m => m.Refs)], "classifier_conflict");
            if (outcomes.Length == 1) return FromMatch(matches[0], facts, terminalRef);
        }

        // No failure proof. Null alone proves nothing either way.
        var result = t.Result;
        if (result is null || result.IsNull)
            return NonFailure(WorkflowOutcomeClassifications.InsufficientEvidence, WorkflowOutcomeReasonCodes.NullResultOnly, WorkflowOutcomeEvidenceStates.Complete, terminalRef);
        if (!result.Supported || result.Json is null)
            return NonFailure(WorkflowOutcomeClassifications.InsufficientEvidence, WorkflowOutcomeReasonCodes.NullResultOnly, WorkflowOutcomeEvidenceStates.Quarantined, terminalRef);
        using (var doc = JsonDocument.Parse(result.Json))
        {
            var root = doc.RootElement;
            var empty = root.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(root.GetString()),
                JsonValueKind.Object => !root.EnumerateObject().Any(),
                JsonValueKind.Array => root.GetArrayLength() == 0,
                JsonValueKind.Null => true,
                _ => false,
            };
            if (empty)
                return NonFailure(WorkflowOutcomeClassifications.InsufficientEvidence, WorkflowOutcomeReasonCodes.NullResultOnly, WorkflowOutcomeEvidenceStates.Complete, terminalRef);
        }
        return NonFailure(WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeReasonCodes.NormalCompletion, WorkflowOutcomeEvidenceStates.Complete, terminalRef);
    }

    /// <summary>
    /// The predecessor update once a successor of a <c>successor_pending</c> run is classified, or
    /// null for no change. A successful retry/closure makes the earlier attempt retry_succeeded; a
    /// final failure or unresolved closure makes it final (coalesced under one incident).
    /// </summary>
    public static WorkflowOutcomePredecessorChange? ResolvePredecessor(string predecessorClassification, string predecessorReason, WorkflowOutcomeVerdict successor)
    {
        if (successor.Classification == WorkflowOutcomeClassifications.ExcludedNonfailure && successor.ReasonCode == WorkflowOutcomeReasonCodes.ContinuedAsNew)
            return null; // the chain continues; wait for its final run
        if (successor.Classification == WorkflowOutcomeClassifications.ConfirmedFailure && successor.EvidenceState == WorkflowOutcomeEvidenceStates.SuccessorPending)
            return new WorkflowOutcomePredecessorChange(predecessorClassification, predecessorReason, WorkflowOutcomeEvidenceStates.SuccessorPending, true);
        if (successor.Classification == WorkflowOutcomeClassifications.ClassificationPending
            && successor.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierUncoveredType)
            // An uncovered type closed Completed with no failure evidence: the retry succeeded at
            // the workflow level, under the per-type coverage gap.
            return new WorkflowOutcomePredecessorChange(WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeReasonCodes.RetrySucceeded,
                WorkflowOutcomeEvidenceStates.Complete, false);
        if (successor.Classification == WorkflowOutcomeClassifications.ClassificationPending
            && successor.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierMissing)
            // The chain closed Completed but its business outcome is not covered: neither a
            // provable retry success nor a final failure. The predecessor joins the visible gap.
            return new WorkflowOutcomePredecessorChange(WorkflowOutcomeClassifications.ClassificationPending, WorkflowOutcomeReasonCodes.ClassifierMissing,
                WorkflowOutcomeEvidenceStates.Complete, false);
        if (successor.Classification is WorkflowOutcomeClassifications.ExcludedNonfailure
            or WorkflowOutcomeClassifications.InsufficientEvidence or WorkflowOutcomeClassifications.IntentionalNonfailure)
            return new WorkflowOutcomePredecessorChange(WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeReasonCodes.RetrySucceeded,
                WorkflowOutcomeEvidenceStates.Complete, false);
        // Final failure, unresolved abort or pending classification: the retry did not provably succeed.
        return new WorkflowOutcomePredecessorChange(predecessorClassification, predecessorReason, WorkflowOutcomeEvidenceStates.Complete,
            successor.Classification == WorkflowOutcomeClassifications.ConfirmedFailure);
    }

    /// <summary>Mirror of the batcher predicate: the only rows that may ever enter a coordinator batch.</summary>
    public static bool IsEnqueueEligible(WorkflowOutcomeRun run, string coordinator) =>
        run.Classification == WorkflowOutcomeClassifications.ConfirmedFailure
        && !run.Quarantined
        && run.ResolvedAt is null
        && run.EvidenceState != WorkflowOutcomeEvidenceStates.SuccessorPending
        && run.Coordinator == coordinator;

    // ─── Evidence evaluation ────────────────────────────────────────────────

    private sealed record Match(WorkflowOutcomeClassifierRule Rule, WorkflowOutcomeDelegate? Delegate, IReadOnlyList<string> Refs);

    private static (List<Match> Matches, bool Undecidable) Evaluate(IEnumerable<WorkflowOutcomeClassifierRule> rules,
        WorkflowOutcomeRunFacts facts, WorkflowOutcomeTerminal t)
    {
        var matches = new List<Match>();
        var undecidable = false;
        foreach (var rule in rules)
        {
            switch (rule.EvidenceKind)
            {
                case WorkflowOutcomeEvidenceKinds.TerminalEvent:
                {
                    if (rule.Path!.StartsWith("/result", StringComparison.Ordinal) && t.Result is { Supported: false })
                    { undecidable = true; break; }
                    using var doc = TerminalDocument(t);
                    if (WorkflowOutcomeJsonPointer.TryResolve(doc.RootElement, rule.Path, out var value) && rule.EqualsValue!.Matches(value))
                        matches.Add(new Match(rule, null, [EventRef(t.EventId)]));
                    break;
                }
                case WorkflowOutcomeEvidenceKinds.FailureBranch:
                {
                    var d = LastDelegate(facts, rule.ExitBranch!, out var ambiguous);
                    if (d is not null) matches.Add(new Match(rule, d, [EventRef(d.ScheduledEventId), "task:" + d.TaskId]));
                    else if (ambiguous) undecidable = true;
                    break;
                }
                default:
                {
                    var d = LastDelegate(facts, rule.ExitBranch!, out var ambiguous);
                    if (d is null) { if (ambiguous) undecidable = true; break; }
                    var document = DelegateDocument(d, out var unreadable);
                    if (unreadable) { undecidable = true; break; }
                    if (document is null) break;
                    using (document)
                    {
                        if (WorkflowOutcomeJsonPointer.TryResolve(document.RootElement, rule.Path!, out var value) && rule.EqualsValue!.Matches(value))
                            matches.Add(new Match(rule, d, d.CloseEventId is { } closeId
                                ? [EventRef(d.ScheduledEventId), EventRef(closeId), "task:" + d.TaskId]
                                : [EventRef(d.ScheduledEventId), "task:" + d.TaskId]));
                    }
                    break;
                }
            }
        }
        return (matches, undecidable);
    }

    /// <summary>The last scheduled delegate of the exact step; ambiguous when some delegate's taskId could not be decoded.</summary>
    private static WorkflowOutcomeDelegate? LastDelegate(WorkflowOutcomeRunFacts facts, string step, out bool ambiguous)
    {
        var taskId = facts.WorkflowId + "/" + step;
        var found = facts.Delegates.LastOrDefault(d => d.TaskId == taskId);
        ambiguous = found is null && facts.Delegates.Any(d => d.ArgumentsUnsupported);
        return found;
    }

    private static JsonDocument TerminalDocument(WorkflowOutcomeTerminal t)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("eventType", t.EventType);
            w.WritePropertyName("attributes");
            using (var attrs = JsonDocument.Parse(t.AttributesJson)) attrs.RootElement.WriteTo(w);
            w.WritePropertyName("result");
            if (t.Result is { Supported: true, Json: { } json })
                using (var result = JsonDocument.Parse(json)) result.RootElement.WriteTo(w);
            else w.WriteNullValue();
            w.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    private static JsonDocument? DelegateDocument(WorkflowOutcomeDelegate d, out bool unreadable)
    {
        unreadable = false;
        switch (d.Outcome)
        {
            case "completed":
                if (d.Result is null || d.Result.IsNull) return null;
                if (!d.Result.Supported || d.Result.Json is null) { unreadable = true; return null; }
                return JsonDocument.Parse(d.Result.Json);
            case "failed":
            case "timed_out":
            case "canceled":
                return JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["activityFailure"] = new Dictionary<string, string?> { ["eventType"] = d.CloseEventType, ["failureType"] = d.FailureType },
                }));
            default:
                return null;
        }
    }

    private static WorkflowOutcomeVerdict FromMatch(Match match, WorkflowOutcomeRunFacts facts, string terminalRef)
    {
        // A matched delegate_result step is the original task; a matched branch is a notifier and
        // never becomes the task owner.
        var owner = TaskOwner(facts, match.Rule.EvidenceKind == WorkflowOutcomeEvidenceKinds.DelegateResult ? match.Delegate : null);
        var refs = CauseRefs(terminalRef, owner).Concat(match.Refs).Distinct(StringComparer.Ordinal).ToArray();
        var taskRefs = TaskRefs(owner);
        if (match.Rule.Classification == WorkflowOutcomeClassifications.IntentionalNonfailure)
            return new WorkflowOutcomeVerdict(match.Rule.Classification, match.Rule.ReasonCode, WorkflowOutcomeEvidenceStates.Complete,
                match.Rule.Id, refs, taskRefs, null, null);
        return new WorkflowOutcomeVerdict(match.Rule.Classification, match.Rule.ReasonCode, WorkflowOutcomeEvidenceStates.Complete,
            match.Rule.Id, refs, taskRefs, owner.Delegate?.Target, null);
    }

    private sealed record Owner(WorkflowOutcomeDelegate? Delegate);

    /// <summary>
    /// The original task owner: the matched task step, else the last delegate with a typed failure
    /// (failed/timed-out activity or a "failed" AgentTaskResult), else the delegate still open when
    /// the run closed, else none (unassigned).
    /// </summary>
    private static Owner TaskOwner(WorkflowOutcomeRunFacts facts, WorkflowOutcomeDelegate? matched)
    {
        if (matched is not null) return new Owner(matched);
        var failed = facts.Delegates.LastOrDefault(d => d.Outcome is "failed" or "timed_out"
            || d.Outcome == "completed" && string.Equals(d.ResultStatus?.Trim(), "failed", StringComparison.OrdinalIgnoreCase));
        if (failed is not null) return new Owner(failed);
        return new Owner(facts.Delegates.LastOrDefault(d => d.Outcome == "open"));
    }

    private static IReadOnlyList<string> CauseRefs(string terminalRef, Owner owner)
    {
        var refs = new List<string> { terminalRef };
        if (owner.Delegate is { } d)
        {
            refs.Add(EventRef(d.ScheduledEventId));
            if (d.CloseEventId is { } closeId) refs.Add(EventRef(closeId));
        }
        return refs;
    }

    private static IReadOnlyList<string> TaskRefs(Owner owner) =>
        owner.Delegate?.TaskId is { } taskId ? ["task:" + taskId] : [];

    private static WorkflowOutcomeVerdict NonFailure(string classification, string reason, string evidenceState, string terminalRef) =>
        new(classification, reason, evidenceState, null, [terminalRef], [], null, null);

    /// <summary>Gap reason of an uncovered type; its health key is per namespace and type.</summary>
    public const string UncoveredTypeGap = "type_uncovered";

    private static WorkflowOutcomeVerdict Pending(string reason, string evidenceState, IReadOnlyList<string> refs, string gap) =>
        new(WorkflowOutcomeClassifications.ClassificationPending, reason, evidenceState, null, refs, [], null, gap);

    internal static string EventRef(long eventId) => "event:" + eventId.ToString(CultureInfo.InvariantCulture);
}

// ─── Ownership ──────────────────────────────────────────────────────────────

/// <summary>A parent run whose ownership was derived from its own verified evidence.</summary>
public sealed record WorkflowOutcomeParentOwnership(
    string Namespace, string WorkflowId, string RunId, string? ProjectName, string? Coordinator, bool Verified);

/// <summary>
/// Derived ownership. <see cref="ProjectSource"/>: input_repo, parent, namespace_map or unknown;
/// <see cref="UnknownReason"/> says why the project is unknown (null when known);
/// <see cref="CoordinatorSource"/>: rule, parent or default.
/// </summary>
public sealed record WorkflowOutcomeOwnershipResult(
    string? ProjectName,
    string ProjectSource,
    string? UnknownReason,
    string? Repo,
    string Coordinator,
    string CoordinatorSource,
    IReadOnlyList<string> EvidenceRefs,
    string PolicyHash)
{
    public string EvidenceJson() => JsonSerializer.Serialize(new
    {
        source = ProjectSource,
        unknownReason = UnknownReason,
        repo = Repo,
        coordinatorSource = CoordinatorSource,
        refs = EvidenceRefs,
        policyHash = PolicyHash,
    });
}

/// <summary>State of <c>input.Repo</c> in the decoded original Started input.</summary>
public enum WorkflowOutcomeRepoState { Absent, Valid, Invalid, Conflicting, Unreadable }

public static class WorkflowOutcomeOwnership
{
    private static readonly Regex RepoPattern = new(
        "^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,38}/[a-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

    /// <summary>Trimmed, lowercase, validated <c>owner/name</c>; null when invalid.</summary>
    public static string? NormalizeRepo(string? value)
    {
        if (value is null) return null;
        var repo = value.Trim().ToLowerInvariant();
        if (!RepoPattern.IsMatch(repo)) return null;
        var name = repo[(repo.IndexOf('/') + 1)..];
        return name is "." or ".." ? null : repo;
    }

    public static (WorkflowOutcomeRepoState State, string? Repo) ReadRepo(WorkflowOutcomePayload? input)
    {
        if (input is null || input.IsNull) return (WorkflowOutcomeRepoState.Absent, null);
        if (!input.Supported || input.Json is null) return (WorkflowOutcomeRepoState.Unreadable, null);
        using var doc = JsonDocument.Parse(input.Json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return (WorkflowOutcomeRepoState.Absent, null);
        // The engine's templates resolve input.Repo case-insensitively, so every spelling counts;
        // two different values are conflicting evidence.
        var values = doc.RootElement.EnumerateObject()
            .Where(p => string.Equals(p.Name, "Repo", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null)
            .Select(p => p.Value).ToArray();
        if (values.Length == 0) return (WorkflowOutcomeRepoState.Absent, null);
        var normalized = values.Select(v => v.ValueKind == JsonValueKind.String ? NormalizeRepo(v.GetString()) : null).ToArray();
        if (normalized.Any(n => n is null)) return (WorkflowOutcomeRepoState.Invalid, null);
        if (normalized.Distinct(StringComparer.Ordinal).Count() > 1) return (WorkflowOutcomeRepoState.Conflicting, null);
        return (WorkflowOutcomeRepoState.Valid, normalized[0]);
    }

    /// <summary>
    /// Project: decoded original input.Repo → exact namespace+repo map (present but invalid or
    /// unmapped → unknown, no fallback); Repo absent → verified parent's derived project → explicit
    /// namespace map → unknown. Never input.Project, workflow type or agent assignment.
    /// Coordinator: exact namespace+project rule → verified same-project parent → default.
    /// </summary>
    public static WorkflowOutcomeOwnershipResult Resolve(WorkflowOutcomeRunFacts facts, WorkflowOutcomeOwnershipPolicy policy,
        WorkflowOutcomeParentOwnership? parent)
    {
        var ns = facts.Namespace;
        var refs = new List<string> { facts.RunRef, facts.StartedEventRef };
        string? project = null;
        string source = "unknown";
        string? unknownReason = null;
        var (state, repo) = ReadRepo(facts.Input);
        switch (state)
        {
            case WorkflowOutcomeRepoState.Valid:
                if (policy.RepoProjects.TryGetValue(ns + "\u001f" + repo, out var mapped)) { project = mapped; source = "input_repo"; }
                else unknownReason = "repo_unmapped";
                break;
            case WorkflowOutcomeRepoState.Invalid:
                unknownReason = "repo_invalid";
                break;
            case WorkflowOutcomeRepoState.Conflicting:
                unknownReason = "repo_conflicting";
                break;
            case WorkflowOutcomeRepoState.Unreadable:
                unknownReason = "input_unreadable";
                break;
            default:
                if (parent is { Verified: true, ProjectName: { } parentProject }
                    && parent.WorkflowId == facts.ParentWorkflowId && parent.RunId == facts.ParentRunId)
                {
                    project = parentProject;
                    source = "parent";
                    refs.Add("run:" + parent.Namespace + "/" + parent.WorkflowId + "/" + parent.RunId);
                }
                else if (policy.NamespaceProjects.TryGetValue(ns, out var nsProject))
                {
                    project = nsProject;
                    source = "namespace_map";
                }
                else unknownReason = "no_mapping";
                break;
        }

        string coordinator;
        string coordinatorSource;
        if (project is not null && policy.Rules.TryGetValue(ns + "\u001f" + project, out var ruled))
        {
            coordinator = ruled;
            coordinatorSource = "rule";
        }
        else if (project is not null && parent is { Verified: true, Coordinator: { } parentCoordinator }
                 && parent.ProjectName == project && parent.WorkflowId == facts.ParentWorkflowId && parent.RunId == facts.ParentRunId)
        {
            coordinator = parentCoordinator;
            coordinatorSource = "parent";
        }
        else
        {
            coordinator = policy.DefaultCoordinator;
            coordinatorSource = "default";
        }

        return new WorkflowOutcomeOwnershipResult(project, source, unknownReason, repo, coordinator, coordinatorSource,
            refs.Distinct(StringComparer.Ordinal).ToArray(), policy.Hash);
    }
}

// ─── JSON helpers ───────────────────────────────────────────────────────────

/// <summary>RFC 6901 JSON pointers over <see cref="JsonElement"/> (case-sensitive, no wildcards).</summary>
public static class WorkflowOutcomeJsonPointer
{
    public static bool IsValid(string pointer)
    {
        if (pointer.Length == 0) return true;
        if (pointer[0] != '/') return false;
        for (var i = 0; i < pointer.Length; i++)
            if (pointer[i] == '~' && (i + 1 >= pointer.Length || pointer[i + 1] is not ('0' or '1'))) return false;
        return true;
    }

    public static bool TryResolve(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        if (!IsValid(pointer)) return false;
        if (pointer.Length == 0) return true;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!value.TryGetProperty(token, out var next)) return false;
                    value = next;
                    break;
                case JsonValueKind.Array:
                    if (token.Length == 0 || token.Length > 1 && token[0] == '0' || !token.All(char.IsAsciiDigit)
                        || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        || index >= value.GetArrayLength())
                        return false;
                    value = value[index];
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
}

internal static class PolicyJson
{
    public static JsonDocument Parse(byte[] bytes, string file)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (JsonException)
        {
            throw new WorkflowOutcomePolicyException(file + ": not valid JSON");
        }
    }

    /// <summary>Object with no duplicate or unknown keys and every required key.</summary>
    public static void ExactKeys(JsonElement element, string[] allowed, string[] required, string at)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new WorkflowOutcomePolicyException(at + ": must be an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new WorkflowOutcomePolicyException(at + ": duplicate key '" + p.Name + "'");
            if (!allowed.Contains(p.Name, StringComparer.Ordinal)) throw new WorkflowOutcomePolicyException(at + ": unknown key '" + p.Name + "'");
        }
        foreach (var r in required)
            if (!seen.Contains(r)) throw new WorkflowOutcomePolicyException(at + ": missing key '" + r + "'");
    }

    public static bool IsInt(JsonElement e, int expected) =>
        e.ValueKind == JsonValueKind.Number && e.GetRawText().All(char.IsAsciiDigit) && e.TryGetInt32(out var v) && v == expected;

    public static string String(JsonElement obj, string name, string at)
    {
        var e = obj.GetProperty(name);
        var value = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new WorkflowOutcomePolicyException(at + ": " + name + " must be a non-blank trimmed string");
        return value;
    }

    public static string? OptionalString(JsonElement obj, string name, string at)
    {
        if (!obj.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
        return String(obj, name, at);
    }
}
