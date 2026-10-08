using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;

namespace Fleet.Orchestrator.Services;

// ─── Names ────────────────────────────────────────────────────────────────────

/// <summary>The three gates an epic grant may decide, by canonical (lowercase) signal name.</summary>
public static class EpicGrantGates
{
    public const string DesignApproval = "design-approval";
    public const string MergeApproval = "merge-approval";
    public const string DocReview = "doc-review";

    public static readonly IReadOnlyList<string> All = [DesignApproval, MergeApproval, DocReview];

    /// <summary>The canonical name for <paramref name="name"/> (case-insensitive), or null.</summary>
    public static string? Canonical(string? name) =>
        name is null ? null : All.FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Refusal codes returned by the decision path (exact strings; part of the bridge contract).</summary>
public static class EpicGrantRefusal
{
    public const string Disabled = "disabled";
    public const string Caller = "caller";
    public const string BadRequest = "bad_request";
    public const string GrantInactive = "grant_inactive";
    public const string Expired = "expired";
    public const string ScopeTampered = "scope_tampered";
    public const string DriverNotRunning = "driver_not_running";
    public const string NotLinked = "not_linked";
    public const string DefinitionOutOfScope = "definition_out_of_scope";
    public const string GateOutOfScope = "gate_out_of_scope";
    public const string TargetOutOfScope = "target_out_of_scope";
    public const string VisibilityUnknown = "visibility_unknown";
    public const string AuthorIsDecider = "author_is_decider";
    public const string StaleVisit = "stale_visit";
    public const string StaleArtifact = "stale_artifact";
    public const string ScrubMissing = "scrub_missing";
    public const string AlreadyDecided = "already_decided";
    public const string Unknown = "unknown";
}

/// <summary>The <c>result</c> values of a decision response.</summary>
public static class EpicGrantDecisionResults
{
    public const string Sent = "sent";
    public const string SendFailed = "send_failed";
    public const string Refused = "refused";
}

// ─── Temporal port ────────────────────────────────────────────────────────────

/// <summary>What the decision path needs from one Temporal describe.</summary>
/// <param name="Status">Execution status name, e.g. <c>Running</c>.</param>
public sealed record EpicRunDescription(
    string Status,
    string RunId,
    string WorkflowType,
    DateTimeOffset? StartTime,
    string? ParentWorkflowId,
    string? ParentRunId,
    string? GateVisit,
    string? ReviewRef,
    string? ReviewScrub)
{
    public bool IsRunning => Status == nameof(WorkflowExecutionStatus.Running);
}

/// <summary>What the decision path reads from a run's first history page.</summary>
/// <param name="InputJson">The first workflow input payload as JSON text, or null when the run has none.</param>
/// <param name="DefinitionName">The <c>LoadWorkflowDefinition</c> result's <c>Name</c>, or null when absent.</param>
/// <param name="DefinitionVersion">The <c>LoadWorkflowDefinition</c> result's <c>Version</c>, or null when absent.</param>
public sealed record EpicRunHistory(string? InputJson, string? DefinitionName, int? DefinitionVersion);

/// <summary>
/// The Temporal calls an epic grant decision makes. Every call is bounded at 10 s by the
/// production implementation; a failure throws, and the caller turns it into <c>unknown</c>.
/// </summary>
public interface IEpicGrantTemporal
{
    /// <summary>
    /// Describes <paramref name="workflowId"/> at <paramref name="runId"/>, or its latest run when
    /// <paramref name="runId"/> is null. Null when Temporal reports the execution does not exist.
    /// </summary>
    Task<EpicRunDescription?> DescribeAsync(string @namespace, string workflowId, string? runId, CancellationToken ct);

    /// <summary>
    /// Reads the first history page of the exact run. Null when there is no page; throws when the
    /// page cannot be read or decoded.
    /// </summary>
    Task<EpicRunHistory?> ReadRunAsync(string @namespace, string workflowId, string runId, CancellationToken ct);

    /// <summary>Signals the exact run with one JSON payload.</summary>
    Task SignalAsync(string @namespace, string workflowId, string runId, string signalName, string payloadJson, CancellationToken ct);
}

/// <summary><see cref="IEpicGrantTemporal"/> over the shared <see cref="TemporalClientRegistry"/>.</summary>
public sealed class TemporalEpicGrantClient(TemporalClientRegistry registry) : IEpicGrantTemporal
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string LoadDefinitionActivity = "LoadWorkflowDefinition";

    public Task<EpicRunDescription?> DescribeAsync(string @namespace, string workflowId, string? runId, CancellationToken ct) =>
        BoundedAsync(ct, async token =>
        {
            var client = await ClientAsync(@namespace, token);
            try
            {
                var d = await client.GetWorkflowHandle(workflowId, runId: runId).DescribeAsync(
                    new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = token, Timeout = Bound } });
                return new EpicRunDescription(
                    Status: d.Status.ToString(),
                    RunId: d.RunId ?? "",
                    WorkflowType: d.WorkflowType ?? "",
                    StartTime: d.StartTime == default ? null : new DateTimeOffset(DateTime.SpecifyKind(d.StartTime, DateTimeKind.Utc)),
                    ParentWorkflowId: d.ParentId,
                    ParentRunId: d.ParentRunId,
                    GateVisit: Keyword(d.TypedSearchAttributes, "GateVisit"),
                    ReviewRef: Keyword(d.TypedSearchAttributes, "ReviewRef"),
                    ReviewScrub: Keyword(d.TypedSearchAttributes, "ReviewScrub"));
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
            {
                return null;
            }
        });

    public Task<EpicRunHistory?> ReadRunAsync(string @namespace, string workflowId, string runId, CancellationToken ct) =>
        BoundedAsync(ct, async token =>
        {
            var client = await ClientAsync(@namespace, token);
            var response = await client.WorkflowService.GetWorkflowExecutionHistoryAsync(
                new Temporalio.Api.WorkflowService.V1.GetWorkflowExecutionHistoryRequest
                {
                    Namespace = client.Options.Namespace,
                    Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = workflowId, RunId = runId },
                },
                new RpcOptions { CancellationToken = token, Timeout = Bound });

            var events = response?.History?.Events;
            if (events is null || events.Count == 0) return null;

            var started = events.FirstOrDefault(e => e.EventType == EventType.WorkflowExecutionStarted)
                ?.WorkflowExecutionStartedEventAttributes
                ?? throw new InvalidOperationException("first history page has no WorkflowExecutionStarted event");

            string? inputJson = null;
            if (started.Input?.Payloads_ is { Count: > 0 } payloads)
                inputJson = JsonPayload(payloads[0]);

            string? name = null;
            int? version = null;
            var scheduled = events.FirstOrDefault(e =>
                e.EventType == EventType.ActivityTaskScheduled
                && e.ActivityTaskScheduledEventAttributes?.ActivityType?.Name == LoadDefinitionActivity);
            if (scheduled is not null)
            {
                var completed = events.FirstOrDefault(e =>
                    e.EventType == EventType.ActivityTaskCompleted
                    && e.ActivityTaskCompletedEventAttributes?.ScheduledEventId == scheduled.EventId);
                if (completed?.ActivityTaskCompletedEventAttributes?.Result?.Payloads_ is { Count: > 0 } result
                    && JsonPayload(result[0]) is { } resultJson)
                {
                    using var doc = JsonDocument.Parse(resultJson);
                    if (EpicJson.Property(doc.RootElement, "Name") is { ValueKind: JsonValueKind.String } n)
                        name = n.GetString();
                    if (EpicJson.Property(doc.RootElement, "Version") is { ValueKind: JsonValueKind.Number } v
                        && v.TryGetInt32(out var parsed))
                        version = parsed;
                }
            }

            return new EpicRunHistory(inputJson, name, version);
        });

    public Task SignalAsync(string @namespace, string workflowId, string runId, string signalName, string payloadJson, CancellationToken ct) =>
        BoundedAsync<object?>(ct, async token =>
        {
            var client = await ClientAsync(@namespace, token);
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
            await client.GetWorkflowHandle(workflowId, runId: runId).SignalAsync(
                signalName, [payload],
                new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = token, Timeout = Bound } });
            return null;
        });

    private async Task<ITemporalClient> ClientAsync(string @namespace, CancellationToken ct) =>
        await registry.GetClientAsync(@namespace, ct)
        ?? throw new InvalidOperationException($"no Temporal client for namespace '{@namespace}'");

    /// <summary>Runs <paramref name="call"/> with a 10 s deadline that holds even if the SDK ignores the token.</summary>
    private static async Task<T> BoundedAsync<T>(CancellationToken ct, Func<CancellationToken, Task<T>> call)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Bound);
        return await call(cts.Token).WaitAsync(Bound, ct);
    }

    private static string? Keyword(SearchAttributeCollection attributes, string name)
    {
        try
        {
            return attributes.TryGetValue(SearchAttributeKey.CreateKeyword(name), out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The payload's JSON text; null for a <c>binary/null</c> payload; throws for anything else.</summary>
    private static string? JsonPayload(Temporalio.Api.Common.V1.Payload payload)
    {
        var encoding = payload.Metadata.TryGetValue("encoding", out var bytes) ? bytes.ToStringUtf8() : "";
        if (encoding == "binary/null") return null;
        if (!encoding.StartsWith("json/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"unsupported payload encoding '{encoding}'");
        return payload.Data.ToStringUtf8();
    }
}

// ─── API shapes (JSON camelCase through the minimal-API defaults) ─────────────

/// <summary>The bridge's forward body for <c>POST /api/epic-grants/{id}/decisions</c>.</summary>
public sealed record EpicGrantDecisionRequest(
    string? Namespace,
    string? WorkflowId,
    string? Gate,
    string? Decision,
    string? VisitId,
    string? ArtifactRef,
    string? Evidence,
    string? Caller);

/// <summary>The outcome of one decision request.</summary>
public sealed record EpicGrantDecisionResult(string Result, string? Reason, long? DecisionId)
{
    public static EpicGrantDecisionResult Refused(string reason) => new(EpicGrantDecisionResults.Refused, reason, null);
}

public class EpicGrantView
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public required string EffectiveStatus { get; init; }
    public required string ScopeSha256 { get; init; }
    public required string DriverNamespace { get; init; }
    public required string DriverWorkflowId { get; init; }
    public required string DriverRunId { get; init; }
    public required string CtoAgent { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime? RevokedAt { get; init; }
    public string? RevokeReason { get; init; }
}

public sealed class EpicGrantDetail : EpicGrantView
{
    public required JsonElement Scope { get; init; }
    public required IReadOnlyList<EpicGrantDecisionView> Decisions { get; init; }
}

public sealed class EpicGrantDecisionView
{
    public required long Id { get; init; }
    public required string GrantId { get; init; }
    public required string Namespace { get; init; }
    public required string WorkflowId { get; init; }
    public required string RunId { get; init; }
    public required string Gate { get; init; }
    public required string VisitId { get; init; }
    public required string ArtifactRef { get; init; }
    public required string Evidence { get; init; }
    public required string Caller { get; init; }
    public required string Status { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

public sealed class ScopeValidationReport
{
    public bool Valid { get; set; }
    public List<string> Errors { get; } = [];
    public string? ScopeSha256 { get; set; }
    public DriverReport? Driver { get; set; }
    public List<WorkflowReport> Workflows { get; } = [];
    public List<TargetReport> Targets { get; } = [];
    public DateTime? ExpiresAt { get; set; }

    /// <param name="Status">Temporal status name (<c>Running</c>, <c>Completed</c>, …), <c>not_found</c> or <c>unknown</c>.</param>
    public sealed record DriverReport(string Namespace, string WorkflowId, string RunId, string Status);

    /// <param name="Sha256">The value pinned in the scope.</param>
    /// <param name="StoredSha256">The hash of the stored (type, version) definition, or null when there is none.</param>
    /// <param name="Gates">Delegable gates the stored definition guards (<c>visitVar</c> + <c>delegatedGuard</c>).</param>
    public sealed record WorkflowReport(
        string Type, int Version, string Sha256, string? StoredSha256,
        bool HashMatches, bool DelegationCapable, IReadOnlyList<string> Gates);

    /// <param name="Visibility"><c>public</c>, <c>private</c> or <c>unknown</c> (denied repos are not read).</param>
    public sealed record TargetReport(string Repo, IReadOnlyList<long> Issues, bool AllowPublic, string Visibility, bool Denied);
}

public enum EpicGrantRevokeOutcome { Revoked, AlreadyRevoked, NotFound }

/// <param name="Grant">The stored grant, or null when <paramref name="Report"/> is invalid.</param>
public sealed record EpicGrantCreateResult(EpicGrantDetail? Grant, ScopeValidationReport Report);

// ─── Scope ────────────────────────────────────────────────────────────────────

internal sealed record EpicScopeDriver(string Namespace, string WorkflowId, string RunId);
internal sealed record EpicScopeTarget(string Repo, IReadOnlyList<long> Issues, bool AllowPublic);
internal sealed record EpicScopeWorkflow(string Type, int Version, string Sha256);
internal sealed record EpicScope(
    EpicScopeDriver Driver,
    IReadOnlyList<EpicScopeTarget> Targets,
    IReadOnlyList<string> Gates,
    IReadOnlyList<EpicScopeWorkflow> Workflows,
    DateTimeOffset ExpiresAt);

/// <summary>Everything the parser could read, plus every structural error it found.</summary>
internal sealed class EpicScopeParse
{
    public List<string> Errors { get; } = [];
    public EpicScopeDriver? Driver { get; set; }
    public List<EpicScopeTarget> Targets { get; } = [];
    public List<string> Gates { get; } = [];
    public List<EpicScopeWorkflow> Workflows { get; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The complete scope, only when there is no structural error.</summary>
    public EpicScope? Scope =>
        Errors.Count == 0 && Driver is not null && ExpiresAt is { } expires
            ? new EpicScope(Driver, Targets, Gates, Workflows, expires)
            : null;
}

/// <summary>
/// The strict structural reader of a grant scope (#436 §5). Only the documented fields are
/// allowed at every level, each at most once; arrays must be non-empty.
/// </summary>
internal static class EpicScopeParser
{
    private static readonly string[] RootKeys = ["driver", "targets", "gates", "workflows", "expiresAt"];
    private static readonly string[] DriverKeys = ["namespace", "workflowId", "runId"];
    private static readonly string[] TargetKeys = ["repo", "issues", "allowPublic"];
    private static readonly string[] WorkflowKeys = ["type", "version", "sha256"];
    private static readonly Regex RepoName = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static EpicScopeParse Parse(string text)
    {
        var result = new EpicScopeParse();
        var errors = result.Errors;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            errors.Add("scope is not valid JSON");
            return result;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("scope must be a JSON object");
                return result;
            }

            CheckKeys(root, "scope", RootKeys, errors);
            foreach (var key in RootKeys)
                if (!root.TryGetProperty(key, out _))
                    errors.Add($"scope: missing field '{key}'");

            if (root.TryGetProperty("driver", out var driver)) ParseDriver(driver, result);
            if (root.TryGetProperty("targets", out var targets)) ParseTargets(targets, result);
            if (root.TryGetProperty("gates", out var gates)) ParseGates(gates, result);
            if (root.TryGetProperty("workflows", out var workflows)) ParseWorkflows(workflows, result);
            if (root.TryGetProperty("expiresAt", out var expires))
            {
                if (expires.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed))
                    result.ExpiresAt = parsed.ToUniversalTime();
                else
                    errors.Add("expiresAt must be an ISO-8601 timestamp string");
            }
        }

        return result;
    }

    private static void ParseDriver(JsonElement driver, EpicScopeParse result)
    {
        var errors = result.Errors;
        if (driver.ValueKind != JsonValueKind.Object)
        {
            errors.Add("driver must be an object");
            return;
        }

        CheckKeys(driver, "driver", DriverKeys, errors);
        var ns = RequiredString(driver, "driver", "namespace", 64, errors);
        var workflowId = RequiredString(driver, "driver", "workflowId", 255, errors);
        var runId = RequiredString(driver, "driver", "runId", 36, errors);
        if (ns is not null && workflowId is not null && runId is not null)
            result.Driver = new EpicScopeDriver(ns, workflowId, runId);
    }

    private static void ParseTargets(JsonElement targets, EpicScopeParse result)
    {
        var errors = result.Errors;
        if (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() == 0)
        {
            errors.Add("targets must be a non-empty array");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var target in targets.EnumerateArray())
        {
            var path = $"targets[{index++}]";
            if (target.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path} must be an object");
                continue;
            }

            var before = errors.Count;
            CheckKeys(target, path, TargetKeys, errors);

            var repo = RequiredString(target, path, "repo", 200, errors);
            if (repo is not null && !RepoName.IsMatch(repo))
            {
                errors.Add($"{path}.repo must be 'owner/name'");
                repo = null;
            }
            else if (repo is not null && !seen.Add(repo))
            {
                errors.Add($"{path}.repo '{repo}' is listed more than once");
            }

            var issues = new List<long>();
            if (!target.TryGetProperty("issues", out var issuesElement))
                errors.Add($"{path}: missing field 'issues'");
            else if (issuesElement.ValueKind != JsonValueKind.Array || issuesElement.GetArrayLength() == 0)
                errors.Add($"{path}.issues must be a non-empty array");
            else
                foreach (var issue in issuesElement.EnumerateArray())
                {
                    if (issue.ValueKind == JsonValueKind.Number && issue.TryGetInt64(out var n) && n > 0)
                        issues.Add(n);
                    else
                        errors.Add($"{path}.issues must contain positive integers only");
                }

            var allowPublic = false;
            if (target.TryGetProperty("allowPublic", out var allow))
            {
                if (allow.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    allowPublic = allow.GetBoolean();
                else
                    errors.Add($"{path}.allowPublic must be a boolean");
            }

            if (errors.Count == before && repo is not null)
                result.Targets.Add(new EpicScopeTarget(repo, issues, allowPublic));
        }
    }

    private static void ParseGates(JsonElement gates, EpicScopeParse result)
    {
        var errors = result.Errors;
        if (gates.ValueKind != JsonValueKind.Array || gates.GetArrayLength() == 0)
        {
            errors.Add("gates must be a non-empty array");
            return;
        }

        foreach (var gate in gates.EnumerateArray())
        {
            var name = gate.ValueKind == JsonValueKind.String ? gate.GetString() : null;
            if (name is null || !EpicGrantGates.All.Contains(name, StringComparer.Ordinal))
                errors.Add($"gates may only contain {string.Join(", ", EpicGrantGates.All)}");
            else if (result.Gates.Contains(name, StringComparer.Ordinal))
                errors.Add($"gate '{name}' is listed more than once");
            else
                result.Gates.Add(name);
        }
    }

    private static void ParseWorkflows(JsonElement workflows, EpicScopeParse result)
    {
        var errors = result.Errors;
        if (workflows.ValueKind != JsonValueKind.Array || workflows.GetArrayLength() == 0)
        {
            errors.Add("workflows must be a non-empty array");
            return;
        }

        var index = 0;
        foreach (var workflow in workflows.EnumerateArray())
        {
            var path = $"workflows[{index++}]";
            if (workflow.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path} must be an object");
                continue;
            }

            var before = errors.Count;
            CheckKeys(workflow, path, WorkflowKeys, errors);
            var type = RequiredString(workflow, path, "type", 100, errors);

            var version = 0;
            if (!workflow.TryGetProperty("version", out var v))
                errors.Add($"{path}: missing field 'version'");
            else if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version) || version <= 0)
                errors.Add($"{path}.version must be a positive integer");

            var sha = RequiredString(workflow, path, "sha256", 64, errors);
            if (sha is not null && !Sha256.IsMatch(sha))
                errors.Add($"{path}.sha256 must be 64 hex characters");

            if (errors.Count != before || type is null || sha is null) continue;
            if (result.Workflows.Any(w => w.Type == type && w.Version == version))
            {
                errors.Add($"{path}: {type} version {version} is listed more than once");
                continue;
            }
            result.Workflows.Add(new EpicScopeWorkflow(type, version, sha));
        }
    }

    private static void CheckKeys(JsonElement obj, string path, string[] allowed, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                errors.Add($"{path}: unknown field '{property.Name}'");
            else if (!seen.Add(property.Name))
                errors.Add($"{path}: duplicate field '{property.Name}'");
        }
    }

    private static string? RequiredString(JsonElement obj, string path, string key, int maxLength, List<string> errors)
    {
        if (!obj.TryGetProperty(key, out var value))
        {
            errors.Add($"{path}: missing field '{key}'");
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{path}.{key} must be a non-empty string");
            return null;
        }

        if (text.Length > maxLength)
        {
            errors.Add($"{path}.{key} is longer than {maxLength} characters");
            return null;
        }

        return text;
    }
}

/// <summary>Generic JSON helpers shared by the scope, definition and run readers.</summary>
internal static class EpicJson
{
    /// <summary>
    /// <paramref name="name"/> on an object: the exact property first, else the first
    /// case-insensitive match — the same lookup the workflow engine's templates use.
    /// </summary>
    public static JsonElement? Property(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (obj.TryGetProperty(name, out var exact)) return exact;
        foreach (var property in obj.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    public static string? String(JsonElement obj, string name) =>
        Property(obj, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>A positive integer given as a JSON number or a numeric string.</summary>
    public static long? PositiveInteger(JsonElement obj, string name)
    {
        switch (Property(obj, name))
        {
            case { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var value) && value > 0:
                return value;
            case { ValueKind: JsonValueKind.String } s
                when long.TryParse(s.GetString()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                     && value > 0:
                return value;
            default:
                return null;
        }
    }

    /// <summary>The only marker a delegation-capable guard may use: the field the orchestrator sends.</summary>
    internal const string GuardMarker = "GrantId";

    /// <summary>
    /// Delegable gates that a stored definition FULLY guards (#436). A gate counts only when the
    /// tree has at least one <c>wait_for_signal</c> on it and EVERY wait on it is guarded the way
    /// the orchestrator's signal is checked at consumption:
    /// <list type="bullet">
    /// <item>a literal, non-blank <c>visitVar</c>;</item>
    /// <item><c>delegatedGuard.marker</c> exactly <c>GrantId</c>;</item>
    /// <item><c>delegatedGuard.require.VisitId</c> exactly <c>{{vars.&lt;that visitVar&gt;}}</c>, so the
    /// guard checks the visit this wait mints;</item>
    /// <item><c>delegatedGuard.require.ArtifactRef</c> exactly <c>{{vars.review_ref}}</c>, the variable
    /// the definition publishes as <c>ReviewRef</c>.</item>
    /// </list>
    /// One unguarded or differently guarded wait on a gate makes that whole gate not delegable: it
    /// could consume a delegated approval the orchestrator checked against a different visit. A
    /// wait whose <c>signalName</c> is a template could resolve to any gate at run time, so its
    /// presence makes the definition guard nothing. Walks the JSON tree generically; an
    /// unparsable definition guards nothing.
    /// </summary>
    public static IReadOnlyList<string> GuardedGates(string definitionJson)
    {
        var verdicts = new Dictionary<string, bool>(StringComparer.Ordinal);
        var templatedWait = false;
        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            Walk(doc.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
        if (templatedWait) return [];
        return [.. verdicts.Where(v => v.Value).Select(v => v.Key).Order(StringComparer.Ordinal)];

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "wait_for_signal")
                    {
                        var signalName = String(element, "signalName") ?? "";
                        if (signalName.Contains("{{", StringComparison.Ordinal))
                            templatedWait = true;
                        else if (EpicGrantGates.Canonical(signalName) is { } gate)
                            verdicts[gate] = (!verdicts.TryGetValue(gate, out var sofar) || sofar) && IsFullyGuarded(element);
                    }
                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item);
                    break;
            }
        }
    }

    private static bool IsFullyGuarded(JsonElement wait)
    {
        var visitVar = String(wait, "visitVar");
        if (string.IsNullOrWhiteSpace(visitVar) || visitVar.Contains("{{", StringComparison.Ordinal)) return false;
        if (Property(wait, "delegatedGuard") is not { ValueKind: JsonValueKind.Object } guard) return false;
        if (!string.Equals(String(guard, "marker"), GuardMarker, StringComparison.Ordinal)) return false;
        if (Property(guard, "require") is not { ValueKind: JsonValueKind.Object } require) return false;

        return require.TryGetProperty("VisitId", out var visit)
            && visit.ValueKind == JsonValueKind.String
            && visit.GetString() == "{{vars." + visitVar + "}}"
            && require.TryGetProperty("ArtifactRef", out var artifact)
            && artifact.ValueKind == JsonValueKind.String
            && artifact.GetString() == "{{vars.review_ref}}";
    }
}

// ─── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Epic grants (#436): scope validation and storage, revocation, and the delegated decision path
/// D1–D11 followed by exactly one signal.
/// </summary>
/// <remarks>
/// <para>
/// A decision stops at the first failing check, logs one line and sends nothing. Only after D11
/// commits a <c>reserved</c> row does it send, once, to the exact run id it pinned at D4; the row
/// then becomes <c>sent</c> or <c>send_failed</c>. There is no retry and no release: the unique
/// key on (namespace, workflow, run, gate, visit) is the at-most-once guard, never a prior read.
/// </para>
/// <para>
/// Scoped: one instance per request, over that request's <see cref="OrchestratorDbContext"/>.
/// </para>
/// </remarks>
public sealed class EpicGrantService(
    OrchestratorDbContext db,
    IEpicGrantTemporal temporal,
    RepoVisibilityReader visibility,
    IOptions<EpicGrantOptions> options,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<EpicGrantService> logger)
{
    public const int MaxScopeBytes = 64 * 1024;
    private const string ApprovedDecision = "approved";
    private static int _misconfiguredLogged;

    private readonly EpicGrantOptions _options = options.Value;

    /// <summary>
    /// The feature switch. <c>Enabled</c> with <c>MaxDays</c> ≤ 0 logs
    /// <c>epic_grants_misconfigured</c> once per process and counts as disabled.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            if (!_options.Enabled) return false;
            if (_options.MaxDays > 0) return true;
            if (Interlocked.Exchange(ref _misconfiguredLogged, 1) == 0)
                logger.LogError(
                    "epic_grants_misconfigured: EpicGrants:Enabled is true but EpicGrants:MaxDays is {MaxDays}; epic grants are disabled",
                    _options.MaxDays);
            return false;
        }
    }

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>The configured CTO agent, read per call, or null when blank.</summary>
    private string? ConfiguredCto =>
        configuration["FLEET_CTO_AGENT"] is { } cto && !string.IsNullOrWhiteSpace(cto) ? cto.Trim() : null;

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EpicGrantView>> ListAsync(CancellationToken ct)
    {
        var grants = await db.EpicGrants.AsNoTracking().OrderByDescending(g => g.CreatedAt).ToListAsync(ct);
        var now = UtcNow;
        return grants.Select(g => ToView(g, now)).ToList();
    }

    public async Task<EpicGrantDetail?> GetAsync(string id, CancellationToken ct)
    {
        if (ParseId(id) is not { } grantId) return null;
        var grant = await db.EpicGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is null) return null;
        var decisions = await db.EpicGrantDecisions.AsNoTracking()
            .Where(d => d.GrantId == grantId)
            .OrderByDescending(d => d.Id)
            .ToListAsync(ct);
        return ToDetail(grant, decisions, UtcNow);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    /// <summary>Runs every §5 creation check against <paramref name="body"/>. Stores nothing.</summary>
    public async Task<ScopeValidationReport> ValidateAsync(string body, CancellationToken ct) =>
        (await ValidateCoreAsync(body, ct)).Report;

    /// <summary>
    /// Validates and, only when every check passes, stores <paramref name="body"/> verbatim as an
    /// active grant. Any failure stores nothing.
    /// </summary>
    public async Task<EpicGrantCreateResult> CreateAsync(string body, CancellationToken ct)
    {
        var (report, scope, cto) = await ValidateCoreAsync(body, ct);
        if (!report.Valid || scope is null || cto is null)
            return new EpicGrantCreateResult(null, report);

        var grant = new EpicGrant
        {
            Id = Guid.NewGuid(),
            Status = EpicGrantStatus.Active,
            ScopeJson = body,
            ScopeSha256 = Sha256Hex(body),
            DriverNamespace = scope.Driver.Namespace,
            DriverWorkflowId = scope.Driver.WorkflowId,
            DriverRunId = scope.Driver.RunId,
            CtoAgent = cto,
            CreatedAt = UtcNow,
            ExpiresAt = scope.ExpiresAt.UtcDateTime,
        };
        db.EpicGrants.Add(grant);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("EpicGrant created grant={GrantId} driver={DriverWorkflowId} expires={ExpiresAt:O}",
            grant.Id, grant.DriverWorkflowId, grant.ExpiresAt);
        return new EpicGrantCreateResult(ToDetail(grant, [], UtcNow), report);
    }

    private async Task<(ScopeValidationReport Report, EpicScope? Scope, string? Cto)> ValidateCoreAsync(
        string body, CancellationToken ct)
    {
        var report = new ScopeValidationReport { ScopeSha256 = Sha256Hex(body) };
        var errors = report.Errors;

        if (Encoding.UTF8.GetByteCount(body) > MaxScopeBytes)
        {
            errors.Add($"scope is larger than {MaxScopeBytes} bytes");
            return (report, null, null);
        }

        var parse = EpicScopeParser.Parse(body);
        errors.AddRange(parse.Errors);

        if (!IsEnabled)
            errors.Add("epic grants are disabled");

        var cto = ConfiguredCto;
        if (cto is null)
            errors.Add("FLEET_CTO_AGENT is not configured");
        else if (cto.Length > 128)
            errors.Add("FLEET_CTO_AGENT is longer than 128 characters");

        var now = clock.GetUtcNow();
        if (parse.ExpiresAt is { } expires)
        {
            report.ExpiresAt = expires.UtcDateTime;
            if (expires <= now)
                errors.Add("expiresAt must be in the future");
            else if (_options.MaxDays > 0 && expires > now.AddDays(_options.MaxDays))
                errors.Add($"expiresAt must be within {_options.MaxDays} days");
        }

        if (parse.Driver is { } driver)
        {
            string status;
            try
            {
                var described = await temporal.DescribeAsync(driver.Namespace, driver.WorkflowId, driver.RunId, ct);
                status = described is null ? "not_found" : described.Status;
                if (described is null || !described.IsRunning || described.RunId != driver.RunId)
                    errors.Add($"driver run is not Running at run id {driver.RunId} (status {status})");
            }
            catch (Exception ex)
            {
                status = "unknown";
                errors.Add("driver run status could not be read");
                logger.LogWarning("EpicGrant validation: driver describe failed ({Error})", ex.GetType().Name);
            }
            report.Driver = new ScopeValidationReport.DriverReport(driver.Namespace, driver.WorkflowId, driver.RunId, status);
        }

        var denied = _options.DeniedRepoSet();
        foreach (var target in parse.Targets)
        {
            var isDenied = denied.Contains(target.Repo);
            var seen = "unknown";
            if (isDenied)
            {
                // The deny list wins over allowPublic, so the repo is not even read.
                errors.Add($"target {target.Repo} is on the deny list");
            }
            else
            {
                var read = await visibility.ReadAsync(target.Repo, ct);
                seen = VisibilityName(read);
                if (read == RepoVisibility.Unknown)
                    errors.Add($"target {target.Repo} visibility is unknown");
                else if (read == RepoVisibility.Public && !target.AllowPublic)
                    errors.Add($"target {target.Repo} is public and needs allowPublic: true");
                else if (read == RepoVisibility.Private && target.AllowPublic)
                    errors.Add($"target {target.Repo} is not public; allowPublic is not allowed");
            }
            report.Targets.Add(new ScopeValidationReport.TargetReport(
                target.Repo, target.Issues, target.AllowPublic, seen, isDenied));
        }

        var served = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workflow in parse.Workflows)
        {
            string? stored;
            try
            {
                stored = await LoadStoredDefinitionAsync(workflow.Type, workflow.Version, ct);
            }
            catch (Exception ex)
            {
                stored = null;
                logger.LogWarning("EpicGrant validation: definition read failed ({Error})", ex.GetType().Name);
            }

            var storedSha = stored is null ? null : Sha256Hex(stored);
            var matches = storedSha is not null && string.Equals(storedSha, workflow.Sha256, StringComparison.OrdinalIgnoreCase);
            var gates = stored is null ? [] : EpicJson.GuardedGates(stored);
            var capable = gates.Count > 0;

            if (stored is null)
                errors.Add($"workflow {workflow.Type} version {workflow.Version} is not a stored definition");
            else if (!matches)
                errors.Add($"workflow {workflow.Type} version {workflow.Version}: sha256 does not match the stored definition");
            if (stored is not null && !capable)
                errors.Add($"workflow {workflow.Type} version {workflow.Version} is not delegation-capable");
            if (matches && capable)
                served.UnionWith(gates);

            report.Workflows.Add(new ScopeValidationReport.WorkflowReport(
                workflow.Type, workflow.Version, workflow.Sha256, storedSha, matches, capable, gates));
        }

        foreach (var gate in parse.Gates)
            if (!served.Contains(gate))
                errors.Add($"gate {gate} is not served by any listed workflow");

        var scope = parse.Scope;
        report.Valid = errors.Count == 0 && scope is not null;
        return (report, scope, cto);
    }

    // ── Revoke ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets <c>revoked</c>, <c>RevokedAt</c> and <c>RevokeReason</c> in one conditional statement,
    /// only while the grant is active. Commits in the same row-lock order as D11, so whichever of a
    /// revoke and a reservation commits first wins.
    /// </summary>
    public async Task<(EpicGrantRevokeOutcome Outcome, EpicGrantView? Grant)> RevokeAsync(
        string id, string? reason, CancellationToken ct)
    {
        if (ParseId(id) is not { } grantId) return (EpicGrantRevokeOutcome.NotFound, null);
        var now = UtcNow;
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > 500 }) trimmed = trimmed[..500];

        var updated = await db.EpicGrants
            .Where(g => g.Id == grantId && g.Status == EpicGrantStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.Status, EpicGrantStatus.Revoked)
                .SetProperty(g => g.RevokedAt, now)
                .SetProperty(g => g.RevokeReason, trimmed), ct);

        var grant = await db.EpicGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is null) return (EpicGrantRevokeOutcome.NotFound, null);
        if (updated == 1)
            logger.LogInformation("EpicGrant revoked grant={GrantId}", id);
        return (updated == 1 ? EpicGrantRevokeOutcome.Revoked : EpicGrantRevokeOutcome.AlreadyRevoked, ToView(grant, UtcNow));
    }

    // ── Decide ───────────────────────────────────────────────────────────────

    /// <summary>The refusal for a decision body that is not parsable JSON. Logs the decision line.</summary>
    public EpicGrantDecisionResult RefuseUnparsable(string grantId)
    {
        var result = EpicGrantDecisionResult.Refused(EpicGrantRefusal.BadRequest);
        LogDecision(grantId, "", "", result, null);
        return result;
    }

    /// <summary>Runs D1–D11 and, only when all pass, sends one signal. Logs exactly one line.</summary>
    public async Task<EpicGrantDecisionResult> DecideAsync(string grantId, EpicGrantDecisionRequest request, CancellationToken ct)
    {
        Exception? error = null;
        EpicGrantDecisionResult result;
        try
        {
            (result, error) = await DecideCoreAsync(grantId, request, ct);
        }
        catch (Exception ex)
        {
            // Every path inside returns its own refusal; this is the last fail-safe.
            (result, error) = (EpicGrantDecisionResult.Refused(EpicGrantRefusal.Unknown), ex);
        }
        LogDecision(grantId, request.WorkflowId ?? "", request.Gate ?? "", result, error);
        return result;
    }

    private async Task<(EpicGrantDecisionResult, Exception?)> DecideCoreAsync(
        string grantId, EpicGrantDecisionRequest request, CancellationToken ct)
    {
        static (EpicGrantDecisionResult, Exception?) Refuse(string reason, Exception? ex = null) =>
            (EpicGrantDecisionResult.Refused(reason), ex);

        // D1 starts with the switch, so a disabled feature answers `disabled` whatever the id.
        if (!IsEnabled) return Refuse(EpicGrantRefusal.Disabled);

        // Then the grant: an unknown id is an inactive grant.
        if (ParseId(grantId) is not { } id) return Refuse(EpicGrantRefusal.GrantInactive);
        EpicGrant? grant;
        try
        {
            grant = await db.EpicGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        }
        catch (Exception ex)
        {
            return Refuse(EpicGrantRefusal.Unknown, ex);
        }
        if (grant is null) return Refuse(EpicGrantRefusal.GrantInactive);

        // D1 — caller, request shape.
        var cto = ConfiguredCto;
        if (cto is null
            || !string.Equals(request.Caller?.Trim(), cto, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(grant.CtoAgent, cto, StringComparison.OrdinalIgnoreCase))
            return Refuse(EpicGrantRefusal.Caller);
        if (!IsWellFormed(request, out var gate))
            return Refuse(EpicGrantRefusal.BadRequest);
        var ns = request.Namespace!;
        var workflowId = request.WorkflowId!;
        var visitId = request.VisitId!;
        var artifactRef = request.ArtifactRef!;

        // D2 — grant active, unexpired, scope intact.
        if (grant.Status != EpicGrantStatus.Active) return Refuse(EpicGrantRefusal.GrantInactive);
        if (!(UtcNow < AsUtc(grant.ExpiresAt))) return Refuse(EpicGrantRefusal.Expired);
        if (!string.Equals(Sha256Hex(grant.ScopeJson), grant.ScopeSha256, StringComparison.OrdinalIgnoreCase))
            return Refuse(EpicGrantRefusal.ScopeTampered);
        if (EpicScopeParser.Parse(grant.ScopeJson).Scope is not { } scope)
            return Refuse(EpicGrantRefusal.ScopeTampered);
        var driver = scope.Driver;

        // D3 — the driver run, at its exact run id, is Running.
        try
        {
            var described = await temporal.DescribeAsync(driver.Namespace, driver.WorkflowId, driver.RunId, ct);
            if (described is null || !described.IsRunning || described.RunId != driver.RunId)
                return Refuse(EpicGrantRefusal.DriverNotRunning);
        }
        catch (Exception ex)
        {
            return Refuse(EpicGrantRefusal.Unknown, ex);
        }

        // D4 — the target's latest run is Running, started after the grant, and linked to the
        // driver. Its run id is pinned here; everything after uses it.
        EpicRunDescription target;
        EpicRunHistory history;
        JsonElement input;
        try
        {
            var described = await temporal.DescribeAsync(ns, workflowId, null, ct);
            if (described is null || !described.IsRunning || string.IsNullOrEmpty(described.RunId)
                || described.StartTime is not { } started || started < AsUtcOffset(grant.CreatedAt))
                return Refuse(EpicGrantRefusal.NotLinked);
            target = described;

            history = await temporal.ReadRunAsync(ns, workflowId, target.RunId, ct)
                ?? throw new InvalidOperationException("target run has no history page");
            input = ParseInput(history.InputJson);
            if (!await IsLinkedAsync(ns, target, input, driver, ct))
                return Refuse(EpicGrantRefusal.NotLinked);
        }
        catch (Exception ex)
        {
            return Refuse(EpicGrantRefusal.Unknown, ex);
        }
        var runId = target.RunId;

        // D5 — the run's own definition is pinned in scope and still hashes to its pin, and that
        // definition guards this gate.
        if (history.DefinitionName is not { } definitionName || history.DefinitionVersion is not { } definitionVersion
            || definitionName != target.WorkflowType)
            return Refuse(EpicGrantRefusal.DefinitionOutOfScope);
        var pinned = scope.Workflows.FirstOrDefault(w => w.Type == definitionName && w.Version == definitionVersion);
        if (pinned is null) return Refuse(EpicGrantRefusal.DefinitionOutOfScope);
        string? stored;
        try
        {
            stored = await LoadStoredDefinitionAsync(definitionName, definitionVersion, ct);
        }
        catch (Exception ex)
        {
            return Refuse(EpicGrantRefusal.Unknown, ex);
        }
        if (stored is null || !string.Equals(Sha256Hex(stored), pinned.Sha256, StringComparison.OrdinalIgnoreCase))
            return Refuse(EpicGrantRefusal.DefinitionOutOfScope);
        if (!scope.Gates.Contains(gate, StringComparer.Ordinal) || !EpicJson.GuardedGates(stored).Contains(gate))
            return Refuse(EpicGrantRefusal.GateOutOfScope);

        // D6 — repo and issue are targets; not denied; visibility known and allowed.
        var repo = EpicJson.String(input, "Repo");
        var issue = EpicJson.PositiveInteger(input, gate == EpicGrantGates.DesignApproval ? "ExistingIssueNumber" : "IssueNumber");
        var targetEntry = repo is null || issue is null
            ? null
            : scope.Targets.FirstOrDefault(t => string.Equals(t.Repo, repo, StringComparison.OrdinalIgnoreCase)
                                                && t.Issues.Contains(issue.Value));
        if (targetEntry is null) return Refuse(EpicGrantRefusal.TargetOutOfScope);
        if (_options.DeniedRepoSet().Contains(targetEntry.Repo)) return Refuse(EpicGrantRefusal.TargetOutOfScope);
        var seen = await visibility.ReadAsync(targetEntry.Repo, ct);
        if (seen == RepoVisibility.Unknown) return Refuse(EpicGrantRefusal.VisibilityUnknown);
        if (seen == RepoVisibility.Public && !targetEntry.AllowPublic) return Refuse(EpicGrantRefusal.TargetOutOfScope);
        var isPublic = seen == RepoVisibility.Public;

        // D7 — the author is not the decider.
        var author = gate == EpicGrantGates.DocReview
            ? (string.IsNullOrWhiteSpace(EpicJson.String(input, "PrepAgent")) ? cto : EpicJson.String(input, "PrepAgent"))
            : EpicJson.String(input, "TargetAgent");
        if (string.IsNullOrWhiteSpace(author) || string.Equals(author.Trim(), cto, StringComparison.OrdinalIgnoreCase))
            return Refuse(EpicGrantRefusal.AuthorIsDecider);

        // D8 — the current gate visit. D9 — the attested artifact. D10 — public scrub.
        if (target.GateVisit != visitId || !visitId.StartsWith(gate + ":", StringComparison.Ordinal))
            return Refuse(EpicGrantRefusal.StaleVisit);
        if (string.IsNullOrEmpty(target.ReviewRef) || target.ReviewRef != artifactRef)
            return Refuse(EpicGrantRefusal.StaleArtifact);
        if (isPublic && target.ReviewScrub != "pass")
            return Refuse(EpicGrantRefusal.ScrubMissing);

        // D11 — reserve the visit.
        var reservation = await ReserveAsync(id, ns, workflowId, runId, gate, visitId, artifactRef,
            request.Evidence!, request.Caller!.Trim(), ct);
        if (reservation.DecisionId is not { } decisionId)
            return Refuse(reservation.Refusal ?? EpicGrantRefusal.Unknown, reservation.Error);

        // Send once, to the pinned run, under the canonical gate name. Never retried. Not tied to
        // the request's token: once reserved, the outcome must be recorded even if the caller left.
        Exception? sendError = null;
        try
        {
            await temporal.SignalAsync(ns, workflowId, runId, gate,
                SignalPayload(FormatId(id), visitId, artifactRef, request.Evidence!), CancellationToken.None);
        }
        catch (Exception ex)
        {
            sendError = ex;
        }

        var outcome = sendError is null ? EpicGrantDecisionStatus.Sent : EpicGrantDecisionStatus.SendFailed;
        Exception? markError = null;
        try
        {
            var now = UtcNow;
            await db.EpicGrantDecisions
                .Where(d => d.Id == decisionId && d.Status == EpicGrantDecisionStatus.Reserved)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, outcome)
                    .SetProperty(d => d.UpdatedAt, now), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The row stays reserved ("delivery unknown"). It is never released.
            markError = ex;
        }

        return sendError is null
            ? (new EpicGrantDecisionResult(EpicGrantDecisionResults.Sent, null, decisionId), markError)
            : (new EpicGrantDecisionResult(EpicGrantDecisionResults.SendFailed, EpicGrantDecisionResults.SendFailed, decisionId), sendError);
    }

    internal sealed record Reservation(long? DecisionId, string? Refusal, Exception? Error);

    /// <summary>
    /// D11: in one transaction, lock the grant row (<c>FOR UPDATE</c> on MySQL), re-check active
    /// and unexpired, and insert the <c>reserved</c> row. A duplicate key is <c>already_decided</c>;
    /// any other failure is <c>unknown</c>. Uniqueness is never checked by reading first.
    /// </summary>
    /// <remarks>
    /// The whole operation runs inside the execution strategy (the production context retries on
    /// transient failures, which forbids a bare user transaction) but never throws out of it, so the
    /// strategy never re-runs an insert whose commit outcome is unknown.
    /// </remarks>
    internal async Task<Reservation> ReserveAsync(
        Guid grantId, string ns, string workflowId, string runId, string gate,
        string visitId, string artifactRef, string evidence, string caller, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var grant = await LockGrantAsync(grantId, ct);
                if (grant is null || grant.Status != EpicGrantStatus.Active)
                    return new Reservation(null, EpicGrantRefusal.GrantInactive, null);
                var now = UtcNow;
                if (!(now < AsUtc(grant.ExpiresAt)))
                    return new Reservation(null, EpicGrantRefusal.Expired, null);

                // From the insert onward the caller's token is not honoured: a cancellation
                // between a committed insert and the send would leave a `reserved` row with nothing
                // sent and the visit undecidable by delegation. A request aborted before this point
                // has written nothing.
                var row = new EpicGrantDecision
                {
                    GrantId = grantId,
                    Namespace = ns,
                    WorkflowId = workflowId,
                    RunId = runId,
                    Gate = gate,
                    VisitId = visitId,
                    ArtifactRef = artifactRef,
                    Evidence = evidence,
                    Caller = caller,
                    Status = EpicGrantDecisionStatus.Reserved,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.EpicGrantDecisions.Add(row);
                await db.SaveChangesAsync(CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);
                return new Reservation(row.Id, null, null);
            }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                return IsDuplicateKey(ex)
                    ? new Reservation(null, EpicGrantRefusal.AlreadyDecided, null)
                    : new Reservation(null, EpicGrantRefusal.Unknown, ex);
            }
        });
    }

    private async Task<EpicGrant?> LockGrantAsync(Guid grantId, CancellationToken ct)
    {
        if (db.Database.IsMySql())
        {
            // Not composed further, so EF sends exactly this statement.
            var locked = await db.EpicGrants
                .FromSqlInterpolated($"SELECT * FROM epic_grants WHERE Id = {grantId} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(ct);
            return locked.FirstOrDefault();
        }

        return await db.EpicGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, ct);
    }

    /// <summary>
    /// Linked when the run's input <c>WaiterWorkflowId</c> is the driver workflow id, or when a
    /// parent at most two levels up is the driver run itself or has that input.
    /// </summary>
    private async Task<bool> IsLinkedAsync(
        string ns, EpicRunDescription target, JsonElement input, EpicScopeDriver driver, CancellationToken ct)
    {
        if (WaitsOn(input, driver)) return true;

        var (parentId, parentRunId) = (target.ParentWorkflowId, target.ParentRunId);
        for (var level = 1; level <= 2; level++)
        {
            if (string.IsNullOrEmpty(parentId) || string.IsNullOrEmpty(parentRunId)) return false;
            if (string.Equals(ns, driver.Namespace, StringComparison.Ordinal)
                && parentId == driver.WorkflowId && parentRunId == driver.RunId)
                return true;

            var parentHistory = await temporal.ReadRunAsync(ns, parentId, parentRunId, ct)
                ?? throw new InvalidOperationException("parent run has no history page");
            if (WaitsOn(ParseInput(parentHistory.InputJson), driver)) return true;
            if (level == 2) break;

            var parent = await temporal.DescribeAsync(ns, parentId, parentRunId, ct);
            if (parent is null) return false;
            (parentId, parentRunId) = (parent.ParentWorkflowId, parent.ParentRunId);
        }

        return false;

        static bool WaitsOn(JsonElement input, EpicScopeDriver driver) =>
            EpicJson.String(input, "WaiterWorkflowId") is { Length: > 0 } waiter && waiter == driver.WorkflowId;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>A grant id in its canonical 36-character <c>D</c> form, or null.</summary>
    private static Guid? ParseId(string? id) =>
        Guid.TryParseExact(id, "D", out var parsed) ? parsed : null;

    private static string FormatId(Guid id) => id.ToString("D");

    private static bool IsWellFormed(EpicGrantDecisionRequest request, out string gate)
    {
        gate = EpicGrantGates.Canonical(request.Gate) ?? "";
        return gate.Length > 0
            && request.Decision == ApprovedDecision
            && Present(request.Namespace, 64)
            && Present(request.WorkflowId, 255)
            && Present(request.VisitId, 64)
            && Present(request.ArtifactRef, 64)
            && Present(request.Caller, 128)
            && Present(request.Evidence, 500)
            && Uri.TryCreate(request.Evidence, UriKind.Absolute, out var evidence)
            && evidence.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(evidence.Host);

        static bool Present(string? value, int maxLength) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;
    }

    /// <summary>The run input as a JSON element; Undefined when there is none; throws when unparsable.</summary>
    private static JsonElement ParseInput(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson)) return default;
        using var doc = JsonDocument.Parse(inputJson);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The stored definition for exactly (type, version): the main row when its version matches,
    /// otherwise the archived version row. Null when neither exists.
    /// </summary>
    private async Task<string?> LoadStoredDefinitionAsync(string type, int version, CancellationToken ct)
    {
        var main = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.Name == type)
            .Select(d => new { d.Id, d.Name, d.Version, d.Definition })
            .FirstOrDefaultAsync(ct);
        if (main is null || main.Name != type) return null;
        if (main.Version == version) return main.Definition;
        return await db.WorkflowDefinitionVersions.AsNoTracking()
            .Where(v => v.WorkflowDefinitionId == main.Id && v.Version == version)
            .Select(v => v.Definition)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Exactly the five contract fields, in contract order.</summary>
    internal static string SignalPayload(string grantId, string visitId, string artifactRef, string evidence) =>
        JsonSerializer.Serialize(new
        {
            Decision = ApprovedDecision,
            GrantId = grantId,
            VisitId = visitId,
            ArtifactRef = artifactRef,
            Evidence = evidence,
        });

    /// <summary>MySQL 1062, or SQLite constraint error 19 on a unique/primary key.</summary>
    internal static bool IsDuplicateKey(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is MySqlException mysql && mysql.Number == 1062) return true;
            if (e.GetType().Name == "SqliteException"
                && e.GetType().GetProperty("SqliteErrorCode")?.GetValue(e) is 19)
            {
                var extended = e.GetType().GetProperty("SqliteExtendedErrorCode")?.GetValue(e) as int?;
                // 2067 SQLITE_CONSTRAINT_UNIQUE, 1555 SQLITE_CONSTRAINT_PRIMARYKEY.
                return extended is null or 0 or 2067 or 1555;
            }
        }
        return false;
    }

    internal static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTimeOffset AsUtcOffset(DateTime value) => new(AsUtc(value));

    private static string VisibilityName(RepoVisibility visibility) => visibility switch
    {
        RepoVisibility.Public => "public",
        RepoVisibility.Private => "private",
        _ => "unknown",
    };

    private void LogDecision(string grantId, string workflowId, string gate, EpicGrantDecisionResult result, Exception? error)
    {
        // Exactly one line per decision. Evidence is never logged.
        logger.Log(
            result.Result == EpicGrantDecisionResults.Sent ? LogLevel.Information : LogLevel.Warning,
            error,
            "EpicGrant decision grant={GrantId} workflow={WorkflowId} gate={Gate} result={Result} reason={Reason}",
            grantId, workflowId, gate, result.Result, result.Reason ?? "none");
    }

    private static EpicGrantView ToView(EpicGrant g, DateTime now) => new()
    {
        Id = FormatId(g.Id),
        Status = g.Status,
        EffectiveStatus = EffectiveStatus(g, now),
        ScopeSha256 = g.ScopeSha256,
        DriverNamespace = g.DriverNamespace,
        DriverWorkflowId = g.DriverWorkflowId,
        DriverRunId = g.DriverRunId,
        CtoAgent = g.CtoAgent,
        CreatedAt = AsUtc(g.CreatedAt),
        ExpiresAt = AsUtc(g.ExpiresAt),
        RevokedAt = g.RevokedAt is { } revoked ? AsUtc(revoked) : null,
        RevokeReason = g.RevokeReason,
    };

    private static EpicGrantDetail ToDetail(EpicGrant g, IEnumerable<EpicGrantDecision> decisions, DateTime now)
    {
        JsonElement scope;
        try
        {
            using var doc = JsonDocument.Parse(g.ScopeJson);
            scope = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            scope = JsonSerializer.SerializeToElement<object?>(null);
        }

        return new EpicGrantDetail
        {
            Id = FormatId(g.Id),
            Status = g.Status,
            EffectiveStatus = EffectiveStatus(g, now),
            ScopeSha256 = g.ScopeSha256,
            DriverNamespace = g.DriverNamespace,
            DriverWorkflowId = g.DriverWorkflowId,
            DriverRunId = g.DriverRunId,
            CtoAgent = g.CtoAgent,
            CreatedAt = AsUtc(g.CreatedAt),
            ExpiresAt = AsUtc(g.ExpiresAt),
            RevokedAt = g.RevokedAt is { } revoked ? AsUtc(revoked) : null,
            RevokeReason = g.RevokeReason,
            Scope = scope,
            Decisions = decisions.Select(d => new EpicGrantDecisionView
            {
                Id = d.Id,
                GrantId = FormatId(d.GrantId),
                Namespace = d.Namespace,
                WorkflowId = d.WorkflowId,
                RunId = d.RunId,
                Gate = d.Gate,
                VisitId = d.VisitId,
                ArtifactRef = d.ArtifactRef,
                Evidence = d.Evidence,
                Caller = d.Caller,
                Status = d.Status,
                CreatedAt = AsUtc(d.CreatedAt),
                UpdatedAt = AsUtc(d.UpdatedAt),
            }).ToList(),
        };
    }

    private static string EffectiveStatus(EpicGrant g, DateTime now) =>
        g.Status == EpicGrantStatus.Revoked ? "revoked"
        : now >= AsUtc(g.ExpiresAt) ? "expired"
        : "active";
}
