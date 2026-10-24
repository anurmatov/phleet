using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Fleet.Shared;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

/// <summary>The exact recovery action a reservation would take, as an approval must name it.</summary>
public sealed record WorkflowRecoveryAction(
    string AuthorityRef,
    string Operation,
    Guid OutcomeId,
    Guid DecisionId,
    string TargetNamespace,
    string? TargetWorkflowType,
    string? TargetTaskQueue,
    string? TargetAgent,
    string? InputSha256);

/// <summary>
/// Production/financial recovery is deny-by-default (#476): a reservation needs a recorded human
/// approval that covers its exact action — operation, failure item, decision, target, input and
/// authority reference — and has not expired. A namespace, a decision reference or a bare URL is
/// never authority on its own.
/// </summary>
/// <remarks>
/// The approvals are operator-owned data in <see cref="WorkflowOutcomeOptions.RecoveryApprovalsFile"/>
/// (recovery-approvals.json v1), read fresh on every reserve and dispatch so removing an entry
/// revokes it. A blank path, a missing, oversized or invalid file all deny.
/// </remarks>
public sealed class WorkflowRecoveryApprovals(IOptions<WorkflowOutcomeOptions> options, TimeProvider clock, ILogger<WorkflowRecoveryApprovals> logger)
{
    /// <summary>The file could not be read or validated; retryable, never a permanent block.</summary>
    public const string Unverified = "authority_unverified";

    /// <summary>No approval names this exact action.</summary>
    public const string NotCovering = "authority_not_covering_action";

    /// <summary>The covering approval has expired.</summary>
    public const string Expired = "authority_expired";

    private const int MaxBytes = 1024 * 1024;
    private static readonly string[] RootKeys = ["schemaVersion", "approvals"];
    private static readonly string[] ApprovalKeys =
        ["authorityRef", "operation", "outcomeId", "decisionId", "targetNamespace", "targetWorkflowType", "targetTaskQueue", "targetAgent", "inputSha256", "expiresAt"];
    private static readonly string[] ApprovalRequired = ["authorityRef", "operation", "outcomeId", "decisionId", "targetNamespace", "expiresAt"];
    private static readonly Regex HexPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private sealed record Approval(WorkflowRecoveryAction Action, DateTimeOffset ExpiresAt);

    /// <summary>Null when an unexpired approval covers exactly <paramref name="action"/>; otherwise the denial reason.</summary>
    public string? Covers(WorkflowRecoveryAction action)
    {
        IReadOnlyList<Approval> approvals;
        try
        {
            approvals = Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowOutcomePolicyException or InvalidDataException)
        {
            logger.LogWarning("workflow_recovery_approvals_unavailable error={Error}", ex is WorkflowOutcomePolicyException ? ex.Message : ex.GetType().Name);
            return Unverified;
        }

        var matching = approvals.Where(a => a.Action == action).ToList();
        if (matching.Count == 0) return NotCovering;
        return matching.Any(a => a.ExpiresAt > clock.GetUtcNow()) ? null : Expired;
    }

    private IReadOnlyList<Approval> Load()
    {
        var path = options.Value.RecoveryApprovalsFile;
        if (string.IsNullOrWhiteSpace(path)) throw new WorkflowOutcomePolicyException("recovery approvals: no file configured");
        var info = new FileInfo(path);
        if (!info.Exists) throw new WorkflowOutcomePolicyException("recovery approvals: file not found");
        if (info.Length > MaxBytes) throw new InvalidDataException("recovery approvals too large");
        return Parse(File.ReadAllBytes(path));
    }

    /// <summary>Strict parse: unknown or duplicate keys and malformed values throw; nothing is defaulted.</summary>
    private static List<Approval> Parse(byte[] bytes)
    {
        using var doc = PolicyJson.Parse(bytes, "recovery approvals");
        var root = doc.RootElement;
        PolicyJson.ExactKeys(root, RootKeys, RootKeys, "recovery approvals");
        if (!PolicyJson.IsInt(root.GetProperty("schemaVersion"), 1))
            throw new WorkflowOutcomePolicyException("recovery approvals: schemaVersion must be 1");
        var list = root.GetProperty("approvals");
        if (list.ValueKind != JsonValueKind.Array) throw new WorkflowOutcomePolicyException("recovery approvals: approvals must be an array");

        var result = new List<Approval>();
        var index = 0;
        foreach (var a in list.EnumerateArray())
        {
            var at = "recovery approvals: approvals[" + index++ + "]";
            PolicyJson.ExactKeys(a, ApprovalKeys, ApprovalRequired, at);
            var authority = PolicyJson.String(a, "authorityRef", at);
            if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0
                || uri.UserInfo.Length > 0 || authority.Length > 512)
                throw new WorkflowOutcomePolicyException(at + ": authorityRef must be the https URL of the human approval");
            var operation = PolicyJson.String(a, "operation", at);
            var start = operation is WorkflowOutcomeOperations.Retry or WorkflowOutcomeOperations.Reassign;
            if (!start && operation != WorkflowOutcomeOperations.Cancel)
                throw new WorkflowOutcomePolicyException(at + ": operation must be retry, reassign or cancel");
            var type = PolicyJson.OptionalString(a, "targetWorkflowType", at);
            var queue = PolicyJson.OptionalString(a, "targetTaskQueue", at);
            var agent = PolicyJson.OptionalString(a, "targetAgent", at);
            var input = PolicyJson.OptionalString(a, "inputSha256", at);
            if (start && (type is null || queue is null || input is null || !HexPattern.IsMatch(input)))
                throw new WorkflowOutcomePolicyException(at + ": a start approval names targetWorkflowType, targetTaskQueue and a 64-hex inputSha256");
            if (operation == WorkflowOutcomeOperations.Reassign && agent is null)
                throw new WorkflowOutcomePolicyException(at + ": a reassign approval names targetAgent");
            if (!start && (type is not null || queue is not null || input is not null))
                throw new WorkflowOutcomePolicyException(at + ": a cancel approval names no start target");
            if (!DateTimeOffset.TryParseExact(PolicyJson.String(a, "expiresAt", at), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires))
                throw new WorkflowOutcomePolicyException(at + ": expiresAt must be a UTC yyyy-MM-ddTHH:mm:ssZ time");
            result.Add(new Approval(new WorkflowRecoveryAction(authority, operation, Id(a, "outcomeId", at), Id(a, "decisionId", at),
                PolicyJson.String(a, "targetNamespace", at), type, queue, agent, input), expires));
        }
        return result;
    }

    private static Guid Id(JsonElement a, string name, string at) =>
        Guid.TryParseExact(PolicyJson.String(a, name, at), "D", out var id) && id != Guid.Empty
            ? id
            : throw new WorkflowOutcomePolicyException(at + ": " + name + " must be a UUID");
}
