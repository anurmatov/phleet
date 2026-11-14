using System.Text.Json;
using Fleet.Shared;
using Fleet.Temporal.Engine;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// The shipped <c>UwePrImplementationWorkflow</c> seed, run end to end with scripted agents: a
/// technical pending state is owned by the run and never becomes a human gate, and an approved
/// merge issues its command at most once.
///
/// <para>
/// Three places wait instead of asking a human: a <c>needs_human_review</c> verdict whose head is
/// not green (review hold), a CI wait that reached its bound (technical stall), and an approved
/// merge whose head is not ready (merge hold). The scripts below answer each CI read by its
/// ordinal, so a scenario states its whole evidence sequence up front.
/// </para>
///
/// <para>
/// In this file the delegate is a stub. What rests on the bridge and the engine keeping
/// <c>noRedelivery</c> (one publish attempt, a cancellation that ends the run at the merge turn,
/// the flag in the activity input) is in <c>PrReadinessHoldTests.MergeTurn.cs</c>.
/// </para>
/// </summary>
public sealed partial class PrReadinessHoldTests
{
    private const string Workflow = "UwePrImplementationWorkflow";
    private const string Reviewer = "reviewer-a";
    private const string Coordinator = "cto-agent";
    private const string PrUrl = "https://github.com/example-org/example-repo/pull/7";

    // ── review path ──────────────────────────────────────────────────────────

    /// <summary>
    /// The first verdict's probe is not green, so the run holds. One hold read is green and buys a
    /// fresh review; that second verdict's probe is not green either. Neither verdict opens the
    /// pause, and the two holds share the head's twelve passes.
    /// </summary>
    [Theory]
    [InlineData("pending", "pending 13, unavailable 0, silent 0")]
    [InlineData("error", "pending 0, unavailable 13, silent 0")]
    [InlineData("error_headless", "pending 0, unavailable 13, silent 0")]
    [InlineData("silent", "pending 0, unavailable 0, silent 13")]
    [InlineData("throw", "pending 0, unavailable 0, silent 13")]
    public async Task RepeatedNeedsHuman_NonSuccessCi_NeverOpensHumanGate(string read, string counted)
    {
        var script = new Script { Review = _ => "needs_human_review", Probe = n => n == 2 ? "success" : read };
        var run = await RunAsync(script, (_, _) => Task.CompletedTask, captureWaiter: true);

        Assert.Equal(2, run.ConsensusInputs.Count);
        Assert.DoesNotContain("human-review", run.Upserts("Phase"));
        Assert.DoesNotContain(run.Calls, IsHumanPauseNotice);
        Assert.Equal(12, Timers(run, 300));
        Assert.Equal(18, run.Calls.Count(c => c.Step == "ci_probe"));
        Assert.Contains(counted, Assert.Single(run.Calls, c => c.Step == "notify_stalled").Instruction);
        Assert.DoesNotContain(run.Calls, c => c.Step is "pin" or "phase4_merge" or "phase4_merge_pinned");
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        Assert.Equal("cancelled", run.WaiterDecision);
    }

    [Fact]
    public async Task RepeatedNeedsHuman_SharedHoldBudget_ThenStalled()
    {
        // Five passes in the first hold (the fifth is green), then a second verdict and seven more.
        var script = new Script { Review = _ => "needs_human_review", Probe = n => n == 6 ? "success" : "pending" };
        var run = await RunAsync(script, (_, _) => Task.CompletedTask);

        Assert.Equal(2, run.ConsensusInputs.Count);
        Assert.Equal(12, Timers(run, 300));
        // Two review entries, then the stall; nothing in between asks a human.
        Assert.Equal(["implementing", "consensus-review", "consensus-review", "technical-stalled"], run.Upserts("Phase"));
        Assert.Equal($"v1;head={script.Head};result=blocked;iso=production;block=ci_timeout;fix=0;batch=0", run.Upserts("Acceptance").Last());
        Assert.Equal(4, Timers(run, 10800));
        ValidWrites(run);
    }

    [Fact]
    public async Task NeedsHumanWithGreenCi_StillOpensHumanGate()
    {
        var script = new Script { Review = round => round == 1 ? "needs_human_review" : "approved" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await WaitPhaseAsync(handle, h, "human-review");
            var before = h.Calls.Select(c => c.Step).ToList();
            Assert.Single(before, s => s == "ci_probe");
            Assert.DoesNotContain("pin", before);
            await handle.SignalAsync("human-review", [Json(new { Decision = "approved" })]);
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        });

        // The probe came before the pause notice, and the approved pause led to a fresh review.
        var steps = run.Calls.ToList();
        Assert.True(steps.FindIndex(c => c.Step == "ci_probe") < steps.FindIndex(c => IsHumanPauseNotice(c)));
        Assert.Single(run.Calls, c => c.Step == "ci_probe");
        Assert.Equal(2, run.ConsensusInputs.Count);
        Assert.Equal(0, Timers(run, 300));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
    }

    [Fact]
    public async Task NeedsHumanWhileCiFailed_ReturnsToImplementer()
    {
        var script = new Script { Review = round => round == 1 ? "needs_human_review" : "approved", Probe = n => n == 1 ? "failure" : "success" };
        var first = script.Head;
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        });

        Assert.DoesNotContain("human-review", run.Upserts("Phase"));
        Assert.DoesNotContain(run.Calls, IsHumanPauseNotice);
        var revision = Assert.Single(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal));
        Assert.Equal("revise_consensus_feedback", revision.Step);
        Assert.Contains($"CI failed at head {first}", revision.Instruction);
        Assert.Contains("build, lint", revision.Instruction);
        Assert.Contains("operational decision", revision.Instruction);   // the reviewers' text
        Assert.NotEqual(first, script.Head);
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
    }

    [Fact]
    public async Task NeedsHumanWithMovedHead_Invalidates_WithoutPauseOrHold()
    {
        var script = new Script { Review = round => round == 1 ? "needs_human_review" : "approved", Probe = n => n == 1 ? "moved" : "success" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        });

        Assert.DoesNotContain("human-review", run.Upserts("Phase"));
        Assert.Equal(0, Timers(run, 300));
        Assert.Single(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
    }

    [Fact]
    public async Task Stalled_ThenGreen_ReviewsAgain_NotStraightToPause()
    {
        // Verdict, a spent hold (13 reads), a stall whose first read is green, then green.
        var script = new Script { Review = _ => "needs_human_review", Probe = n => n <= 13 ? "pending" : "success" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "Phase") == "human-review");
            await handle.SignalAsync("human-review", [Json(new { Decision = "rejected" })]);
        });

        // The stored verdict predates the green read: a review runs between the stall and the pause.
        Assert.Equal(2, run.ConsensusInputs.Count);
        Assert.Equal(["implementing", "consensus-review", "technical-stalled", "consensus-review", "human-review"], Phases(run));
        Assert.Equal(12, Timers(run, 300));
        Assert.Equal(1, Timers(run, 10800));
        Assert.Equal(15, run.Calls.Count(c => c.Step == "ci_probe"));   // 13, the stall's read, the second verdict's
    }

    [Fact]
    public async Task CiProbe_HasNoPlanOwnerOrAccessStep_InRequiredMode()
    {
        var probes = Definition().Where(s => Name(s) == "ci_probe").ToList();
        Assert.Equal(3, probes.Count);
        Assert.All(probes, p =>
        {
            Assert.Equal(probes[0].GetProperty("instruction").GetString(), p.GetProperty("instruction").GetString());
            Assert.Equal("delegate", Type(p));
            Assert.Equal("{{config.CtoAgent}}", p.GetProperty("target").GetString());
            Assert.True(p.GetProperty("ignoreFailure").GetBoolean());
            Assert.False(p.GetProperty("retryOnIncomplete").GetBoolean());
            Assert.Equal(0, p.GetProperty("maxIncompleteRetries").GetInt32());
            Assert.Equal(15, p.GetProperty("timeoutMinutes").GetInt32());
        });
        Assert.DoesNotContain("acceptance_mode", probes[0].GetProperty("instruction").GetString());

        string? Rendered(SeedRun run) => run.Calls.First(c => c.Step == "ci_probe").Instruction;
        async Task<SeedRun> RunInMode(bool required) => await RunAsync(
            new Script { Required = required, Review = round => round == 1 ? "needs_human_review" : "approved" },
            async (handle, h) =>
            {
                await WaitPhaseAsync(handle, h, "human-review");
                await handle.SignalAsync("human-review", [Json(new { Decision = "rejected" })]);
            }, required: required);

        var requiredMode = Rendered(await RunInMode(required: true))!;
        Assert.Equal(Rendered(await RunInMode(required: false)), requiredMode);
        Assert.Contains("ci-rollup", requiredMode);
        // Nothing but the head and the CI pages: none of the acceptance work a required-mode PIN does.
        Assert.DoesNotMatch("(?i)plan|owner|access|receipt|capabilit|design|prior|harness|body|REQUIRE_ACCEPTANCE|change-class",
            requiredMode.Replace("/opt/fleet/acceptance_plan.py", "<validator>", StringComparison.Ordinal));
    }

    [Fact]
    public void ReviewPrompt_SaysChecksAreVerifiedAfterReview_AndWhatNeedsHumanIsFor()
    {
        var review = Assert.Single(WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).OfType<ChildWorkflowStep>(), c => c.Name == "consensus_review");
        var prompt = review.Args!["ReviewPrompt"]!.ToString()!;
        Assert.Contains("the workflow verifies every check at this exact head after review", prompt);
        Assert.Contains("a check that is not green blocks the merge gate", prompt);
        Assert.Contains("Do not hold or change a verdict because a check is still running.", prompt);
        Assert.Contains("A failed check that matters is a BLOCKER with changes_requested.", prompt);
        Assert.Contains("needs_human_review is for a decision a human must make.", prompt);
    }

    [Fact]
    public async Task ReviewHold_NeverPublishesReviewRef()
    {
        var script = new Script { Review = _ => "needs_human_review", Probe = _ => "pending" };
        var run = await RunAsync(script, (_, _) => Task.CompletedTask);

        // The whole hold and the stall after it ran: twelve passes, four reads, no human.
        Assert.Equal(12, Timers(run, 300));
        Assert.Equal(["implementing", "consensus-review", "technical-stalled"], Phases(run));
        Assert.NotEmpty(run.Upserts("ReviewRef"));
        Assert.All(run.Upserts("ReviewRef"), value => Assert.Equal("", value));
        Assert.All(run.Upserts("ReviewScrub"), value => Assert.Equal("", value));
        Assert.Empty(run.Upserts("GateRef"));
        Assert.Empty(run.Upserts("GateVisit"));
        Assert.Single(run.ConsensusInputs);
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase2_5_unresolved_check" or "pin" or "phase4_merge" or "phase4_merge_pinned");
    }

    // ── CI wait and the technical stall ──────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CiPendingPastBound_NoCeoGate(bool required)
    {
        var script = new Script { Required = required, Pin = _ => "pending", Probe = _ => "pending" };
        var run = await RunAsync(script, (_, _) => Task.CompletedTask, required: required);

        Assert.DoesNotContain(run.Upserts("Phase"), p => p is "acceptance-blocked" or "acceptance-continue" or "human-review" or "merge-approval");
        Assert.Equal("technical-stalled", run.Upserts("Phase").Last());
        Assert.Empty(run.Upserts("GateRef"));
        Assert.Empty(run.Upserts("GateVisit"));
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_acceptance_gate");
        Assert.DoesNotContain(run.Calls, c => c.Instruction.StartsWith("Notify the primary human", StringComparison.Ordinal));
        Assert.Contains($"v1;head={script.Head};result=blocked;iso={(required ? "isolated" : "production")};block=ci_timeout;fix=0;batch=0", run.Upserts("Acceptance"));

        var notice = Assert.Single(run.Calls, c => c.Step == "notify_stalled");
        Assert.Equal(Coordinator, notice.Target);
        Assert.Contains("Do not notify the primary human", notice.Instruction);
        Assert.Contains($"head {script.Head}, reason ci_timeout", notice.Instruction);
        Assert.Contains("pending 13, unavailable 0, silent 0", notice.Instruction);
        Assert.Contains("180, 360, 540 and 720 minutes", notice.Instruction);
        Assert.Equal(12, Timers(run, 300));
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        ValidWrites(run);
    }

    [Fact]
    public async Task Stalled_FourTimersOf10800_FourReads_ThenTimeoutOutcome()
    {
        var script = new Script { Pin = _ => "pending", Probe = _ => "pending" };
        var run = await RunAsync(script, (_, _) => Task.CompletedTask, captureWaiter: true);

        Assert.Equal(Enumerable.Repeat(300L, 12).Concat(Enumerable.Repeat(10800L, 4)), TimerSeconds(run.History));
        Assert.Equal(TimerSeconds(run.History), FiredSeconds(run.History));
        Assert.Equal(4, run.Calls.Count(c => c.Step == "ci_probe"));
        // Each read follows its own timer.
        var afterStall = run.Calls.SkipWhile(c => c.Step != "notify_stalled").Skip(1).Select(c => c.Step).ToList();
        Assert.Equal(["ci_probe", "ci_probe", "ci_probe", "ci_probe"], afterStall);
        // The outcome a gate timeout has: the waiter is told cancelled, the PR is open, the run is not Failed.
        Assert.Equal("cancelled", run.WaiterDecision);
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned" or "notify_outer_loop_exhausted");
        Assert.Empty(run.ChildStarts("UweDocMaintenanceWorkflow"));
    }

    [Fact]
    public async Task Stalled_GreenAtSecondRead_ResumesRoute()
    {
        // The PIN and its twelve waits are pending; the stall's second read is green and the PIN after it too.
        var script = new Script { Pin = n => n <= 13 ? "pending" : "success", Probe = n => n == 1 ? "pending" : "success" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        });

        Assert.Equal(2, Timers(run, 10800));
        Assert.Equal(2, run.Calls.Count(c => c.Step == "ci_probe"));
        Assert.Equal(14, run.Calls.Count(c => c.Step == "pin"));
        Assert.Single(run.ConsensusInputs);                       // the resume route is PIN, not a new review
        Assert.Single(run.Calls, c => c.Step == "notify_stalled");
        Assert.Equal(["implementing", "consensus-review", "technical-stalled", "merge-approval"], Phases(run));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        ValidWrites(run);
    }

    [Fact]
    public async Task Stalled_CiFailure_ReturnsToImplementer()
    {
        var script = new Script { Pin = n => n <= 13 ? "pending" : "success", Probe = _ => "failure" };
        var first = script.Head;
        var run = await RunAsync(script, async (handle, h) =>
        {
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        });

        Assert.Equal(1, Timers(run, 10800));
        var revision = Assert.Single(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal));
        Assert.Contains($"CI failed at head {first}", revision.Instruction);
        Assert.Contains("build, lint", revision.Instruction);
        Assert.Equal(2, run.ConsensusInputs.Count);
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
    }

    [Fact]
    public async Task Stalled_HeadMoves_Invalidates()
    {
        var script = new Script { Pin = n => n <= 13 ? "pending" : "success", Probe = _ => "moved" };
        var first = script.Head;
        var run = await RunAsync(script, async (handle, h) =>
        {
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        });

        Assert.Equal(1, Timers(run, 10800));
        Assert.Single(run.Calls, c => c.Step == "notify_invalidation");
        Assert.DoesNotContain(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal));
        Assert.Equal([first, script.Head], run.ConsensusInputs.Select(i => i.ReviewRef));
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
    }

    [Fact]
    public async Task Stalled_SignalsOfAnyName_ChangeNothing()
    {
        SeedRun quiet = await RunAsync(new Script { Pin = _ => "pending", Probe = _ => "pending" }, (_, _) => Task.CompletedTask, captureWaiter: true);
        var signalled = await RunAsync(new Script { Pin = _ => "pending", Probe = _ => "pending" }, async (handle, h) =>
        {
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "Phase") == "technical-stalled" && TimerSeconds(history).Contains(10800));
            await handle.SignalAsync("merge-approval", [Json(new { Decision = "approved" })]);
            await handle.SignalAsync("merge-approval", [Json(new { Decision = "approved", Ref = "merge:" + new string('1', 40) + ":1" })]);
            await handle.SignalAsync("acceptance-decision", [Json(new { Decision = "continue", Ref = "block:1:0:00000000" })]);
            await handle.SignalAsync("acceptance-decision", [Json(new { Decision = "stop" })]);
            await handle.SignalAsync("human-review", [Json(new { Decision = "approved" })]);
            await handle.SignalAsync("escalation-decision", [Json(new { Decision = "retry" })]);
            await handle.SignalAsync("technical-stalled", [Json(new { Decision = "continue" })]);
        }, captureWaiter: true);

        Assert.Equal(Enumerable.Repeat(300L, 12).Concat(Enumerable.Repeat(10800L, 4)), FiredSeconds(signalled.History));
        Assert.Equal(TimerSeconds(quiet.History), TimerSeconds(signalled.History));
        Assert.Equal(quiet.Calls.Select(c => c.Step), signalled.Calls.Select(c => c.Step));
        Assert.Equal(quiet.Upserts("Phase"), signalled.Upserts("Phase"));
        Assert.Empty(signalled.Upserts("GateRef"));
        Assert.Equal("cancelled", signalled.WaiterDecision);
        Assert.Equal(WorkflowExecutionStatus.Completed, signalled.Status);
    }

    [Fact]
    public async Task Stalled_CancelDuringTimer_Canceled()
    {
        WorkflowExecutionStatus? status = null;
        List<string> calls = [];
        var ex = await Assert.ThrowsAsync<WorkflowFailedException>(() =>
            RunAsync(new Script { Pin = _ => "pending", Probe = _ => "pending" }, async (handle, h) =>
            {
                await DriveToAsync(handle, h, history => h.LastUpsert(history, "Phase") == "technical-stalled" && TimerSeconds(history).Contains(10800));
                await handle.CancelAsync();
                await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync<JsonElement?>());
                status = (await handle.DescribeAsync()).Status;
                calls = h.Calls.Select(c => c.Step).ToList();
            }));

        Assert.IsType<CanceledFailureException>(ex.InnerException);
        Assert.Equal(WorkflowExecutionStatus.Canceled, status);
        Assert.Equal("notify_stalled", calls[^1]);   // cancelled inside the first timer: no read, no resume
        Assert.DoesNotContain("ci_probe", calls);
    }

    [Theory]
    [InlineData("silent")]
    [InlineData("throw")]
    public async Task SilentPins_RecordedAsSilent_NotAsRunningCi(string silent)
    {
        // One answered PIN says pending; the twelve waits get no usable answer.
        var script = new Script { Pin = n => n == 1 ? "pending" : silent, Probe = _ => "pending" };
        var run = await RunAsync(script, (_, _) => Task.CompletedTask);

        Assert.Equal(12, Timers(run, 300));
        Assert.Contains("pending 1, unavailable 0, silent 12", Assert.Single(run.Calls, c => c.Step == "notify_stalled").Instruction);
        // No answer is not a moved head: nothing is invalidated and no review is repeated.
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Single(run.ConsensusInputs);
        Assert.Contains(run.Upserts("Acceptance"), a => a.Contains("block=ci_timeout;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("error_success")]
    public async Task ReadError_RecordedAsUnavailable(string read)
    {
        // A read that reports its own failure is never success, whatever CI line comes with it.
        var script = new Script { Pin = _ => read, Probe = _ => "pending" };
        var run = await RunAsync(script, (_, _) => Task.CompletedTask);

        Assert.Equal(12, Timers(run, 300));
        Assert.Contains("pending 0, unavailable 13, silent 0", Assert.Single(run.Calls, c => c.Step == "notify_stalled").Instruction);
        Assert.Empty(run.Upserts("GateRef"));
        Assert.DoesNotContain("merge-approval", run.Upserts("Phase"));
    }

    [Fact]
    public async Task ReadCounters_RestartWhenTheHeadChanges()
    {
        // Head one: a failed probe after two pending holds. Head two: a full CI wait.
        var script = new Script
        {
            Review = round => round == 1 ? "needs_human_review" : "approved",
            Probe = n => n <= 3 ? "pending" : n == 4 ? "failure" : "pending",
            Pin = _ => "pending",
        };
        var run = await RunAsync(script, (_, _) => Task.CompletedTask);

        Assert.Single(run.Calls, c => c.Step == "revise_consensus_feedback");
        var notice = Assert.Single(run.Calls, c => c.Step == "notify_stalled");
        Assert.Contains($"head {script.Head}", notice.Instruction);
        Assert.Contains("pending 13, unavailable 0, silent 0", notice.Instruction);   // not 16
    }

    // ── after a human approval ───────────────────────────────────────────────

    [Fact]
    public async Task ApprovedThenPendingCiSameHead_KeepsApproval_OneMerge()
    {
        var script = new Script { Repin = n => n <= 2 ? "pending" : "success" };
        var run = await RunAsync(script, ApproveFirstVisit);

        Assert.Single(run.ConsensusInputs);
        Assert.Equal(["merge-approval:1"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Equal([$"merge:{script.Head}:1"], run.Upserts("GateRef"));
        Assert.Equal(["", script.Head], run.Upserts("ReviewRef"));           // published once, never withdrawn
        Assert.Equal(2, Timers(run, 300));
        Assert.Equal(3, run.Calls.Count(c => c.Step == "pin_after_approval"));
        Assert.Single(run.Calls, c => c.Step == "merge_preflight");
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
        Assert.DoesNotContain(run.Calls, c => c.Step is "notify_invalidation" or "notify_stalled");
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "merge-hold"], Phases(run));
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("silent")]
    [InlineData("throw")]
    public async Task ApprovedThenUnreadableCiSameHead_KeepsApproval_OneMerge(string read)
    {
        var script = new Script { Repin = n => n == 1 ? read : "success" };
        var run = await RunAsync(script, ApproveFirstVisit);

        Assert.Single(run.ConsensusInputs);
        Assert.Equal(1, Timers(run, 300));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_invalidation");
    }

    [Fact]
    public async Task ApprovedThenCiFailureSameHead_ReturnsToImplementer()
    {
        var script = new Script { Repin = n => n == 1 ? "failure" : "success" };
        var first = script.Head;
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.DoesNotContain(h.Calls, c => c.Step == "phase4_merge");
            await h.SignalHumanApprovalAsync(handle);
        });

        var revision = Assert.Single(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal));
        Assert.Contains("CI: failure", revision.Instruction);
        Assert.Contains("build, lint", revision.Instruction);
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Equal(0, Timers(run, 300));
        Assert.Equal([first, script.Head], run.ConsensusInputs.Select(i => i.ReviewRef));
        Assert.Equal([$"merge:{first}:1", $"merge:{script.Head}:2"], run.Upserts("GateRef"));
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
    }

    [Fact]
    public async Task PreflightHeld_NoMergeCommandIssued()
    {
        var held = true;
        var script = new Script { Preflight = _ => held ? "held" : "clear" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await DriveToAsync(handle, h, history => Timers(history, 300) >= 3, pastStall: false);

            var history = await handle.FetchHistoryAsync();
            Assert.Equal("merge-hold", h.LastUpsert(history, "Phase"));
            Assert.Equal($"merge:{script.Head}:1", h.LastUpsert(history, "GateRef"));   // left as published
            Assert.DoesNotContain(h.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned" or "verify_merge_status");
            held = false;
        });

        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Single(run.Upserts("Phase"), p => p == "merge-hold");
    }

    [Theory]
    [InlineData("silent")]
    [InlineData("throw")]
    [InlineData("unknown_token")]
    [InlineData("clear_without_draft")]
    public async Task PreflightSilentOrUnparseable_IsHeld(string answer)
    {
        var script = new Script { Preflight = n => n == 1 ? answer : "clear" };
        var run = await RunAsync(script, ApproveFirstVisit);

        Assert.Equal(1, Timers(run, 300));
        Assert.Contains("merge-hold", run.Upserts("Phase"));
        Assert.Equal(2, run.Calls.Count(c => c.Step == "merge_preflight"));
        Assert.Equal(["merge_preflight", "pin_after_approval", "merge_preflight", "phase4_merge"],
            run.Calls.SkipWhile(c => c.Step != "merge_preflight").Select(c => c.Step).Take(4));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
    }

    [Fact]
    public async Task HeldThenClear_OneMergeSamePinnedHead_OneGateVisit()
    {
        var script = new Script { Preflight = n => n <= 4 ? "held" : "clear" };
        var run = await RunAsync(script, ApproveFirstVisit);

        Assert.Equal(4, Timers(run, 300));
        Assert.Equal(5, run.Calls.Count(c => c.Step == "pin_after_approval"));   // every pass re-pins before it asks
        Assert.Equal(5, run.Calls.Count(c => c.Step == "merge_preflight"));
        Assert.Equal(["merge-approval:1"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Single(run.ConsensusInputs);
        var merge = Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Contains($"gh pr merge {PrUrl} --repo example-org/example-repo --squash --match-head-commit {script.Head}", merge.Instruction);
        Assert.DoesNotContain("gh pr ready", merge.Instruction);
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    [Fact]
    public async Task DraftAtPreflight_MergeTurnMarksReadyFirst()
    {
        var script = new Script { Preflight = _ => "clear_draft" };
        var run = await RunAsync(script, ApproveFirstVisit);

        var merge = Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction;
        var ready = merge.IndexOf($"gh pr ready {PrUrl} --repo example-org/example-repo\n", StringComparison.Ordinal);
        Assert.True(ready >= 0 && ready < merge.IndexOf("gh pr merge ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HoldThenHeadMoves_Invalidates_NoMerge()
    {
        var script = new Script { Preflight = _ => "held", Repin = n => n == 3 ? "moved" : "success" };
        var first = script.Head;
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:2", pastStall: false);
            var gateRef = h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef");
            await handle.SignalAsync("merge-approval", [Json(new { Decision = "rejected", Ref = gateRef })]);
        });

        Assert.NotEqual(first, script.Head);
        Assert.Equal(2, Timers(run, 300));
        Assert.Single(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Equal([first, script.Head], run.ConsensusInputs.Select(i => i.ReviewRef));
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
        Assert.Equal(["", first, "", "", script.Head], run.Upserts("ReviewRef"));
    }

    [Fact]
    public async Task HoldBound_TwelvePasses_ThenStalled_NotFailed()
    {
        // CI is green throughout: what holds the merge is the preflight, and it never clears.
        var script = new Script { Preflight = _ => "held" };
        var run = await RunAsync(script, ApproveFirstVisit, captureWaiter: true);

        Assert.Equal(Enumerable.Repeat(300L, 12).Concat(Enumerable.Repeat(10800L, 4)), FiredSeconds(run.History));
        // The check after approval, twelve passes, then the stall's four reads, each of them the
        // same check: a re-pin and a preflight. Green CI alone ends nothing here.
        Assert.Equal(13 + 4, run.Calls.Count(c => c.Step == "merge_preflight"));
        Assert.Equal(13 + 4, run.Calls.Count(c => c.Step == "pin_after_approval"));
        Assert.DoesNotContain(run.Calls, c => c.Step == "ci_probe");
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_outer_loop_exhausted");
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "merge-hold", "technical-stalled"], Phases(run));
        Assert.Equal($"v1;head={script.Head};result=blocked;iso=production;block=merge_held;fix=0;batch=0", run.Upserts("Acceptance").Last());
        Assert.Contains("reason merge_held", Assert.Single(run.Calls, c => c.Step == "notify_stalled").Instruction);
        Assert.Equal([$"merge:{script.Head}:1"], run.Upserts("GateRef"));       // the stall writes none
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned" or "notify_merge_failed" or "notify_acceptance_gate");
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        Assert.Null(run.Failure);
        Assert.Equal("cancelled", run.WaiterDecision);
        ValidWrites(run);
    }

    [Fact]
    public async Task MergeHoldStalled_ThenGreen_ChecksAgain_AndMergesOnce()
    {
        // Thirteen held answers spend the hold; the stall's first read is the check again, and it is clear.
        var script = new Script { Preflight = n => n <= 13 ? "held" : "clear" };
        var run = await RunAsync(script, ApproveFirstVisit);

        Assert.DoesNotContain(run.Calls, c => c.Step == "ci_probe");
        Assert.Equal(["notify_stalled", "pin_after_approval", "merge_preflight", "phase4_merge"],
            run.Calls.SkipWhile(c => c.Step != "notify_stalled").Select(c => c.Step).Take(4));
        Assert.Equal(12, Timers(run, 300));
        Assert.Equal(1, Timers(run, 10800));
        Assert.Equal(14, run.Calls.Count(c => c.Step == "merge_preflight"));
        Assert.Equal(["merge-approval:1"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Single(run.ConsensusInputs);
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        // Back from the stall the record is the gate's again, not a blocked one.
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "merge-hold", "technical-stalled", "merge-hold"], Phases(run));
        Assert.Equal($"v1;head={script.Head};result=not_required;iso=production;block=none;fix=0;batch=0", run.Upserts("Acceptance").Last());
        ValidWrites(run);
    }

    [Fact]
    public async Task MergeHoldStalled_HeldThenClearAtTheSecondRead_OneMergeSamePinnedHead()
    {
        // Thirteen held answers in the hold, one more at the stall's first read, clear at its second.
        var script = new Script { Preflight = n => n <= 14 ? "held" : "clear" };
        var run = await RunAsync(script, ApproveFirstVisit);

        Assert.Equal(Enumerable.Repeat(300L, 12).Concat(Enumerable.Repeat(10800L, 2)), FiredSeconds(run.History));
        Assert.Single(run.Calls, c => c.Step == "notify_stalled");
        Assert.DoesNotContain(run.Calls, c => c.Step == "ci_probe");
        Assert.Equal(15, run.Calls.Count(c => c.Step == "merge_preflight"));
        Assert.Equal(15, run.Calls.Count(c => c.Step == "pin_after_approval"));
        Assert.Equal(["merge-approval:1"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Equal([$"merge:{script.Head}:1"], run.Upserts("GateRef"));
        Assert.Single(run.ConsensusInputs);
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
        // Stalled while it waited, the gate's record again before the command.
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "merge-hold", "technical-stalled", "merge-hold"], Phases(run));
        Assert.Equal($"v1;head={script.Head};result=not_required;iso=production;block=none;fix=0;batch=0", run.Upserts("Acceptance").Last());
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        ValidWrites(run);
    }

    [Fact]
    public async Task MergeHoldStalled_CiFailureAtAStalledRead_ReturnsToImplementer()
    {
        // The hold's thirteen re-pins are green and held by the preflight; the stall's first re-pin is red.
        var script = new Script { Preflight = n => n <= 13 ? "held" : "clear", Repin = n => n == 14 ? "failure" : "success" };
        var first = script.Head;
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:2");
            Assert.DoesNotContain(h.Calls, c => c.Step == "phase4_merge");
            await h.SignalHumanApprovalAsync(handle);
        });

        Assert.Equal(1, Timers(run, 10800));
        Assert.DoesNotContain(run.Calls, c => c.Step == "ci_probe");
        Assert.Single(run.Calls, c => c.Step == "notify_stalled");
        var revision = Assert.Single(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal));
        Assert.Contains($"CI failed at head {first}. Failing checks: build, lint.", revision.Instruction);
        Assert.Contains("CI: failure", revision.Instruction);
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Equal([first, script.Head], run.ConsensusInputs.Select(i => i.ReviewRef));
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
    }

    [Fact]
    public async Task MergeHoldStalled_HeadMovesAtAStalledRead_Invalidates_NoMerge()
    {
        var script = new Script { Preflight = _ => "held", Repin = n => n == 14 ? "moved" : "success" };
        var first = script.Head;
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:2");
            var gateRef = h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef");
            await handle.SignalAsync("merge-approval", [Json(new { Decision = "rejected", Ref = gateRef })]);
        });

        Assert.NotEqual(first, script.Head);
        Assert.Equal(1, Timers(run, 10800));
        Assert.DoesNotContain(run.Calls, c => c.Step == "ci_probe");
        Assert.Single(run.Calls, c => c.Step == "notify_stalled");
        Assert.Single(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Equal([first, script.Head], run.ConsensusInputs.Select(i => i.ReviewRef));
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
    }

    [Fact]
    public async Task MergeTurnAnswersHeld_TreatedAsFailed_NoRetry()
    {
        var script = new Script { Merge = _ => "held", Verify = _ => "open" };
        var run = await RunAsync(script, ApproveFirstVisit, allowFailure: true);

        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Single(run.Calls, c => c.Step == "merge_preflight");
        Assert.Equal(0, Timers(run, 300));
        Assert.DoesNotContain("merge-hold", run.Upserts("Phase"));
        Assert.Single(run.Calls, c => c.Step == "notify_merge_failed");
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Equal(SeedReviewLimitTests.DeliveryFailed, run.FailureMessage);
    }

    [Fact]
    public async Task MergeCommandError_NotMerged_NeverReissued()
    {
        var script = new Script { Merge = _ => "failed", Verify = _ => "open" };
        var run = await RunAsync(script, ApproveFirstVisit, allowFailure: true);

        // One clear preflight, one command, one answered read (not repeated), the notice. Nothing after.
        Assert.Equal(["merge_preflight", "phase4_merge", "verify_merge_status", "notify_merge_failed"],
            run.Calls.SkipWhile(c => c.Step != "merge_preflight").Select(c => c.Step));
        Assert.Equal(0, Timers(run, 60));
        Assert.DoesNotContain(run.Calls, c => c.Step.EndsWith("_escalation_notify", StringComparison.Ordinal));
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Equal(SeedReviewLimitTests.DeliveryFailed, run.FailureMessage);
    }

    [Fact]
    public async Task MergeRefusedForMovedHead_Invalidates_AndTheNextVisitIssuesItsOwnCommand()
    {
        var script = new Script { Merge = n => n == 1 ? "head_changed" : "merged", Verify = n => n == 1 ? "open" : "merged" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            await h.SignalHumanApprovalAsync(handle);
        });

        // One command per gate visit: the first was refused, a fresh review and approval allow the second.
        Assert.Equal(2, run.Calls.Count(c => c.Step == "phase4_merge"));
        Assert.Equal(2, run.ConsensusInputs.Count);
        Assert.Single(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MergeTurnThrowsOrTimesOut_ReadbackOnly(bool landed)
    {
        var script = new Script { Merge = _ => "throw", Verify = _ => landed ? "merged" : "open" };
        var run = await RunAsync(script, ApproveFirstVisit, allowFailure: true);

        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Single(run.Calls, c => c.Step == "verify_merge_status");
        Assert.DoesNotContain(run.Calls, c => c.Step.EndsWith("_escalation_notify", StringComparison.Ordinal));
        Assert.DoesNotContain("escalation", run.Upserts("Phase"));
        if (landed)
        {
            Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
            Assert.Single(run.ChildStarts("UweDocMaintenanceWorkflow"));
        }
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
            Assert.Equal(SeedReviewLimitTests.DeliveryFailed, run.FailureMessage);
            Assert.Single(run.Calls, c => c.Step == "notify_merge_failed");
        }
    }

    [Theory]
    [InlineData("error", "silent")]
    [InlineData("throw", "error")]
    public async Task ReadbackErrorTwiceThenMerged_OneMergeThreeReads(string firstRead, string secondRead)
    {
        var script = new Script { Verify = n => n == 1 ? firstRead : n == 2 ? secondRead : "merged" };
        var run = await RunAsync(script, ApproveFirstVisit);

        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Equal(3, run.Calls.Count(c => c.Step == "verify_merge_status"));
        Assert.Equal(2, Timers(run, 60));
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_merge_failed");
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        Assert.Single(run.ChildStarts("UweDocMaintenanceWorkflow"));
    }

    [Fact]
    public async Task ReadbackNeverAnswers_ThreeReads_OneMerge_Failed()
    {
        var script = new Script { Verify = _ => "error" };
        var run = await RunAsync(script, ApproveFirstVisit, allowFailure: true);

        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Equal(3, run.Calls.Count(c => c.Step == "verify_merge_status"));
        Assert.Equal(2, Timers(run, 60));
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Equal(SeedReviewLimitTests.DeliveryFailed, run.FailureMessage);
    }

    /// <summary>
    /// The merge turn used to escalate: a failed attempt parked on <c>escalation-decision</c>, and a
    /// <c>retry</c> ran the step again, with a replaced instruction if one was sent. The signal is
    /// sent while the read after the failed turn is still held open, so the run is alive to take it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EscalationRetrySignal_CannotReachTheMergeTurn(bool updatedInstruction)
    {
        const string injected = "gh pr merge --admin";
        using var signalled = new ManualResetEventSlim(false);
        var script = new Script { Merge = n => n == 1 ? "throw" : "merged", Verify = _ => "open", HoldVerify = signalled };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await WaitForCallAsync(h, "phase4_merge");
            object decision = updatedInstruction ? new { Decision = "retry", UpdatedInstruction = injected } : new { Decision = "retry" };
            await handle.SignalAsync("escalation-decision", [Json(decision)]);
            // Before this change the failed turn notified and waited for exactly this signal.
            await WaitForCallAsync(h, "verify_merge_status", "phase4_merge_escalation_notify");
            await handle.SignalAsync("escalation-decision", [Json(decision)]);
            signalled.Set();
        }, allowFailure: true);

        var merge = Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.DoesNotContain(injected, merge.Instruction);
        Assert.DoesNotContain(run.Calls, c => c.Instruction.Contains(injected, StringComparison.Ordinal));
        Assert.DoesNotContain(run.Calls, c => c.Step == "phase4_merge_escalation_notify");
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Equal(SeedReviewLimitTests.DeliveryFailed, run.FailureMessage);
    }

    // ── the grant path ───────────────────────────────────────────────────────

    /// <summary>
    /// A delegated approval is routed as before: a re-pin that is not green voids it and the head
    /// is reviewed again; no hold, no preflight, one read after the pinned merge.
    /// </summary>
    [Fact]
    public async Task DelegatedApproval_PathUnchanged()
    {
        var script = new Script { Repin = n => n == 1 ? "pending" : "success", Verify = _ => "error" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", script.Head)]);
            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:2", script.Head)]);
        }, allowFailure: true);

        Assert.Single(run.Calls, c => c.Step == "notify_invalidation");
        Assert.Equal(2, run.ConsensusInputs.Count);
        Assert.Equal(2, run.Calls.Count(c => c.Step == "pin_after_approval"));
        Assert.DoesNotContain(run.Calls, c => c.Step is "merge_preflight" or "ci_probe" or "phase4_merge" or "notify_stalled");
        Assert.DoesNotContain(run.Upserts("Phase"), p => p is "merge-hold" or "technical-stalled");
        Assert.Equal(0, Timers(run, 300));
        Assert.Equal(0, Timers(run, 60));
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge_pinned").Instruction);
        Assert.Single(run.Calls, c => c.Step == "verify_merge_status");
        Assert.Single(run.Calls, c => c.Step == "notify_merge_failed");
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
    }

    /// <summary>
    /// The grant path's steps, compared as JSON with what <c>main</c> shipped before the holds: the
    /// pinned merge, the whole re-pin (<c>Fixtures/seed-main/pr-merge-repin.json</c>, taken from
    /// <c>main</c>'s seed) and its routing, and the single read with its outcome branch.
    /// </summary>
    [Fact]
    public void Seed_GrantPathSubtree_ByteEqualToMain()
    {
        const string pinnedOnMain = """
            {"name": "phase4_merge_pinned", "type": "delegate", "target": "{{config.CtoAgent}}", "outputVar": "merge_result", "instruction": "Merge the PR at {{vars.pr_url}} in repo {{input.Repo}}, approved under an epic grant for the reviewed head {{vars.review_ref}}. Run exactly:\ngh pr merge {{vars.pr_url}} --repo {{input.Repo}} --squash --match-head-commit {{vars.review_ref}}\n\nIf it fails (for example because the head moved after review), do NOT retry, rewrite branch history or merge any other way. Report the error. Confirm once merged.\nReturn MERGE_RESULT: merged|head_changed|failed.", "ignoreFailure": true, "timeoutMinutes": 10}
            """;
        const string routingOnMain = """
            {"name": "approval_head_still_exact", "type": "sequence", "steps": [{"name": "approval_head_still_exact_pair", "type": "set_variable", "vars": {"pair": "{{vars.head_oid}}={{vars.H}}"}}, {"name": "approval_head_still_exact_equal", "type": "branch", "on": "{{vars.pair | extract: '^([^\\r\\n]+)=\\1$'}}", "cases": {"": {"name": "route_INVALIDATED", "type": "set_variable", "vars": {"state": "INVALIDATED"}}}, "default": {"name": "approval_ci_success", "type": "branch", "on": "{{vars.ci}}", "cases": {"success": {"name": "route_MERGE", "type": "set_variable", "vars": {"state": "MERGE"}}}, "default": {"name": "route_INVALIDATED", "type": "set_variable", "vars": {"state": "INVALIDATED"}}}}]}
            """;
        var steps = Definition().ToList();

        var pinned = Assert.Single(steps, s => Name(s) == "phase4_merge_pinned");
        Assert.Equal(Canonical(pinnedOnMain), Canonical(pinned.GetRawText()));
        var routing = Assert.Single(steps, s => Name(s) == "approval_head_still_exact");
        Assert.Equal(Canonical(routingOnMain), Canonical(routing.GetRawText()));

        // A decision that carries a GrantId takes these and nothing else: approved → the old re-pin.
        var approved = Assert.Single(steps, s => Name(s) == "approved_decision_path");
        Assert.Equal("{{vars.merge_decision.GrantId | default: 'none'}}", approved.GetProperty("on").GetString());
        var repin = approved.GetProperty("default");
        Assert.Equal("merge_repin", Name(repin));
        // The whole re-pin a delegated approval takes, its PIN instruction included, is the subtree main shipped.
        var repinOnMain = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seed-main", "pr-merge-repin.json"));
        Assert.Equal(Canonical(repinOnMain), Canonical(repin.GetRawText()));
        Assert.Single(steps, s => Name(s) == "merge_repin");
        Assert.Equal(["pin_after_approval", "pin_fields_mode", "approval_head_still_exact"], repin.GetProperty("steps").EnumerateArray().Select(Name));
        Assert.Equal("set_variable", Type(approved.GetProperty("cases").GetProperty("none")));

        var mode = Assert.Single(steps, s => Name(s) == "phase4_merge_mode");
        Assert.Equal("{{vars.merge_decision.GrantId | default: 'none'}}", mode.GetProperty("on").GetString());
        var grant = mode.GetProperty("default");
        Assert.Equal("phase4_and_5", Name(grant));
        Assert.Equal(
            ["phase4_merge_pinned", "verify_merge_status", "capture_pr_state", "capture_pr_state_merged", "capture_pr_merged_at", "compose_merge_check", "check_merge_state", "merge_outcome_branch"],
            grant.GetProperty("steps").EnumerateArray().Select(Name));
        Assert.Equal(Canonical(pinnedOnMain), Canonical(grant.GetProperty("steps")[0].GetRawText()));
        // No loop, sleep or hold in either grant subtree.
        foreach (var subtree in new[] { repin, grant })
            Assert.DoesNotContain(Descendants(subtree), s => Type(s) is "loop" or "sleep" || Name(s) is "merge_preflight" or "ci_probe");
    }

    // ── the merge turn, as defined ───────────────────────────────────────────

    [Fact]
    public void Seed_HumanMergeTurn_IsPlainNoRedeliveryDelegate()
    {
        var steps = WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).ToList();
        var merge = Assert.Single(steps.OfType<DelegateStep>(), d => d.Name == "phase4_merge");

        Assert.Equal(typeof(DelegateStep), merge.GetType());   // not delegate_with_escalation
        Assert.True(merge.NoRedelivery);
        Assert.False(merge.RetryOnIncomplete);
        Assert.Equal(0, merge.MaxIncompleteRetries);
        Assert.True(merge.IgnoreFailure);
        Assert.Equal("merge_status", merge.StatusVar);
        Assert.Equal("merge_result", merge.OutputVar);
        Assert.Equal(10, merge.TimeoutMinutes);
        Assert.Equal("{{config.CtoAgent}}", merge.Target);
        Assert.DoesNotContain(steps, s => s is DelegateWithEscalationStep);
        // The only step in the definition that opts in.
        Assert.Equal([merge], steps.OfType<DelegateStep>().Where(d => d.NoRedelivery));
    }

    [Fact]
    public void Definition_PreflightHasNoWriteCommand_MergeTurnHasNoCheck()
    {
        var steps = WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).OfType<DelegateStep>().ToList();

        // Two copies of one step: the check in the hold and the same check in the stall after it.
        var preflights = steps.Where(d => d.Name == "merge_preflight").ToList();
        Assert.Equal(2, preflights.Count);
        Assert.Equal(preflights[0], preflights[1]);
        var preflight = preflights[0];
        Assert.Equal(typeof(DelegateStep), preflight.GetType());
        Assert.True(preflight.IgnoreFailure);
        Assert.False(preflight.RetryOnIncomplete);
        Assert.Equal(0, preflight.MaxIncompleteRetries);
        Assert.False(preflight.NoRedelivery);
        Assert.Contains("gh pr view {{vars.pr_url}} --repo {{input.Repo}} --json state,mergeStateStatus,mergeable,isDraft,headRefOid", preflight.Instruction);
        Assert.Contains("MERGE_PREFLIGHT: clear|held", preflight.Instruction);
        Assert.Contains("HOLD_REASON: checks_pending|queue|window_closed|other", preflight.Instruction);
        // The one command it carries is the read. No command that writes appears in it.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(preflight.Instruction!, @"\bgh \w+"));
        Assert.DoesNotMatch(@"gh pr (ready|merge|close|edit|comment|review)|git push|gh api|--admin|--auto|gh run|gh workflow", preflight.Instruction!);

        var merge = Assert.Single(steps, d => d.Name == "phase4_merge");
        Assert.Contains("{{vars.merge_ready_command}}gh pr merge {{vars.pr_url}} --repo {{input.Repo}} --squash --match-head-commit {{vars.review_ref}}\n", merge.Instruction);
        Assert.Contains("MERGE_RESULT: merged|head_changed|failed", merge.Instruction);
        Assert.DoesNotContain("held", merge.Instruction);
        // The pinned command and nothing that reads: the state was read before, and is read after, by other turns.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(merge.Instruction!, @"\bgh \w+"));
        Assert.DoesNotMatch(@"gh pr (view|checks|status|list)|gh run|gh api|--json|mergeStateStatus|--admin|--auto", merge.Instruction!);
        // The draft step is the workflow's, from the preflight's answer; the turn does not look.
        var ready = WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).OfType<SetVariableStep>().Where(s => s.Name == "merge_ready_first").ToList();
        Assert.Equal(2, ready.Count);
        Assert.All(ready, r => Assert.Equal("gh pr ready {{vars.pr_url}} --repo {{input.Repo}}\n", r.Vars["merge_ready_command"]));
    }

    [Fact]
    public void Definition_NoRouteFromMergeTurnBackToMergeTurn()
    {
        var root = Definition().First();
        var all = Descendants(root).ToList();
        string? StateSet(JsonElement s) => Type(s) == "set_variable" && s.GetProperty("vars").TryGetProperty("state", out var v) ? v.GetString() : null;

        // 1. The command is behind a once-per-visit guard, and the visit is recorded immediately before it.
        var human = Assert.Single(all, s => Name(s) == "human_merge");
        var humanSteps = human.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["merge_command_pair", "merge_command_once", "merge_readback_entry", "merge_readback_loop", "merge_outcome_branch"], humanSteps.Select(Name));
        Assert.Equal("{{vars.merge_issued_seq}}={{vars.merge_gate_seq}}", humanSteps[0].GetProperty("vars").GetProperty("pair").GetString());
        var once = humanSteps[1];
        Assert.False(once.TryGetProperty("default", out _));
        var issue = once.GetProperty("cases").GetProperty("").GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["record_merge_command", "phase4_merge"], issue.Select(Name));
        Assert.Equal("{{vars.merge_gate_seq}}", issue[0].GetProperty("vars").GetProperty("merge_issued_seq").GetString());
        Assert.Single(all, s => Name(s) == "phase4_merge");

        // 2. Everything after the command only reads, ends the run, or invalidates the head.
        var after = humanSteps.Skip(2).SelectMany(Descendants).ToList();
        Assert.DoesNotContain(after, s => Name(s) is "phase4_merge" or "merge_preflight" or "pin_after_approval");
        Assert.Equal(["INVALIDATED"], after.Select(StateSet).Where(v => v is not null).Distinct());
        Assert.All(after.Where(s => Type(s) == "delegate"), s => Assert.Contains(Name(s), new[] { "verify_merge_status", "notify_merge_failed" }));

        // 3. A visit is one entry to MERGE_GATE; nothing else moves the counter the guard compares.
        var counters = all.Where(s => Type(s) == "set_variable" && s.GetProperty("vars").TryGetProperty("merge_gate_seq", out _)).ToList();
        Assert.Equal(["init_acceptance_state", "next_merge_gate"], counters.Select(Name));
        var gate = Assert.Single(all, s => Name(s) == "MERGE_GATE");
        Assert.Equal("next_merge_gate", Name(gate.GetProperty("steps")[0]));
        var issued = all.Where(s => Type(s) == "set_variable" && s.GetProperty("vars").TryGetProperty("merge_issued_seq", out _)).ToList();
        Assert.Equal(["init_acceptance_state", "record_merge_command"], issued.Select(Name));

        // 4. The states that lead to the command are entered only from before it. A state is the
        //    case of its own dispatch branch in the delivery loop.
        var loop = Assert.Single(all, s => Name(s) == "outer_loop");
        var states = loop.GetProperty("steps").EnumerateArray()
            .Where(s => Name(s)!.StartsWith("dispatch_", StringComparison.Ordinal))
            .ToDictionary(s => Name(s)!["dispatch_".Length..], s => s.GetProperty("cases").GetProperty(Name(s)!["dispatch_".Length..]));
        string[] Setters(string target) => states.Where(kv => Descendants(kv.Value).Any(s => StateSet(s) == target)).Select(kv => kv.Key).Order().ToArray();
        // MERGE: a delegated approval's re-pin, a clear check in the hold, a clear check in the stall
        // after it. MERGE_HOLD: the gate only, so a stall never returns to the hold's waiting passes.
        Assert.Equal(["MERGE_GATE", "MERGE_HOLD", "STALLED"], Setters("MERGE"));
        Assert.Equal(["MERGE_GATE"], Setters("MERGE_HOLD"));
        // In the stall the route to the merge turn exists only for a held merge whose check was clear,
        // and only after the gate's record is back.
        var resume = Assert.Single(all, s => Name(s) == "stalled_resume_route");
        Assert.Equal("{{vars.stall_resume}}", resume.GetProperty("on").GetString());
        Assert.Equal(["restore_merge_record", "route_MERGE"], resume.GetProperty("cases").GetProperty("MERGE").GetProperty("steps").EnumerateArray().Select(Name));
        Assert.Equal(["MERGE"], Descendants(states["STALLED"]).Where(s => StateSet(s) == "MERGE").Select(_ => "MERGE"));
        Assert.Equal(["MERGE"], all.Where(s => Type(s) == "set_variable" && s.GetProperty("vars").TryGetProperty("stall_resume", out var to) && to.GetString() is not ("REVIEW" or "PIN")).Select(s => s.GetProperty("vars").GetProperty("stall_resume").GetString()));
        Assert.Equal(["CI_WAIT", "MERGE_HOLD", "REVIEW_HOLD"], Setters("STALLED"));
        Assert.Equal(["INVALIDATED"], Descendants(states["MERGE"]).Select(StateSet).Where(v => v is not null).Distinct());
        // The one templated route (a continue gate's return) names ACCEPT or IMPLEMENT, never a merge state.
        Assert.Equal(["{{vars.continue_to}}"], all.Select(StateSet).Where(v => v is not null && v.Contains("{{", StringComparison.Ordinal)).Distinct());
        Assert.Equal(["ACCEPT", "IMPLEMENT"], all.Where(s => Type(s) == "set_variable" && s.GetProperty("vars").TryGetProperty("continue_to", out _))
            .Select(s => s.GetProperty("vars").GetProperty("continue_to").GetString()).Distinct().Order());
        // Before the loop the run can only start at a review or a revision.
        var before = Descendants(root).TakeWhile(s => Name(s) != "outer_loop").Select(StateSet).Where(v => v is not null).Distinct().Order();
        Assert.Equal(["IMPLEMENT", "REVIEW"], before);
    }

    [Fact]
    public void Definition_MergeTurnHasNoIgnoreFailureAncestor()
    {
        var path = PathTo(Definition().First(), "phase4_merge");
        Assert.NotNull(path);
        Assert.Equal("phase4_merge", Name(path![^1]));
        var ancestors = path.Take(path.Count - 1).ToList();
        Assert.NotEmpty(ancestors);
        Assert.All(ancestors, a => Assert.False(a.TryGetProperty("ignoreFailure", out var flag) && flag.ValueKind == JsonValueKind.True, Name(a)));
        // One loop above it, the delivery loop; the readback loop comes after and does not contain it.
        Assert.Equal(["outer_loop"], ancestors.Where(a => Type(a) == "loop").Select(Name));
        Assert.Contains(ancestors, a => Name(a) == "merge_command_once");
    }

    [Fact]
    public void Definition_HoldsAndStallAreOwnedStates_WithLiteralBounds()
    {
        var root = SeedHarness.Parse(Workflow);
        var steps = WorkflowDefinitionValidator.Walk(root).ToList();
        var loops = steps.OfType<LoopStep>().ToDictionary(l => l.Name!);
        Assert.Equal(12, loops["ci_wait_loop"].MaxIterations);
        Assert.Equal(12, loops["review_hold_loop"].MaxIterations);
        Assert.Equal(13, loops["merge_hold_loop"].MaxIterations);   // the check after approval, then twelve passes
        Assert.Equal(4, loops["stalled_reads"].MaxIterations);
        Assert.Equal(3, loops["merge_readback_loop"].MaxIterations);

        long Sleep(string name) => Assert.Single(steps.OfType<SleepStep>(), s => s.Name == name).Seconds!.Value;
        Assert.Equal(300, Sleep("review_hold_sleep"));
        Assert.Equal(300, Sleep("merge_hold_sleep"));
        Assert.Equal(10800, Sleep("stalled_sleep"));
        Assert.Equal(60, Sleep("merge_readback_sleep"));
        // Four reads three hours apart: the 720 minutes of the gate this state replaces.
        Assert.Equal(720 * 60, loops["stalled_reads"].MaxIterations * Sleep("stalled_sleep"));

        // The stall and the holds wait on timers only: no signal wait, no gate reference.
        foreach (var state in new[] { "STALLED", "REVIEW_HOLD", "MERGE_HOLD" })
        {
            var inside = WorkflowDefinitionValidator.Walk(Assert.Single(steps.OfType<SequenceStep>(), s => s.Name == state)).ToList();
            Assert.Empty(inside.OfType<WaitForSignalStep>());
            Assert.DoesNotContain(inside.OfType<SetAttributeStep>(), w => w.Attributes.ContainsKey("GateRef"));
            Assert.All(inside.OfType<SetAttributeStep>(), w => Assert.True(w.Required));
        }
        // The new phases are no wait's phase, so no signal can be addressed to them.
        Assert.DoesNotContain(steps.OfType<WaitForSignalStep>(), w => w.Phase is "technical-stalled" or "merge-hold");
        Assert.Equal(["acceptance-decision", "acceptance-decision", "human-review", "human-review", "merge-approval"],
            steps.OfType<WaitForSignalStep>().Select(w => w.SignalName).Order());

        var notice = Assert.Single(steps.OfType<DelegateStep>(), d => d.Name == "notify_stalled");
        Assert.Equal("{{config.CtoAgent}}", notice.Target);
        Assert.True(notice.IgnoreFailure);
    }

    // ── script ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Input(bool required, bool waiter)
    {
        var input = new Dictionary<string, object?>
        {
            ["Repo"] = "example-org/example-repo",
            ["IssueNumber"] = 7,
            ["BranchName"] = "feat/example",
            ["TargetAgent"] = "impl-agent",
            ["ConsensusAgents"] = Reviewer,
        };
        if (required) input["RequireAcceptance"] = "true";
        if (waiter) input["WaiterWorkflowId"] = "waiter-example";
        return input;
    }

    private static Task<SeedRun> RunAsync(Script script, Func<WorkflowHandle, SeedHarness, Task> drive,
        bool captureWaiter = false, bool allowFailure = false, bool required = false,
        Action<System.Text.Json.Nodes.JsonNode>? mutateSeed = null,
        Func<Call, object?[], Task<Fleet.Temporal.Models.AgentTaskResult>?>? delegateOverride = null) =>
        SeedHarness.RunAsync(Workflow, Input(required, captureWaiter), script.Respond, drive,
            captureWaiter: captureWaiter, allowFailure: allowFailure, mutateSeed: mutateSeed, delegateOverride: delegateOverride);

    private static async Task ApproveFirstVisit(WorkflowHandle handle, SeedHarness h)
    {
        await h.WaitForGateVisitAsync(handle, "merge-approval:1");
        await h.SignalHumanApprovalAsync(handle);
    }

    /// <summary>
    /// Answers every delegation by step name. A CI read (<c>pin</c>, <c>pin_after_approval</c>,
    /// <c>ci_probe</c>) is chosen by its own ordinal, starting at 1: <c>success</c>,
    /// <c>pending</c>, <c>failure</c>, <c>error</c> (the read says it failed), <c>error_headless</c>
    /// (and reports no head), <c>error_success</c> (a failed read beside a success line),
    /// <c>silent</c> (no token line), <c>throw</c> (the turn fails) or <c>moved</c> (the head moves
    /// and the read reports it).
    /// </summary>
    private sealed class Script
    {
        private readonly Dictionary<string, int> _counts = [];
        private int _head = 1;
        private int _rounds;

        public string Head => _head.ToString("x40");
        public bool Required;
        public Func<int, string> Review = _ => "approved";
        public Func<int, string> Pin = _ => "success";
        public Func<int, string> Repin = _ => "success";
        public Func<int, string> Probe = _ => "success";
        public Func<int, string> Preflight = _ => "clear";
        public Func<int, string> Merge = _ => "merged";
        public Func<int, string> Verify = _ => "merged";
        public ManualResetEventSlim? HoldVerify;

        /// <summary>A step named here does not answer until its gate is set: the turn is out for as long as the test wants.</summary>
        public readonly Dictionary<string, ManualResetEventSlim> Hold = [];

        public string Respond(Call call)
        {
            int n;
            lock (_counts) n = _counts[call.Step] = _counts.GetValueOrDefault(call.Step) + 1;
            if (Hold.TryGetValue(call.Step, out var held) && !held.Wait(TimeSpan.FromSeconds(90))) throw new TimeoutException($"{call.Step} held");
            switch (call.Step)
            {
                case "phase1_implement":
                    return $"IMPLEMENTATION: complete\nPR_URL: {PrUrl}\nHEAD_RANGE: {Head}..{Head}\nHEAD_SHA: {Head}";
                case "pin": return CiRead(Pin(n), pin: true);
                case "pin_after_approval": return CiRead(Repin(n), pin: true);
                case "ci_probe": return CiRead(Probe(n), pin: false);
                case "phase2_5_unresolved_check": return "UNRESOLVED: none";
                case "notify_invalidation": return $"HEAD_OID: {Head}";
                case "prepare": return "PREP: NO_DOC";
                case "merge_preflight":
                    return Preflight(n) switch
                    {
                        "clear" => SeedHarness.PreflightClear,
                        "clear_draft" => SeedHarness.PreflightClear.Replace("IS_DRAFT: false", "IS_DRAFT: true"),
                        "held" => SeedHarness.PreflightHeld(),
                        "clear_without_draft" => "MERGE_PREFLIGHT: clear\nHOLD_REASON: none",
                        "unknown_token" => "MERGE_PREFLIGHT: probably\nHOLD_REASON: none\nIS_DRAFT: false",
                        "silent" => "The PR looks fine to me.",
                        _ => throw Synthetic("preflight"),
                    };
                case "phase4_merge" or "phase4_merge_pinned":
                    return Merge(n) switch
                    {
                        "merged" => "Merged.\nMERGE_RESULT: merged",
                        "head_changed" => "error: Head branch was modified. Review and try the merge again.\nMERGE_RESULT: head_changed",
                        "failed" => "error: merge is blocked by branch protection\nMERGE_RESULT: failed",
                        "held" => "The merge queue is busy.\nMERGE_RESULT: held",
                        _ => throw Synthetic("merge"),
                    };
                case "verify_merge_status":
                    if (HoldVerify is { } gate && !gate.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("verify held");
                    return Verify(n) switch
                    {
                        "merged" => "PR_STATE_JSON: {\"mergeCommit\":{\"oid\":\"" + Head + "\"},\"mergedAt\":\"2026-01-01T00:00:00Z\",\"state\":\"MERGED\"}",
                        "open" => "PR_STATE_JSON: {\"mergeCommit\":null,\"mergedAt\":null,\"state\":\"OPEN\"}",
                        "error" => "PR_STATE_JSON: ERROR",
                        "silent" => "gh did not answer.",
                        _ => throw Synthetic("verify"),
                    };
            }
            if (call.Step.StartsWith("revise_", StringComparison.Ordinal))
            {
                var old = Head;
                _head++;
                return $"IMPLEMENTATION: complete\nPR_URL: {PrUrl}\nHEAD_RANGE: {old}..{Head}\nHEAD_SHA: {Head}";
            }
            if (call.Step == $"review-{Reviewer}")
            {
                return Review(Interlocked.Increment(ref _rounds)) switch
                {
                    "approved" => $"SUMMARY: checked\nVERDICT: approved\nREVIEWED_REF: {Head}\nPUBLIC_SCRUB: pass",
                    "needs_human_review" => "SUMMARY: human decision required\nBLOCKER: operational decision\nVERDICT: needs_human_review",
                    var other => throw new InvalidOperationException(other),
                };
            }
            // Notices and anything else: not "ok", which the harness would replace with its defaults.
            return "done";
        }

        private string CiRead(string mode, bool pin)
        {
            switch (mode)
            {
                case "throw": throw Synthetic("read");
                case "silent": return "I could not reach the repository.";
                case "error_headless": return "CI_READ: error\nCI: pending\nCI_FAILING: none\nCI_DETAIL: none";
                case "moved": _head++; mode = "success"; break;
            }
            var (read, ci) = mode switch
            {
                "error" => ("error", "pending"),
                "error_success" => ("error", "success"),
                _ => ("ok", mode),
            };
            var lines = $"CI_READ: {read}\nCI: {ci}\nCI_FAILING: {(ci == "failure" ? "build, lint" : "none")}\nCI_DETAIL: {(mode == "pending" ? "running" : "none")}";
            if (!pin) return $"HEAD_OID: {Head}\n{lines}";
            return Required
                ? SeedHarness.DocOnlyPin(Head).Replace("CI: success", lines, StringComparison.Ordinal)
                : $"HEAD_OID: {Head}\n{lines}\nCHANGE_CLASS: code";
        }

        private static ApplicationFailureException Synthetic(string what) =>
            new($"synthetic {what} failure", errorType: "Synthetic", nonRetryable: true);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsHumanPauseNotice(Call call) =>
        call.Instruction.StartsWith("Notify the primary human that the PR needs an operational human review decision", StringComparison.Ordinal);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Delegated(string visitId, string artifactRef) => JsonSerializer.SerializeToElement(new
    {
        Decision = "approved",
        GrantId = "00000000-0000-0000-0000-000000000436",
        VisitId = visitId,
        ArtifactRef = artifactRef,
        Evidence = "https://example.com/review/1",
    });

    /// <summary>
    /// The phases in order, one entry per change: a gate publishes its phase in the required write
    /// before it and again when it parks.
    /// </summary>
    private static List<string> Phases(SeedRun run)
    {
        var phases = new List<string>();
        foreach (var phase in run.Upserts("Phase"))
            if (phases.Count == 0 || phases[^1] != phase) phases.Add(phase);
        return phases;
    }

    private static void ValidWrites(SeedRun run) =>
        Assert.All(run.Upserts("Acceptance"), v => Assert.True(v == "" || AcceptanceAttribute.IsValid(v), v));

    private static List<long> TimerSeconds(WorkflowHistory history) =>
        history.Events.Where(e => e.EventType == EventType.TimerStarted)
            .Select(e => e.TimerStartedEventAttributes.StartToFireTimeout.Seconds).ToList();

    /// <summary>
    /// The durations of the timers that fired, in order. A gate's reminder timer is cancelled when
    /// its decision arrives, so this counts the run's own sleeps and not an answered gate (a merge
    /// gate's reminder is 180 minutes, the same length as a stall's sleep).
    /// </summary>
    private static List<long> FiredSeconds(WorkflowHistory history)
    {
        var started = history.Events.Where(e => e.EventType == EventType.TimerStarted)
            .ToDictionary(e => e.EventId, e => e.TimerStartedEventAttributes.StartToFireTimeout.Seconds);
        return history.Events.Where(e => e.EventType == EventType.TimerFired)
            .Select(e => started[e.TimerFiredEventAttributes.StartedEventId]).ToList();
    }

    private static int Timers(WorkflowHistory history, long seconds) => FiredSeconds(history).Count(s => s == seconds);

    private static int Timers(SeedRun run, long seconds) => Timers(run.History, seconds);

    private static async Task WaitPhaseAsync(WorkflowHandle handle, SeedHarness h, string phase)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (h.LastUpsert(await handle.FetchHistoryAsync(), "Phase") != phase)
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(phase);
            await Task.Delay(20);
        }
    }

    private static async Task WaitForCallAsync(SeedHarness h, params string[] anyOf)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!h.Calls.Any(c => anyOf.Contains(c.Step)))
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(string.Join("|", anyOf));
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Lets the run's own sleeps pass until <paramref name="reached"/> holds. Only the definition's
    /// literal sleeps are skipped (60, 300 and 10800 seconds), so a gate is still open when the
    /// caller signals it. A merge gate's reminder is also 10800 seconds: once a scenario has opened
    /// one, pass <paramref name="pastStall"/> false unless the gate has been answered.
    /// </summary>
    private static async Task DriveToAsync(WorkflowHandle handle, SeedHarness h, Func<WorkflowHistory, bool> reached, bool pastStall = true)
    {
        var end = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var history = await handle.FetchHistoryAsync();
            if (reached(history)) return;
            if (DateTime.UtcNow > end) throw new TimeoutException("the run did not reach the expected point");

            var done = history.Events.Where(e => e.EventType == EventType.TimerFired).Select(e => e.TimerFiredEventAttributes.TimerId)
                .Concat(history.Events.Where(e => e.EventType == EventType.TimerCanceled).Select(e => e.TimerCanceledEventAttributes.TimerId))
                .ToHashSet();
            var sleeping = history.Events
                .Where(e => e.EventType == EventType.TimerStarted && !done.Contains(e.TimerStartedEventAttributes.TimerId))
                .Select(e => e.TimerStartedEventAttributes.StartToFireTimeout.Seconds)
                .Where(s => s is 60 or 300 || (pastStall && s == 10800))
                .ToList();
            if (sleeping.Count > 0) await h.AdvanceAsync(TimeSpan.FromSeconds(sleeping.Max() + 1));
            else await Task.Delay(20);
        }
    }

    // ── the raw definition ───────────────────────────────────────────────────

    /// <summary>Every step object of the PR definition as shipped, root first.</summary>
    private static IEnumerable<JsonElement> Definition()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json"))) dir = dir.Parent;
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "seed.example.json")));
        var root = seed.RootElement.GetProperty("workflowDefinitions").EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == Workflow).GetProperty("definition").Clone();
        return Descendants(root);
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) yield return node;
                foreach (var property in node.EnumerateObject())
                    foreach (var child in Descendants(property.Value)) yield return child;
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    foreach (var child in Descendants(item)) yield return child;
                break;
        }
    }

    private static List<JsonElement>? PathTo(JsonElement node, string name)
    {
        if (node.ValueKind == JsonValueKind.Array)
            return node.EnumerateArray().Select(item => PathTo(item, name)).FirstOrDefault(found => found is not null);
        if (node.ValueKind != JsonValueKind.Object) return null;

        var isStep = node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String;
        if (isStep && Name(node) == name) return [node];
        foreach (var property in node.EnumerateObject())
        {
            if (PathTo(property.Value, name) is not { } below) continue;
            if (isStep) below.Insert(0, node);
            return below;
        }
        return null;
    }

    private static string? Name(JsonElement step) =>
        step.ValueKind == JsonValueKind.Object && step.TryGetProperty("name", out var name) ? name.GetString() : null;

    private static string? Type(JsonElement step) =>
        step.ValueKind == JsonValueKind.Object && step.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    /// <summary>One spelling of a JSON value: member order kept, insignificant whitespace removed.</summary>
    private static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private sealed class RawEquality : IEqualityComparer<JsonElement>
    {
        public static readonly RawEquality Instance = new();
        public bool Equals(JsonElement x, JsonElement y) => x.GetRawText() == y.GetRawText();
        public int GetHashCode(JsonElement obj) => obj.GetRawText().GetHashCode(StringComparison.Ordinal);
    }
}
