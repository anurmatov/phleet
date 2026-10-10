using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #442 — a merge-gate <c>changes_requested</c> whose feedback review returns
/// <c>needs_human_review</c> pauses the SAME run at a bounded <c>human-review</c> wait instead of
/// ending it FAILED.
///
/// <para>
/// Same harness as <see cref="SeedEpicGrantDefinitionTests"/>: the shipped seed, the production
/// engine and the real <c>ConsensusReviewWorkflow</c>; only agents are scripted, by step name.
/// The feedback review is told apart from a PR review by its prompt, and its reviewer answers
/// <c>needs_human_review</c>, which fails the round closed without synthesis.
/// </para>
///
/// <para>
/// <c>human-review</c> is agent-sendable, so no decision at the pause may reach a merge step:
/// <c>approved</c> and <c>changes_requested</c> both go through a fresh attested review and a new
/// merge-approval visit.
/// </para>
/// </summary>
public sealed class SeedPostFeedbackHumanReviewTests
{
    private const string Workflow = "UwePrImplementationWorkflow";
    private const string Repo = "example-org/example-repo";
    private const string Author = "impl-agent";
    private const string Cto = "cto-agent";
    private const string Reviewer = "reviewer-a";
    private const string Waiter = "waiter-example";
    private const string PrUrl = "https://github.com/example-org/example-repo/pull/7";
    private const string GrantId = "00000000-0000-0000-0000-000000000442";
    private const string PauseNotify = "notify_post_feedback_human_review";
    private const string FeedbackReviewMarker = "Merge-gate feedback requested changes";

    private static readonly string HeadA = new('a', 40);
    private static readonly string HeadB = new('b', 40);

    // ── T1 entry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// T1. The feedback review returns needs_human_review: the run is still Running, parked in
    /// Phase human-review with one pause notice, and the old FAILED exit never ran. Ended here by a
    /// rejection; T5 covers that exit.
    /// </summary>
    [Fact]
    public async Task T1_FeedbackReviewNeedsHuman_PausesTheRunInHumanReview()
    {
        WorkflowExecutionStatus? statusAtPause = null;
        string? phaseAtPause = null;
        string? refAtPause = null;
        List<string> callsAtPause = [];

        await SeedHarness.RunAsync(Workflow, PrInput(), Responder(), async (handle, h) =>
        {
            await EnterPauseAsync(handle, h);

            statusAtPause = (await handle.DescribeAsync()).Status;
            var history = await handle.FetchHistoryAsync();
            phaseAtPause = h.LastUpsert(history, "Phase");
            refAtPause = h.LastUpsert(history, "ReviewRef");
            callsAtPause = h.Calls.Select(c => c.Step).ToList();

            await handle.SignalAsync("human-review", [Json("""{"Decision":"rejected"}""")]);
        });

        Assert.Equal(WorkflowExecutionStatus.Running, statusAtPause);
        Assert.Equal("human-review", phaseAtPause);
        Assert.Equal("", refAtPause);
        Assert.Single(callsAtPause, s => s == PauseNotify);
        Assert.DoesNotContain("notify_ceo_consensus_needs_human", callsAtPause);
        Assert.DoesNotContain("revise_ceo_feedback", callsAtPause);
        Assert.DoesNotContain("renotify_ceo_reviewers_approve", callsAtPause);
    }

    // ── T2 resume approved ───────────────────────────────────────────────────

    /// <summary>
    /// T2. human-review approved does not merge: it re-runs the full PR review on the current head,
    /// and only a human approval at the new merge-approval visit merges.
    /// </summary>
    [Fact]
    public async Task T2_HumanReviewApproved_ReReviewsTheHead_ThenMergesAtTheNextVisit()
    {
        var run = await SeedHarness.RunAsync(Workflow, PrInput(), Responder(), async (handle, h) =>
        {
            await EnterPauseAsync(handle, h);
            await handle.SignalAsync("human-review", [Json("""{"Decision":"approved"}""")]);

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.Equal(HeadA, h.LastUpsert(await handle.FetchHistoryAsync(), "ReviewRef"));
            await handle.SignalAsync("merge-approval", [Json("""{"Decision":"approved"}""")]);
        });

        // PR review, feedback review (publishes nothing), then exactly one fresh PR review of A.
        Assert.Equal([HeadA, null, HeadA], run.ConsensusInputs.Select(i => i.ReviewRef));
        Assert.Equal(HeadA, Assert.Single(run.ConsensusInputs.Skip(2)).ReviewRef);
        Assert.All(run.ConsensusInputs, i => Assert.Equal([Author, Cto], i.ExcludedAgents));

        Assert.Contains("merge-approval:2", run.Upserts("GateVisit"));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.DoesNotContain(run.Calls, c => c.Step == "phase4_merge_pinned");
        Assert.DoesNotContain(run.Calls, c => c.Step is "revise_post_feedback_human" or "revise_ceo_feedback");
        Assert.Equal("approved", WaiterDecision(run));
    }

    // ── T3 revision ──────────────────────────────────────────────────────────

    /// <summary>
    /// T3. human-review changes_requested: Phase moves to revision before the implementer is
    /// called, the instruction carries both comments, and the new head is reviewed and published
    /// before the next merge-approval visit.
    /// </summary>
    [Fact]
    public async Task T3_HumanReviewChangesRequested_RevisesThenReReviewsTheNewHead()
    {
        var head = HeadA;
        var run = await SeedHarness.RunAsync(Workflow, PrInput(), Respond, async (handle, h) =>
        {
            await EnterPauseAsync(handle, h, mergeComment: "please tighten Y");
            await handle.SignalAsync("human-review", [Json("""{"Decision":"changes_requested","Comment":"fix X"}""")]);

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.Equal(HeadB, h.LastUpsert(await handle.FetchHistoryAsync(), "ReviewRef"));
            await handle.SignalAsync("merge-approval", [Json("""{"Decision":"approved"}""")]);
        });

        var revise = Assert.Single(run.Calls, c => c.Step == "revise_post_feedback_human");
        Assert.Equal(Author, revise.Target);
        Assert.Contains("fix X", revise.Instruction);
        Assert.Contains("please tighten Y", revise.Instruction);
        Assert.Contains("HEAD_SHA:", revise.Instruction);

        // The two Phase upserts right before the revise activity: the pause, then revision.
        var scheduled = ScheduledIndex(run, "revise_post_feedback_human");
        var phasesBefore = PhaseUpserts(run).Where(p => p.Index < scheduled).Select(p => p.Value).ToList();
        Assert.Equal(["human-review", "revision"], phasesBefore.TakeLast(2));

        Assert.Equal([HeadA, null, HeadB], run.ConsensusInputs.Select(i => i.ReviewRef));
        Assert.Equal(["", HeadA, "", "", HeadB], run.Upserts("ReviewRef"));
        Assert.Contains("merge-approval:2", run.Upserts("GateVisit"));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Equal("approved", WaiterDecision(run));

        string Respond(Call call)
        {
            if (call.Step == "revise_post_feedback_human")
            {
                head = HeadB;
                return $"fixed\nHEAD_SHA: {HeadB}";
            }
            return Responder(() => head)(call);
        }
    }

    // ── T4 timeout ───────────────────────────────────────────────────────────

    /// <summary>T4. No decision in 180 minutes: 1 notice + 2 reminders, then the run ends cancelled.</summary>
    [Fact]
    public async Task T4_NoDecision_TimesOutAfterThreeNotices_AndEndsCancelled()
    {
        // The drive returns at the pause; the harness then waits for the result, which lets the
        // time-skipping server run the 180-minute pause out.
        var run = await SeedHarness.RunAsync(Workflow, PrInput(), Responder(), EnterPauseAsync);

        Assert.Equal(3, run.Calls.Count(c => c.Step == PauseNotify));
        Assert.Single(run.Calls, c => c.Step == "notify_post_feedback_human_review_timeout");
        Assert.Equal("cancelled", WaiterDecision(run));
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
        Assert.Equal(2, run.ConsensusInputs.Count);
    }

    // ── T5 rejected ──────────────────────────────────────────────────────────

    [Fact]
    public async Task T5_HumanReviewRejected_EndsTheRunRejected_WithoutMerging()
    {
        var run = await SeedHarness.RunAsync(Workflow, PrInput(), Responder(), async (handle, h) =>
        {
            await EnterPauseAsync(handle, h);
            await handle.SignalAsync("human-review", [Json("""{"Decision":"rejected","Comment":"not this way"}""")]);
        });

        Assert.Equal("rejected", WaiterDecision(run));
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
        var notice = Assert.Single(run.Calls, c => c.Step == "notify_post_feedback_human_rejected");
        Assert.Contains("not this way", notice.Instruction);
        Assert.Equal(2, run.ConsensusInputs.Count);
    }

    // ── T6 stale ref and visit ───────────────────────────────────────────────

    /// <summary>
    /// T6. The pause publishes no ref, and a delegated approval for visit 1 sent during the pause
    /// is buffered and then discarded at visit 2, even though the re-review attests the same head.
    /// </summary>
    [Fact]
    public async Task T6_PauseKeepsNoRef_AndAStaleDelegatedApprovalIsDiscardedAtTheNextVisit()
    {
        string? refAtPause = null;
        var run = await SeedHarness.RunAsync(Workflow, PrInput(), Responder(), async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            Assert.Equal(HeadA, h.LastUpsert(await handle.FetchHistoryAsync(), "ReviewRef"));
            await handle.SignalAsync("merge-approval", [Json("""{"Decision":"changes_requested","Comment":"is this safe?"}""")]);
            await WaitForCallAsync(h, PauseNotify);
            refAtPause = h.LastUpsert(await handle.FetchHistoryAsync(), "ReviewRef");

            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", HeadA)]);
            await handle.SignalAsync("human-review", [Json("""{"Decision":"approved"}""")]);

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            await handle.SignalAsync("merge-approval", [Json("""{"Decision":"approved"}""")]);
        });

        Assert.Equal("", refAtPause);
        // Published A, withdrawn by the feedback, cleared by the fresh round, then A again from it.
        Assert.Equal(["", HeadA, "", "", HeadA], run.Upserts("ReviewRef"));
        Assert.Equal(1, run.Discards);
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.DoesNotContain(run.Calls, c => c.Step == "phase4_merge_pinned");
    }

    // ── T7 cancel ────────────────────────────────────────────────────────────

    [Fact]
    public async Task T7_CancelDuringThePause_CancelsTheRun_WithoutMerging()
    {
        WorkflowExecutionStatus? status = null;
        List<string> calls = [];

        var ex = await Assert.ThrowsAsync<WorkflowFailedException>(() =>
            SeedHarness.RunAsync(Workflow, PrInput(), Responder(), async (handle, h) =>
            {
                await EnterPauseAsync(handle, h);
                await handle.CancelAsync();

                await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync<JsonElement?>());
                status = (await handle.DescribeAsync()).Status;
                calls = h.Calls.Select(c => c.Step).ToList();
            }));

        Assert.IsType<CanceledFailureException>(ex.InnerException);
        Assert.Equal(WorkflowExecutionStatus.Canceled, status);
        Assert.DoesNotContain(calls, s => s is "phase4_merge" or "phase4_merge_pinned");
    }

    // ── T8 independence (structural) ─────────────────────────────────────────

    [Fact]
    public void T8_FeedbackReview_ExcludesTheSameAgentsAsThePrReview_AndPublishesNoRef()
    {
        var root = SeedHarness.Parse(Workflow);
        var children = WorkflowDefinitionValidator.Walk(root).OfType<ChildWorkflowStep>().ToList();
        var feedback = Assert.Single(children, c => c.Name == "ceo_consensus_review");
        var review = Assert.Single(children, c => c.Name == "consensus_review");

        Assert.Equal("{{input.TargetAgent}},{{config.CtoAgent}}", Arg(review, "ExcludedAgents"));
        Assert.Equal(Arg(review, "ExcludedAgents"), Arg(feedback, "ExcludedAgents"));
        Assert.False(feedback.Args!.ContainsKey("ReviewRef"));
    }

    // ── T9 wording (structural) ──────────────────────────────────────────────

    [Fact]
    public void T9_NoMergeGateFeedbackIsCalledACeoDecision_AndTheBranchNeverFails()
    {
        var entry = Entry();
        var strings = Strings(entry).ToList();

        foreach (var forbidden in new[]
                 {
                     "The CEO requested", "CEO's concerns", "CEO-triggered", "CEO merge-gate feedback", "CEO feedback: {{",
                 })
        {
            Assert.DoesNotContain(strings, s => s.Contains(forbidden, StringComparison.Ordinal));
        }

        // The generic guidance line is retained on purpose (D3).
        Assert.Contains(strings, s => s.Contains("When incorporating prior consensus or CEO feedback:", StringComparison.Ordinal));

        var branch = Assert.Single(Objects(entry["definition"]), o => Name(o) == "ceo_consensus_verdict_branch");
        Assert.DoesNotContain(Objects(branch), o =>
            Type(o) == "set_variable" && o["vars"]?["outer_done"]?.GetValue<string>() == "FAILED");

        // The pause hands the exit back to outer_exit_check: no case breaks out of the loop itself.
        var pause = branch["cases"]!["needs_human_review"]!;
        Assert.DoesNotContain(Objects(pause), o => Type(o) == "break");
        Assert.DoesNotContain(Objects(entry["definition"]), o => Name(o) == "notify_ceo_consensus_needs_human");
    }

    // ── drive helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Visit 1 → merge-gate changes_requested → feedback review → the pause notice has been sent,
    /// so the human-review waiter is registered.
    /// </summary>
    private static Task EnterPauseAsync(WorkflowHandle handle, SeedHarness h) =>
        EnterPauseAsync(handle, h, mergeComment: "is this safe?");

    private static async Task EnterPauseAsync(WorkflowHandle handle, SeedHarness h, string mergeComment)
    {
        await h.WaitForGateVisitAsync(handle, "merge-approval:1");
        await handle.SignalAsync("merge-approval",
            [JsonSerializer.SerializeToElement(new { Decision = "changes_requested", Comment = mergeComment })]);
        await WaitForCallAsync(h, PauseNotify);
    }

    private static async Task WaitForCallAsync(SeedHarness h, string step)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!h.Calls.Any(c => c.Step == step))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"step {step} was never called");
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// PR reviews approve and attest the current head; the feedback review needs a human. The merge
    /// is verified MERGED and the doc run prepares nothing.
    /// </summary>
    private static Func<Call, string> Responder(Func<string>? head = null) => call =>
    {
        var current = head?.Invoke() ?? HeadA;
        return call.Step switch
        {
            "phase1_implement" => $"done\nPR_URL: {PrUrl}\nHEAD_SHA: {current}",
            "verify_merge_status" => "PR_STATE_JSON: {\"mergeCommit\":{\"oid\":\"" + current + "\"},\"mergedAt\":\"2026-10-10T12:00:00Z\",\"state\":\"MERGED\"}",
            "prepare" => "PREP: NO_DOC",
            $"review-{Reviewer}" when call.Instruction.Contains(FeedbackReviewMarker, StringComparison.Ordinal) =>
                Review("needs_human_review", blocker: "the trade-off needs a human call"),
            $"review-{Reviewer}" => Review("approved", reviewedRef: current),
            _ => "ok",
        };
    };

    private static Dictionary<string, object?> PrInput() => new()
    {
        ["Repo"] = Repo,
        ["IssueNumber"] = 7,
        ["TargetAgent"] = Author,
        ["ConsensusAgents"] = Reviewer,
        ["DocPrepAgent"] = "prep-agent",
        ["WaiterWorkflowId"] = Waiter,
    };

    private static string Review(string verdict, string? reviewedRef = null, string? blocker = null)
    {
        var lines = new List<string> { "detailed review", "SUMMARY: reviewed", "EVIDENCE: none" };
        lines.Add($"BLOCKER: {blocker ?? "none"}");
        if (reviewedRef is not null) lines.Add($"REVIEWED_REF: {reviewedRef}");
        lines.Add($"VERDICT: {verdict}");
        return string.Join('\n', lines);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static JsonElement Delegated(string visitId, string artifactRef) =>
        JsonSerializer.SerializeToElement(new
        {
            Decision = "approved",
            GrantId,
            VisitId = visitId,
            ArtifactRef = artifactRef,
            Evidence = "https://example.com/review/1",
        });

    // ── history helpers ──────────────────────────────────────────────────────

    /// <summary>The decision the single terminal wakeup sent to the waiter.</summary>
    private static string? WaiterDecision(SeedRun run)
    {
        var sent = Assert.Single(run.History.Events,
            e => e.EventType == EventType.SignalExternalWorkflowExecutionInitiated);
        var attributes = sent.SignalExternalWorkflowExecutionInitiatedEventAttributes;
        Assert.Equal("blocker-resolved", attributes.SignalName);
        Assert.Equal(Waiter, attributes.WorkflowExecution.WorkflowId);
        var payload = DataConverter.Default.PayloadConverter.ToValue<JsonElement>(attributes.Input.Payloads_[0]);
        return payload.GetProperty("decision").GetString();
    }

    private static List<(int Index, string Value)> PhaseUpserts(SeedRun run) =>
        run.History.Events
            .Select((e, i) => (Event: e, Index: i))
            .Where(x => x.Event.EventType == EventType.UpsertWorkflowSearchAttributes
                && x.Event.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields.ContainsKey("Phase"))
            .Select(x => (x.Index, DataConverter.Default.PayloadConverter.ToValue<string>(
                x.Event.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields["Phase"])))
            .ToList();

    /// <summary>History index of the delegate activity scheduled for <paramref name="step"/>.</summary>
    private static int ScheduledIndex(SeedRun run, string step) =>
        run.History.Events
            .Select((e, i) => (Event: e, Index: i))
            .Single(x => x.Event.EventType == EventType.ActivityTaskScheduled
                && x.Event.ActivityTaskScheduledEventAttributes.ActivityType.Name == DelegateToAgentActivity.ActivityName
                && DataConverter.Default.PayloadConverter.ToValue<string>(
                    x.Event.ActivityTaskScheduledEventAttributes.Input.Payloads_[2]).EndsWith("/" + step, StringComparison.Ordinal))
            .Index;

    // ── seed helpers ─────────────────────────────────────────────────────────

    private static string? Arg(ChildWorkflowStep step, string key) => step.Args![key]?.ToString();

    private static JsonObject Entry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var seed = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "seed.example.json")))!;
        return Assert.Single(seed["workflowDefinitions"]!.AsArray(),
            d => d!["name"]!.GetValue<string>() == Workflow)!.AsObject();
    }

    private static string? Name(JsonObject node) => node["name"]?.GetValue<string>();

    private static string? Type(JsonObject node) => node["type"]?.GetValue<string>();

    /// <summary>Every decoded string value, so escaped characters cannot hide a match.</summary>
    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(kv => Strings(kv.Value)),
        JsonArray arr => arr.SelectMany(Strings),
        JsonValue value when value.TryGetValue<string>(out var s) => [s],
        _ => [],
    };

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return obj;
                foreach (var (_, value) in obj)
                    foreach (var child in Objects(value))
                        yield return child;
                break;

            case JsonArray arr:
                foreach (var item in arr)
                    foreach (var child in Objects(item))
                        yield return child;
                break;
        }
    }
}
