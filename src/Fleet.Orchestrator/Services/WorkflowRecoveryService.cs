using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Orchestrator.Services;

// ─── Operator-mediated recovery (#476) ───────────────────────────────────────────────────────
// The coordinator only records intent. An operator plans (read-only) and reserves one operation;
// the bridge then asks once to dispatch it through an existing guarded tool and reports the
// receipt. Nothing here starts, signals or cancels a workflow, and an ambiguous dispatch never
// releases or recycles its reservation.

/// <summary>Read-only facts the recovery checks need. Every call is bounded; an error is never a stop proof.</summary>
public interface IWorkflowRecoveryProbe
{
    /// <summary>
    /// The exact run (<paramref name="runId"/> given) or the latest run of the workflow id (null).
    /// Null when Temporal reports the execution does not exist; throws on any error or timeout.
    /// </summary>
    Task<WorkflowRecoveryRunState?> DescribeAsync(string @namespace, string workflowId, string? runId, CancellationToken ct);

    /// <summary>Task ids some agent currently reports as running (heartbeat <c>CurrentTaskId</c>).</summary>
    IReadOnlyCollection<string> ActiveTaskIds();
}

/// <summary>One Describe answer. Type and task queue let a replacement run be matched to its reservation.</summary>
public sealed record WorkflowRecoveryRunState(string RunId, string Status, string? WorkflowType = null, string? TaskQueue = null);

/// <summary><see cref="IWorkflowRecoveryProbe"/> over Temporal Describe and the agent heartbeat registry.</summary>
public sealed class TemporalWorkflowRecoveryProbe(
    TemporalClientRegistry registry,
    AgentRegistry agents,
    IOptions<WorkflowOutcomeOptions> options) : IWorkflowRecoveryProbe
{
    public async Task<WorkflowRecoveryRunState?> DescribeAsync(string @namespace, string workflowId, string? runId, CancellationToken ct)
    {
        var bound = TimeSpan.FromSeconds(Math.Clamp(options.Value.RpcTimeoutSeconds, 1, 60));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(bound);
        var client = await registry.GetClientAsync(@namespace, cts.Token)
            ?? throw new InvalidOperationException("namespace_unavailable");
        try
        {
            var d = await client.GetWorkflowHandle(workflowId, runId: runId)
                .DescribeAsync(new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = cts.Token, Timeout = bound } })
                .WaitAsync(bound, ct);
            return new WorkflowRecoveryRunState(d.RunId ?? "", d.Status.ToString(), d.WorkflowType, d.TaskQueue);
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            return null;
        }
    }

    public IReadOnlyCollection<string> ActiveTaskIds() =>
        agents.GetAll().Select(a => a.CurrentTaskId).OfType<string>().Where(t => t.Length > 0).ToList();
}

/// <summary>Body of <c>POST /{id}/recovery-plan</c>; the proofs are optional so a plan can be rechecked with them.</summary>
public sealed record WorkflowRecoveryPlanRequest(Guid DecisionId, IReadOnlyList<string>? StopProofRefs);

public sealed record WorkflowRecoveryPlan(bool Allowed, string Operation, string? BlockedReason, IReadOnlyList<string> MissingProofs, DateTimeOffset CheckedAt);

public sealed class WorkflowRecoveryService(
    OrchestratorDbContext db,
    WorkflowOutcomeAuthorization auth,
    IWorkflowRecoveryProbe probe,
    IWorkflowRecoveryArtifactReader artifacts,
    WorkflowRecoveryApprovals approvals,
    IOptions<WorkflowOutcomeOptions> options,
    TimeProvider clock,
    ILogger<WorkflowRecoveryService> logger)
{
    public const string ReplacementPrefix = "recovery-";
    public const string LateSuccessStatus = "completed";
    private const int MaxStopProofs = 50;

    public const string ReplacementUnverified = "replacement_unverified";
    public const string ReplacementMismatch = "replacement_mismatch";

    /// <summary>A safety verdict; <see cref="ArtifactHash"/> is what an allowed reservation binds about its issue/PR.</summary>
    private sealed record Check(string? BlockedReason, IReadOnlyList<string> MissingProofs, string? ArtifactHash = null)
    {
        public static readonly Check Allowed = new(null, []);
        public static Check Block(string reason, params string[] missing) => new(reason, missing);
    }

    // ── Plan ──────────────────────────────────────────────────────────────────

    /// <summary>Read-only authoritative recheck. Never cancels, starts or reserves anything.</summary>
    public async Task<WorkflowOutcomeResult> PlanAsync(WorkflowOutcomePrincipal principal, Guid outcomeId, WorkflowRecoveryPlanRequest? request, CancellationToken ct)
    {
        if (!principal.IsOperator) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request is null || request.DecisionId == Guid.Empty) return WorkflowOutcomeResult.Error(400, "malformed_request");
        var proofs = request.StopProofRefs ?? [];
        if (proofs.Count > MaxStopProofs || !proofs.All(WorkflowOutcomeLimits.IsEvidenceRef)) return WorkflowOutcomeResult.Error(400, "stop_proofs_invalid");

        var run = await db.WorkflowOutcomeRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == outcomeId, ct);
        if (run is null) return WorkflowOutcomeResult.Error(404, "outcome_not_found");
        var decision = await db.WorkflowOutcomeDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.DecisionId && d.OutcomeId == outcomeId, ct);
        if (decision is null) return WorkflowOutcomeResult.Error(404, "decision_not_found");

        var check = await CheckAsync(run, decision, proofs, decision.Operation == WorkflowOutcomeOperations.Reassign ? decision.Owner : null, null, null, ct);
        return WorkflowOutcomeResult.Ok(new WorkflowRecoveryPlan(check.BlockedReason is null, decision.Operation, check.BlockedReason,
            check.MissingProofs, WorkflowOutcomeApi.Utc(WorkflowOutcomeApi.Now(clock))));
    }

    /// <summary>
    /// Every safety rule, cheapest first, returning the first precise blocking reason. Retry and
    /// reassign need an authoritative terminal source, no running successor, task or replacement,
    /// no late success, a stop proof per recorded task and, when an issue/PR exists, its exact
    /// artifact proof plus a read of the issue/PR/head/branch/checks that finds no existing work.
    /// </summary>
    private async Task<Check> CheckAsync(
        WorkflowOutcomeRun run, WorkflowOutcomeDecision decision, IReadOnlyList<string> proofs, string? targetAgent,
        string? targetNamespace, Guid? operationId, CancellationToken ct)
    {
        if (decision.Status != "accepted") return Check.Block("decision_not_accepted");
        if (decision.ExpectedVersion != run.Version) return Check.Block("stale_decision");
        if (run.ResolvedAt is not null) return Check.Block("outcome_resolved");
        if (run.Classification != WorkflowOutcomeClassifications.ConfirmedFailure) return Check.Block("not_confirmed_failure");

        var source = RunRef(run.Namespace, run.WorkflowId, run.RunId);
        switch (decision.Operation)
        {
            case WorkflowOutcomeOperations.Resume:
                // No deployed delegate-recovery handler exists in v1; a closed run is never resumed.
                return Check.Block(WorkflowOutcomeApi.IsTerminal(run.Status) ? "closed_run_cannot_resume" : "delegate_recovery_handler_unavailable");
            case WorkflowOutcomeOperations.AcceptLate:
                return Check.Block("delegate_recovery_handler_unavailable");
            case WorkflowOutcomeOperations.Cancel:
            {
                WorkflowRecoveryRunState? target;
                try { target = await probe.DescribeAsync(run.Namespace, run.WorkflowId, run.RunId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return Check.Block("target_state_unverified", source); }
                if (target is null) return Check.Block("target_not_found", source);
                return target.Status == "Running" ? Check.Allowed : Check.Block("closed_target_noop");
            }
            case WorkflowOutcomeOperations.Retry or WorkflowOutcomeOperations.Reassign:
                break;
            default:
                return Check.Block("not_a_recovery_operation");
        }

        if (await db.WorkflowRecoveryOperations.AnyAsync(o => o.OutcomeId == run.Id && o.State != WorkflowRecoveryStates.Blocked, ct))
            return Check.Block("replacement_exists");
        if (decision.Operation == WorkflowOutcomeOperations.Reassign)
        {
            var agent = targetAgent?.Trim();
            if (string.IsNullOrEmpty(agent) || !await db.Agents.AnyAsync(a => a.Name == agent, ct))
                return Check.Block("reassign_target_ineligible");
        }

        var taskIds = WorkflowOutcomeApi.TaskIds(run);
        if (taskIds is null) return Check.Block("task_ledger_missing", source);
        if (await LateSuccessAsync(run, taskIds, ct)) return Check.Block("late_success_present");
        var running = probe.ActiveTaskIds().Intersect(taskIds, StringComparer.Ordinal).Select(t => "task:" + t).ToArray();
        if (running.Length > 0) return Check.Block("task_running", running);

        WorkflowRecoveryRunState? exact, latest;
        try { exact = await probe.DescribeAsync(run.Namespace, run.WorkflowId, run.RunId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return Check.Block("source_state_unverified", source); }
        if (exact is null) return Check.Block("source_run_not_found", source);
        if (!WorkflowOutcomeApi.IsTerminal(exact.Status)) return Check.Block("source_running", source);

        try { latest = await probe.DescribeAsync(run.Namespace, run.WorkflowId, null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return Check.Block("successor_state_unverified", "run:" + run.Namespace + "/" + run.WorkflowId); }
        if (latest is not null && latest.RunId != run.RunId && !WorkflowOutcomeApi.IsTerminal(latest.Status))
            return Check.Block("successor_running", RunRef(run.Namespace, run.WorkflowId, latest.RunId));

        if (operationId is { } op && targetNamespace is not null)
        {
            var replacementId = ReplacementPrefix + op.ToString("D");
            try
            {
                if (await probe.DescribeAsync(targetNamespace, replacementId, null, ct) is not null) return Check.Block("replacement_exists");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Check.Block("replacement_state_unverified", "run:" + targetNamespace + "/" + replacementId);
            }
        }

        var missingTasks = taskIds.Select(t => "task:" + t).Where(r => !proofs.Contains(r, StringComparer.Ordinal)).ToList();
        if (missingTasks.Count > 0) return new Check("stop_proof_missing", missingTasks);
        return await ArtifactAsync(run, proofs, ct);
    }

    /// <summary>
    /// The run's issue/PR before a new attempt. At reserve the operator's artifact proof must be
    /// exactly <see cref="ArtifactRef"/>, and any other artifact proof is refused; the artifact is
    /// then read. A merged or open pull request, unfinished check runs, a head branch that moved
    /// past the pull request, a closed issue or an open pull request referencing the issue is
    /// existing work and blocks. An unreadable artifact is unverified. Allowed carries the hash of
    /// the exact reference and the facts read, so a later change invalidates the reservation.
    /// </summary>
    private async Task<Check> ArtifactAsync(WorkflowOutcomeRun run, IReadOnlyList<string>? proofs, CancellationToken ct)
    {
        if (run.Repo is null) return Check.Allowed;
        if (run.IssueNumber is null && run.PrNumber is null)
            return Check.Allowed with { ArtifactHash = WorkflowOutcomeHashes.Sha256Hex("artifact:" + run.Repo) };

        var expected = ArtifactRef(run);
        if (proofs is not null)
        {
            var given = proofs.Where(p => p.StartsWith("artifact:", StringComparison.Ordinal)).ToList();
            if (given.Any(p => !string.Equals(p, expected, StringComparison.Ordinal))) return Check.Block("artifact_proof_mismatch", expected);
            if (given.Count == 0) return Check.Block("artifact_state_unverified", expected);
        }

        var read = await artifacts.ReadAsync(run.Repo, run.IssueNumber, run.PrNumber, ct);
        if (read.Facts is not { } facts) return Check.Block("artifact_state_unverified", expected);
        if (facts.Kind != (run.PrNumber is null ? WorkflowRecoveryArtifactFacts.Issue : WorkflowRecoveryArtifactFacts.PullRequest))
            return Check.Block("artifact_state_unverified", expected);
        if (facts.Kind == WorkflowRecoveryArtifactFacts.PullRequest)
        {
            if (facts.Merged) return Check.Block("artifact_merged", expected);
            if (facts.State == "open") return Check.Block("artifact_pr_open", expected);
            if (facts.PendingChecks > 0) return Check.Block("artifact_checks_running", expected);
            if (facts.BranchSha is not null && facts.BranchSha != facts.HeadSha) return Check.Block("artifact_branch_moved", expected);
        }
        else
        {
            if (facts.State != "open") return Check.Block("artifact_issue_closed", expected);
            if (facts.OpenPullRequests.Count > 0)
                return new Check("artifact_pr_open", facts.OpenPullRequests
                    .Select(n => "artifact:" + run.Repo + "#pr/" + n.ToString(CultureInfo.InvariantCulture)).ToArray());
        }
        return Check.Allowed with { ArtifactHash = WorkflowOutcomeHashes.Sha256Hex(expected + "\n" + facts.Digest) };
    }

    private Task<bool> LateSuccessAsync(WorkflowOutcomeRun run, IReadOnlyList<string> taskIds, CancellationToken ct) =>
        db.WorkflowOutcomeLateResults.AnyAsync(l => l.ResultStatus == LateSuccessStatus && (l.OutcomeId == run.Id || taskIds.Contains(l.TaskId)), ct);

    private static string RunRef(string ns, string workflowId, string runId) => "run:" + ns + "/" + workflowId + "/" + runId;

    private static string ArtifactRef(WorkflowOutcomeRun run) =>
        "artifact:" + run.Repo + (run.PrNumber is { } pr ? "#pr/" + pr.ToString(CultureInfo.InvariantCulture) : "#issue/" + run.IssueNumber!.Value.ToString(CultureInfo.InvariantCulture));

    // ── Reserve ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Operator-only, once per decision and once per outcome: binds coordinator, decision, exact
    /// source run, target, scope, proofs and authority. The replacement id is <c>recovery-&lt;operationId&gt;</c>.
    /// </summary>
    public async Task<WorkflowOutcomeResult> ReserveAsync(WorkflowOutcomePrincipal principal, Guid operationId, WorkflowRecoveryReserveRequest? request, CancellationToken ct)
    {
        if (!principal.IsOperator) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request is null || request.DecisionId == Guid.Empty) return WorkflowOutcomeResult.Error(400, "malformed_request");
        var proofs = request.StopProofRefs ?? [];
        if (string.IsNullOrWhiteSpace(request.TargetNamespace) || request.TargetNamespace.Length > 64
            || string.IsNullOrWhiteSpace(request.Scope) || request.Scope.Length > 500
            || request.TargetAgent is { Length: > 128 } || request.TargetWorkflowType is { Length: > 255 } || request.TargetTaskQueue is { Length: > 255 }
            || proofs.Count > MaxStopProofs || !proofs.All(WorkflowOutcomeLimits.IsEvidenceRef))
            return WorkflowOutcomeResult.Error(422, "request_invalid");
        if (!WorkflowOutcomeApi.IsAuthorityRef(request.AuthorityRef)) return WorkflowOutcomeResult.Error(422, "authority_missing");

        var decision = await db.WorkflowOutcomeDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.DecisionId, ct);
        if (decision is null) return WorkflowOutcomeResult.Error(404, "decision_not_found");
        var run = await db.WorkflowOutcomeRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == decision.OutcomeId, ct);
        if (run is null) return WorkflowOutcomeResult.Error(404, "outcome_not_found");
        if (request.ExpectedVersion != run.Version || decision.ExpectedVersion != run.Version)
            return new(409, new { error = "stale_version", currentVersion = run.Version });
        // One reservation per operation id, per decision and per live failure attempt.
        if (await db.WorkflowRecoveryOperations.AnyAsync(o => o.Id == operationId || o.DecisionId == decision.Id
                || (o.OutcomeId == run.Id && o.State != WorkflowRecoveryStates.Blocked), ct))
            return WorkflowOutcomeResult.Error(409, "already_reserved");

        var start = decision.Operation is WorkflowOutcomeOperations.Retry or WorkflowOutcomeOperations.Reassign;
        if (start)
        {
            if (string.IsNullOrWhiteSpace(request.TargetWorkflowType) || string.IsNullOrWhiteSpace(request.TargetTaskQueue)
                || !WorkflowOutcomeApi.IsHex64(request.InputSha256))
                return WorkflowOutcomeResult.Error(422, "target_invalid");
            if (!options.Value.NamespaceList().Contains(request.TargetNamespace, StringComparer.Ordinal))
                return WorkflowOutcomeResult.Error(422, "target_namespace_unknown");
        }
        else if (decision.Operation == WorkflowOutcomeOperations.Cancel && request.TargetNamespace != run.Namespace)
            return WorkflowOutcomeResult.Error(422, "target_mismatch");

        var coordinator = auth.Coordinator;
        if (coordinator is null || !WorkflowOutcomeApi.SameName(run.Coordinator, coordinator))
            return WorkflowOutcomeResult.Error(422, "coordinator_unassigned");

        var check = await CheckAsync(run, decision, proofs, request.TargetAgent, request.TargetNamespace, operationId, ct);
        if (check.BlockedReason is not null)
            return new(422, new { error = check.BlockedReason, missingProofs = check.MissingProofs });

        var now = WorkflowOutcomeApi.Now(clock);
        var taskIds = WorkflowOutcomeApi.TaskIds(run) ?? [];
        var row = new WorkflowRecoveryOperation
        {
            Id = operationId,
            DecisionId = decision.Id,
            OutcomeId = run.Id,
            OutcomeVersion = run.Version,
            Operation = decision.Operation,
            SourceNamespace = run.Namespace,
            SourceWorkflowId = run.WorkflowId,
            SourceRunId = run.RunId,
            SourceTaskHash = WorkflowOutcomeHashes.Sha256Hex(string.Join('\n', taskIds.Order(StringComparer.Ordinal))),
            SourceArtifactHash = check.ArtifactHash,
            TargetNamespace = request.TargetNamespace,
            TargetWorkflowType = start ? request.TargetWorkflowType : null,
            TargetTaskQueue = start ? request.TargetTaskQueue : null,
            TargetAgent = request.TargetAgent,
            InputSha256 = start ? request.InputSha256 : null,
            ReplacementWorkflowId = ReplacementPrefix + operationId.ToString("D"),
            Scope = request.Scope,
            Coordinator = coordinator,
            StopProofRefsJson = JsonSerializer.Serialize(proofs),
            AuthorityRef = request.AuthorityRef,
            State = WorkflowRecoveryStates.Reserved,
            ReservedBy = WorkflowOutcomePrincipal.OperatorKind,
            ReservedAt = now,
            UpdatedAt = now,
        };

        // Deny-by-default: an approval must cover this exact action; a namespace or URL shape is not authority.
        if (approvals.Covers(ActionOf(row)) is { } unauthorized) return WorkflowOutcomeResult.Error(422, unauthorized);

        string? refusal;
        try
        {
            refusal = await WorkflowOutcomeApi.InTransactionAsync<string?>(db, async () =>
            {
                // Serialized on the run row: of two racing reservations for one failure, one wins.
                var locked = await WorkflowOutcomeApi.LockRunAsync(db, run.Id, ct);
                if (locked is null || locked.Version != run.Version || locked.ResolvedAt is not null) return ("stale_version", false);
                if (await db.WorkflowRecoveryOperations.AnyAsync(o => o.OutcomeId == run.Id && o.State != WorkflowRecoveryStates.Blocked, ct))
                    return ("already_reserved", false);
                if (await LateSuccessAsync(run, taskIds, ct)) return ("late_success_present", false);
                db.WorkflowRecoveryOperations.Add(row);
                await db.SaveChangesAsync(ct);
                return (null, true);
            }, ct);
        }
        catch (Exception ex) when (EpicGrantService.IsDuplicateKey(ex))
        {
            db.ChangeTracker.Clear();
            refusal = "already_reserved";
        }

        if (refusal is not null)
            return refusal == "late_success_present"
                ? new(422, new { error = refusal, missingProofs = Array.Empty<string>() })
                : WorkflowOutcomeResult.Error(409, refusal);

        logger.LogInformation("workflow_recovery_reserved operation={OperationId} outcome={OutcomeId} kind={Operation}", operationId, run.Id, decision.Operation);
        return new(201, View(row));
    }

    private static WorkflowRecoveryAction ActionOf(WorkflowRecoveryOperation o) =>
        new(o.AuthorityRef, o.Operation, o.OutcomeId, o.DecisionId, o.TargetNamespace, o.TargetWorkflowType, o.TargetTaskQueue, o.TargetAgent, o.InputSha256);

    // ── Readback ──────────────────────────────────────────────────────────────

    public async Task<WorkflowOutcomeResult> GetAsync(WorkflowOutcomePrincipal principal, Guid operationId, CancellationToken ct)
    {
        if (!principal.IsOperator && !principal.IsBridge) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        var row = await db.WorkflowRecoveryOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId, ct);
        return row is null ? WorkflowOutcomeResult.Error(404, "operation_not_found") : WorkflowOutcomeResult.Ok(View(row));
    }

    private static object View(WorkflowRecoveryOperation o) => new
    {
        operationId = o.Id,
        decisionId = o.DecisionId,
        outcomeId = o.OutcomeId,
        outcomeVersion = o.OutcomeVersion,
        operation = o.Operation,
        state = o.State,
        coordinator = o.Coordinator,
        source = new { @namespace = o.SourceNamespace, workflowId = o.SourceWorkflowId, runId = o.SourceRunId },
        target = new { @namespace = o.TargetNamespace, workflowType = o.TargetWorkflowType, taskQueue = o.TargetTaskQueue, agent = o.TargetAgent, inputSha256 = o.InputSha256 },
        replacementWorkflowId = o.ReplacementWorkflowId,
        scope = o.Scope,
        stopProofRefs = WorkflowOutcomeApi.Strings(o.StopProofRefsJson),
        authorityRef = o.AuthorityRef,
        reservedBy = o.ReservedBy,
        reservedAt = WorkflowOutcomeApi.Utc(o.ReservedAt),
        dispatchingAt = WorkflowOutcomeApi.Utc(o.DispatchingAt),
        resultRunId = o.ResultRunId,
        receiptEventRef = o.ReceiptEventRef,
        receipt = o.ReceiptJson is null ? null : JsonNode.Parse(o.ReceiptJson),
        blockedReason = o.BlockedReason,
        updatedAt = WorkflowOutcomeApi.Utc(o.UpdatedAt),
    };

    // ── Dispatch (bridge) ─────────────────────────────────────────────────────

    /// <summary>
    /// The bridge asks before an existing tool acts with a <c>recovery_operation_id</c>. On an exact
    /// match the reservation moves reserved→dispatching once (a conditional UPDATE); every later
    /// call, including a retry after a lost reply, is 409 and must be resolved by readback.
    /// </summary>
    public async Task<WorkflowOutcomeResult> AuthorizeDispatchAsync(WorkflowOutcomePrincipal principal, Guid operationId, WorkflowRecoveryDispatchRequest? request, CancellationToken ct)
    {
        if (!principal.IsBridge) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request is null || string.IsNullOrWhiteSpace(request.Coordinator) || string.IsNullOrWhiteSpace(request.Tool) || string.IsNullOrWhiteSpace(request.Namespace))
            return WorkflowOutcomeResult.Error(400, "malformed_request");
        var op = await db.WorkflowRecoveryOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (op is null) return WorkflowOutcomeResult.Error(404, "operation_not_found");

        if (auth.Coordinator is null || !WorkflowOutcomeApi.SameName(request.Coordinator, op.Coordinator)
            || !WorkflowOutcomeApi.SameName(request.Coordinator, auth.Coordinator))
            return Deny(422, "coordinator_mismatch");
        if (Mismatch(op, request) is { } mismatch) return Deny(422, mismatch);
        if (op.State != WorkflowRecoveryStates.Reserved)
            return Deny(409, op.State == WorkflowRecoveryStates.Blocked ? "blocked" : "already_dispatching");

        // Recheck the reservation's facts. A changed fact invalidates the proof for good; a probe
        // that cannot answer only refuses this call.
        var run = await db.WorkflowOutcomeRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == op.OutcomeId, ct);
        var decision = await db.WorkflowOutcomeDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == op.DecisionId, ct);
        string? invalidated = run is null || decision is null || run.Version != op.OutcomeVersion ? "stale_reservation"
            : run.ResolvedAt is not null ? "outcome_resolved"
            : null;
        if (invalidated is null && approvals.Covers(ActionOf(op)) is { } unauthorized)
        {
            // An unreadable approvals file only refuses this call; a revoked or expired approval is final.
            if (unauthorized == WorkflowRecoveryApprovals.Unverified) return Deny(422, unauthorized);
            invalidated = unauthorized;
        }
        if (invalidated is null)
        {
            var check = await RecheckAsync(op, run!, ct);
            if (check.BlockedReason is { } reason)
            {
                if (reason.EndsWith("_unverified", StringComparison.Ordinal)) return Deny(422, reason);
                invalidated = reason;
            }
        }
        if (invalidated is not null)
        {
            await BlockAsync(op.Id, WorkflowRecoveryStates.Reserved, invalidated, ct);
            return Deny(422, invalidated);
        }

        var now = WorkflowOutcomeApi.Now(clock);
        var moved = await db.WorkflowRecoveryOperations
            .Where(o => o.Id == operationId && o.State == WorkflowRecoveryStates.Reserved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.State, WorkflowRecoveryStates.Dispatching)
                .SetProperty(o => o.DispatchingAt, now)
                .SetProperty(o => o.UpdatedAt, now), ct);
        if (moved != 1) return Deny(409, "already_dispatching");

        logger.LogInformation("workflow_recovery_dispatching operation={OperationId} kind={Operation}", operationId, op.Operation);
        return WorkflowOutcomeResult.Ok(new WorkflowRecoveryDispatchResponse(true, IsStart(op) ? op.ReplacementWorkflowId : null, null));
    }

    /// <summary>The dispatch-time recheck: the source/successor/task facts a reservation relied on.</summary>
    private async Task<Check> RecheckAsync(WorkflowRecoveryOperation op, WorkflowOutcomeRun run, CancellationToken ct)
    {
        if (!IsStart(op))
        {
            WorkflowRecoveryRunState? target;
            try { target = await probe.DescribeAsync(op.SourceNamespace, op.SourceWorkflowId, op.SourceRunId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return Check.Block("target_state_unverified"); }
            return target is { Status: "Running" } ? Check.Allowed : Check.Block("closed_target_noop");
        }

        var taskIds = WorkflowOutcomeApi.TaskIds(run) ?? [];
        if (await LateSuccessAsync(run, taskIds, ct)) return Check.Block("late_success_present");
        if (probe.ActiveTaskIds().Intersect(taskIds, StringComparer.Ordinal).Any()) return Check.Block("task_running");
        try
        {
            var latest = await probe.DescribeAsync(op.SourceNamespace, op.SourceWorkflowId, null, ct);
            if (latest is not null && latest.RunId != op.SourceRunId && !WorkflowOutcomeApi.IsTerminal(latest.Status))
                return Check.Block("successor_running");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Check.Block("successor_state_unverified");
        }

        // The issue/PR the proof covered: a change since the reserve (new head, reopened, checks
        // running, a new pull request) invalidates it; an unreadable artifact only refuses this call.
        var artifact = await ArtifactAsync(run, null, ct);
        if (artifact.BlockedReason is not null) return artifact;
        return artifact.ArtifactHash == op.SourceArtifactHash ? Check.Allowed : Check.Block("artifact_changed");
    }

    private static bool IsStart(WorkflowRecoveryOperation op) =>
        op.Operation is WorkflowOutcomeOperations.Retry or WorkflowOutcomeOperations.Reassign;

    /// <summary>The first field of the tool call that differs from the reservation, or null on an exact match.</summary>
    private static string? Mismatch(WorkflowRecoveryOperation op, WorkflowRecoveryDispatchRequest request)
    {
        var tool = request.Tool.Trim() switch
        {
            "temporal_start_workflow" or "start" => "start",
            "temporal_cancel_workflow" or "cancel" => "cancel",
            "temporal_signal_workflow" or "signal" => "signal",
            _ => "",
        };
        if (IsStart(op))
        {
            if (tool != "start") return "tool_mismatch";
            if (request.Namespace != op.TargetNamespace) return "namespace_mismatch";
            if (request.WorkflowType != op.TargetWorkflowType) return "workflow_type_mismatch";
            if (request.TaskQueue != op.TargetTaskQueue) return "task_queue_mismatch";
            if (request.InputSha256?.ToLowerInvariant() != op.InputSha256) return "input_mismatch";
            if (request.TargetWorkflowId is not null && request.TargetWorkflowId != op.ReplacementWorkflowId) return "target_mismatch";
            return null;
        }
        if (op.Operation == WorkflowOutcomeOperations.Cancel)
        {
            if (tool != "cancel") return "tool_mismatch";
            if (request.Namespace != op.SourceNamespace) return "namespace_mismatch";
            if (request.TargetWorkflowId != op.SourceWorkflowId || request.TargetRunId != op.SourceRunId) return "target_mismatch";
            return null;
        }
        // No signal-based recovery (resume/accept_late) has a deployed handler in v1.
        return "tool_mismatch";
    }

    private static WorkflowOutcomeResult Deny(int status, string reason) =>
        new(status, new WorkflowRecoveryDispatchResponse(false, null, reason));

    private async Task BlockAsync(Guid id, string from, string reason, CancellationToken ct)
    {
        var now = WorkflowOutcomeApi.Now(clock);
        await db.WorkflowRecoveryOperations.Where(o => o.Id == id && o.State == from)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.State, WorkflowRecoveryStates.Blocked)
                .SetProperty(o => o.BlockedReason, reason)
                .SetProperty(o => o.UpdatedAt, now), ct);
        logger.LogInformation("workflow_recovery_blocked operation={OperationId} reason={Reason}", id, reason);
    }

    // ── Receipt (bridge) ──────────────────────────────────────────────────────

    /// <summary>
    /// Monotonic: started→dispatched (or verified with an event readback), rejected→blocked with the
    /// definitive rejection retained, unknown→dispatch_unknown. dispatch_unknown is permanent until a
    /// readback is confirmed; nothing ever returns to reserved.
    /// </summary>
    /// <remarks>
    /// A readback is never trusted on the bridge's word: <see cref="VerifyReadbackAsync"/> asks
    /// Temporal for the exact run first. An unreadable answer leaves a positive receipt dispatched
    /// (a later readback may still verify it); an answer that contradicts the receipt makes the
    /// dispatch outcome unknown, with its health incident, rather than verified.
    /// </remarks>
    public async Task<WorkflowOutcomeResult> ReceiptAsync(WorkflowOutcomePrincipal principal, Guid operationId, WorkflowRecoveryReceiptRequest? request, CancellationToken ct)
    {
        if (!principal.IsBridge) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request is null || !WorkflowRecoveryReceiptResults.All.Contains(request.Result)
            || request.ResultRunId is { Length: > 64 } || request.ResultWorkflowId is { Length: > 255 }
            || request.EventRef is not null && !WorkflowOutcomeLimits.IsEvidenceRef(request.EventRef)
            || request.SanitizedFailure is { Length: > 500 })
            return WorkflowOutcomeResult.Error(400, "malformed_request");

        var op = await db.WorkflowRecoveryOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (op is null) return WorkflowOutcomeResult.Error(404, "operation_not_found");

        var receiptJson = JsonSerializer.Serialize(request, WorkflowOutcomeApi.Json);
        if (op.ReceiptJson == receiptJson) return WorkflowOutcomeResult.Ok(View(op));

        var start = IsStart(op);
        var positive = request.Result == (start ? WorkflowRecoveryReceiptResults.Started : WorkflowRecoveryReceiptResults.CancelRequested);
        if (request.Result is WorkflowRecoveryReceiptResults.Started or WorkflowRecoveryReceiptResults.CancelRequested or WorkflowRecoveryReceiptResults.Signaled)
        {
            var expectedWorkflow = start ? op.ReplacementWorkflowId : op.SourceWorkflowId;
            if (!positive || request.ResultWorkflowId is not null && request.ResultWorkflowId != expectedWorkflow
                || start && string.IsNullOrEmpty(request.ResultRunId)
                || !start && request.ResultRunId is not null && request.ResultRunId != op.SourceRunId)
                return WorkflowOutcomeResult.Error(422, "receipt_mismatch");
        }
        var readback = positive && !string.IsNullOrEmpty(request.ResultRunId) && request.EventRef is not null;
        // A readback the eligible transitions would accept is checked against Temporal first.
        var eligible = readback && (op.State is WorkflowRecoveryStates.Dispatching or WorkflowRecoveryStates.DispatchUnknown
            || op.State == WorkflowRecoveryStates.Dispatched && (op.ResultRunId is null || op.ResultRunId == request.ResultRunId));
        var verification = eligible ? await VerifyReadbackAsync(op, request.ResultRunId!, ct) : null;
        var confirmed = eligible && verification is null;
        var contradicted = verification == ReplacementMismatch;

        var to = (op.State, request.Result) switch
        {
            (WorkflowRecoveryStates.Dispatching, WorkflowRecoveryReceiptResults.Rejected) => WorkflowRecoveryStates.Blocked,
            (WorkflowRecoveryStates.Dispatching, WorkflowRecoveryReceiptResults.Unknown) => WorkflowRecoveryStates.DispatchUnknown,
            (WorkflowRecoveryStates.Dispatching, _) when positive => contradicted ? WorkflowRecoveryStates.DispatchUnknown
                : confirmed ? WorkflowRecoveryStates.Verified : WorkflowRecoveryStates.Dispatched,
            (WorkflowRecoveryStates.Dispatched, _) when contradicted => WorkflowRecoveryStates.DispatchUnknown,
            (WorkflowRecoveryStates.Dispatched, _) when confirmed => WorkflowRecoveryStates.Verified,
            (WorkflowRecoveryStates.DispatchUnknown, _) when confirmed => WorkflowRecoveryStates.Verified,
            _ => null,
        };
        if (to is null)
            return new(409, new
            {
                error = verification ?? op.State switch
                {
                    WorkflowRecoveryStates.Reserved => "not_dispatching",
                    WorkflowRecoveryStates.DispatchUnknown => "dispatch_unknown_requires_readback",
                    _ => "not_monotonic",
                },
                state = op.State,
            });

        var now = WorkflowOutcomeApi.Now(clock);
        var blocked = to == WorkflowRecoveryStates.Blocked ? "dispatch_rejected" : op.BlockedReason;
        var incident = to == WorkflowRecoveryStates.DispatchUnknown || op.State == WorkflowRecoveryStates.DispatchUnknown;
        var reason = contradicted
            ? "recovery receipt contradicts Temporal for the replacement; inspect the replacement id before any further action"
            : "recovery dispatch outcome unknown; inspect the replacement id before any further action";
        // The state change and its incident commit together: an unknown dispatch never lacks its alert.
        var moved = await WorkflowOutcomeApi.InTransactionAsync<bool>(db, async () =>
        {
            var changed = await db.WorkflowRecoveryOperations.Where(o => o.Id == operationId && o.State == op.State)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.State, to)
                    .SetProperty(o => o.ResultRunId, request.ResultRunId ?? op.ResultRunId)
                    .SetProperty(o => o.ReceiptEventRef, request.EventRef ?? op.ReceiptEventRef)
                    .SetProperty(o => o.ReceiptJson, receiptJson)
                    .SetProperty(o => o.BlockedReason, blocked)
                    .SetProperty(o => o.UpdatedAt, now), ct);
            if (changed != 1) return (false, false);
            if (incident)
                await WorkflowOutcomeApi.UpsertHealthAsync(db, "recovery", "dispatch_unknown:" + operationId.ToString("D"),
                    to == WorkflowRecoveryStates.DispatchUnknown, reason, 1, now, ct);
            return (true, true);
        }, ct);
        if (!moved) return new(409, new { error = "not_monotonic", state = op.State });

        logger.LogInformation("workflow_recovery_receipt operation={OperationId} from={From} to={To} verification={Verification}",
            operationId, op.State, to, verification ?? (confirmed ? "confirmed" : "none"));
        var fresh = await db.WorkflowRecoveryOperations.AsNoTracking().FirstAsync(o => o.Id == operationId, ct);
        return WorkflowOutcomeResult.Ok(View(fresh));
    }

    /// <summary>
    /// Temporal's own answer for the run a readback names. A start is confirmed only as the exact
    /// <c>recovery-&lt;id&gt;</c> run with the reserved workflow type and task queue; a cancel only as
    /// the exact source run, now Canceled. Null when confirmed; <see cref="ReplacementMismatch"/> when
    /// Temporal contradicts the receipt; <see cref="ReplacementUnverified"/> when it cannot tell yet.
    /// </summary>
    private async Task<string?> VerifyReadbackAsync(WorkflowRecoveryOperation op, string runId, CancellationToken ct)
    {
        var start = IsStart(op);
        WorkflowRecoveryRunState? state;
        try
        {
            state = start
                ? await probe.DescribeAsync(op.TargetNamespace, op.ReplacementWorkflowId, runId, ct)
                : await probe.DescribeAsync(op.SourceNamespace, op.SourceWorkflowId, runId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ReplacementUnverified;
        }
        if (state is null || !string.Equals(state.RunId, runId, StringComparison.Ordinal)) return ReplacementMismatch;
        if (start)
            return string.Equals(state.WorkflowType, op.TargetWorkflowType, StringComparison.Ordinal)
                && string.Equals(state.TaskQueue, op.TargetTaskQueue, StringComparison.Ordinal) ? null : ReplacementMismatch;
        return state.Status == "Canceled" ? null : ReplacementUnverified;
    }
}
