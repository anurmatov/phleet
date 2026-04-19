using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Temporalio.Workflows;

namespace Fleet.Temporal.Workflows.Fleet;

/// <summary>
/// Reusable child workflow that orchestrates a single round of multi-agent consensus review.
///
/// Flow:
///   1. Fan out: all ReviewerAgents review in parallel via Workflow.WhenAllAsync.
///      Each agent receives the base ReviewPrompt plus the domain rubric selected by
///      ReviewDomain (defaults to "code_review" when null or unrecognised).
///      If AgentPerspectives contains an entry for the agent, that perspective instruction
///      is appended after the rubric.
///   2. Fast path: unanimous "approved" → return immediately without synthesis.
///   3. Any "needs_human_review" → propagate immediately.
///   4. Synthesizer consolidates divergent reviews into a single verdict + reasoning.
///
/// This workflow is single-pass. Callers (e.g. UwePrImplementationWorkflow) drive the
/// revision loop — they re-invoke this workflow after applying changes.
/// </summary>
[Workflow]
public class ConsensusReviewWorkflow
{
    [WorkflowRun]
    public async Task<ConsensusReviewOutput> RunAsync(ConsensusReviewInput input)
    {
        if (input.ReviewerAgents is not { Length: > 0 })
            throw new ArgumentException("ReviewerAgents is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Synthesizer))
            throw new ArgumentException("Synthesizer is required.", nameof(input));

        var reviewers = input.ReviewerAgents;
        var synthesizer = input.Synthesizer;
        var workflowId = Workflow.Info.WorkflowId;

        // Inject the domain-specific rubric between the base prompt and the verdict instruction.
        var rubric = ConsensusReviewDomains.GetRubric(input.ReviewDomain);

        var verdictInstruction =
            $"\n\nEnd your response with exactly one of these verdict lines:\n" +
            $"VERDICT: {ReviewVerdict.Approved}\n" +
            $"VERDICT: {ReviewVerdict.ChangesRequested}\n" +
            $"VERDICT: {ReviewVerdict.NeedsHumanReview}";

        // ── Fan out: all agents review in parallel ────────────────────────────────
        var reviewTasks = reviewers
            .Select(agent =>
            {
                var perspective = input.AgentPerspectives?.GetValueOrDefault(agent);
                var instruction = input.ReviewPrompt + "\n\n" + rubric;
                if (!string.IsNullOrWhiteSpace(perspective))
                    instruction += "\n\n" + perspective;
                instruction += verdictInstruction;

                return Workflow.ExecuteActivityAsync(
                    (DelegateToAgentActivity a) => a.DelegateToAgentAsync(
                        agent,
                        instruction,
                        $"{workflowId}/review-{agent}"),
                    new ActivityOptions
                    {
                        StartToCloseTimeout = TimeSpan.FromMinutes(15),
                        HeartbeatTimeout = TimeSpan.FromSeconds(90),
                        CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                    });
            })
            .ToArray();

        await Workflow.WhenAllAsync(reviewTasks);

        var agentReviews = reviewers
            .Zip(reviewTasks, (agent, task) =>
                new AgentReview(agent, task.Result.Text, ParseVerdict(task.Result.Text)))
            .ToArray();

        // ── needs_human_review: propagate immediately ─────────────────────────────
        var humanFlagged = agentReviews.FirstOrDefault(r => r.Verdict == ReviewVerdict.NeedsHumanReview);
        if (humanFlagged is not null)
        {
            return new ConsensusReviewOutput(
                FinalVerdict: ReviewVerdict.NeedsHumanReview,
                ConsolidatedReasoning: $"{humanFlagged.AgentName} flagged this for human review:\n{humanFlagged.ReviewText}",
                PerAgentVerdicts: agentReviews);
        }

        // ── Fast path: unanimous approval — skip synthesis ───────────────────────
        if (agentReviews.All(r => r.Verdict == ReviewVerdict.Approved))
        {
            var combined = string.Join("\n\n---\n\n", agentReviews.Select(r =>
                $"## {r.AgentName}\n{r.ReviewText}"));
            return new ConsensusReviewOutput(
                FinalVerdict: ReviewVerdict.Approved,
                ConsolidatedReasoning: combined,
                PerAgentVerdicts: agentReviews);
        }

        // ── Synthesis: synthesizer consolidates into a single verdict ─────────────
        var reviewSummaries = string.Join("\n\n---\n\n", agentReviews.Select(r =>
            $"## {r.AgentName} (verdict: {r.Verdict})\n{r.ReviewText}"));

        var synthesisInstruction =
            $"You have received {agentReviews.Length} independent reviews of: {input.Subject}\n\n" +
            $"## Independent reviews\n\n{reviewSummaries}\n\n" +
            $"Synthesize these into a single actionable verdict. " +
            $"Identify the most important issues that must be addressed. " +
            $"If all substantive concerns are minor or cosmetic, you may approve.\n" +
            $"End your response with exactly one of these verdict lines:\n" +
            $"VERDICT: {ReviewVerdict.Approved}\n" +
            $"VERDICT: {ReviewVerdict.ChangesRequested}\n" +
            $"VERDICT: {ReviewVerdict.NeedsHumanReview}";

        var synthesisResult = await Workflow.ExecuteActivityAsync(
            (DelegateToAgentActivity a) => a.DelegateToAgentAsync(
                synthesizer,
                synthesisInstruction,
                $"{workflowId}/synthesis"),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(15),
                HeartbeatTimeout = TimeSpan.FromSeconds(90),
            });

        return new ConsensusReviewOutput(
            FinalVerdict: ParseVerdict(synthesisResult.Text),
            ConsolidatedReasoning: synthesisResult.Text,
            PerAgentVerdicts: agentReviews);
    }

    private static string ParseVerdict(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase))
                continue;

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
