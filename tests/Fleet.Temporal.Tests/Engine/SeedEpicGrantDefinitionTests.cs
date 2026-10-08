using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Shared;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Fleet.Temporal.Workflows.Fleet;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #436 AC-D — the shipped <c>seed.example.json</c> definitions, run end to end.
///
/// <para>
/// Everything below the definitions is production code: <see cref="UniversalWorkflow"/>,
/// <see cref="LoadWorkflowDefinitionActivity"/> (parse + validation, fed the seed over a fake HTTP
/// handler) and the real <see cref="ConsensusReviewWorkflow"/>, so the attestation path is the one
/// that ships — reviewer <c>REVIEWED_REF:</c> lines → <c>AttestedRef</c> → published
/// <c>ReviewRef</c> → the gate's guard. Only the agents are scripted: every delegation is answered
/// by step name.
/// </para>
///
/// <para>
/// The orchestrator's checks (D1–D11, including the server-side <c>stale_artifact</c> refusal) are
/// covered in <c>EpicGrantServiceTests</c>; here a delegated payload is sent straight to the run,
/// which is exactly what the orchestrator does after its checks pass.
/// </para>
/// </summary>
public sealed class SeedEpicGrantDefinitionTests
{
    private const string Repo = "example-org/example-repo";
    private const string Cto = "cto-agent";
    private const string Reviewer = "reviewer-a";
    private const string PrUrl = "https://github.com/example-org/example-repo/pull/7";
    private const string GrantId = "00000000-0000-0000-0000-000000000436";
    private const string Evidence = "https://example.com/review/1";

    private static readonly string HeadA = new('a', 40);
    private static readonly string HeadB = new('b', 40);
    private static readonly string BodyC = new('c', 64);
    private static readonly string BodyD = new('d', 64);
    private static readonly string ArtifactE = new('e', 64);
    private static readonly string ArtifactF = new('f', 64);

    // ── AC-D1 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("UwePrImplementationWorkflow", "merge-approval", "merge_visit")]
    [InlineData("UweDesignWorkflow", "design-approval", "design_visit")]
    [InlineData("UweDocMaintenanceWorkflow", "doc-review", "doc_visit")]
    public void EachDelegationCapableDefinition_ValidatesAndGuardsItsGate(string workflow, string gate, string visitVar)
    {
        var root = SeedHarness.Parse(workflow);   // throws if the validator refuses it

        var waits = WorkflowDefinitionValidator.Walk(root).OfType<WaitForSignalStep>()
            .Where(w => w.SignalName == gate).ToList();
        Assert.NotEmpty(waits);
        Assert.All(waits, w =>
        {
            Assert.Equal(visitVar, w.VisitVar);
            Assert.NotNull(w.DelegatedGuard);
            Assert.Equal("GrantId", w.DelegatedGuard!.Marker);
            Assert.Equal("{{vars." + visitVar + "}}", w.DelegatedGuard.Require!["VisitId"]);
            Assert.Equal("{{vars.review_ref}}", w.DelegatedGuard.Require["ArtifactRef"]);
        });
    }

    // ── PR definition ────────────────────────────────────────────────────────

    /// <summary>
    /// AC-D2 (push, then re-review) + AC-D3 + the AC-D4 doc-start inputs. A CEO changes-request
    /// produces a new head; the re-review publishes the new ref and a new visit. The old approval —
    /// both buffered across the visits and sent live naming the old ref — is discarded; the one
    /// naming the current visit and ref merges with <c>--match-head-commit</c> of that ref.
    /// </summary>
    [Fact]
    public async Task Pr_PushThenReReview_PublishesTheNewRef_DiscardsTheOldApproval_AndMergesPinned()
    {
        var head = HeadA;
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", PrInput(), Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            Assert.Equal(HeadA, h.LastUpsert(await handle.FetchHistoryAsync(), "ReviewRef"));

            head = HeadB;   // the author's revision pushes a new head
            await handle.SignalAsync("merge-approval", [Json("""{"Decision":"changes_requested","Comment":"push a fix"}""")]);
            // Sent while the run is between visits: buffered, then judged at visit 2.
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", HeadA)]);

            await h.WaitForGateVisitAsync(handle, "merge-approval:2");
            Assert.Equal(HeadB, h.LastUpsert(await handle.FetchHistoryAsync(), "ReviewRef"));

            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:2", HeadA)]);   // old ref
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:2", HeadB)]);
        });

        Assert.Equal(["", HeadA, "", HeadB], run.Upserts("ReviewRef"));
        Assert.Equal(2, run.Discards);

        var pinned = Assert.Single(run.Calls, c => c.Step == "phase4_merge_pinned");
        Assert.Contains($"--match-head-commit {HeadB}", pinned.Instruction);
        Assert.DoesNotContain(run.Calls, c => c.Step == "phase4_merge");

        // Both consensus rounds excluded the author and the CTO, and reviewed the head of the time.
        var reviews = run.ConsensusInputs.Where(i => i.ReviewRef is not null).ToList();
        Assert.Equal([HeadA, HeadB], reviews.Select(i => i.ReviewRef));
        Assert.All(reviews, i => Assert.Equal(["impl-agent", Cto], i.ExcludedAgents));

        // AC-D4: the doc run the PR starts receives the three scope inputs.
        var docArgs = Assert.Single(run.ChildStarts("UweDocMaintenanceWorkflow"));
        Assert.Equal(7, docArgs.GetProperty("IssueNumber").GetInt32());
        Assert.Equal(Reviewer, docArgs.GetProperty("ConsensusAgents").GetString());
        Assert.Equal("prep-agent", docArgs.GetProperty("PrepAgent").GetString());

        string Respond(Call call) => call.Step switch
        {
            "phase1_implement" => $"done\nPR_URL: {PrUrl}\nHEAD_SHA: {head}",
            "revise_ceo_feedback" => $"fixed\nHEAD_SHA: {head}",
            // The CEO-feedback round has a dissent, so it goes to the synthesizer.
            "synthesis" => "the CEO's concern holds\nVERDICT: changes_requested",
            "verify_merge_status" => "MERGED",
            "prepare" => "PREP: NO_DOC",
            $"review-{Reviewer}" when call.Instruction.Contains("The CEO has reviewed") =>
                Review("changes_requested", blocker: "apply the CEO's fix"),
            $"review-{Reviewer}" => Review("approved", reviewedRef: head),
            _ => "ok",
        };
    }

    /// <summary>
    /// AC-D2b (push, no re-review): the published ref is still the old head, so the decision is
    /// accepted; the merge is pinned to the reviewed head, GitHub refuses it, and the run takes the
    /// failure path with a notice. Nothing is merged and doc maintenance never starts.
    /// </summary>
    [Fact]
    public async Task Pr_PushWithoutReReview_MergeIsPinnedToTheReviewedHead_AndFails()
    {
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", PrInput(), Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", HeadA)]);
        });

        var pinned = Assert.Single(run.Calls, c => c.Step == "phase4_merge_pinned");
        Assert.Contains($"--match-head-commit {HeadA}", pinned.Instruction);
        Assert.Single(run.Calls, c => c.Step == "notify_merge_failed");
        Assert.Empty(run.ChildStarts("UweDocMaintenanceWorkflow"));

        // An approving PUBLIC_SCRUB: pass is published beside the ref (D10 reads it).
        Assert.Equal(["", "pass"], run.Upserts("ReviewScrub"));

        static string Respond(Call call) => call.Step switch
        {
            "phase1_implement" => $"done\nPR_URL: {PrUrl}\nHEAD_SHA: {HeadA}",
            // The mocked GitHub merge: the head moved after review.
            "phase4_merge_pinned" => "error: Head branch was modified. Review and try the merge again.",
            "verify_merge_status" => "FAILED",
            $"review-{Reviewer}" => Review("approved", reviewedRef: HeadA, scrub: "pass"),
            _ => "ok",
        };
    }

    /// <summary>AC-D3: a human approval merges exactly as before — no pin, no pinned step.</summary>
    [Fact]
    public async Task Pr_HumanApproval_MergesUnpinned()
    {
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", PrInput(), Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [Json("""{"Decision":"approved"}""")]);
        });

        var merge = Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.DoesNotContain("--match-head-commit", merge.Instruction);
        Assert.DoesNotContain(run.Calls, c => c.Step == "phase4_merge_pinned");
        Assert.Equal(0, run.Discards);

        static string Respond(Call call) => call.Step switch
        {
            "phase1_implement" => $"done\nPR_URL: {PrUrl}\nHEAD_SHA: {HeadA}",
            "verify_merge_status" => "MERGED",
            "prepare" => "PREP: NO_DOC",
            $"review-{Reviewer}" => Review("approved", reviewedRef: HeadA),
            _ => "ok",
        };
    }

    /// <summary>
    /// A review that does not attest the head (here: no REVIEWED_REF line) publishes an empty ref,
    /// so a delegated approval can never match; the human path still works.
    /// </summary>
    [Fact]
    public async Task Pr_UnattestedReview_PublishesNoRef_AndOnlyAHumanCanApprove()
    {
        var run = await SeedHarness.RunAsync("UwePrImplementationWorkflow", PrInput(), Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", HeadA)]);
            await handle.SignalAsync("merge-approval", [Json("""{"Decision":"approved"}""")]);
        });

        Assert.Equal(["", ""], run.Upserts("ReviewRef"));
        Assert.Equal(1, run.Discards);
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Equal(["", ""], run.Upserts("ReviewScrub"));

        static string Respond(Call call) => call.Step switch
        {
            "phase1_implement" => $"done\nPR_URL: {PrUrl}\nHEAD_SHA: {HeadA}",
            "verify_merge_status" => "MERGED",
            "prepare" => "PREP: NO_DOC",
            $"review-{Reviewer}" => Review("approved", reviewedRef: null, scrub: "pass"),
            _ => "ok",
        };
    }

    // ── Design definition ────────────────────────────────────────────────────

    /// <summary>
    /// AC-D2c: the issue body changed after the approved review. The grant decision is accepted at
    /// the gate, <c>verify_approved_body</c> outputs a different hash, and the run fails with
    /// <c>ERROR:approved_artifact_changed</c>: the design is not approved and no result is emitted.
    /// </summary>
    [Fact]
    public async Task Design_BodyEditedAfterReview_FailsApprovedArtifactChanged()
    {
        var run = await SeedHarness.RunAsync("UweDesignWorkflow", DesignInput(), DesignResponder(BodyD), async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "design-approval:1");
            Assert.Equal(BodyC, h.LastUpsert(await handle.FetchHistoryAsync(), "ReviewRef"));
            await handle.SignalAsync("design-approval", [Delegated("design-approval:1", BodyC)]);
        });

        Assert.Single(run.Calls, c => c.Step == "verify_approved_body");
        Assert.Single(run.Calls, c => c.Step == "notify_approved_body_changed");
        Assert.DoesNotContain(run.Calls, c => c.Step is "notify_ceo_approved" or "emit_approved_result");
        Assert.Null(run.Result);
    }

    [Fact]
    public async Task Design_GrantDecisionWithUnchangedBody_IsApproved()
    {
        var run = await SeedHarness.RunAsync("UweDesignWorkflow", DesignInput(), DesignResponder(BodyC), async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "design-approval:1");
            await handle.SignalAsync("design-approval", [Delegated("design-approval:1", BodyC)]);
        });

        Assert.Single(run.Calls, c => c.Step == "verify_approved_body");
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_approved_body_changed");
        Assert.Single(run.Calls, c => c.Step == "emit_approved_result");
    }

    /// <summary>AC-D2c: a human approval does not run the body re-hash.</summary>
    [Fact]
    public async Task Design_HumanApproval_SkipsTheBodyCheck()
    {
        var run = await SeedHarness.RunAsync("UweDesignWorkflow", DesignInput(), DesignResponder(BodyD), async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "design-approval:1");
            await handle.SignalAsync("design-approval", [Json("""{"Decision":"approved"}""")]);
        });

        Assert.DoesNotContain(run.Calls, c => c.Step == "verify_approved_body");
        Assert.Single(run.Calls, c => c.Step == "emit_approved_result");
        var review = Assert.Single(run.ConsensusInputs);
        Assert.Equal(BodyC, review.ReviewRef);
        Assert.Equal(["design-agent", Cto], review.ExcludedAgents);
    }

    // ── Doc definition ───────────────────────────────────────────────────────

    /// <summary>
    /// AC-D4: nothing is written before the gate — the only delegations before <c>doc-review</c>
    /// are the read-only prepare, the reviewers and the gate notice — and an applied artifact
    /// whose hash differs from the reviewed ref takes the failure path.
    /// </summary>
    [Fact]
    public async Task Doc_WritesNothingBeforeTheGate_AndAnApplyHashMismatchFails()
    {
        var gateCalls = new List<string>();
        var run = await SeedHarness.RunAsync("UweDocMaintenanceWorkflow", DocInput(), DocResponder("memory new", ArtifactF), async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "doc-review:1");
            gateCalls.AddRange(h.Calls.Select(c => c.Step));
            Assert.Equal(ArtifactE, h.LastUpsert(await handle.FetchHistoryAsync(), "ReviewRef"));
            await handle.SignalAsync("doc-review", [Delegated("doc-review:1", ArtifactE)]);
        });

        Assert.Contains("prepare", gateCalls);
        Assert.Contains($"review-{Reviewer}", gateCalls);
        Assert.All(gateCalls, step => Assert.Contains(step, new[] { "prepare", $"review-{Reviewer}", "notify_doc_review" }));
        Assert.Equal("prep-agent", Assert.Single(run.Calls, c => c.Step == "prepare").Target);
        Assert.Single(run.Calls, c => c.Step == "apply");
        Assert.Single(run.Calls, c => c.Step == "notify_apply_mismatch");
        Assert.Contains("complete-apply-mismatch", run.Upserts("Phase"));

        var review = Assert.Single(run.ConsensusInputs);
        Assert.Equal(ArtifactE, review.ReviewRef);
        Assert.Equal(["prep-agent", Cto], review.ExcludedAgents);
    }

    [Fact]
    public async Task Doc_ApplyOfTheReviewedArtifact_Completes()
    {
        var run = await SeedHarness.RunAsync("UweDocMaintenanceWorkflow", DocInput(), DocResponder("repo_doc example-org/example-repo:docs/a.md", ArtifactE), async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "doc-review:1");
            await handle.SignalAsync("doc-review", [Delegated("doc-review:1", ArtifactE)]);
        });

        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_apply_mismatch");
        Assert.Contains("complete-approved", run.Upserts("Phase"));
    }

    /// <summary>AC-D4: an instruction target is never delegable — the approved review publishes no ref.</summary>
    [Fact]
    public async Task Doc_InstructionTarget_PublishesNoRef_AndTheDelegatedApprovalIsDiscarded()
    {
        var run = await SeedHarness.RunAsync("UweDocMaintenanceWorkflow", DocInput(), DocResponder("instruction example-instruction", ArtifactE), async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "doc-review:1");
            await handle.SignalAsync("doc-review", [Delegated("doc-review:1", ArtifactE)]);
            await handle.SignalAsync("doc-review", [Json("""{"Decision":"rejected","Comment":"not now"}""")]);
        });

        Assert.Equal(["", ""], run.Upserts("ReviewRef"));
        Assert.Equal(1, run.Discards);
        Assert.DoesNotContain(run.Calls, c => c.Step == "apply");
        Assert.Contains("complete-rejected", run.Upserts("Phase"));
    }

    /// <summary>
    /// A doc run started without the new inputs (an older PR definition) skips the review and
    /// defaults the preparer to the CTO; the gate stays a human decision and apply checks the
    /// prepared artifact.
    /// </summary>
    [Fact]
    public async Task Doc_WithoutTheNewInputs_IsHumanOnly_AndStillAppliesThePreparedArtifact()
    {
        var input = DocInput();
        input.Remove("ConsensusAgents");
        input.Remove("PrepAgent");
        input.Remove("IssueNumber");

        var run = await SeedHarness.RunAsync("UweDocMaintenanceWorkflow", input, DocResponder("memory new", ArtifactE), async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "doc-review:1");
            await handle.SignalAsync("doc-review", [Json("""{"Decision":"approved"}""")]);
        });

        Assert.Empty(run.ConsensusInputs);
        Assert.Equal(Cto, Assert.Single(run.Calls, c => c.Step == "prepare").Target);
        Assert.Equal(["", ""], run.Upserts("ReviewRef"));
        Assert.Contains("complete-approved", run.Upserts("Phase"));
    }

    // ── scripts ──────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> PrInput() => new()
    {
        ["Repo"] = Repo,
        ["IssueNumber"] = 7,
        ["TargetAgent"] = "impl-agent",
        ["ConsensusAgents"] = Reviewer,
        ["DocPrepAgent"] = "prep-agent",
    };

    private static Dictionary<string, object?> DesignInput() => new()
    {
        ["Repo"] = Repo,
        ["ExistingIssueNumber"] = 12,
        ["TargetAgent"] = "design-agent",
        ["ConsensusAgents"] = Reviewer,
        ["Description"] = "an example feature",
    };

    private static Dictionary<string, object?> DocInput() => new()
    {
        ["Repo"] = Repo,
        ["PrNumber"] = 7,
        ["BranchName"] = "feat/issue-7",
        ["MergeSummary"] = "merged",
        ["IssueNumber"] = 7,
        ["ConsensusAgents"] = Reviewer,
        ["PrepAgent"] = "prep-agent",
    };

    private static Func<Call, string> DesignResponder(string bodyNow) => call => call.Step switch
    {
        "create_or_refine" => $"refined\nISSUE_NUMBER: 12\nBODY_SHA256: {BodyC}",
        "verify_approved_body" => $"BODY_SHA256_NOW: {bodyNow}",
        "emit_approved_result" => """{"IssueNumber": 12}""",
        $"review-{Reviewer}" => Review("approved", reviewedRef: BodyC),
        _ => "ok",
    };

    private static Func<Call, string> DocResponder(string target, string appliedHash) => call => call.Step switch
    {
        "prepare" => string.Join('\n',
            "PREP: ARTIFACT",
            $"TARGET: {target}",
            $"ARTIFACT_SHA256: {ArtifactE}",
            "-----BEGIN DOC ARTIFACT-----",
            $"TARGET: {target}",
            "example documentation content",
            "-----END DOC ARTIFACT-----"),
        "apply" => $"applied\nAPPLIED_ARTIFACT_SHA256: {appliedHash}",
        $"review-{Reviewer}" => Review("approved", reviewedRef: ArtifactE),
        _ => "ok",
    };

    private static string Review(string verdict, string? reviewedRef = null, string? blocker = null, string? scrub = null)
    {
        var lines = new List<string> { "detailed review", "SUMMARY: reviewed", "EVIDENCE: none" };
        lines.Add($"BLOCKER: {blocker ?? "none"}");
        if (reviewedRef is not null) lines.Add($"REVIEWED_REF: {reviewedRef}");
        if (scrub is not null) lines.Add($"PUBLIC_SCRUB: {scrub}");
        lines.Add($"VERDICT: {verdict}");
        return string.Join('\n', lines);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>Exactly the five fields the orchestrator sends.</summary>
    private static JsonElement Delegated(string visitId, string artifactRef) =>
        JsonSerializer.SerializeToElement(new
        {
            Decision = "approved",
            GrantId,
            VisitId = visitId,
            ArtifactRef = artifactRef,
            Evidence,
        });
}

// ---------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------

internal sealed record Call(string Step, string Target, string Instruction);

internal sealed record ConsensusCall(string? ReviewRef, string[] ExcludedAgents);

internal sealed record SeedRun(
    object? Result,
    WorkflowHistory History,
    IReadOnlyList<Call> Calls,
    IReadOnlyList<ConsensusCall> ConsensusInputs,
    int Discards)
{
    public List<string> Upserts(string attribute) => SeedHarness.Upserts(History, attribute);

    /// <summary>The input of every child of this type the run started.</summary>
    public List<JsonElement> ChildStarts(string workflowType) =>
        History.Events
            .Where(e => e.EventType == EventType.StartChildWorkflowExecutionInitiated
                && e.StartChildWorkflowExecutionInitiatedEventAttributes.WorkflowType.Name == workflowType)
            .Select(e => DataConverter.Default.PayloadConverter.ToValue<JsonElement>(
                e.StartChildWorkflowExecutionInitiatedEventAttributes.Input.Payloads_[0]))
            .ToList();
}

internal sealed class SeedHarness
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private readonly List<Call> _calls = [];
    private readonly List<ConsensusCall> _consensus = [];

    public IReadOnlyList<Call> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    /// <summary>The seed definition, through the production parse and validation path.</summary>
    public static StepDefinition Parse(string workflow)
    {
        var activity = new LoadWorkflowDefinitionActivity(new SeedHttpClientFactory());
        return activity.LoadAsync(workflow).GetAwaiter().GetResult().Root;
    }

    public static async Task<SeedRun> RunAsync(
        string workflowType,
        Dictionary<string, object?> input,
        Func<Call, string> respond,
        Func<WorkflowHandle, SeedHarness, Task> drive)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var register = new AddSearchAttributesRequest { Namespace = env.Client.Options.Namespace };
        foreach (var name in SearchAttributeTypeRegistry.GetNames())
        {
            register.SearchAttributes[name] = SearchAttributeTypeRegistry.GetType(name) switch
            {
                SearchAttributeTypeRegistry.AttributeType.Int => IndexedValueType.Int,
                SearchAttributeTypeRegistry.AttributeType.DateTime => IndexedValueType.Datetime,
                _ => IndexedValueType.Keyword,
            };
        }
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(register);

        var harness = new SeedHarness();
        var logs = new DiscardCounter();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        // The seed starts its children on task queue "fleet"; each test has its own environment.
        const string taskQueue = "fleet";
        var workflowId = $"seed-{Guid.NewGuid():N}";

        var loader = new LoadWorkflowDefinitionActivity(new SeedHttpClientFactory());
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue) { LoggerFactory = loggerFactory }
                .AddWorkflow<UniversalWorkflow>()
                .AddWorkflow<ConsensusReviewWorkflow>()
                .AddAllActivities(typeof(LoadWorkflowDefinitionActivity), loader)
                .AddActivity(ActivityDefinition.Create(
                    "LoadWorkflowConfig", typeof(JsonElement), [], 0,
                    _ => JsonSerializer.SerializeToElement(new { CtoAgent = "cto-agent", EscalationTarget = "cto-agent" })))
                .AddActivity(ActivityDefinition.Create("LoadAgentBudgetSeconds", typeof(int), [], 0, _ => 600))
                .AddActivity(harness.DelegateStub(respond)));

        object? result = null;
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                workflowType, [input], new WorkflowOptions(id: workflowId, taskQueue: taskQueue));
            await drive(handle, harness);
            result = await handle.GetResultAsync<JsonElement?>().WaitAsync(TimeSpan.FromSeconds(120));
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        await harness.CollectConsensusInputsAsync(env, history);
        return new SeedRun(
            result is JsonElement { ValueKind: JsonValueKind.Null } ? null : result,
            history, harness.Calls, harness._consensus, logs.Count);
    }

    private ActivityDefinition DelegateStub(Func<Call, string> respond) =>
        ActivityDefinition.Create(
            DelegateToAgentActivity.ActivityName,
            typeof(AgentTaskResult),
            [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int), typeof(int), typeof(string)],
            3,
            args =>
            {
                var taskId = (string)args[2]!;
                var call = new Call(taskId.Split('/').Last(), (string)args[0]!, (string)args[1]!);
                lock (_calls) _calls.Add(call);
                return new AgentTaskResult(respond(call), "completed");
            });

    /// <summary>Every consensus child's input, read from the children's own histories.</summary>
    private async Task CollectConsensusInputsAsync(WorkflowEnvironment env, WorkflowHistory parent)
    {
        var children = parent.Events
            .Where(e => e.EventType == EventType.ChildWorkflowExecutionStarted
                && e.ChildWorkflowExecutionStartedEventAttributes.WorkflowType.Name == "ConsensusReviewWorkflow")
            .Select(e => e.ChildWorkflowExecutionStartedEventAttributes.WorkflowExecution.WorkflowId);

        foreach (var id in children)
        {
            var history = await env.Client.GetWorkflowHandle(id).FetchHistoryAsync();
            var started = history.Events.First(e => e.EventType == EventType.WorkflowExecutionStarted);
            var input = DataConverter.Default.PayloadConverter.ToValue<ConsensusReviewInput>(
                started.WorkflowExecutionStartedEventAttributes.Input.Payloads_[0]);
            _consensus.Add(new ConsensusCall(input.ReviewRef, input.ExcludedAgents ?? []));
        }
    }

    public async Task WaitForGateVisitAsync(WorkflowHandle handle, string visit)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (LastUpsert(await handle.FetchHistoryAsync(), "GateVisit") != visit)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"gate visit {visit} not reached");
            await Task.Delay(50);
        }
    }

    public string? LastUpsert(WorkflowHistory history, string attribute) => Upserts(history, attribute).LastOrDefault();

    public static List<string> Upserts(WorkflowHistory history, string attribute) =>
        history.Events
            .Where(e => e.EventType == EventType.UpsertWorkflowSearchAttributes)
            .SelectMany(e => e.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields
                .Where(kv => kv.Key == attribute)
                .Select(kv => DataConverter.Default.PayloadConverter.ToValue<string>(kv.Value)))
            .ToList();

    /// <summary>Serves every seed definition the way the orchestrator's REST API does.</summary>
    private sealed class SeedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new SeedHandler()) { BaseAddress = new Uri("http://orchestrator.test/") };
    }

    private sealed class SeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = request.RequestUri!.Segments.Last();
            var seed = JsonNode.Parse(File.ReadAllText(SeedPath()))!;
            var entry = seed["workflowDefinitions"]!.AsArray().FirstOrDefault(d => d!["name"]!.GetValue<string>() == name);
            if (entry is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var body = JsonSerializer.Serialize(new
            {
                name,
                @namespace = entry["namespace"]!.GetValue<string>(),
                taskQueue = entry["taskQueue"]!.GetValue<string>(),
                definition = entry["definition"]!.ToJsonString(),
                version = 1,
            }, WebOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string SeedPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "seed.example.json");
    }

    /// <summary>Counts the engine's discard warnings.</summary>
    private sealed class DiscardCounter : ILoggerProvider
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(DiscardCounter owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (formatter(state, exception).StartsWith("delegated signal discarded: stale visit or artifact", StringComparison.Ordinal))
                    Interlocked.Increment(ref owner._count);
            }
        }
    }
}
