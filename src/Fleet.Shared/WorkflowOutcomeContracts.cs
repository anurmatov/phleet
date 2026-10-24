using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleet.Shared;

// Wire contracts for workflow failure accountability (#476). The orchestrator is the final
// validator; the coordinator runtime and the bridge use the same shapes and checks so a value
// rejected on one side is rejected on the other. JSON is camelCase on the wire.

/// <summary>Classification of one exact run <c>(namespace, workflowId, runId)</c>.</summary>
public static class WorkflowOutcomeClassifications
{
    /// <summary>Proven failure-caused exit. The ONLY value that can enter the coordinator inbox.</summary>
    public const string ConfirmedFailure = "confirmed_failure";

    /// <summary>Authenticated deliberate human rejection, cancellation, hold or successful closure.</summary>
    public const string IntentionalNonfailure = "intentional_nonfailure";

    /// <summary>Normal completion, normal continue-as-new or a successful scheduled run.</summary>
    public const string ExcludedNonfailure = "excluded_nonfailure";

    /// <summary>Completed with null/empty/unknown output or human text alone: neither success nor failure handoff.</summary>
    public const string InsufficientEvidence = "insufficient_evidence";

    /// <summary>Canceled/Terminated with an absent or conflicting cause: visible cause review, never a failure claim.</summary>
    public const string CauseUnresolved = "cause_unresolved";

    /// <summary>Classifier absent/truncated/unknown for the loaded definition: coverage gap, not a failure claim.</summary>
    public const string ClassificationPending = "classification_pending";

    public static readonly IReadOnlyList<string> All =
        [ConfirmedFailure, IntentionalNonfailure, ExcludedNonfailure, InsufficientEvidence, CauseUnresolved, ClassificationPending];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>Reason codes stored beside a classification. <c>business_*</c> codes ride on confirmed_failure.</summary>
public static class WorkflowOutcomeReasonCodes
{
    public const string WorkflowFailed = "workflow_failed";
    public const string WorkflowTimedOut = "workflow_timed_out";
    public const string FailureCausedCancel = "failure_caused_cancel";
    public const string FailureCausedTerminate = "failure_caused_terminate";
    public const string BusinessTaskFailed = "business_task_failed";
    public const string BusinessDeadline = "business_deadline";
    public const string BusinessReviewCap = "business_review_cap";
    public const string BusinessArtifactMissing = "business_artifact_missing";
    public const string NormalCompletion = "normal_completion";
    public const string ContinuedAsNew = "continued_as_new";
    public const string DeliberateDisposition = "deliberate_disposition";
    public const string AbortCauseUnknown = "abort_cause_unknown";
    public const string AbortCauseConflicting = "abort_cause_conflicting";
    public const string NullResultOnly = "null_result_only";
    public const string ClassifierMissing = "classifier_missing";

    /// <summary>
    /// A Completed run of a workflow type with no classifier rules at all and no failure evidence:
    /// nonfailure-metadata TTL, while the durable per-type coverage gap stays open.
    /// </summary>
    public const string ClassifierUncoveredType = "classifier_uncovered_type";
    public const string HistoryIncomplete = "history_incomplete";
    public const string CorrelationUnverified = "correlation_unverified";
    public const string RetrySucceeded = "retry_succeeded";

    public static readonly IReadOnlyList<string> Business =
        [BusinessTaskFailed, BusinessDeadline, BusinessReviewCap, BusinessArtifactMissing];

    public static bool IsBusiness(string? code) => code is not null && Business.Contains(code, StringComparer.Ordinal);
}

/// <summary>Values of <c>workflow_outcome_runs.EvidenceState</c>.</summary>
public static class WorkflowOutcomeEvidenceStates
{
    public const string Complete = "complete";
    public const string Pending = "pending";
    public const string Missing = "missing";
    public const string Unverified = "unverified";
    public const string Quarantined = "quarantined";

    /// <summary>Failed/TimedOut with a retry in progress: retained, not yet a final logical failure, not enqueued.</summary>
    public const string SuccessorPending = "successor_pending";
}

/// <summary>The nine coordinator/operator decisions. Exact lowercase; nothing else parses.</summary>
public static class WorkflowOutcomeOperations
{
    public const string Wait = "wait";
    public const string Hold = "hold";
    public const string Resume = "resume";
    public const string Retry = "retry";
    public const string Reassign = "reassign";
    public const string AcceptLate = "accept_late";
    public const string Finish = "finish";
    public const string Close = "close";
    public const string Cancel = "cancel";

    public static readonly IReadOnlyList<string> All = [Wait, Hold, Resume, Retry, Reassign, AcceptLate, Finish, Close, Cancel];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>wait and hold must carry a future <c>dueAt</c>.</summary>
    public static bool RequiresDueAt(string operation) => operation is Wait or Hold;

    /// <summary>Operations that can lead to a recovery reservation (never executed by a decision alone).</summary>
    public static bool IsRecovery(string operation) => operation is Resume or Retry or Reassign or AcceptLate or Cancel;
}

/// <summary>Handoff stages shown per failure item. A later stage never implies an earlier one was skipped.</summary>
public static class WorkflowOutcomeStages
{
    public const string Detected = "detected";
    public const string Stored = "stored";
    public const string Enqueued = "enqueued";
    public const string Delivered = "delivered";
    public const string Acknowledged = "acknowledged";
    public const string DecisionMade = "decision_made";
    public const string Unresolved = "unresolved";
    public const string Escalated = "escalated";
}

/// <summary>States of <c>workflow_recovery_operations</c>. An ambiguous dispatch never releases a reservation.</summary>
public static class WorkflowRecoveryStates
{
    public const string Reserved = "reserved";
    public const string Dispatching = "dispatching";
    public const string Dispatched = "dispatched";
    public const string DispatchUnknown = "dispatch_unknown";
    public const string Verified = "verified";
    public const string Blocked = "blocked";
}

/// <summary>Bounds shared by every side of the protocol.</summary>
public static class WorkflowOutcomeLimits
{
    public const int MaxBatchItems = 20;
    public const int MaxOwnerChars = 200;
    public const int MaxReasonChars = 2000;
    public const int MaxEvidenceRefs = 10;
    public const int MaxEvidenceRefChars = 500;
    public const int MaxTurnOutputBytes = 64 * 1024;
    public const int MaxLateResultBytes = 256 * 1024;
    public const int MaxPageLimit = 100;

    /// <summary>Accepted evidence reference prefixes. Anything else is not an authorized ref.</summary>
    public static readonly IReadOnlyList<string> EvidenceRefPrefixes =
        ["outcome:", "run:", "event:", "task:", "artifact:", "late-result:", "decision:", "operation:", "https://"];

    public static bool IsEvidenceRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxEvidenceRefChars) return false;
        if (value.Any(char.IsControl)) return false;
        if (value != value.Trim()) return false;
        return EvidenceRefPrefixes.Any(p => value.StartsWith(p, StringComparison.Ordinal) && value.Length > p.Length);
    }
}

/// <summary>Identity of the coordinator's internal failure-review turns.</summary>
public static class WorkflowOutcomeTurnIdentity
{
    /// <summary><c>ConversationRef.ChannelId</c> for the dedicated conversation of one batch.</summary>
    public const string ChannelId = "workflow-outcome";

    /// <summary><c>outcome/&lt;batchId&gt;</c> — the task id of the one internal turn for a batch.</summary>
    public static string TaskId(Guid batchId) => "outcome/" + batchId.ToString("D");

    /// <summary><c>&lt;coordinator&gt;/&lt;generation&gt;</c> — the principal of the dedicated conversation.</summary>
    public static string Principal(string coordinator, long generation) =>
        coordinator + "/" + generation.ToString(CultureInfo.InvariantCulture);
}

public sealed record WorkflowOutcomeItemRef(Guid Id, int Version);

/// <summary>
/// One failure member of a batch: minimal facts only. Evidence text is never included; an item the
/// coordinator may not read in full carries <see cref="AccessNeeded"/> and opaque identifiers.
/// </summary>
public sealed record WorkflowOutcomeInboxItem(
    Guid Id,
    int Version,
    string Namespace,
    string WorkflowId,
    string RunId,
    string WorkflowType,
    string Classification,
    string? ReasonCode,
    string? TerminalEventType,
    long? TerminalEventId,
    DateTimeOffset? ClosedAt,
    string? Project,
    bool AccessNeeded,
    string? TaskOwner,
    Guid? FailureIncidentId,
    IReadOnlyList<string> EvidenceRefs);

public sealed record WorkflowOutcomeInboxBatch(
    Guid BatchId,
    long Generation,
    string Nonce,
    string Coordinator,
    DateTimeOffset CreatedAt,
    string ItemVersionsHash,
    IReadOnlyList<WorkflowOutcomeInboxItem> Items);

public sealed record WorkflowOutcomeInboxPage(IReadOnlyList<WorkflowOutcomeInboxBatch> Batches, string? NextCursor);

/// <summary>Sent only after the coordinator runtime fsynced the batch into its local inbox.</summary>
public sealed record WorkflowOutcomeDeliveredRequest(string Nonce, long Generation, string ItemVersionsHash);

/// <summary>Authoritative receipt readback; used to reconcile a lost POST response.</summary>
public sealed record WorkflowOutcomeReceipt(
    Guid BatchId,
    long Generation,
    string Nonce,
    string ItemVersionsHash,
    IReadOnlyList<WorkflowOutcomeItemRef> Items,
    IReadOnlyList<WorkflowOutcomeItemRef> DeliveredVersions,
    IReadOnlyList<WorkflowOutcomeItemRef> AckedVersions,
    string? ReceiptHash,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? AcknowledgedAt);

/// <summary>Explicit per-item acknowledgement. Never a wildcard; unseen members cannot be acked.</summary>
public sealed record WorkflowOutcomeAckRequest(long Generation, string Nonce, IReadOnlyList<WorkflowOutcomeItemRef> Items, string? TaskId);

/// <summary>Record-only decision. The server never executes it.</summary>
public sealed record WorkflowOutcomeDecisionRequest(
    int ExpectedVersion,
    string Operation,
    string Owner,
    string Reason,
    IReadOnlyList<string> EvidenceRefs,
    DateTimeOffset? DueAt,
    string? TaskRef,
    string? ArtifactRef,
    Guid? BatchId,
    long? Generation);

/// <summary>Operator classification review for a cause_unresolved/insufficient run.</summary>
public sealed record WorkflowOutcomeClassificationRequest(
    int ExpectedVersion,
    string Classification,
    string ReasonCode,
    string TerminalEventRef,
    IReadOnlyList<string> CausalEvidenceRefs,
    string AuthorityRef);

public sealed record WorkflowOutcomeTurnDecision(
    Guid Id,
    int Version,
    string Operation,
    string Owner,
    string Reason,
    IReadOnlyList<string> EvidenceRefs,
    DateTimeOffset? DueAt);

/// <summary>The one raw JSON object a coordinator failure-review turn must return (schema v1).</summary>
public sealed record WorkflowOutcomeTurnOutput(
    int SchemaVersion,
    Guid BatchId,
    long Generation,
    IReadOnlyList<WorkflowOutcomeItemRef> Ack,
    IReadOnlyList<WorkflowOutcomeTurnDecision> Decisions);

/// <summary>Bridge → orchestrator capture of a reply whose waiter was already gone.</summary>
public sealed record WorkflowOutcomeLateResultRequest(
    string? Namespace,
    string TaskId,
    string MessageId,
    string ResultSha256,
    string Status,
    string Text,
    int Attempt,
    DateTimeOffset ReceivedAt);

/// <summary>Operator reservation of one recovery operation for an accepted decision.</summary>
public sealed record WorkflowRecoveryReserveRequest(
    Guid DecisionId,
    int ExpectedVersion,
    string TargetNamespace,
    string TargetWorkflowType,
    string TargetTaskQueue,
    string? TargetAgent,
    string InputSha256,
    string Scope,
    IReadOnlyList<string> StopProofRefs,
    string AuthorityRef);

/// <summary>
/// Bridge → orchestrator, before an existing tool acts with a <c>recovery_operation_id</c>. The
/// orchestrator moves reserved→dispatching exactly once; a second call or lost reply is ambiguous.
/// </summary>
public sealed record WorkflowRecoveryDispatchRequest(
    string Coordinator,
    string Tool,
    string Namespace,
    string? WorkflowType,
    string? TaskQueue,
    string? InputSha256,
    string? TargetWorkflowId,
    string? TargetRunId,
    string? SignalName);

public sealed record WorkflowRecoveryDispatchResponse(bool Allowed, string? ReplacementWorkflowId, string? Reason);

/// <summary>Bridge → orchestrator result of one dispatched operation. <c>Result</c>: started, signaled, cancel_requested, rejected, unknown.</summary>
public sealed record WorkflowRecoveryReceiptRequest(string Result, string? ResultWorkflowId, string? ResultRunId, string? EventRef, string? SanitizedFailure);

public static class WorkflowRecoveryReceiptResults
{
    public const string Started = "started";
    public const string Signaled = "signaled";
    public const string CancelRequested = "cancel_requested";
    public const string Rejected = "rejected";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = [Started, Signaled, CancelRequested, Rejected, Unknown];
}

/// <summary>Hashes that both sides compute identically.</summary>
public static class WorkflowOutcomeHashes
{
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Hash key of an exact run; the originals are always stored and compared beside it.</summary>
    public static string IdentityHash(string @namespace, string workflowId, string runId) =>
        Sha256Hex(@namespace + "\u001f" + workflowId + "\u001f" + runId);

    /// <summary>SHA-256 over the ordinal-sorted <c>"&lt;id:D&gt;:&lt;version&gt;\n"</c> lines.</summary>
    public static string ItemVersionsHash(IEnumerable<WorkflowOutcomeItemRef> items)
    {
        var lines = items
            .Select(i => i.Id.ToString("D") + ":" + i.Version.ToString(CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal);
        return Sha256Hex(string.Concat(lines.Select(l => l + "\n")));
    }
}

/// <summary>
/// Strict parser for <see cref="WorkflowOutcomeTurnOutput"/>. Accepts exactly one raw JSON object
/// (surrounding whitespace only): no fences, prose, comments, trailing text, duplicate or unknown
/// keys, unknown operations or foreign ids. A malformed output acknowledges and decides nothing.
/// </summary>
public static class WorkflowOutcomeTurnOutputParser
{
    private const string OutcomeRefPrefix = "outcome:";

    /// <summary>A finish decision's artifact ref; the coordinator runtime sends the first one as <c>ArtifactRef</c>.</summary>
    public const string ArtifactRefPrefix = "artifact:";

    private static readonly string[] RootKeys = ["schemaVersion", "batchId", "generation", "ack", "decisions"];
    private static readonly string[] AckKeys = ["id", "version"];
    private static readonly string[] DecisionKeys = ["id", "version", "operation", "owner", "reason", "evidenceRefs", "dueAt"];
    private static readonly string[] DecisionRequiredKeys = ["id", "version", "operation", "owner", "reason", "evidenceRefs"];

    /// <param name="raw">The admitted turn's final text, exactly as returned.</param>
    /// <param name="expectedBatchId">The admitted batch.</param>
    /// <param name="expectedGeneration">The admitted batch's coordinator generation.</param>
    /// <param name="deliveredMembers">Members delivered in this batch (exact versions).</param>
    /// <param name="now">Clock for the future-<c>dueAt</c> rule.</param>
    public static bool TryParse(
        string? raw,
        Guid expectedBatchId,
        long expectedGeneration,
        IReadOnlyCollection<WorkflowOutcomeItemRef> deliveredMembers,
        DateTimeOffset now,
        out WorkflowOutcomeTurnOutput? output,
        out string error)
    {
        output = null;
        error = "";
        if (raw is null) { error = "empty"; return false; }
        if (Encoding.UTF8.GetByteCount(raw) > WorkflowOutcomeLimits.MaxTurnOutputBytes) { error = "too_large"; return false; }
        var text = raw.Trim();
        if (text.Length == 0) { error = "empty"; return false; }
        if (text[0] != '{' || text[^1] != '}') { error = "not_a_raw_object"; return false; }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        }
        catch (JsonException) { error = "invalid_json"; return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!ExactKeys(root, RootKeys, RootKeys, out error)) return false;

            if (!TryInt(root.GetProperty("schemaVersion"), out var schema) || schema != 1) { error = "schema_version"; return false; }
            if (!TryGuid(root.GetProperty("batchId"), out var batchId) || batchId != expectedBatchId) { error = "batch_mismatch"; return false; }
            if (!TryLong(root.GetProperty("generation"), out var generation) || generation != expectedGeneration) { error = "generation_mismatch"; return false; }

            var delivered = deliveredMembers.ToDictionary(m => m.Id, m => m.Version);

            var ackElement = root.GetProperty("ack");
            if (ackElement.ValueKind != JsonValueKind.Array || ackElement.GetArrayLength() > WorkflowOutcomeLimits.MaxBatchItems) { error = "ack_shape"; return false; }
            var acks = new List<WorkflowOutcomeItemRef>();
            var ackIds = new HashSet<Guid>();
            foreach (var a in ackElement.EnumerateArray())
            {
                if (!ExactKeys(a, AckKeys, AckKeys, out error)) return false;
                if (!TryGuid(a.GetProperty("id"), out var id)) { error = "ack_id"; return false; }
                if (!TryInt(a.GetProperty("version"), out var version) || version <= 0) { error = "ack_version"; return false; }
                if (!ackIds.Add(id)) { error = "ack_duplicate"; return false; }
                if (!delivered.TryGetValue(id, out var deliveredVersion)) { error = "ack_foreign"; return false; }
                if (deliveredVersion != version) { error = "ack_stale_version"; return false; }
                acks.Add(new WorkflowOutcomeItemRef(id, version));
            }

            var decisionsElement = root.GetProperty("decisions");
            if (decisionsElement.ValueKind != JsonValueKind.Array || decisionsElement.GetArrayLength() > WorkflowOutcomeLimits.MaxBatchItems) { error = "decisions_shape"; return false; }
            var decisions = new List<WorkflowOutcomeTurnDecision>();
            var decisionIds = new HashSet<Guid>();
            foreach (var d in decisionsElement.EnumerateArray())
            {
                if (!ExactKeys(d, DecisionKeys, DecisionRequiredKeys, out error)) return false;
                if (!TryGuid(d.GetProperty("id"), out var id)) { error = "decision_id"; return false; }
                if (!TryInt(d.GetProperty("version"), out var version) || version <= 0) { error = "decision_version"; return false; }
                if (!decisionIds.Add(id)) { error = "decision_duplicate"; return false; }
                if (!ackIds.Contains(id) || acks.First(x => x.Id == id).Version != version) { error = "decision_not_acked"; return false; }

                var opElement = d.GetProperty("operation");
                var operation = opElement.ValueKind == JsonValueKind.String ? opElement.GetString() : null;
                if (!WorkflowOutcomeOperations.IsKnown(operation)) { error = "decision_operation"; return false; }

                if (!TryBoundedString(d.GetProperty("owner"), WorkflowOutcomeLimits.MaxOwnerChars, out var owner)) { error = "decision_owner"; return false; }
                if (!TryBoundedString(d.GetProperty("reason"), WorkflowOutcomeLimits.MaxReasonChars, out var reason)) { error = "decision_reason"; return false; }

                var refsElement = d.GetProperty("evidenceRefs");
                if (refsElement.ValueKind != JsonValueKind.Array || refsElement.GetArrayLength() > WorkflowOutcomeLimits.MaxEvidenceRefs) { error = "decision_evidence_shape"; return false; }
                // The server refuses a decision without evidence; refusing it here keeps it from being acked first.
                if (refsElement.GetArrayLength() == 0) { error = "decision_evidence_empty"; return false; }
                var refs = new List<string>();
                foreach (var r in refsElement.EnumerateArray())
                {
                    var value = r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    if (!WorkflowOutcomeLimits.IsEvidenceRef(value)) { error = "decision_evidence_ref"; return false; }
                    refs.Add(value!);
                }

                DateTimeOffset? dueAt = null;
                if (d.TryGetProperty("dueAt", out var dueElement))
                {
                    if (!TryTimestamp(dueElement, out var parsed)) { error = "decision_due_at"; return false; }
                    dueAt = parsed;
                }
                if (WorkflowOutcomeOperations.RequiresDueAt(operation!) && (dueAt is null || dueAt <= now)) { error = "decision_due_at_required"; return false; }

                // Mirrors of the server's disposition and scope rules that need no database.
                if (refs.Any(r => r.StartsWith(OutcomeRefPrefix, StringComparison.Ordinal) && r != OutcomeRefPrefix + id.ToString("D")))
                { error = "decision_evidence_out_of_scope"; return false; }
                if (operation == WorkflowOutcomeOperations.Finish && !refs.Any(r => r.StartsWith(ArtifactRefPrefix, StringComparison.Ordinal)))
                { error = "decision_finish_artifact_missing"; return false; }
                if (operation == WorkflowOutcomeOperations.Close
                    && !refs.Any(r => !r.StartsWith(OutcomeRefPrefix, StringComparison.Ordinal) && !r.StartsWith("run:", StringComparison.Ordinal)))
                { error = "decision_close_evidence_missing"; return false; }

                decisions.Add(new WorkflowOutcomeTurnDecision(id, version, operation!, owner, reason, refs, dueAt));
            }

            output = new WorkflowOutcomeTurnOutput(schema, batchId, generation, acks, decisions);
            return true;
        }
    }

    private static bool ExactKeys(JsonElement element, string[] allowed, string[] required, out string error)
    {
        error = "";
        if (element.ValueKind != JsonValueKind.Object) { error = "not_object"; return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
        {
            if (!seen.Add(p.Name)) { error = "duplicate_key"; return false; }
            if (!allowed.Contains(p.Name, StringComparer.Ordinal)) { error = "unknown_key"; return false; }
        }
        if (required.Any(r => !seen.Contains(r))) { error = "missing_key"; return false; }
        return true;
    }

    private static bool TryInt(JsonElement e, out int value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value) && e.GetRawText().All(char.IsAsciiDigit);
    }

    private static bool TryLong(JsonElement e, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value) && e.GetRawText().All(char.IsAsciiDigit);
    }

    private static bool TryGuid(JsonElement e, out Guid value)
    {
        value = Guid.Empty;
        return e.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(e.GetString(), "D", out value)
            && value != Guid.Empty;
    }

    private static bool TryBoundedString(JsonElement e, int max, out string value)
    {
        value = "";
        if (e.ValueKind != JsonValueKind.String) return false;
        var s = e.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(s) || s.Length > max) return false;
        value = s;
        return true;
    }

    private static bool TryTimestamp(JsonElement e, out DateTimeOffset value)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.String) return false;
        var s = e.GetString();
        // An explicit offset is required: a zone-less timestamp is ambiguous.
        if (s is null || !(s.EndsWith('Z') || s.Length > 6 && (s[^6] == '+' || s[^6] == '-'))) return false;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }
}
