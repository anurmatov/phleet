using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// Operator-mediated recovery (#476) over real HTTP: the read-only plan, the one-time
/// reservation, the bridge's once-only dispatch authorization and the monotonic receipt.
/// Temporal is the scripted probe, which has no way to start, signal or cancel anything.
/// </summary>
public sealed class WorkflowRecoveryTests : IAsyncLifetime
{
    private const string Root = "/api/workflow-outcomes";
    private const string Authority = "https://example.com/approvals/42";
    private const string InputHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private ApiHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await ApiHost.StartAsync();
        await _host.GrantProjectAsync();
        await _host.SeedAgentsAsync("agent1", "agent2");
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _host.Database.DisposeAsync();
    }

    /// <summary>An acked failure with an accepted decision and a terminal source run in Temporal.</summary>
    private async Task<(WorkflowOutcomeRun Run, Guid DecisionId)> DecidedAsync(string operation = "retry", Action<WorkflowOutcomeRun>? configure = null, string owner = "agent2")
    {
        var (run, _) = await _host.AckedFailureAsync(configure);
        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, run.Status);
        var response = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, operation, owner: owner));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (run, (await ApiHost.JsonAsync(response)).GetProperty("decisionId").GetGuid());
    }

    private async Task<Guid> InsertDecisionAsync(WorkflowOutcomeRun run, string operation)
    {
        var id = Guid.NewGuid();
        await _host.Database.WithContextAsync(async db =>
        {
            db.WorkflowOutcomeDecisions.Add(new WorkflowOutcomeDecision
            {
                Id = id, OutcomeId = run.Id, ExpectedVersion = run.Version, Operation = operation, DecidedBy = "operator", DecidedVia = "operator",
                Owner = "operator", Reason = "r", EvidenceRefsJson = "[]", Status = "accepted", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    private static string[] Proofs(WorkflowOutcomeRun run, params string[] extra) => ["task:" + run.WorkflowId + "/step-a", .. extra];

    /// <summary>The exact action <see cref="Reserve"/> asks for, as a recorded human approval must name it.</summary>
    private static WorkflowRecoveryAction Action(WorkflowOutcomeRun run, Guid decisionId, string operation = "retry", string? agent = "agent2",
        string authority = Authority, string ns = "namespace-a", string type = "TaskDelegationWorkflow")
    {
        var start = operation is "retry" or "reassign";
        return new(authority, operation, run.Id, decisionId, ns, start ? type : null, start ? "fleet" : null, agent, start ? InputHash : null);
    }

    private void Approve(WorkflowOutcomeRun run, Guid decisionId, string operation = "retry", string? agent = "agent2") =>
        _host.Approvals.Add(Action(run, decisionId, operation, agent));

    private void SetReplacement(Guid operationId, string runId = "run-1", string type = "TaskDelegationWorkflow", string queue = "fleet") =>
        _host.Probe.SetRun("namespace-a", "recovery-" + operationId.ToString("D"), runId, "Running", type, queue);

    private async Task<JsonElement> PlanAsync(WorkflowOutcomeRun run, Guid decisionId, string[]? proofs = null)
    {
        var response = await _host.AsAdmin(HttpMethod.Post, $"{Root}/{run.Id}/recovery-plan", new { decisionId, stopProofRefs = proofs });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiHost.JsonAsync(response);
    }

    private static object Reserve(Guid decisionId, string[] proofs, int version = 1, string? agent = "agent2", string authority = Authority,
        string ns = "namespace-a", string type = "TaskDelegationWorkflow") => new
    {
        decisionId, expectedVersion = version, targetNamespace = ns, targetWorkflowType = type, targetTaskQueue = "fleet",
        targetAgent = agent, inputSha256 = InputHash, scope = "retry the failed delegation once", stopProofRefs = proofs, authorityRef = authority,
    };

    private Task<HttpResponseMessage> ReserveAsync(Guid operationId, object body) =>
        _host.AsAdmin(HttpMethod.Post, $"{Root}/recovery/{operationId}/reserve", body);

    private static WorkflowRecoveryDispatchRequest Start(string? type = "TaskDelegationWorkflow", string? input = InputHash, string coordinator = ApiHost.Coordinator) =>
        new(coordinator, "temporal_start_workflow", "namespace-a", type, "fleet", input, null, null, null);

    private Task<HttpResponseMessage> AuthorizeAsync(Guid operationId, WorkflowRecoveryDispatchRequest request, string? token = ApiHost.AdminToken) =>
        _host.SendAsync(HttpMethod.Post, $"{Root}/recovery/{operationId}/authorize-dispatch", token, request);

    private Task<HttpResponseMessage> ReceiptAsync(Guid operationId, WorkflowRecoveryReceiptRequest request) =>
        _host.AsAdmin(HttpMethod.Post, $"{Root}/recovery/{operationId}/receipt", request);

    private Task<WorkflowRecoveryOperation> OperationAsync(Guid id) =>
        _host.Database.QueryAsync(db => db.WorkflowRecoveryOperations.AsNoTracking().SingleAsync(o => o.Id == id));

    private async Task<(WorkflowOutcomeRun Run, Guid OperationId)> ReservedAsync()
    {
        var (run, decisionId) = await DecidedAsync();
        Approve(run, decisionId);
        var operationId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(operationId, Reserve(decisionId, Proofs(run)))).StatusCode);
        return (run, operationId);
    }

    // ── recovery: plan ───────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task Plan_StoppedExactIdsAndScope_AllowsAndChangesNothing()
    {
        var (run, decisionId) = await DecidedAsync();
        var plan = await PlanAsync(run, decisionId, Proofs(run));

        Assert.True(plan.GetProperty("allowed").GetBoolean());
        Assert.Equal("retry", plan.GetProperty("operation").GetString());
        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowRecoveryOperations.CountAsync()));
        Assert.All(_host.Probe.Calls, c => Assert.StartsWith("describe:", c));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task Plan_SourceStillRunning_SuccessorRunningOrTaskActive_Blocks()
    {
        var (run, decisionId) = await DecidedAsync();

        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Running");
        Assert.Equal("source_running", (await PlanAsync(run, decisionId, Proofs(run))).GetProperty("blockedReason").GetString());

        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Failed");
        _host.Probe.SetLatest(run.Namespace, run.WorkflowId, "successor-run", "Running");
        var successor = await PlanAsync(run, decisionId, Proofs(run));
        Assert.Equal("successor_running", successor.GetProperty("blockedReason").GetString());
        Assert.False(successor.GetProperty("allowed").GetBoolean());

        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Failed");
        _host.Probe.ActiveTasks.Add(run.WorkflowId + "/step-a");
        var active = await PlanAsync(run, decisionId, Proofs(run));
        Assert.Equal("task_running", active.GetProperty("blockedReason").GetString());
        Assert.Equal(["task:" + run.WorkflowId + "/step-a"], active.GetProperty("missingProofs").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task Plan_UnknownOrDegradedProbe_BlocksWithTheMissingProof()
    {
        var (run, decisionId) = await DecidedAsync();

        _host.Probe.Error = new TimeoutException("rpc deadline");
        var degraded = await PlanAsync(run, decisionId, Proofs(run));
        Assert.Equal("source_state_unverified", degraded.GetProperty("blockedReason").GetString());
        Assert.Equal($"run:{run.Namespace}/{run.WorkflowId}/{run.RunId}", degraded.GetProperty("missingProofs")[0].GetString());

        _host.Probe.Error = null;
        var unknownRun = await _host.SeedRunAsync();
        var unknownDecision = await InsertDecisionAsync(unknownRun, "retry");
        Assert.Equal("source_run_not_found", (await PlanAsync(unknownRun, unknownDecision, Proofs(unknownRun))).GetProperty("blockedReason").GetString());

        var noLedger = await _host.SeedRunAsync(r => r.SourceTaskRefsJson = null);
        Assert.Equal("task_ledger_missing", (await PlanAsync(noLedger, await InsertDecisionAsync(noLedger, "retry"))).GetProperty("blockedReason").GetString());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task Plan_LateSuccessOrExistingPullRequest_Blocks()
    {
        var (run, decisionId) = await DecidedAsync(configure: r => { r.Repo = "owner/repo"; r.PrNumber = 17; });
        _host.Artifacts.Set("owner/repo", null, 17, FakeArtifactReader.ClosedPull());

        var pr = await PlanAsync(run, decisionId, Proofs(run));
        Assert.Equal("artifact_state_unverified", pr.GetProperty("blockedReason").GetString());
        Assert.Equal("artifact:owner/repo#pr/17", pr.GetProperty("missingProofs")[0].GetString());
        Assert.True((await PlanAsync(run, decisionId, Proofs(run, "artifact:owner/repo#pr/17"))).GetProperty("allowed").GetBoolean());

        var missing = await PlanAsync(run, decisionId, []);
        Assert.Equal("stop_proof_missing", missing.GetProperty("blockedReason").GetString());

        var text = "done after all";
        (await _host.AsAdmin(HttpMethod.Post, $"{Root}/late-results", new WorkflowOutcomeLateResultRequest(
            run.Namespace, run.WorkflowId + "/step-a", "m-9", WorkflowOutcomeHashes.Sha256Hex(text), "completed", text, 1, _host.Clock.GetUtcNow()))).EnsureSuccessStatusCode();
        Assert.Equal("late_success_present", (await PlanAsync(run, decisionId, Proofs(run, "artifact:owner/repo#pr/17"))).GetProperty("blockedReason").GetString());
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    [InlineData("resume", "Failed", "closed_run_cannot_resume")]
    [InlineData("resume", "Running", "delegate_recovery_handler_unavailable")]
    [InlineData("accept_late", "Failed", "delegate_recovery_handler_unavailable")]
    [InlineData("wait", "Failed", "not_a_recovery_operation")]
    [InlineData("cancel", "Failed", "closed_target_noop")]
    public async Task Plan_UnsupportedOrClosedTarget_BlocksAndNeverActs(string operation, string status, string reason)
    {
        var run = await _host.SeedRunAsync(r => r.Status = status);
        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, status);
        var plan = await PlanAsync(run, await InsertDecisionAsync(run, operation));

        Assert.Equal((false, reason), (plan.GetProperty("allowed").GetBoolean(), plan.GetProperty("blockedReason").GetString()));
        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowRecoveryOperations.CountAsync()));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task Plan_Cancel_OnlyTheExactRunningTarget()
    {
        var run = await _host.SeedRunAsync(r => r.Status = "Running");
        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Running");
        Assert.True((await PlanAsync(run, await InsertDecisionAsync(run, "cancel"))).GetProperty("allowed").GetBoolean());
    }

    // ── decisions: reserve ───────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Reserve_OnceWithDeterministicReplacementId_SecondIsRefused()
    {
        var (run, decisionId) = await DecidedAsync();
        Approve(run, decisionId);
        var operationId = Guid.NewGuid();

        var first = await ReserveAsync(operationId, Reserve(decisionId, Proofs(run)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var view = await ApiHost.JsonAsync(first);
        Assert.Equal(("reserved", "recovery-" + operationId.ToString("D"), ApiHost.Coordinator),
            (view.GetProperty("state").GetString(), view.GetProperty("replacementWorkflowId").GetString(), view.GetProperty("coordinator").GetString()));

        Assert.Equal(HttpStatusCode.Conflict, (await ReserveAsync(operationId, Reserve(decisionId, Proofs(run)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ReserveAsync(Guid.NewGuid(), Reserve(decisionId, Proofs(run)))).StatusCode);
        // A second decision for the same failure cannot reserve a second attempt.
        var other = await ApiHost.JsonAsync(await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, "retry")));
        var second = await ReserveAsync(Guid.NewGuid(), Reserve(other.GetProperty("decisionId").GetGuid(), Proofs(run)));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await _host.Database.QueryAsync(db => db.WorkflowRecoveryOperations.CountAsync()));

        // Readback is the same for the operator and the bridge (same admin bearer, read-only).
        var readback = await _host.AsAdmin(HttpMethod.Get, $"{Root}/recovery/{operationId}");
        Assert.Equal("reserved", (await ApiHost.JsonAsync(readback)).GetProperty("state").GetString());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Reserve_CoordinatorTokenIsForbidden_AndTouchesNothing()
    {
        var (run, decisionId) = await DecidedAsync();
        _host.Database.Commands.Reset();
        var reserve = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/recovery/{Guid.NewGuid()}/reserve", Reserve(decisionId, Proofs(run)));
        var plan = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/recovery-plan", new { decisionId });

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (reserve.StatusCode, plan.StatusCode));
        Assert.Equal(0, _host.Database.Commands.Count);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Reserve_StaleUnsafeOrUnauthorized_IsRefusedWithNoRow()
    {
        var (run, decisionId) = await DecidedAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await ReserveAsync(Guid.NewGuid(), Reserve(decisionId, Proofs(run), version: 2))).StatusCode);
        var noAuthority = await ReserveAsync(Guid.NewGuid(), Reserve(decisionId, Proofs(run), authority: "operator said so"));
        Assert.Equal("authority_missing", (await ApiHost.JsonAsync(noAuthority)).GetProperty("error").GetString());
        var unknownNs = await ReserveAsync(Guid.NewGuid(), Reserve(decisionId, Proofs(run), ns: "namespace-x"));
        Assert.Equal("target_namespace_unknown", (await ApiHost.JsonAsync(unknownNs)).GetProperty("error").GetString());
        var unproven = await ReserveAsync(Guid.NewGuid(), Reserve(decisionId, []));
        Assert.Equal((HttpStatusCode)422, unproven.StatusCode);
        Assert.Equal("stop_proof_missing", (await ApiHost.JsonAsync(unproven)).GetProperty("error").GetString());

        // The replacement id already exists in Temporal: never a second start.
        var operationId = Guid.NewGuid();
        _host.Probe.SetRun("namespace-a", "recovery-" + operationId.ToString("D"), "r-1", "Running");
        Assert.Equal("replacement_exists", (await ApiHost.JsonAsync(await ReserveAsync(operationId, Reserve(decisionId, Proofs(run))))).GetProperty("error").GetString());

        _host.Probe.Error = new InvalidOperationException("namespace_unavailable");
        Assert.Equal("source_state_unverified", (await ApiHost.JsonAsync(await ReserveAsync(Guid.NewGuid(), Reserve(decisionId, Proofs(run))))).GetProperty("error").GetString());
        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowRecoveryOperations.CountAsync()));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Reserve_Reassign_NeedsAnEligibleTargetAgent()
    {
        var (run, decisionId) = await DecidedAsync(operation: "reassign");
        Approve(run, decisionId, "reassign");
        var refused = await ReserveAsync(Guid.NewGuid(), Reserve(decisionId, Proofs(run), agent: "nobody"));
        Assert.Equal("reassign_target_ineligible", (await ApiHost.JsonAsync(refused)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(Guid.NewGuid(), Reserve(decisionId, Proofs(run), agent: "agent2"))).StatusCode);
    }

    // ── dispatch ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task AuthorizeDispatch_CalledTwice_AllowsExactlyOnce()
    {
        var (_, operationId) = await ReservedAsync();

        var first = await AuthorizeAsync(operationId, Start());
        var second = await AuthorizeAsync(operationId, Start());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var allowed = await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(first);
        Assert.Equal(new WorkflowRecoveryDispatchResponse(true, "recovery-" + operationId.ToString("D"), null), allowed);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(new WorkflowRecoveryDispatchResponse(false, null, "already_dispatching"), await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(second));
        Assert.Equal(WorkflowRecoveryStates.Dispatching, (await OperationAsync(operationId)).State);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task AuthorizeDispatch_LostReply_IsAmbiguous_AndDispatchUnknownIsPermanentUntilReadback()
    {
        var (_, operationId) = await ReservedAsync();
        await AuthorizeAsync(operationId, Start()); // reply lost by the bridge

        // The retry cannot dispatch again; readback shows the reservation is spent.
        Assert.Equal(HttpStatusCode.Conflict, (await AuthorizeAsync(operationId, Start())).StatusCode);
        var readback = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}/recovery/{operationId}"));
        Assert.Equal("dispatching", readback.GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(operationId, new("unknown", null, null, null, "rpc deadline"))).StatusCode);
        Assert.Equal(WorkflowRecoveryStates.DispatchUnknown, (await OperationAsync(operationId)).State);
        Assert.Single(await _host.Database.QueryAsync(db => db.WorkflowOutcomeHealth.AsNoTracking().Where(h => h.State == "open" && h.Component == "recovery").ToListAsync()));

        // Text, a rejection or an unread start cannot clear it; nothing returns it to reserved.
        Assert.Equal(HttpStatusCode.Conflict, (await ReceiptAsync(operationId, new("rejected", null, null, null, "looks fine"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ReceiptAsync(operationId, new("started", "recovery-" + operationId.ToString("D"), "run-1", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AuthorizeAsync(operationId, Start())).StatusCode);
        Assert.Equal(WorkflowRecoveryStates.DispatchUnknown, (await OperationAsync(operationId)).State);

        SetReplacement(operationId);
        var verified = await ReceiptAsync(operationId, new("started", "recovery-" + operationId.ToString("D"), "run-1", "event:1", null));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var row = await OperationAsync(operationId);
        Assert.Equal((WorkflowRecoveryStates.Verified, "run-1", "event:1"), (row.State, row.ResultRunId, row.ReceiptEventRef));
        Assert.Empty(await _host.Database.QueryAsync(db => db.WorkflowOutcomeHealth.AsNoTracking().Where(h => h.State == "open" && h.Component == "recovery").ToListAsync()));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task AuthorizeDispatch_SecondTargetOrWrongTool_IsRefusedAndTheReservationStays()
    {
        var (run, operationId) = await ReservedAsync();

        var cases = new (WorkflowRecoveryDispatchRequest Request, string Reason)[]
        {
            (Start(type: "UweDesignWorkflow"), "workflow_type_mismatch"),
            (Start(input: new string('f', 64)), "input_mismatch"),
            (Start(coordinator: "agent1"), "coordinator_mismatch"),
            (Start() with { TaskQueue = "other" }, "task_queue_mismatch"),
            (Start() with { Namespace = "namespace-b" }, "namespace_mismatch"),
            (Start() with { TargetWorkflowId = "another-id" }, "target_mismatch"),
            (new(ApiHost.Coordinator, "temporal_cancel_workflow", "namespace-a", null, null, null, run.WorkflowId, run.RunId, null), "tool_mismatch"),
            (new(ApiHost.Coordinator, "temporal_signal_workflow", "namespace-a", null, null, null, run.WorkflowId, run.RunId, "approve"), "tool_mismatch"),
        };
        foreach (var (request, reason) in cases)
        {
            var response = await AuthorizeAsync(operationId, request);
            Assert.Equal((HttpStatusCode)422, response.StatusCode);
            Assert.Equal(reason, (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(response)).Reason);
        }
        Assert.Equal(WorkflowRecoveryStates.Reserved, (await OperationAsync(operationId)).State);

        // The scoped coordinator token can never authorize a dispatch.
        Assert.Equal(HttpStatusCode.Forbidden, (await AuthorizeAsync(operationId, Start(), ApiHost.CoordinatorToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AuthorizeAsync(operationId, Start())).StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task AuthorizeDispatch_LateSuccessAfterReservation_InvalidatesTheProofForGood()
    {
        var (run, operationId) = await ReservedAsync();
        var text = "late success";
        (await _host.AsAdmin(HttpMethod.Post, $"{Root}/late-results", new WorkflowOutcomeLateResultRequest(
            run.Namespace, run.WorkflowId + "/step-a", "m-2", WorkflowOutcomeHashes.Sha256Hex(text), "completed", text, 1, _host.Clock.GetUtcNow()))).EnsureSuccessStatusCode();

        var response = await AuthorizeAsync(operationId, Start());
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("late_success_present", (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(response)).Reason);
        var row = await OperationAsync(operationId);
        Assert.Equal((WorkflowRecoveryStates.Blocked, "late_success_present"), (row.State, row.BlockedReason));
        Assert.Equal(HttpStatusCode.Conflict, (await AuthorizeAsync(operationId, Start())).StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task AuthorizeDispatch_ProbeUnavailable_RefusesWithoutSpendingTheReservation()
    {
        var (_, operationId) = await ReservedAsync();
        _host.Probe.Error = new TimeoutException("deadline");
        var refused = await AuthorizeAsync(operationId, Start());
        _host.Probe.Error = null;

        Assert.Equal("successor_state_unverified", (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(refused)).Reason);
        Assert.Equal(WorkflowRecoveryStates.Reserved, (await OperationAsync(operationId)).State);
        Assert.Equal(HttpStatusCode.OK, (await AuthorizeAsync(operationId, Start())).StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task Receipt_IsMonotonic_StartedThenVerified_RejectedRetainedAsBlocked()
    {
        var (_, started) = await ReservedAsync();
        await AuthorizeAsync(started, Start());
        Assert.Equal(HttpStatusCode.NotFound, (await ReceiptAsync(Guid.NewGuid(), new("started", null, "x", null, null))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await ReceiptAsync(started, new("started", "some-other-id", "run-1", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(started, new("started", "recovery-" + started.ToString("D"), "run-1", null, null))).StatusCode);
        Assert.Equal(WorkflowRecoveryStates.Dispatched, (await OperationAsync(started)).State);
        // A duplicate of the same receipt is idempotent; going backwards is not allowed.
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(started, new("started", "recovery-" + started.ToString("D"), "run-1", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ReceiptAsync(started, new("unknown", null, null, null, null))).StatusCode);
        SetReplacement(started);
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(started, new("started", "recovery-" + started.ToString("D"), "run-1", "event:1", null))).StatusCode);
        Assert.Equal(WorkflowRecoveryStates.Verified, (await OperationAsync(started)).State);

        var rejected = await _host.SeedRunAsync();
        _host.Probe.SetRun(rejected.Namespace, rejected.WorkflowId, rejected.RunId, "Failed");
        var operationId = Guid.NewGuid();
        await _host.Database.WithContextAsync(async db =>
        {
            db.WorkflowRecoveryOperations.Add(new WorkflowRecoveryOperation
            {
                Id = operationId, DecisionId = Guid.NewGuid(), OutcomeId = rejected.Id, OutcomeVersion = 1, Operation = "retry",
                SourceNamespace = rejected.Namespace, SourceWorkflowId = rejected.WorkflowId, SourceRunId = rejected.RunId,
                TargetNamespace = "namespace-a", ReplacementWorkflowId = "recovery-" + operationId.ToString("D"), Scope = "s",
                Coordinator = ApiHost.Coordinator, StopProofRefsJson = "[]", AuthorityRef = Authority, State = WorkflowRecoveryStates.Dispatching,
                ReservedBy = "operator", ReservedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(operationId, new("rejected", null, null, null, "WorkflowAlreadyStarted"))).StatusCode);
        var blocked = await OperationAsync(operationId);
        Assert.Equal((WorkflowRecoveryStates.Blocked, "dispatch_rejected"), (blocked.State, blocked.BlockedReason));
        Assert.Contains("WorkflowAlreadyStarted", blocked.ReceiptJson);
        Assert.Equal(HttpStatusCode.Conflict, (await ReceiptAsync(operationId, new("started", "recovery-" + operationId.ToString("D"), "r", "event:2", null))).StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task Cancel_DispatchesOnlyAgainstTheExactRunningRun_AndAClosedTargetBlocks()
    {
        var run = await _host.SeedRunAsync(r => r.Status = "Running");
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();
        (await _host.DeliverAsync(batch)).EnsureSuccessStatusCode();
        (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(run.Id, 1))).EnsureSuccessStatusCode();
        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Running");
        var decision = await ApiHost.JsonAsync(await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, "cancel")));
        _host.Approvals.Add(Action(run, decision.GetProperty("decisionId").GetGuid(), "cancel", agent: null, ns: run.Namespace));
        var operationId = Guid.NewGuid();
        var reserve = await ReserveAsync(operationId, new
        {
            decisionId = decision.GetProperty("decisionId").GetGuid(), expectedVersion = 1, targetNamespace = run.Namespace,
            scope = "cancel the stuck run", stopProofRefs = Array.Empty<string>(), authorityRef = Authority,
        });
        Assert.Equal(HttpStatusCode.Created, reserve.StatusCode);
        Assert.Equal("recovery-" + operationId.ToString("D"), (await ApiHost.JsonAsync(reserve)).GetProperty("replacementWorkflowId").GetString());

        var wrongRun = new WorkflowRecoveryDispatchRequest(ApiHost.Coordinator, "temporal_cancel_workflow", run.Namespace, null, null, null, run.WorkflowId, "other-run", null);
        Assert.Equal("target_mismatch", (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(await AuthorizeAsync(operationId, wrongRun))).Reason);

        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Canceled");
        var closed = await AuthorizeAsync(operationId, wrongRun with { TargetRunId = run.RunId });
        Assert.Equal("closed_target_noop", (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(closed)).Reason);
        Assert.Equal(WorkflowRecoveryStates.Blocked, (await OperationAsync(operationId)).State);
    }

    // ── recovery: the run's exact issue/PR, read before any new attempt ─────

    public static TheoryData<string, WorkflowRecoveryArtifactRead, string> ArtifactCases => new()
    {
        { "pr: merged", Pull(merged: true), "artifact_merged" },
        { "pr: still open", Pull(state: "open"), "artifact_pr_open" },
        { "pr: checks running", Pull(pending: 2), "artifact_checks_running" },
        { "pr: head branch moved", Pull(branchSha: "fedcba9876543210fedcba9876543210fedcba98"), "artifact_branch_moved" },
        { "pr: unreadable", WorkflowRecoveryArtifactRead.Failed("artifact_read_failed"), "artifact_state_unverified" },
        { "pr: private or missing", WorkflowRecoveryArtifactRead.Failed("artifact_not_found_or_private"), "artifact_state_unverified" },
        { "pr: answered as an issue", FakeArtifactReader.OpenIssue(), "artifact_state_unverified" },
    };

    private static WorkflowRecoveryArtifactRead Pull(string state = "closed", bool merged = false, int pending = 0, string? branchSha = null) =>
        new(new WorkflowRecoveryArtifactFacts(WorkflowRecoveryArtifactFacts.PullRequest, state, merged,
            "0123456789abcdef0123456789abcdef01234567", "feat/x", branchSha, pending, []), null);

    [Theory]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    [MemberData(nameof(ArtifactCases))]
    public async Task Plan_PullRequestRead_ExistingWorkOrAnUnreadableArtifactBlocks(string _, WorkflowRecoveryArtifactRead read, string reason)
    {
        var (run, decisionId) = await DecidedAsync(configure: r => { r.Repo = "owner/repo"; r.PrNumber = 17; });
        _host.Artifacts.Set("owner/repo", null, 17, read);

        var plan = await PlanAsync(run, decisionId, Proofs(run, "artifact:owner/repo#pr/17"));

        Assert.Equal((false, reason), (plan.GetProperty("allowed").GetBoolean(), plan.GetProperty("blockedReason").GetString()));
        Assert.Contains("owner/repo||17", _host.Artifacts.Calls);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task Plan_ArtifactProof_MustBeExactlyTheRunsReference_ForeignOrStaleProofsAreRefused()
    {
        var (run, decisionId) = await DecidedAsync(configure: r => { r.Repo = "owner/repo"; r.PrNumber = 17; });
        _host.Artifacts.Set("owner/repo", null, 17, FakeArtifactReader.ClosedPull());

        foreach (var foreign in new[] { "artifact:owner/other#pr/17", "artifact:owner/repo#pr/18", "artifact:owner/repo#issue/17", "artifact:owner/repo#pr/17@closed" })
        {
            var plan = await PlanAsync(run, decisionId, Proofs(run, foreign));
            Assert.Equal("artifact_proof_mismatch", plan.GetProperty("blockedReason").GetString());
            // A foreign proof is refused even beside the exact one.
            Assert.Equal("artifact_proof_mismatch", (await PlanAsync(run, decisionId, Proofs(run, "artifact:owner/repo#pr/17", foreign))).GetProperty("blockedReason").GetString());
        }
        Assert.True((await PlanAsync(run, decisionId, Proofs(run, "artifact:owner/repo#pr/17"))).GetProperty("allowed").GetBoolean());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task Plan_IssueRead_ClosedOrWithAnOpenPullRequestBlocks()
    {
        var (run, decisionId) = await DecidedAsync(configure: r => { r.Repo = "owner/repo"; r.IssueNumber = 5; });
        var proofs = Proofs(run, "artifact:owner/repo#issue/5");

        _host.Artifacts.Set("owner/repo", 5, null, FakeArtifactReader.OpenIssue(31));
        var withPull = await PlanAsync(run, decisionId, proofs);
        Assert.Equal("artifact_pr_open", withPull.GetProperty("blockedReason").GetString());
        Assert.Equal("artifact:owner/repo#pr/31", withPull.GetProperty("missingProofs")[0].GetString());

        _host.Artifacts.Set("owner/repo", 5, null, new(new WorkflowRecoveryArtifactFacts(WorkflowRecoveryArtifactFacts.Issue, "closed", false, null, null, null, 0, []), null));
        Assert.Equal("artifact_issue_closed", (await PlanAsync(run, decisionId, proofs)).GetProperty("blockedReason").GetString());

        _host.Artifacts.Set("owner/repo", 5, null, FakeArtifactReader.OpenIssue());
        Assert.True((await PlanAsync(run, decisionId, proofs)).GetProperty("allowed").GetBoolean());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task AuthorizeDispatch_ArtifactChangedSinceReserve_InvalidatesTheProof_AnUnreadableOneOnlyRefuses()
    {
        var (run, decisionId) = await DecidedAsync(configure: r => { r.Repo = "owner/repo"; r.PrNumber = 17; });
        _host.Artifacts.Set("owner/repo", null, 17, FakeArtifactReader.ClosedPull());
        Approve(run, decisionId);
        var operationId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(operationId, Reserve(decisionId, Proofs(run, "artifact:owner/repo#pr/17")))).StatusCode);

        _host.Artifacts.Set("owner/repo", null, 17, WorkflowRecoveryArtifactRead.Failed("artifact_read_failed"));
        var unreadable = await AuthorizeAsync(operationId, Start());
        Assert.Equal("artifact_state_unverified", (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(unreadable)).Reason);
        Assert.Equal(WorkflowRecoveryStates.Reserved, (await OperationAsync(operationId)).State);

        // A new head on the closed pull request: the reviewed artifact is not the one now there.
        _host.Artifacts.Set("owner/repo", null, 17, FakeArtifactReader.ClosedPull("fedcba9876543210fedcba9876543210fedcba98"));
        var changed = await AuthorizeAsync(operationId, Start());
        Assert.Equal("artifact_changed", (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(changed)).Reason);
        Assert.Equal((WorkflowRecoveryStates.Blocked, "artifact_changed"), ((await OperationAsync(operationId)).State, (await OperationAsync(operationId)).BlockedReason));
    }

    // ── decisions: deny-by-default authority for the exact action ───────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Reserve_WithoutAnApprovalForTheExactAction_IsDeniedByDefault_AndWritesNoRow()
    {
        var (run, decisionId) = await DecidedAsync();
        var other = await _host.SeedRunAsync();
        async Task<string?> Denied(object? body = null) =>
            (await ApiHost.JsonAsync(await ReserveAsync(Guid.NewGuid(), body ?? Reserve(decisionId, Proofs(run))))).GetProperty("error").GetString();

        Assert.Equal(WorkflowRecoveryApprovals.NotCovering, await Denied());

        // Each approval differs from the requested action in exactly one field.
        var exact = Action(run, decisionId);
        foreach (var near in new[]
        {
            exact with { AuthorityRef = "https://example.com/approvals/other" },
            exact with { Operation = "reassign" },
            exact with { OutcomeId = other.Id },
            exact with { DecisionId = Guid.NewGuid() },
            exact with { TargetNamespace = "namespace-b" },
            exact with { TargetWorkflowType = "UweDesignWorkflow" },
            exact with { TargetTaskQueue = "other" },
            exact with { TargetAgent = "agent1" },
            exact with { InputSha256 = new string('f', 64) },
        })
        {
            _host.Approvals.Clear();
            _host.Approvals.Add(near);
            Assert.Equal(WorkflowRecoveryApprovals.NotCovering, await Denied());
        }

        // A decision reference or a bare https URL shape is never authority, and no approval names it.
        _host.Approvals.Clear();
        _host.Approvals.Add(exact);
        Assert.Equal(WorkflowRecoveryApprovals.NotCovering, await Denied(Reserve(decisionId, Proofs(run), authority: "decision:" + decisionId.ToString("D"))));

        _host.Approvals.Clear();
        _host.Approvals.Add(exact, expiresAt: _host.Clock.GetUtcNow().AddMinutes(-1));
        Assert.Equal(WorkflowRecoveryApprovals.Expired, await Denied());

        _host.Approvals.WriteRaw("{\"schemaVersion\":1,\"approvals\":[{\"authorityRef\":\"http://example.com/a\"}]}");
        Assert.Equal(WorkflowRecoveryApprovals.Unverified, await Denied());
        _host.Approvals.Delete();
        Assert.Equal(WorkflowRecoveryApprovals.Unverified, await Denied());

        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowRecoveryOperations.CountAsync()));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task AuthorizeDispatch_ApprovalUnreadable_OnlyRefuses_RevokedOrExpired_BlocksForGood()
    {
        var (run, operationId) = await ReservedAsync();
        var decisionId = (await OperationAsync(operationId)).DecisionId;

        _host.Approvals.Delete();
        var unreadable = await AuthorizeAsync(operationId, Start());
        Assert.Equal(WorkflowRecoveryApprovals.Unverified, (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(unreadable)).Reason);
        Assert.Equal(WorkflowRecoveryStates.Reserved, (await OperationAsync(operationId)).State);

        _host.Approvals.Clear();
        _host.Approvals.Add(Action(run, decisionId), expiresAt: _host.Clock.GetUtcNow().AddMinutes(-1));
        var expired = await AuthorizeAsync(operationId, Start());
        Assert.Equal(WorkflowRecoveryApprovals.Expired, (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(expired)).Reason);
        Assert.Equal((WorkflowRecoveryStates.Blocked, WorkflowRecoveryApprovals.Expired),
            ((await OperationAsync(operationId)).State, (await OperationAsync(operationId)).BlockedReason));

        var (revokedRun, revoked) = await ReservedAsync();
        _host.Approvals.Clear();
        Assert.Equal(WorkflowRecoveryApprovals.NotCovering, (await ApiHost.ReadAsync<WorkflowRecoveryDispatchResponse>(await AuthorizeAsync(revoked, Start()))).Reason);
        Assert.Equal(WorkflowRecoveryStates.Blocked, (await OperationAsync(revoked)).State);
        Assert.NotEqual(revokedRun.Id, run.Id);
    }

    // ── dispatch: a readback is verified against Temporal, never on the bridge's word ──

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task Receipt_Readback_IsVerifiedOnlyWhenTemporalConfirmsTheExactReplacement()
    {
        async Task<Guid> DispatchingAsync()
        {
            var (_, id) = await ReservedAsync();
            Assert.Equal(HttpStatusCode.OK, (await AuthorizeAsync(id, Start())).StatusCode);
            return id;
        }
        WorkflowRecoveryReceiptRequest Readback(Guid id, string runId = "run-1") => new("started", "recovery-" + id.ToString("D"), runId, "event:1", null);
        Task<List<WorkflowOutcomeHealth>> OpenIncidentsAsync() =>
            _host.Database.QueryAsync(db => db.WorkflowOutcomeHealth.AsNoTracking().Where(h => h.State == "open" && h.Component == "recovery").ToListAsync());

        // Confirmed: the exact recovery-<id> run, reported run id, reserved type and task queue.
        var confirmed = await DispatchingAsync();
        SetReplacement(confirmed);
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(confirmed, Readback(confirmed))).StatusCode);
        Assert.Equal(WorkflowRecoveryStates.Verified, (await OperationAsync(confirmed)).State);

        // Temporal unavailable: the start is recorded as dispatched, not verified; a later confirmed readback verifies it.
        var unavailable = await DispatchingAsync();
        _host.Probe.Error = new TimeoutException("deadline");
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(unavailable, Readback(unavailable))).StatusCode);
        _host.Probe.Error = null;
        Assert.Equal(WorkflowRecoveryStates.Dispatched, (await OperationAsync(unavailable)).State);
        _host.Probe.Error = new TimeoutException("deadline");
        var stillUnread = await ReceiptAsync(unavailable, Readback(unavailable) with { EventRef = "event:2" });
        _host.Probe.Error = null;
        Assert.Equal((HttpStatusCode.Conflict, WorkflowRecoveryService.ReplacementUnverified),
            (stillUnread.StatusCode, (await ApiHost.JsonAsync(stillUnread)).GetProperty("error").GetString()));
        SetReplacement(unavailable);
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(unavailable, Readback(unavailable) with { EventRef = "event:3" })).StatusCode);
        Assert.Equal(WorkflowRecoveryStates.Verified, (await OperationAsync(unavailable)).State);
        Assert.Empty(await OpenIncidentsAsync());

        // Contradicted: no such run, another run id, or another type/queue makes the dispatch outcome unknown, with its alert.
        var contradictions = new (Action<Guid> Arrange, string Label)[]
        {
            (_ => { }, "no such run"),
            (id => _host.Probe.SetAnswer("namespace-a", "recovery-" + id.ToString("D"), "run-1", new WorkflowRecoveryRunState("run-2", "Running", "TaskDelegationWorkflow", "fleet")), "other run id"),
            (id => SetReplacement(id, type: "UweDesignWorkflow"), "other type"),
            (id => SetReplacement(id, queue: "other"), "other task queue"),
        };
        foreach (var (arrange, label) in contradictions)
        {
            var id = await DispatchingAsync();
            arrange(id);
            Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(id, Readback(id))).StatusCode);
            Assert.Equal((label, WorkflowRecoveryStates.DispatchUnknown), (label, (await OperationAsync(id)).State));
            Assert.Contains(await OpenIncidentsAsync(), h => h.OutageKey == "dispatch_unknown:" + id.ToString("D") && h.Reason.Contains("contradicts"));
            // Still contradicted: stays unknown, never verified on a resend.
            var resend = await ReceiptAsync(id, Readback(id) with { EventRef = "event:9" });
            Assert.Equal((HttpStatusCode.Conflict, WorkflowRecoveryService.ReplacementMismatch),
                (resend.StatusCode, (await ApiHost.JsonAsync(resend)).GetProperty("error").GetString()));
            Assert.Equal(WorkflowRecoveryStates.DispatchUnknown, (await OperationAsync(id)).State);
        }
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task Receipt_CancelReadback_IsVerifiedOnlyOnceTheExactRunIsCanceled()
    {
        var run = await _host.SeedRunAsync(r => r.Status = "Running");
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();
        (await _host.DeliverAsync(batch)).EnsureSuccessStatusCode();
        (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(run.Id, 1))).EnsureSuccessStatusCode();
        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Running");
        var decisionId = (await ApiHost.JsonAsync(await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, "cancel")))).GetProperty("decisionId").GetGuid();
        _host.Approvals.Add(Action(run, decisionId, "cancel", agent: null, ns: run.Namespace));
        var operationId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(operationId, new
        {
            decisionId, expectedVersion = 1, targetNamespace = run.Namespace, scope = "cancel the stuck run",
            stopProofRefs = Array.Empty<string>(), authorityRef = Authority,
        })).StatusCode);
        var cancel = new WorkflowRecoveryDispatchRequest(ApiHost.Coordinator, "temporal_cancel_workflow", run.Namespace, null, null, null, run.WorkflowId, run.RunId, null);
        Assert.Equal(HttpStatusCode.OK, (await AuthorizeAsync(operationId, cancel)).StatusCode);

        // Cancel requested but the run is still running: dispatched, not verified.
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(operationId, new("cancel_requested", run.WorkflowId, run.RunId, "event:4", null))).StatusCode);
        Assert.Equal(WorkflowRecoveryStates.Dispatched, (await OperationAsync(operationId)).State);

        _host.Probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Canceled");
        Assert.Equal(HttpStatusCode.OK, (await ReceiptAsync(operationId, new("cancel_requested", run.WorkflowId, run.RunId, "event:5", null))).StatusCode);
        Assert.Equal(WorkflowRecoveryStates.Verified, (await OperationAsync(operationId)).State);
    }
}
