using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Shared;
using Temporalio.Client;

namespace Fleet.Temporal.Tests.Engine;

public sealed partial class AcceptanceScenarioTests
{
    private const string Pr = "https://github.com/example-org/example-repo/pull/7";
    private static readonly string Plan = new('b', 64);
    private static Dictionary<string, object?> ParentInput() => new()
    {
        ["Repo"] = "example-org/example-repo",
        ["IssueNumber"] = 7,
        ["BranchName"] = "feat/example",
        ["TargetAgent"] = "impl-agent",
        ["ConsensusAgents"] = "reviewer-a",
        ["WaiterWorkflowId"] = "waiter-example"
    };

    [Fact]
    public async Task Parent_FirstPassBindsHeadAndRoundZeroToMerge()
    {
        var actor = new Actor();
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            Assert.Equal($"merge:{actor.Head}:1", h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef"));
            await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Single(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.Contains($"v1;head={actor.Head};result=passed;iso=isolated;block=none;fix=0;batch=0", run.Upserts("Acceptance"));
        Assert.Contains($"--match-head-commit {actor.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
        ValidWrites(run);
    }

    [Fact]
    public async Task Parent_SixProductFailuresAskBeforeTheSixthFixAndStopLeavesPrOpen()
    {
        var actor = new Actor { StageResult = "product_fail", Label = "product_defect" };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-continue");
            var history = await handle.FetchHistoryAsync();
            Assert.Equal($"cont:1:6:{Plan[..8]}", h.LastUpsert(history, "GateRef"));
            Assert.Contains("fix=5;batch=5", h.LastUpsert(history, "Acceptance"));
            await Decision(handle, h, "stop");
        });
        Assert.Equal(5, run.Calls.Count(c => c.Step.StartsWith("revise_", StringComparison.Ordinal)));
        Assert.Equal(6, run.ChildStarts("UwePrAcceptanceWorkflow").Count);
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
        ValidWrites(run);
    }

    [Theory]
    [InlineData("exceeds", "plan_amendment")]
    [InlineData("missing", "plan_missing")]
    [InlineData("invalid", "plan_invalid")]
    public async Task Parent_InvalidPlanBlocksBeforeAnyChild(string plan, string reason)
    {
        var actor = new Actor { PlanStatus = plan };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-blocked");
            Assert.Contains($"block={reason};", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance"));
            await Decision(handle, h, "stop");
        });
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow")); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_DocOnlySkipsOwnerButRecordsNotRequired()
    {
        var actor = new Actor { DocOnly = true };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.Contains("result=not_required;iso=production;block=none", run.Upserts("Acceptance").Last()); ValidWrites(run);
    }

    [Theory]
    [InlineData("3", "4")]
    [InlineData("3", "6")]
    [InlineData("3", "-1")]
    [InlineData(null, null)]
    public async Task Parent_InvalidRecoveryNeverStartsAChild(string? fix, string? batch)
    {
        var actor = new Actor { PriorReceipts = true, PriorRounds = "invalid" }; var input = ParentInput(); input["PriorFixRounds"] = fix; input["PriorBatchRounds"] = batch;
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", input, actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-blocked");
            Assert.Contains("block=prior_rounds_unknown;", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await Decision(handle, h, "stop");
        });
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow")); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_UnknownHarnessEvidenceAllowsInitialAttemptButRefusesRetry()
    {
        var actor = new Actor { StageResult = "harness_fail", Label = "harness", HarnessEvidence = "unknown" };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-blocked");
            Assert.Contains("block=harness_evidence_unknown;", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await Decision(handle, h, "stop");
        });
        Assert.Single(run.ChildStarts("UwePrAcceptanceWorkflow")); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_HarnessReservationPermitsOnlyOneSameHeadRetry()
    {
        var actor = new Actor { StageResult = "harness_fail", Label = "harness" };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-blocked");
            Assert.Contains("block=harness_repeated;", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await Decision(handle, h, "stop");
        });
        Assert.Equal(2, run.ChildStarts("UwePrAcceptanceWorkflow").Count);
        Assert.Single(run.Calls, c => c.Step == "reserve_harness_retry");
        Assert.All(run.Upserts("Acceptance").Where(v => v.Length > 0), v => Assert.Contains("fix=0;batch=0", v)); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_UnapprovedPlanContinueBindsFullHashAndThenTests()
    {
        var actor = new Actor { DesignApproval = "none" };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-blocked");
            Assert.Equal($"plan:1:{Plan}", h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef"));
            await Decision(handle, h, "continue");
            await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Single(run.ChildStarts("UwePrAcceptanceWorkflow")); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_StaleMergeDecisionIsConsumedBeforeMatchingDecision()
    {
        var actor = new Actor();
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [JsonSerializer.SerializeToElement(new { Decision = "approved", Ref = "merge:old:1" })]);
            await h.WaitForGateVisitAsync(handle, "merge-approval:2"); await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Single(run.Calls, c => c.Step == "phase4_merge"); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_ContinueResetsOnlyBatchThenNewHeadCountsSixAndOne()
    {
        var actor = new Actor { StageResult = "product_fail", Label = "product_defect" };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-continue"); actor.StageResult = "done"; await Decision(handle, h, "continue");
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            Assert.Contains("fix=6;batch=1", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Equal(7, run.ChildStarts("UwePrAcceptanceWorkflow").Count); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_PlanGateApprovesItsRenderedHashNotAChangedLaterRead()
    {
        var actor = new Actor { DesignApproval = "none" };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-blocked"); actor.PlanHash = new string('c', 64); await Decision(handle, h, "continue");
            await WaitGateRef(handle, h, $"plan:2:{actor.PlanHash}");
            Assert.Contains("block=plan_changed;", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await Decision(handle, h, "stop");
        });
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow")); ValidWrites(run);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("changes_requested")]
    [InlineData("timeout")]
    public async Task Parent_StaleRefCannotBecomeADecisionIncludingForgedTimeout(string decision)
    {
        var actor = new Actor();
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [JsonSerializer.SerializeToElement(new { Decision = decision, Ref = "old" })]);
            await h.WaitForGateVisitAsync(handle, "merge-approval:2"); await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Single(run.Calls, c => c.Step == "phase4_merge"); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_ApprovalVoidedByOutsideHeadChangeRequiresFreshReviewAndRetest()
    {
        var actor = new Actor(); var initial = actor.Head;
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1"); actor.MoveHead(); await h.SignalHumanApprovalAsync(handle);
            await h.WaitForGateVisitAsync(handle, "merge-approval:2"); await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Equal(2, run.ChildStarts("UwePrAcceptanceWorkflow").Count);
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Contains($"--match-head-commit {actor.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
        Assert.Contains("", run.Upserts("Acceptance")); Assert.Contains("fix=1;batch=1", run.Upserts("Acceptance").Last()); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_SixStaleDeliveriesBlockInsteadOfMerging()
    {
        var actor = new Actor();
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            for (var i = 0; i < 6; i++) { await h.WaitForGateVisitAsync(handle, $"merge-approval:{i + 1}"); await handle.SignalAsync("merge-approval", [JsonSerializer.SerializeToElement(new { Decision = "approved", Ref = "old" })]); }
            await WaitPhase(handle, h, "acceptance-blocked"); Assert.Contains("block=stale_signals;", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await Decision(handle, h, "stop");
        });
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned"); ValidWrites(run);
    }

    [Theory]
    [InlineData("3", "3", false)]
    [InlineData("5", "5", true)]
    public async Task Parent_ValidRecoveryKeepsCountersAndEntryGuard(string fix, string batch, bool gateFirst)
    {
        var actor = new Actor { PriorReceipts = true, PriorHead = new string('0', 40) }; var input = ParentInput(); input["PriorFixRounds"] = fix; input["PriorBatchRounds"] = batch;
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", input, actor.Respond, async (handle, h) =>
        {
            if (gateFirst) { await WaitPhase(handle, h, "acceptance-continue"); Assert.Contains("fix=5;batch=5", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await Decision(handle, h, "stop"); }
            else { await h.WaitForGateVisitAsync(handle, "merge-approval:1"); Assert.Contains("fix=4;batch=4", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await h.SignalHumanApprovalAsync(handle); }
        });
        Assert.Equal(gateFirst ? 0 : 1, run.ChildStarts("UwePrAcceptanceWorkflow").Count); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_RoundFivePassThenMergeFeedbackMustAskBeforeTestingANewHead()
    {
        var actor = new Actor { StageResult = "product_fail", Label = "product_defect", PassOnAttempt = 6, FeedbackNeedsFix = true };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [JsonSerializer.SerializeToElement(new { Decision = "changes_requested", Comment = "fix" })]);
            await WaitPhase(handle, h, "acceptance-continue"); Assert.Contains("fix=5;batch=5", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await Decision(handle, h, "stop");
        });
        Assert.Equal(6, run.ChildStarts("UwePrAcceptanceWorkflow").Count);
        Assert.Equal(6, run.Calls.Count(c => c.Step.StartsWith("revise_", StringComparison.Ordinal))); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_IncompleteImplementationNeverPinsOrStartsAChild()
    {
        var actor = new Actor { Incomplete = true };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, (_, _) => Task.CompletedTask);
        Assert.DoesNotContain(run.Calls, c => c.Step == "pin"); Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
    }

    [Fact]
    public async Task Parent_CiPendingUsesTwelveDurableSleepsThenBlocks()
    {
        var actor = new Actor { Ci = "pending" };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitTimers(handle, 1); await h.AdvanceAsync(TimeSpan.FromSeconds(3601));
            await WaitPhase(handle, h, "acceptance-blocked"); Assert.Contains("block=ci_timeout;", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance")); await Decision(handle, h, "stop");
        });
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.Equal(12, run.History.Events.Count(e => e.EventType == Temporalio.Api.Enums.V1.EventType.TimerStarted && e.TimerStartedEventAttributes.StartToFireTimeout.Seconds == 300)); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_OwnerFailureContinueWaitsForVerifiedCleanupBeforeAnotherAttempt()
    {
        var actor = new Actor { ThrowOwner = true };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await WaitPhase(handle, h, "acceptance-blocked"); Assert.Contains("block=owner_timeout;", h.LastUpsert(await handle.FetchHistoryAsync(), "Acceptance"));
            actor.ThrowOwner = false; actor.PriorClean = "no"; await Decision(handle, h, "continue");
            await WaitTimers(handle, 2);
            var history = await handle.FetchHistoryAsync();
            Assert.Single(history.Events.Where(e => e.EventType == Temporalio.Api.Enums.V1.EventType.StartChildWorkflowExecutionInitiated && e.StartChildWorkflowExecutionInitiatedEventAttributes.WorkflowType.Name == "UwePrAcceptanceWorkflow"));
            actor.PriorClean = "yes"; await h.AdvanceAsync(TimeSpan.FromSeconds(1801));
            await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Equal(2, run.ChildStarts("UwePrAcceptanceWorkflow").Count); Assert.Single(run.Calls, c => c.Step == "cleanup"); ValidWrites(run);
    }

    [Fact]
    public async Task Parent_HeadChangedDuringReviewInvalidatesAndReviewsTheActualNewHead()
    {
        var actor = new Actor { MoveDuringReview = true };
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", ParentInput(), actor.Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Equal(2, run.ConsensusInputs.Count(i => i.ReviewRef is not null));
        Assert.Single(run.ChildStarts("UwePrAcceptanceWorkflow")); Assert.Single(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Contains($"--match-head-commit {actor.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction); ValidWrites(run);
    }

    private static async Task WaitTimers(WorkflowHandle handle, int count)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while ((await handle.FetchHistoryAsync()).Events.Count(e => e.EventType == Temporalio.Api.Enums.V1.EventType.TimerStarted) < count)
        { if (DateTime.UtcNow > end) throw new TimeoutException("timer"); await Task.Delay(20); }
    }

    private static async Task WaitGateRef(WorkflowHandle handle, SeedHarness h, string reference)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef") != reference)
        { if (DateTime.UtcNow > end) throw new TimeoutException(reference); await Task.Delay(20); }
    }

    private static void ValidWrites(SeedRun run) => Assert.All(run.Upserts("Acceptance"), v => Assert.True(v == "" || AcceptanceAttribute.IsValid(v), v));
    private static async Task Decision(WorkflowHandle handle, SeedHarness h, string decision)
    {
        var gate = h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef");
        await handle.SignalAsync("acceptance-decision", [JsonSerializer.SerializeToElement(new { Decision = decision, Ref = gate })]);
    }
    private static async Task WaitPhase(WorkflowHandle handle, SeedHarness h, string phase)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (h.LastUpsert(await handle.FetchHistoryAsync(), "Phase") != phase)
        { if (DateTime.UtcNow > end) throw new TimeoutException(phase); await Task.Delay(20); }
    }

    private sealed class Actor
    {
        private int _head = 1;
        public string Head => _head.ToString("x40");
        public void MoveHead() => _head++;
        public string PlanHash = Plan;
        public string PriorHead = "none";
        public int PassOnAttempt;
        public bool FeedbackNeedsFix, MoveDuringReview;
        public bool ThrowOwner, Incomplete;
        public string Ci = "success", PriorClean = "yes";
        public string StageResult = "done", Label = "product_defect", PlanStatus = "ok", PriorRounds = "ok", DesignApproval = "design", HarnessEvidence = "no";
        public bool DocOnly, PriorReceipts;
        public string Respond(Call call)
        {
            if (call.Step == "phase1_implement" || call.Step.StartsWith("revise_", StringComparison.Ordinal))
            {
                var old = Head; if (call.Step != "phase1_implement") _head++;
                return $"IMPLEMENTATION: {(Incomplete ? "incomplete" : "complete")}\nPR_URL: {Pr}\nHEAD_RANGE: {old}..{Head}\nHEAD_SHA: {Head}";
            }
            if (call.Step is "pin" or "pin_after_approval") return SeedHarness.DocOnlyPin(Head)
                .Replace("CI: success", "CI: " + Ci).Replace("PRIOR_ATTEMPTS_CLEAN: yes", "PRIOR_ATTEMPTS_CLEAN: " + PriorClean).Replace("CHANGE_CLASS: doc_only", $"CHANGE_CLASS: {(DocOnly ? "doc_only" : "code")}")
                .Replace("PLAN_SHA256: " + Plan, "PLAN_SHA256: " + PlanHash).Replace("PLAN: ok", $"PLAN: {PlanStatus}").Replace("PRIOR_RECEIPTS: no", $"PRIOR_RECEIPTS: {(PriorReceipts ? "yes" : "no")}")
                .Replace("PRIOR_ROUNDS: ok", $"PRIOR_ROUNDS: {PriorRounds}").Replace("PLAN_APPROVAL: design", $"PLAN_APPROVAL: {DesignApproval}")
                .Replace("PRIOR_LAST_HEAD: none", "PRIOR_LAST_HEAD: " + PriorHead).Replace("HARNESS_RETRY_SPENT: no", $"HARNESS_RETRY_SPENT: {HarnessEvidence}");
            if (call.Step == "phase2_5_unresolved_check") return "UNRESOLVED: none";
            if (call.Step == "reserve_harness_retry") return "RESERVATION: reserved 123";
            if (call.Step == "pre_stage")
            {
                if (ThrowOwner) throw new Temporalio.Exceptions.ApplicationFailureException("synthetic owner failure", errorType: "Synthetic", nonRetryable: true);
                var attempt = Regex.Match(call.Instruction, @"ATTEMPT: ([^,]+)").Groups[1].Value;
                var number = int.Parse(Regex.Match(attempt, @"-a([0-9]+)$").Groups[1].Value);
                var result = PassOnAttempt > 0 && number >= PassOnAttempt ? "done" : StageResult;
                return $"ATTEMPT: {attempt}\nHEAD: {Head}\nSTAGE_RESULT: {result}\nREASON: synthetic\nRECEIPT: https://example.com/stage";
            }
            if (call.Step is "cleanup" or "backstop_cleanup") return "CLEANUP: verified\nRECEIPT: https://example.com/cleanup";
            if (call.Step == "child_status") return "CHILD: running";
            if (MoveDuringReview && call.Step.StartsWith("review-", StringComparison.Ordinal)) { MoveDuringReview = false; MoveHead(); return "SUMMARY: head changed\nBLOCKER: head_changed\nVERDICT: needs_human_review"; }
            if (call.Step == "synthesis") return "SUMMARY: applies\nVERDICT: changes_requested";
            if (FeedbackNeedsFix && call.Step.StartsWith("review-", StringComparison.Ordinal) && call.Instruction.Contains("Merge-gate feedback requested changes", StringComparison.Ordinal))
                return "SUMMARY: revise\nBLOCKER: change the implementation\nVERDICT: changes_requested";
            if (call.Step.StartsWith("review-", StringComparison.Ordinal)) return call.Instruction.Contains("Classify only", StringComparison.Ordinal)
                ? $"SUMMARY: LABEL: {Label}\nBLOCKER: {(Label == "product_defect" ? "receipt expected pass actual failure test.cs:1" : "none")}\nVERDICT: {(Label == "product_defect" ? "changes_requested" : "approved")}"
                : $"SUMMARY: checked\nVERDICT: approved\nREVIEWED_REF: {Head}\nPUBLIC_SCRUB: pass";
            if (call.Step == "verify_merge_status") return "PR_STATE_JSON: {\"state\":\"MERGED\",\"mergedAt\":\"2026-01-01T00:00:00Z\",\"mergeCommit\":{\"oid\":\"" + Head + "\"}}";
            if (call.Step == "notify_invalidation") return $"HEAD_OID: {Head}";
            if (call.Step == "prepare") return "PREP: NO_DOC";
            return "ok";
        }
    }
}
