using System.Text.Json;
using Fleet.Temporal.Engine;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #495 — the PR workflow allows 50 consensus rounds per review, and a technical end (review
/// rounds exhausted, a failed implementation/review/merge step, delivery passes exhausted) closes
/// the run Failed instead of Completed.
///
/// <para>
/// Same harness as <see cref="SeedEpicGrantDefinitionTests"/>: the shipped seed, the production
/// engine and the real <c>ConsensusReviewWorkflow</c>; only agents are scripted. The terminal branch
/// runs after the waiter wake, so the waiter still receives the unchanged non-approval decision,
/// and a failed notice or wake cannot turn the outcome green.
/// </para>
/// </summary>
public sealed class SeedReviewLimitTests
{
    internal const string ReviewRoundsExhausted = "review_rounds_exhausted: 50 consensus rounds ended without an approval";
    internal const string DeliveryFailed = "delivery_failed: an implementation, review or merge step failed";
    internal const string DeliveryPassesExhausted = "delivery_passes_exhausted: 60 delivery passes ended without a terminal decision";

    private const string Workflow = "UwePrImplementationWorkflow";
    private const string Repo = "example-org/example-repo";
    private const string Reviewer = "reviewer-a";
    private const string ReviewStep = "review-" + Reviewer;
    private const string Waiter = "waiter-example";
    private const string PrUrl = "https://github.com/example-org/example-repo/pull/7";

    private static readonly string HeadA = new('a', 40);

    // ── structure ────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheConsensusCapRises_AndTheTerminalBranchFailsOnlyTechnicalEnds()
    {
        var root = SeedHarness.Parse(Workflow);
        var steps = WorkflowDefinitionValidator.Walk(root).ToList();
        var loops = steps.OfType<LoopStep>().ToDictionary(l => l.Name!);
        Assert.Equal(50, loops["consensus_loop"].MaxIterations);
        Assert.Equal(60, loops["outer_loop"].MaxIterations);

        var sequence = Assert.IsType<SequenceStep>(root);
        Assert.Equal("wake_parked_waiter", sequence.Steps[^2].Name);
        var terminal = Assert.IsType<BranchStep>(sequence.Steps[^1]);
        Assert.Equal("fail_on_technical_outcome", terminal.Name);
        Assert.False(terminal.IgnoreFailure);

        foreach (var human in new[] { "APPROVED", "REJECTED", "TIMEOUT", "STOPPED" })
            Assert.IsType<NoopStep>(terminal.Cases[human]);
        Assert.IsType<FailStep>(terminal.Cases["FAILED"]);
        Assert.Equal(DeliveryPassesExhausted, Assert.IsType<FailStep>(terminal.Default).Message);

        var notice = steps.OfType<DelegateStep>().Single(d => d.Name == "notify_consensus_exhausted");
        Assert.StartsWith("Fifty consensus rounds", notice.Instruction);
        Assert.True(notice.IgnoreFailure);
    }

    // ── exhaustion ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every round approves without attesting the head. Round 6 is still running, the run then
    /// stops at round 50, sends one notice, wakes the waiter with the unchanged non-approval
    /// decision, and closes Failed with the fixed exhaustion reason. Nothing reaches a merge.
    /// </summary>
    [Fact]
    public async Task UnattestedRounds_StayActivePastRoundSix_ThenFailAfterFifty()
    {
        using var hold = new ManualResetEventSlim();
        var reviews = 0;
        WorkflowExecutionStatus? statusInRoundSeven = null;
        var completedBeforeRoundSeven = -1;
        List<string> callsInRoundSeven = [];

        var run = await SeedHarness.RunAsync(Workflow, PrInput(), Respond, async (handle, h) =>
        {
            await WaitForAsync(() => h.Calls.Count(c => c.Step == ReviewStep) >= 7);
            statusInRoundSeven = (await handle.DescribeAsync()).Status;
            completedBeforeRoundSeven = CompletedConsensusChildren(await handle.FetchHistoryAsync());
            callsInRoundSeven = h.Calls.Select(c => c.Step).ToList();
            hold.Set();
        }, captureWaiter: true, allowFailure: true);

        Assert.Equal(WorkflowExecutionStatus.Running, statusInRoundSeven);
        Assert.Equal(6, completedBeforeRoundSeven);
        Assert.DoesNotContain("notify_consensus_exhausted", callsInRoundSeven);

        Assert.Equal(50, run.ConsensusInputs.Count);
        Assert.All(run.ConsensusInputs, i => Assert.Equal(HeadA, i.ReviewRef));
        Assert.Single(run.Calls, c => c.Step == "notify_consensus_exhausted");
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
        Assert.Empty(run.Upserts("GateRef"));
        Assert.Equal("rejected", run.WaiterDecision);

        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Equal(ReviewRoundsExhausted, run.FailureMessage);
        Assert.Equal("ExplicitFail", run.FailureType);

        string Respond(Call call)
        {
            if (call.Step == ReviewStep && Interlocked.Increment(ref reviews) == 7)
                hold.Wait(TimeSpan.FromSeconds(60));
            return UnattestedResponder(call);
        }
    }

    /// <summary>
    /// The exhaustion notice throws and the waiter does not exist, so its wake fails too. Both are
    /// swallowed as before, the wake still carried the non-approval decision, and the run still
    /// closes Failed.
    /// </summary>
    [Fact]
    public async Task FailedNoticeAndFailedWake_StillEndFailed()
    {
        var run = await SeedHarness.RunAsync(Workflow, PrInput(), call => call.Step == "notify_consensus_exhausted"
                ? throw new InvalidOperationException("notice delivery failed")
                : UnattestedResponder(call),
            (_, _) => Task.CompletedTask, allowFailure: true);

        Assert.Single(run.Calls, c => c.Step == "notify_consensus_exhausted");
        Assert.Contains(run.History.Events, e => e.EventType == EventType.ActivityTaskFailed);
        Assert.Contains(run.History.Events, e => e.EventType == EventType.SignalExternalWorkflowExecutionFailed);
        Assert.Equal("rejected", SentWaiterDecision(run));

        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Equal(ReviewRoundsExhausted, run.FailureMessage);
    }

    /// <summary>
    /// The real local Temporal dev server, not the time-skipping test server: the server itself
    /// reports the exhausted run Failed.
    /// </summary>
    [Fact]
    public async Task OnALocalDevServer_ExhaustionClosesFailed()
    {
        var run = await SeedHarness.RunAsync(Workflow, PrInput(), UnattestedResponder, (_, _) => Task.CompletedTask,
            captureWaiter: true, allowFailure: true, localServer: true);

        Assert.Equal(50, run.ConsensusInputs.Count);
        Assert.Equal("rejected", run.WaiterDecision);
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Equal(ReviewRoundsExhausted, run.FailureMessage);
        Assert.Equal(EventType.WorkflowExecutionFailed, run.History.Events.Last().EventType);
    }

    // ── approval is unchanged ────────────────────────────────────────────────

    /// <summary>
    /// An attested approval in round 3 goes straight to the existing human merge gate: no extra
    /// rounds, no merge without the human decision, and the merged run still completes.
    /// </summary>
    [Fact]
    public async Task ApprovalInRoundThree_ReachesTheHumanMergeGate_AndCompletes()
    {
        var reviews = 0;
        var run = await SeedHarness.RunAsync(Workflow, PrInput(), Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            Assert.DoesNotContain(h.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
            await h.SignalHumanApprovalAsync(handle);
        }, captureWaiter: true);

        Assert.Equal(3, run.ConsensusInputs.Count);
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_consensus_exhausted");
        Assert.Equal("approved", run.WaiterDecision);
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);

        string Respond(Call call) =>
            call.Step == ReviewStep && Interlocked.Increment(ref reviews) == 3
                ? Review("approved", reviewedRef: HeadA)
                : UnattestedResponder(call);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string UnattestedResponder(Call call) => call.Step switch
    {
        "phase1_implement" => $"done\nPR_URL: {PrUrl}\nHEAD_SHA: {HeadA}",
        "verify_merge_status" => "PR_STATE_JSON: {\"mergeCommit\":{\"oid\":\"" + HeadA + "\"},\"mergedAt\":\"2026-10-10T12:00:00Z\",\"state\":\"MERGED\"}",
        "prepare" => "PREP: NO_DOC",
        // Approves without REVIEWED_REF, so the round never attests the head.
        ReviewStep => Review("approved", reviewedRef: null),
        _ => "ok",
    };

    private static Dictionary<string, object?> PrInput() => new()
    {
        ["Repo"] = Repo,
        ["IssueNumber"] = 7,
        ["TargetAgent"] = "impl-agent",
        ["ConsensusAgents"] = Reviewer,
        ["DocPrepAgent"] = "prep-agent",
        ["WaiterWorkflowId"] = Waiter,
    };

    private static string Review(string verdict, string? reviewedRef)
    {
        var lines = new List<string> { "detailed review", "SUMMARY: reviewed", "EVIDENCE: none", "BLOCKER: none" };
        if (reviewedRef is not null) lines.Add($"REVIEWED_REF: {reviewedRef}");
        lines.Add($"VERDICT: {verdict}");
        return string.Join('\n', lines);
    }

    private static int CompletedConsensusChildren(WorkflowHistory history) =>
        history.Events.Count(e => e.EventType == EventType.ChildWorkflowExecutionCompleted
            && e.ChildWorkflowExecutionCompletedEventAttributes.WorkflowType.Name == "ConsensusReviewWorkflow");

    /// <summary>The decision in the one wake the run sent, whether or not it was delivered.</summary>
    private static string? SentWaiterDecision(SeedRun run)
    {
        var sent = Assert.Single(run.History.Events,
            e => e.EventType == EventType.SignalExternalWorkflowExecutionInitiated);
        var payload = DataConverter.Default.PayloadConverter.ToValue<JsonElement>(
            sent.SignalExternalWorkflowExecutionInitiatedEventAttributes.Input.Payloads_[0]);
        return payload.GetProperty("decision").GetString();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition never held");
            await Task.Delay(50);
        }
    }
}
