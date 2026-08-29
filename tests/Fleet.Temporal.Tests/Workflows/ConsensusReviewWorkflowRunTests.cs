using Fleet.Temporal.Models;
using Fleet.Temporal.Workflows.Fleet;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Workflows;

/// <summary>
/// Workflow-level coverage: these drive <see cref="ConsensusReviewWorkflow.RunAsync"/> itself in
/// a real Temporal worker, rather than calling the deterministic helpers in isolation.
///
/// That distinction is the whole point of this file. Helper tests prove the parser and the
/// renderer behave; they cannot prove that the envelope actually reaches a reviewer, that the
/// synthesizer activity is genuinely not scheduled on the short-circuit paths, or that the patch
/// gate keeps an old history on the legacy branch. Each of those is a property of the orchestration,
/// and the only honest way to assert it is to run the orchestration and look at what happened.
///
/// The reviewer/synthesizer activity is replaced by a stub registered under the same activity
/// name, so every instruction the workflow builds is captured verbatim for assertion.
/// </summary>
[Collection("consensus-workflow")]
public class ConsensusReviewWorkflowRunTests
{
    private const string TaskQueue = "consensus-review-tests";
    private const string ActivityName = "DelegateToAgent";
    private const string Sentinel = "ZZQ-RAW-ONLY-SENTINEL-7413";

    /// <summary>Records every delegation the workflow makes, in order.</summary>
    private sealed class Delegations
    {
        private readonly List<(string Agent, string Instruction)> _calls = [];
        private readonly Lock _gate = new();

        public void Record(string agent, string instruction)
        {
            lock (_gate) _calls.Add((agent, instruction));
        }

        public IReadOnlyList<(string Agent, string Instruction)> Calls
        {
            get { lock (_gate) return [.. _calls]; }
        }

        public string InstructionFor(string agent) =>
            Calls.Single(c => c.Agent == agent).Instruction;
    }

    /// <summary>
    /// Stub standing in for DelegateToAgentActivity. Registered under the same activity name, so
    /// the workflow's own call site is exercised unchanged — nothing in production code is aware
    /// of the substitution.
    /// </summary>
    private static ActivityDefinition BuildStub(
        Delegations log, Func<string, string> responseFor) =>
        ActivityDefinition.Create(
            ActivityName,
            typeof(AgentTaskResult),
            [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int)],
            3,   // retryOnIncomplete and maxIncompleteRetries are optional at the call site
            args =>
            {
                var agent = (string)args[0]!;
                var instruction = (string)args[1]!;
                log.Record(agent, instruction);
                return new AgentTaskResult(responseFor(agent), "completed");
            });

    private static async Task<(ConsensusReviewOutput Output, Delegations Log, string WorkflowId)>
        RunAsync(
            Func<string, string> responseFor,
            string[] reviewers)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var log = new Delegations();

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddActivity(BuildStub(log, responseFor))
                .AddWorkflow<ConsensusReviewWorkflow>());

        ConsensusReviewOutput output = null!;
        var workflowId = $"consensus-{Guid.NewGuid():N}";

        await worker.ExecuteAsync(async () =>
        {
            var wfInput = new ConsensusReviewInput(
                "a change under review", "review it", reviewers, null, "synthesizer");
            output = await env.Client.ExecuteWorkflowAsync(
                (ConsensusReviewWorkflow wf) => wf.RunAsync(wfInput),
                new WorkflowOptions(workflowId, TaskQueue));
        });

        return (output, log, workflowId);
    }

    private static string Response(
        string summary, string verdict, string[]? blockers = null, string? evidence = "none")
    {
        var lines = new List<string> { $"Long detailed body for the reviewer. {Sentinel}" };
        lines.Add($"SUMMARY: {summary}");
        if (evidence is not null) lines.Add($"EVIDENCE: {evidence}");
        foreach (var b in blockers ?? []) lines.Add($"BLOCKER: {b}");
        lines.Add($"VERDICT: {verdict}");
        return string.Join("\n", lines);
    }

    // ── The envelope actually reaches the reviewers ───────────────────────────

    [Fact]
    public async Task RunAsync_InjectsTheFourMarkerEnvelopeExactlyOnce_IntoEveryReviewerInstruction()
    {
        var (_, log, _) = await RunAsync(
            _ => Response("no objection", ReviewVerdict.Approved),
            ["reviewer-one", "reviewer-two"]);

        foreach (var agent in new[] { "reviewer-one", "reviewer-two" })
        {
            var instruction = log.InstructionFor(agent);

            // The caller's own prompt is still there...
            Assert.Contains("review it", instruction);

            // ...and each marker heads exactly one line. Twice would mean the envelope was
            // appended both centrally and by the caller — the duplication this design forbids.
            foreach (var marker in new[] { "SUMMARY:", "EVIDENCE:", "BLOCKER:", "VERDICT:" })
            {
                var headings = instruction.Split('\n')
                    .Count(l => l.TrimStart().StartsWith(marker, StringComparison.Ordinal));
                Assert.True(headings == 1,
                    $"{agent}: expected {marker} to head exactly one line, found {headings}");
            }
        }
    }

    [Fact]
    public async Task RunAsync_AppendsThePerAgentPerspectiveAndTheEnvelope_WithoutLosingEither()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var log = new Delegations();

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddActivity(BuildStub(log, _ => Response("fine", ReviewVerdict.Approved)))
                .AddWorkflow<ConsensusReviewWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var wfInput = new ConsensusReviewInput(
                "subject", "base prompt",
                new[] { "reviewer-one" },
                new Dictionary<string, string> { ["reviewer-one"] = "Your perspective: security." },
                "synthesizer");
            await env.Client.ExecuteWorkflowAsync(
                (ConsensusReviewWorkflow wf) => wf.RunAsync(wfInput),
                new WorkflowOptions($"consensus-{Guid.NewGuid():N}", TaskQueue));
        });

        var instruction = log.InstructionFor("reviewer-one");
        Assert.Contains("base prompt", instruction);
        Assert.Contains("Your perspective: security.", instruction);
        Assert.Contains("SUMMARY:", instruction);
    }

    // ── The synthesizer is genuinely not scheduled on the short-circuit paths ─

    [Fact]
    public async Task RunAsync_UnanimousApproval_DoesNotScheduleTheSynthesizer()
    {
        var (output, log, _) = await RunAsync(
            _ => Response("no objection", ReviewVerdict.Approved),
            ["reviewer-one", "reviewer-two"]);

        Assert.Equal(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.DoesNotContain(log.Calls, c => c.Agent == "synthesizer");
        Assert.Equal(2, log.Calls.Count);
    }

    [Fact]
    public async Task RunAsync_AnyNeedsHumanReview_DoesNotScheduleTheSynthesizer()
    {
        var (output, log, _) = await RunAsync(
            agent => agent == "reviewer-one"
                ? Response("cannot judge this safely", ReviewVerdict.NeedsHumanReview)
                : Response("no objection", ReviewVerdict.Approved),
            ["reviewer-one", "reviewer-two"]);

        Assert.Equal(ReviewVerdict.NeedsHumanReview, output.FinalVerdict);
        Assert.DoesNotContain(log.Calls, c => c.Agent == "synthesizer");
    }

    [Fact]
    public async Task RunAsync_BlockerOverflow_ShortCircuitsBeforeTheSynthesizer()
    {
        // Enough distinct blockers that the rendered block alone busts the 2000-unit cap.
        var (output, log, _) = await RunAsync(
            agent => Response("too much to fix", ReviewVerdict.ChangesRequested,
                blockers: [new string(agent[^1], 900)]),
            ["reviewer-1", "reviewer-2", "reviewer-3"]);

        Assert.Equal(ReviewVerdict.NeedsHumanReview, output.FinalVerdict);
        Assert.DoesNotContain(log.Calls, c => c.Agent == "synthesizer");
        Assert.StartsWith("[blocker overflow:", output.ConsolidatedReasoning);
        // Not one fragment of a blocker leaked into the message.
        Assert.DoesNotContain(new string('1', 50), output.ConsolidatedReasoning);
    }

    [Fact]
    public async Task RunAsync_MixedVerdicts_DoesScheduleTheSynthesizer()
    {
        // Control for the three assertions above: if the synthesizer were never scheduled on any
        // path, "not scheduled" would be trivially true and prove nothing.
        var (_, log, _) = await RunAsync(
            agent => agent == "reviewer-one"
                ? Response("one real problem", ReviewVerdict.ChangesRequested, blockers: ["rename the flag"])
                : Response("no objection", ReviewVerdict.Approved),
            ["reviewer-one", "reviewer-two"]);

        Assert.Contains(log.Calls, c => c.Agent == "synthesizer");
    }

    // ── What the synthesizer actually received ────────────────────────────────

    [Fact]
    public async Task RunAsync_SynthesizerInstruction_CarriesCompactFieldsOnly()
    {
        var (output, log, _) = await RunAsync(
            agent => agent == "synthesizer"
                ? "The flag naming is the blocking issue.\nVERDICT: changes_requested"
                : agent == "reviewer-one"
                    ? Response("one real problem", ReviewVerdict.ChangesRequested, blockers: ["rename the flag"])
                    : Response("no objection", ReviewVerdict.Approved),
            ["reviewer-one", "reviewer-two"]);

        var synthesisInstruction = log.InstructionFor("synthesizer");

        // The raw review body never reaches the synthesizer...
        Assert.DoesNotContain(Sentinel, synthesisInstruction);
        // ...and neither does the blocker list — that transport is owned by workflow code.
        Assert.DoesNotContain("rename the flag", synthesisInstruction);
        // The compact fields do.
        Assert.Contains("one real problem", synthesisInstruction);

        // The blocker still reaches the implementer verbatim, assembled after synthesis.
        Assert.Contains("- reviewer-one: rename the flag", output.ConsolidatedReasoning);
        Assert.DoesNotContain(Sentinel, output.ConsolidatedReasoning);

        // ...while the complete original response stays durable.
        Assert.Contains(Sentinel, output.PerAgentVerdicts[0].ReviewText);
    }

    [Fact]
    public async Task RunAsync_NeverApprovesWhileABlockerIsNamed_EvenWhenTheSynthesizerApproves()
    {
        // The synthesizer resolves a lone dissenter into an approval. The label must not follow it.
        var (output, _, _) = await RunAsync(
            agent => agent == "synthesizer"
                ? "The single concern is minor.\nVERDICT: approved"
                : agent == "reviewer-one"
                    ? Response("one real problem", ReviewVerdict.ChangesRequested,
                        blockers: ["the migration has no down step"])
                    : Response("no objection", ReviewVerdict.Approved),
            ["reviewer-one", "reviewer-two"]);

        Assert.NotEqual(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.Contains("- reviewer-one: the migration has no down step", output.ConsolidatedReasoning);
    }

    // ── Patch-history replay ──────────────────────────────────────────────────

    [Fact]
    public async Task NewHistory_RecordsThePatchMarker_AndReplaysWithoutNondeterminism()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var log = new Delegations();
        var workflowId = $"consensus-{Guid.NewGuid():N}";

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddActivity(BuildStub(log, _ => Response("fine", ReviewVerdict.Approved)))
                .AddWorkflow<ConsensusReviewWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var wfInput = new ConsensusReviewInput(
                "subject", "review it", new[] { "reviewer-one" }, null, "synthesizer");
            await env.Client.ExecuteWorkflowAsync(
                (ConsensusReviewWorkflow wf) => wf.RunAsync(wfInput),
                new WorkflowOptions(workflowId, TaskQueue));
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();

        // The patch marker is recorded, so a later worker knows this execution took the new path.
        var markers = history.Events
            .Where(e => e.MarkerRecordedEventAttributes is not null)
            .Select(e => e.MarkerRecordedEventAttributes.MarkerName)
            .ToList();
        Assert.Contains("core_patch", markers);

        // And the recorded history replays against the current code.
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<ConsensusReviewWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

    [Fact]
    public async Task PrePatchHistory_StaysOnTheLegacyBranch_AndReplaysWithoutNondeterminism()
    {
        // A history produced by code that never called Workflow.Patched — i.e. an execution
        // already in flight when this change deploys. Replaying it against the CURRENT workflow
        // must take the legacy branch. This is the test that proves the gate does its job; without
        // it, "the patch is positioned correctly" is an assertion nobody checked.
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var log = new Delegations();
        var workflowId = $"consensus-legacy-{Guid.NewGuid():N}";

        using var legacyWorker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddActivity(BuildStub(log, _ =>
                    // No BLOCKER: line. The legacy parser reads this as changes_requested and
                    // schedules the synthesizer; the compact parser escalates it to
                    // needs_human_review and schedules nothing. That difference in ACTIVITY
                    // SEQUENCE is what replay can actually detect — Temporal compares the
                    // sequence of commands, not activity input payloads, so a history whose
                    // shape matches on both paths would replay clean either way and prove
                    // nothing about the gate.
                    Response("needs work", ReviewVerdict.ChangesRequested)))
                .AddWorkflow<LegacyConsensusReviewWorkflowDouble>());

        await legacyWorker.ExecuteAsync(async () =>
        {
            var wfInput = new ConsensusReviewInput(
                "subject", "review it", new[] { "reviewer-one" }, null, "synthesizer");
            await env.Client.ExecuteWorkflowAsync(
                (LegacyConsensusReviewWorkflowDouble wf) => wf.RunAsync(wfInput),
                new WorkflowOptions(workflowId, TaskQueue));
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();

        // The legacy history carries no patch marker...
        Assert.DoesNotContain(
            history.Events.Where(e => e.MarkerRecordedEventAttributes is not null)
                          .Select(e => e.MarkerRecordedEventAttributes.MarkerName),
            m => m == "core_patch");

        // ...the reviewer instruction it recorded is the legacy one, without the envelope...
        var legacyInstruction = log.InstructionFor("reviewer-one");
        Assert.DoesNotContain("SUMMARY:", legacyInstruction);

        // ...and it scheduled the synthesizer, which the compact path would not have.
        Assert.Contains(log.Calls, c => c.Agent == "synthesizer");

        // Replaying it against the CURRENT workflow must not raise a nondeterminism error.
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<ConsensusReviewWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }
}

/// <summary>
/// A byte-for-byte copy of the pre-patch workflow body, registered under the same workflow name
/// so the history it produces is indistinguishable from one recorded by the old deployed code.
///
/// It exists solely to generate a legacy history for the replay test above. Do not "refactor"
/// it to share code with the production workflow — the moment it shares the patch gate, it stops
/// being able to produce a pre-patch history and the replay test silently stops testing anything.
/// </summary>
[Workflow("ConsensusReviewWorkflow")]
public class LegacyConsensusReviewWorkflowDouble
{
    [WorkflowRun]
    public async Task<ConsensusReviewOutput> RunAsync(ConsensusReviewInput input)
    {
        var reviewers = input.ReviewerAgents!;
        var synthesizer = input.Synthesizer!;
        var workflowId = Workflow.Info.WorkflowId;

        var verdictInstruction =
            $"\n\nEnd your response with exactly one of these verdict lines:\n" +
            $"VERDICT: {ReviewVerdict.Approved}\n" +
            $"VERDICT: {ReviewVerdict.ChangesRequested}\n" +
            $"VERDICT: {ReviewVerdict.NeedsHumanReview}";

        var reviewTasks = reviewers
            .Select(agent => Workflow.ExecuteActivityAsync<AgentTaskResult>(
                "DelegateToAgent",
                [agent, input.ReviewPrompt + verdictInstruction, $"{workflowId}/review-{agent}", true, 3],
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(15),
                    HeartbeatTimeout = TimeSpan.FromSeconds(90),
                    CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                }))
            .ToArray();

        await Workflow.WhenAllAsync(reviewTasks);

        var agentReviews = reviewers
            .Zip(reviewTasks, (agent, task) =>
                new AgentReview(agent, task.Result.Text, LegacyParseVerdict(task.Result.Text)))
            .ToArray();

        if (agentReviews.Any(r => r.Verdict == ReviewVerdict.NeedsHumanReview))
        {
            return new ConsensusReviewOutput(
                ReviewVerdict.NeedsHumanReview, "legacy human review", agentReviews);
        }

        if (agentReviews.All(r => r.Verdict == ReviewVerdict.Approved))
        {
            return new ConsensusReviewOutput(ReviewVerdict.Approved, "legacy approved", agentReviews);
        }

        // The divergence that makes the replay test meaningful: legacy schedules a SECOND
        // activity here where the compact path short-circuits.
        var synthesis = await Workflow.ExecuteActivityAsync<AgentTaskResult>(
            "DelegateToAgent",
            [synthesizer, "legacy synthesis instruction", $"{workflowId}/synthesis", true, 3],
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(15),
                HeartbeatTimeout = TimeSpan.FromSeconds(90),
            });

        return new ConsensusReviewOutput(
            LegacyParseVerdict(synthesis.Text), synthesis.Text, agentReviews);
    }

    /// <summary>The pre-patch parser: an unrecognised verdict fell through to changes_requested.</summary>
    private static string LegacyParseVerdict(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = trimmed["VERDICT:".Length..].Trim().ToLowerInvariant().Replace(' ', '_');
            return value switch
            {
                ReviewVerdict.Approved => ReviewVerdict.Approved,
                ReviewVerdict.ChangesRequested => ReviewVerdict.ChangesRequested,
                ReviewVerdict.NeedsHumanReview => ReviewVerdict.NeedsHumanReview,
                _ => ReviewVerdict.ChangesRequested,
            };
        }
        return ReviewVerdict.ChangesRequested;
    }
}
