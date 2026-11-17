using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Client;
using Temporalio.Common;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// Feedback on <c>merge-approval</c> that arrives after the gate has been decided. The shipped
/// seed, run end to end.
///
/// <para>
/// A human approves; then the configured CTO, or the human again, sends
/// <c>{"Decision":"changes_requested"}</c>. The gate's wait is over, so the engine buffers the
/// signal. Until the merge command is dispatched that feedback voids the approval and sends the
/// PR down the gate's own feedback route, once. It is read in three places: in the merge hold (a
/// look on the pass after the approval, then each five-minute pause), in the stall of a held
/// merge before each read, and at the entry to the merge turn. Any other payload met there is
/// dropped and decides nothing, then or at a later gate.
/// </para>
///
/// <para>
/// A later gate that opens undecided is observed as its visit: a wait that takes a buffered
/// signal still mints a visit id but never publishes it, so visit 2 being published means
/// nothing was left over for the gate to take.
/// </para>
/// </summary>
public sealed partial class PrReadinessHoldTests
{
    private const string FeedbackPrompt = "Merge-gate feedback requested changes";

    // ── behind an approval ───────────────────────────────────────────────────

    /// <summary>
    /// The approval arrives while the gate's notice is still out, and the feedback after it: the
    /// gate's waiter is already completed, so the feedback is buffered. The preflight would
    /// answer held. Before this the run circled in its hold with the feedback unread.
    /// </summary>
    [Fact]
    public async Task ApprovedThenFeedbackWhileTheGateNoticeIsOut_GoesBackOnce_NoMerge_AndTheNextGateOpensUndecided()
    {
        using var notice = new ManualResetEventSlim(false);
        var held = true;
        var script = new Script { Preflight = _ => held ? "held" : "clear" };
        script.Hold["delegate"] = notice;   // the gate's notice: the one unnamed delegation of this run

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await handle.SignalAsync("merge-approval", [Feedback("the retry is unbounded")]);
            notice.Set();

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.DoesNotContain(h.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned" or "merge_preflight" or "pin_after_approval");
            held = false;
            await h.SignalHumanApprovalAsync(handle);
        });

        AssertOneFeedbackPass(run, "the retry is unbounded");
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "revision", "consensus-review", "merge-approval", "merging"], Phases(run));
        Assert.Equal(["merge-approval:1", "merge-approval:2"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Equal([$"merge:{script.Head}:1", $"merge:{script.Head}:2"], run.Upserts("GateRef"));
        // Published, withdrawn by the feedback, cleared and published again by the review after it.
        Assert.Equal(["", script.Head, "", "", script.Head], run.Upserts("ReviewRef"));
        Assert.Equal(0, Timers(run, 300));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    /// <summary>Two feedback signals behind one approval are one pass, with the later comment.</summary>
    [Fact]
    public async Task TwoFeedbackSignalsBehindOneApproval_AreOnePassThroughTheRoute()
    {
        using var notice = new ManualResetEventSlim(false);
        var script = new Script();
        script.Hold["delegate"] = notice;

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await handle.SignalAsync("merge-approval", [Feedback("first thought")]);
            await handle.SignalAsync("merge-approval", [Feedback("second thought")]);
            notice.Set();

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.DoesNotContain(h.Calls, c => c.Step == "phase4_merge");
            await h.SignalHumanApprovalAsync(handle);
        });

        AssertOneFeedbackPass(run, "second thought");
        Assert.DoesNotContain(run.Calls, c => c.Instruction.Contains("first thought", StringComparison.Ordinal));
        Assert.Equal(["merge-approval:1", "merge-approval:2"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
    }

    // ── in the hold ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FeedbackDuringAHoldPause_EndsThePauseEarly_GoesBack_NoMerge()
    {
        var held = true;
        var script = new Script { Preflight = _ => held ? "held" : "clear" };

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            // The pass after the approval was held, and the hold is in its first pause.
            await WaitForHistoryAsync(handle, history => OpenTimers(history, 300) == 1);
            await handle.SignalAsync("merge-approval", [Feedback("not yet")]);

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.DoesNotContain(h.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
            held = false;
            await h.SignalHumanApprovalAsync(handle);
        });

        AssertOneFeedbackPass(run, "not yet");
        // The pause did not run out: its timer was cancelled by the signal, and nothing was skipped here.
        Assert.Equal(0, Timers(run, 300));
        Assert.Equal(1, CancelledTimers(run.History, 300));
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "merge-hold", "revision", "consensus-review", "merge-approval", "merging"], Phases(run));
        // One held check before the pause; the feedback ended the hold without another.
        Assert.Equal(2, run.Calls.Count(c => c.Step == "merge_preflight"));   // that one, and the clear one after the second approval
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
    }

    /// <summary>
    /// The feedback arrives while a read of the pass is out, and that pass then clears: the last
    /// look, at the entry to the merge turn, takes it. No merge command and no <c>merging</c>.
    /// </summary>
    [Theory]
    [InlineData("pin_after_approval")]
    [InlineData("merge_preflight")]
    public async Task FeedbackDuringAReadOfThePassThatClears_StopsAtTheMergeTurnsEntry(string read)
    {
        using var answer = new ManualResetEventSlim(false);
        var script = new Script();
        script.Hold[read] = answer;

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await WaitForCallAsync(h, read);
            await handle.SignalAsync("merge-approval", [Feedback("stop the merge")]);
            answer.Set();

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.DoesNotContain(h.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
            Assert.DoesNotContain("merging", SeedHarness.Upserts(await handle.FetchHistoryAsync(), "Phase"));
            await h.SignalHumanApprovalAsync(handle);
        });

        AssertOneFeedbackPass(run, "stop the merge");
        Assert.Equal(0, Timers(run, 300));
        Assert.DoesNotContain("merge-hold", run.Upserts("Phase"));
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "revision", "consensus-review", "merge-approval", "merging"], Phases(run));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
    }

    /// <summary>
    /// What is not feedback is dropped where it is met. It ends the pause it arrived in, the merge
    /// goes ahead when the preflight clears, nothing is revised, and the next gate (here after a
    /// refused merge) opens as visit 2: the payload was not left for it.
    /// </summary>
    [Theory]
    [InlineData("duplicate approval")]
    [InlineData("approval without a ref")]
    [InlineData("rejection")]
    [InlineData("rejection without a ref")]
    [InlineData("feedback for another gate")]
    [InlineData("feedback with an empty ref")]
    [InlineData("stale delegated approval")]
    [InlineData("not an object")]
    public async Task InAHold_WhatIsNotFeedbackIsDropped_TheMergeGoesAhead_AndALaterGateOpensUndecided(string stray)
    {
        var script = new Script
        {
            Preflight = n => n == 1 ? "held" : "clear",
            Merge = n => n == 1 ? "head_changed" : "merged",
            Verify = n => n == 1 ? "open" : "merged",
        };
        var first = script.Head;

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            var gateRef = h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef")!;
            await h.SignalHumanApprovalAsync(handle);
            await WaitForHistoryAsync(handle, history => OpenTimers(history, 300) == 1);

            object payload = stray switch
            {
                "duplicate approval" => new { Decision = "approved", Ref = gateRef },
                "approval without a ref" => new { Decision = "approved" },
                "rejection" => new { Decision = "rejected", Ref = gateRef, Comment = "no" },
                "rejection without a ref" => new { Decision = "rejected", Comment = "no" },
                "feedback for another gate" => new { Decision = "changes_requested", Ref = gateRef + "9", Comment = "elsewhere" },
                "feedback with an empty ref" => new { Decision = "changes_requested", Ref = "", Comment = "elsewhere" },
                "stale delegated approval" => Delegated("merge-approval:1", first),
                _ => "changes_requested",
            };
            await handle.SignalAsync("merge-approval", [payload is JsonElement element ? element : Json(payload)]);

            // The hold checks again, clear this time; the merge is refused for a moved head, and
            // the run comes back to its gate after a new review.
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:2", pastStall: false);
            Assert.Single(h.Calls, c => c.Step == "phase4_merge");   // the approval was kept and used
            Assert.DoesNotContain("revision", SeedHarness.Upserts(await handle.FetchHistoryAsync(), "Phase"));
            await h.SignalHumanApprovalAsync(handle);
        });

        Assert.DoesNotContain("revision", run.Upserts("Phase"));
        Assert.All(run.ConsensusInputs, i => Assert.NotNull(i.ReviewRef));   // two reviews, no feedback review
        Assert.DoesNotContain(run.Calls, c => c.Instruction.Contains(FeedbackPrompt, StringComparison.Ordinal));
        Assert.Equal(["merge-approval:1", "merge-approval:2"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Equal(2, run.Calls.Count(c => c.Step == "phase4_merge"));
        Assert.Contains($"--match-head-commit {first}", run.Calls.First(c => c.Step == "phase4_merge").Instruction);
        // The payload ended the pause it arrived in: that timer was cancelled, and none ran out.
        Assert.Equal(0, Timers(run, 300));
        Assert.Equal(1, CancelledTimers(run.History, 300));
        Assert.Equal(3, run.Calls.Count(c => c.Step == "merge_preflight"));   // held, clear, and clear after the second approval
        Assert.Equal(0, run.Discards);   // the late read is not the gate's guard: it logs no discard
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    /// <summary>
    /// A hold entered before the gate, because the preflight was held, reads the same way. The
    /// feedback sends the PR back, and the gate that opens afterwards is the run's first visit:
    /// before this the feedback stayed buffered and decided that gate before anyone saw it.
    /// </summary>
    [Fact]
    public async Task BeforeTheGate_FeedbackInAHold_GoesBack_AndTheGateThenOpensUndecided()
    {
        var gateChecks = 0;
        var script = new Script();   // only the first check before the gate is held

        var run = await RunAsync(script, async (handle, h) =>
        {
            await WaitForHistoryAsync(handle, history => OpenTimers(history, 300) == 1);
            Assert.Equal("merge-hold", h.LastUpsert(await handle.FetchHistoryAsync(), "Phase"));
            Assert.Empty(SeedHarness.Upserts(await handle.FetchHistoryAsync(), "GateVisit"));
            await handle.SignalAsync("merge-approval", [Feedback("before anyone approved")]);

            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        }, delegateOverride: (call, _) => call.Step == "merge_gate_preflight" && Interlocked.Increment(ref gateChecks) == 1
            ? Task.FromResult(new Fleet.Temporal.Models.AgentTaskResult(SeedHarness.PreflightHeld(), "completed")) : null);

        AssertOneFeedbackPass(run, "before anyone approved");
        Assert.Equal(["merge-approval:1"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Equal(["implementing", "consensus-review", "merge-hold", "revision", "consensus-review", "merge-approval", "merging"], Phases(run));
        Assert.Equal(0, Timers(run, 300));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
    }

    /// <summary>Feedback bound to the gate that was approved is feedback: the Ref is the current one.</summary>
    [Fact]
    public async Task InAHold_FeedbackCarryingTheCurrentGateRef_GoesBack()
    {
        var held = true;
        var script = new Script { Preflight = _ => held ? "held" : "clear" };

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            var gateRef = h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef");
            await h.SignalHumanApprovalAsync(handle);
            await WaitForHistoryAsync(handle, history => OpenTimers(history, 300) == 1);
            await handle.SignalAsync("merge-approval", [Json(new { Decision = "changes_requested", Ref = gateRef, Comment = "bound to this gate" })]);

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            held = false;
            await h.SignalHumanApprovalAsync(handle);
        });

        AssertOneFeedbackPass(run, "bound to this gate");
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
    }

    /// <summary>
    /// The feedback the reviewers uphold reaches the author with its comment, whichever wait took
    /// it: the route reads a variable of its own, not the gate's payload.
    /// </summary>
    [Fact]
    public async Task LateFeedbackTheReviewersUphold_ReachesTheAuthorWithItsComment()
    {
        var held = true;
        var script = new Script { Preflight = _ => held ? "held" : "clear", FeedbackVerdict = "changes_requested" };
        var first = script.Head;

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await WaitForHistoryAsync(handle, history => OpenTimers(history, 300) == 1);
            await handle.SignalAsync("merge-approval", [Feedback("rename the flag")]);

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            held = false;
            await h.SignalHumanApprovalAsync(handle);
        });

        // The route's phase, and the same phase again for the author's revision turn.
        AssertOneFeedbackPass(run, "rename the flag", revisionWrites: 2);
        var revision = Assert.Single(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal));
        Assert.Equal("revise_ceo_feedback", revision.Step);
        Assert.Contains("Feedback: rename the flag", revision.Instruction);
        Assert.NotEqual(first, script.Head);
        Assert.Contains($"--match-head-commit {script.Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
    }

    // ── in the stall ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FeedbackBufferedDuringTheHoldsLastPass_IsTakenAtTheStallsFirstRead()
    {
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var held = true;
        var script = new Script
        {
            Preflight = n =>
            {
                // The thirteenth check is the hold's last. It stays out until the feedback is in.
                if (n == 13) { reached.Set(); release.Wait(TimeSpan.FromSeconds(60)); }
                return held ? "held" : "clear";
            },
        };

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await DriveToAsync(handle, h, _ => reached.IsSet, pastStall: false);
            await handle.SignalAsync("merge-approval", [Feedback("found during the hold")]);
            release.Set();

            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:2");
            Assert.DoesNotContain(h.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
            held = false;
            await h.SignalHumanApprovalAsync(handle);
        });

        AssertOneFeedbackPass(run, "found during the hold");
        Assert.Equal(12, Timers(run, 300));
        Assert.Equal(1, Timers(run, StallSleepSeconds));
        // The look comes before the read: after the stall's notice nothing was read again.
        Assert.StartsWith("review-", run.Calls.SkipWhile(c => c.Step != "notify_stalled").Skip(1).First().Step);
        Assert.Equal(13 + 1, run.Calls.Count(c => c.Step == "pin_after_approval"));   // the hold's, and one after the second approval
        Assert.Equal(
            ["implementing", "consensus-review", "merge-approval", "merge-hold", "technical-stalled", "revision", "consensus-review", "merge-approval", "merging"],
            Phases(run));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    // ── a delegated approval ─────────────────────────────────────────────────

    /// <summary>
    /// A delegated approval does not go through the hold. Its route to the merge turn passes the
    /// same last look, so feedback that arrives before the pinned merge is dispatched stops it.
    /// </summary>
    [Fact]
    public async Task DelegatedApproval_FeedbackBeforeTheMergeIsDispatched_StopsIt()
    {
        using var preflight = new ManualResetEventSlim(false);
        var script = new Script();
        script.Hold["merge_approval_preflight"] = preflight;

        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", script.Head)]);
            await WaitForCallAsync(h, "merge_approval_preflight");
            await handle.SignalAsync("merge-approval", [Feedback("hold on")]);
            preflight.Set();

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.DoesNotContain(h.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:2", script.Head)]);
        });

        AssertOneFeedbackPass(run, "hold on");
        Assert.Single(run.Calls, c => c.Step == "phase4_merge_pinned");
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "merge_preflight");
        Assert.DoesNotContain(run.Upserts("Phase"), p => p is "merge-hold" or "technical-stalled");
        Assert.Equal(0, run.Discards);
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "revision", "consensus-review", "merge-approval", "merging"], Phases(run));
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    // ── no feedback ──────────────────────────────────────────────────────────

    /// <summary>
    /// The run says it is merging and issues the command in one workflow task: after the last
    /// read's answer there is one task, and in it the phase and then the command. No signal can be
    /// delivered between the look and the dispatch, because nothing between them ends the task.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoFeedback_ReportsMerging_ThenIssuesTheCommand_InOneWorkflowTask(bool delegated)
    {
        var script = new Script();
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            if (delegated) await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", script.Head)]);
            else await h.SignalHumanApprovalAsync(handle);
        });

        var (lastRead, command) = delegated ? ("merge_approval_preflight", "phase4_merge_pinned") : ("merge_preflight", "phase4_merge");
        Assert.Single(run.Calls, c => c.Step == command);
        Assert.Equal(["implementing", "consensus-review", "merge-approval", "merging"], Phases(run));
        Assert.DoesNotContain("revision", run.Upserts("Phase"));

        var events = run.History.Events.ToList();
        var answered = events.Single(e => e.EventType == EventType.ActivityTaskCompleted
            && e.ActivityTaskCompletedEventAttributes.ScheduledEventId == Scheduled(events, lastRead).EventId);
        var dispatched = Scheduled(events, command);
        var between = events.Where(e => e.EventId > answered.EventId && e.EventId < dispatched.EventId).ToList();
        Assert.Equal(
            [EventType.WorkflowTaskScheduled, EventType.WorkflowTaskStarted, EventType.WorkflowTaskCompleted, EventType.UpsertWorkflowSearchAttributes],
            between.Select(e => e.EventType));
        Assert.Equal("merging", Decode<string>(between[^1].UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields["Phase"]));
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    // ── as defined ───────────────────────────────────────────────────────────

    [Fact]
    public void Definition_LateFeedbackHasOneRoute_ReachedFromTheGateTheHoldTheStallAndTheMergeEntry()
    {
        var root = Definition().First();
        var all = Descendants(root).ToList();
        string? StateSet(JsonElement s) => Type(s) == "set_variable" && s.GetProperty("vars").TryGetProperty("state", out var v) ? v.GetString() : null;

        // One copy of the route: the case of its own state, after the merge turn's.
        var loop = Assert.Single(all, s => Name(s) == "outer_loop");
        var order = loop.GetProperty("steps").EnumerateArray().Select(Name).ToList();
        Assert.Equal(["dispatch_MERGE_HOLD", "merge_entry", "dispatch_MERGE", "dispatch_MERGE_FEEDBACK", "outer_exit_check"], order.Skip(order.IndexOf("dispatch_MERGE_HOLD")));
        var route = Assert.Single(all, s => Name(s) == "merge_feedback");
        var state = Assert.Single(all, s => Name(s) == "dispatch_MERGE_FEEDBACK");
        Assert.Equal("{{vars.state}}", state.GetProperty("on").GetString());
        Assert.Equal(Canonical(route.GetRawText()), Canonical(state.GetProperty("cases").GetProperty("MERGE_FEEDBACK").GetRawText()));
        Assert.Equal(["pr_withdraw_review_attrs", "pr_withdraw_review_ref", "set_revision_phase", "ceo_consensus_review", "ceo_consensus_verdict_branch"],
            route.GetProperty("steps").EnumerateArray().Select(Name));
        Assert.Single(all, s => Name(s) == "ceo_consensus_review");
        Assert.Single(all, s => Type(s) == "set_attribute" && s.GetProperty("attributes").TryGetProperty("Phase", out var p) && p.GetString() == "revision"
            && Name(s) == "set_revision_phase");

        // Four ways in, and each sets the comment the route reads before it sets the state.
        var entries = all.Where(s => Type(s) == "set_variable" && s.GetProperty("vars").TryGetProperty("merge_feedback_comment", out _)).ToList();
        Assert.Equal(
            ["merge_entry_late_feedback", "merge_hold_late_feedback", "route_MERGE_FEEDBACK", "stalled_late_feedback"],
            entries.Select(Name).Distinct().Order());
        Assert.All(entries, e => Assert.Equal(
            Name(e) == "route_MERGE_FEEDBACK" ? "{{vars.merge_decision.Comment}}" : "{{vars.merge_late_signal.Comment}}",
            e.GetProperty("vars").GetProperty("merge_feedback_comment").GetString()));
        var gate = Assert.Single(all, s => Name(s) == "MERGE_GATE");
        Assert.Equal(["MERGE_FEEDBACK"], Descendants(gate).Where(s => Name(s) == "route_MERGE_FEEDBACK").Select(StateSet));
        foreach (var exit in new[] { "merge_hold_exit", "stalled_exit" })
            Assert.Equal("feedback", Assert.Single(all, s => Name(s) == exit).GetProperty("cases").EnumerateObject().Single(c => StateSet(c.Value) == "MERGE_FEEDBACK").Name);
        Assert.All(all.Where(s => Name(s) == "merge_entry_late_feedback"), s => Assert.Equal("MERGE_FEEDBACK", StateSet(s)));
        // The route's readers read that variable; the gate's payload comment is read once, by the gate's hand-over.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(root.GetRawText(), @"vars\.merge_decision\.Comment").Count);
        Assert.Equal(
            ["ceo_consensus_review", "notify_post_feedback_human_review", "revise_ceo_feedback", "revise_post_feedback_human"],
            all.Where(s => OwnText(s).Contains("{{vars.merge_feedback_comment", StringComparison.Ordinal)).Select(Name).Order());

        // The four reads: one look in the hold after the approval, its five-minute pause, one look
        // before each read of a stalled held merge, one look at the entry to the merge turn.
        var reads = WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).OfType<WaitForSignalStep>()
            .Where(w => w.OutputVar == "merge_late_signal").ToDictionary(w => w.Name!, w => w.TimeoutMinutes);
        Assert.Equal(4, reads.Count);
        Assert.Equal(0, reads["merge_hold_feedback_take"]);
        Assert.Equal(5, reads["merge_hold_feedback_wait"]);
        Assert.Equal(0, reads["stalled_feedback_take"]);
        Assert.Equal(0, reads["merge_entry_feedback_take"]);
        Assert.Equal("merge_hold_feedback_wait", Name(Assert.Single(all, s => Name(s) == "merge_hold_wait").GetProperty("steps").EnumerateArray().Last()));
        Assert.Equal(["merge_hold_checked", "merge_hold_feedback_take"],
            Assert.Single(all, s => Name(s) == "merge_hold_pass").GetProperty("cases").GetProperty("check").GetProperty("steps").EnumerateArray().Select(Name));
        Assert.Equal(["merge_hold_pass", "merge_hold_feedback", "merge_repin_check", "merge_hold_route"],
            Assert.Single(all, s => Name(s) == "merge_hold_loop").GetProperty("steps").EnumerateArray().Select(Name));
        Assert.Equal(["stalled_feedback_take", "stalled_feedback", "merge_repin_check", "stalled_merge_route"],
            Assert.Single(all, s => Name(s) == "stalled_merge_read").GetProperty("steps").EnumerateArray().Select(Name));

        // The three places ask the same three questions: the decision, an absent Ref, an equal Ref.
        foreach (var name in new[] { "merge_hold_feedback", "stalled_feedback", "merge_entry_feedback" })
        {
            var decision = Assert.Single(all, s => Name(s) == name);
            Assert.Equal("{{vars.merge_late_signal.Decision}}", decision.GetProperty("on").GetString());
            Assert.Equal(["changes_requested"], decision.GetProperty("cases").EnumerateObject().Select(c => c.Name));
            Assert.False(decision.TryGetProperty("default", out _));
            var absent = decision.GetProperty("cases").GetProperty("changes_requested");
            Assert.Equal("{{vars.merge_late_signal.Ref | default: 'absent'}}", absent.GetProperty("on").GetString());
            Assert.Equal(["absent"], absent.GetProperty("cases").EnumerateObject().Select(c => c.Name));
            var bound = absent.GetProperty("default").GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal("{{vars.merge_late_signal.Ref}}\n{{vars.gate_ref}}", bound[0].GetProperty("vars").GetProperty("pair").GetString());
            Assert.Equal(@"{{vars.pair | extract: '\A([^\r\n]+)\n\1\z'}}", bound[1].GetProperty("on").GetString());
            Assert.Equal("noop", Type(bound[1].GetProperty("cases").GetProperty("")));
            Assert.Equal(Canonical(absent.GetProperty("cases").GetProperty("absent").GetRawText()), Canonical(bound[1].GetProperty("default").GetRawText()));
        }
    }

    /// <summary>
    /// Between the look at the entry to the merge turn and either merge command there is no step
    /// that ends the workflow task: only variable and attribute writes and branches.
    /// </summary>
    [Fact]
    public void Definition_NothingBetweenTheLastLookAndTheMergeCommand_EndsTheWorkflowTask()
    {
        var all = Definition().ToList();
        var entry = Assert.Single(all, s => Name(s) == "merge_entry");
        Assert.Equal("{{vars.state}}", entry.GetProperty("on").GetString());
        var check = entry.GetProperty("cases").GetProperty("MERGE");
        Assert.Equal(["MERGE"], entry.GetProperty("cases").EnumerateObject().Select(c => c.Name));
        Assert.False(entry.TryGetProperty("default", out _));
        Assert.Equal(["merge_entry_feedback_take", "merge_entry_feedback", "merge_entry_phase"], check.GetProperty("steps").EnumerateArray().Select(Name));

        // After the look: nothing that schedules, sleeps, waits or starts a child.
        var afterTheLook = check.GetProperty("steps").EnumerateArray().Skip(1).SelectMany(Descendants).ToList();
        Assert.All(afterTheLook, s => Assert.Contains(Type(s), new[] { "branch", "sequence", "set_variable", "set_attribute", "noop" }));
        // The phase is written only if the state is still MERGE, and is required.
        var phase = check.GetProperty("steps")[2];
        Assert.Equal("{{vars.state}}", phase.GetProperty("on").GetString());
        var write = phase.GetProperty("cases").GetProperty("MERGE");
        Assert.Equal("set_attribute", Type(write));
        Assert.True(write.GetProperty("required").GetBoolean());
        Assert.Equal("""{"Phase":"merging"}""", Canonical(write.GetProperty("attributes").GetRawText()));
        Assert.Single(all, s => Type(s) == "set_attribute" && s.GetProperty("attributes").TryGetProperty("Phase", out var p) && p.GetString() == "merging");

        // The merge turn itself, up to each command: the delegated one is its first step, the
        // human one follows two variable writes and the once-per-visit branch.
        var mode = Assert.Single(all, s => Name(s) == "MERGE").GetProperty("steps")[0];
        Assert.Equal("phase4_merge_mode", Name(mode));
        Assert.Equal("phase4_merge_pinned", Name(mode.GetProperty("default").GetProperty("steps")[0]));
        var human = mode.GetProperty("cases").GetProperty("none").GetProperty("steps").EnumerateArray().Take(2).ToList();
        Assert.Equal(["set_variable", "branch"], human.Select(Type));
        Assert.Equal(["set_variable", "delegate"], human[1].GetProperty("cases").GetProperty("").GetProperty("steps").EnumerateArray().Select(Type));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JsonElement Feedback(string comment) => Json(new { Decision = "changes_requested", Comment = comment });

    /// <summary>
    /// One pass through the feedback route: the phase once, one review of the feedback and no
    /// more, and that review was given the comment.
    /// </summary>
    private static void AssertOneFeedbackPass(SeedRun run, string comment, int revisionWrites = 1)
    {
        Assert.Equal(revisionWrites, run.Upserts("Phase").Count(p => p == "revision"));
        Assert.Single(run.ConsensusInputs, i => i.ReviewRef is null);
        var review = Assert.Single(run.Calls, c => c.Step == $"review-{Reviewer}" && c.Instruction.Contains(FeedbackPrompt, StringComparison.Ordinal));
        Assert.Contains($"Feedback comment:\n{comment}\n", review.Instruction);
    }

    private static async Task WaitForHistoryAsync(WorkflowHandle handle, Func<WorkflowHistory, bool> reached)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!reached(await handle.FetchHistoryAsync()))
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("the run did not reach the expected point");
            await Task.Delay(20);
        }
    }

    /// <summary>Timers of this length that were started and have neither fired nor been cancelled.</summary>
    private static int OpenTimers(WorkflowHistory history, long seconds)
    {
        var done = history.Events.Where(e => e.EventType == EventType.TimerFired).Select(e => e.TimerFiredEventAttributes.TimerId)
            .Concat(history.Events.Where(e => e.EventType == EventType.TimerCanceled).Select(e => e.TimerCanceledEventAttributes.TimerId))
            .ToHashSet();
        return history.Events.Count(e => e.EventType == EventType.TimerStarted
            && e.TimerStartedEventAttributes.StartToFireTimeout.Seconds == seconds
            && !done.Contains(e.TimerStartedEventAttributes.TimerId));
    }

    private static int CancelledTimers(WorkflowHistory history, long seconds)
    {
        var cancelled = history.Events.Where(e => e.EventType == EventType.TimerCanceled).Select(e => e.TimerCanceledEventAttributes.TimerId).ToHashSet();
        return history.Events.Count(e => e.EventType == EventType.TimerStarted
            && e.TimerStartedEventAttributes.StartToFireTimeout.Seconds == seconds
            && cancelled.Contains(e.TimerStartedEventAttributes.TimerId));
    }

    private static HistoryEvent Scheduled(List<HistoryEvent> events, string step) =>
        events.Single(e => e.EventType == EventType.ActivityTaskScheduled
            && e.ActivityTaskScheduledEventAttributes.ActivityType.Name == DelegateToAgentActivity.ActivityName
            && Decode<string>(e.ActivityTaskScheduledEventAttributes.Input.Payloads_[2]).EndsWith("/" + step, StringComparison.Ordinal));

    /// <summary>A step's own members as text, without the steps nested in it.</summary>
    private static string OwnText(JsonElement step) =>
        string.Join("\n", step.EnumerateObject()
            .Where(p => p.Name is not ("steps" or "cases" or "default" or "notifyStep"))
            .Select(p => p.Value.GetRawText()));
}
