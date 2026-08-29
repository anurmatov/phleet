using System.Text;
using Fleet.Shared;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Models;
using Temporalio.Workflows;

namespace Fleet.Temporal.Workflows.Fleet;

/// <summary>
/// Reusable child workflow that orchestrates a single round of multi-agent consensus review.
///
/// Flow:
///   1. Fan out: all ReviewerAgents review in parallel via Workflow.WhenAllAsync.
///      Each agent receives the base ReviewPrompt; if AgentPerspectives contains an entry
///      for the agent, that perspective instruction is appended.
///   2. Parse each response into durable detail plus compact fields.
///   3. Assemble the verbatim blocker list in code, and overflow-check it.
///   4. Fail-closed / unanimous-approval paths return without synthesis.
///   5. Otherwise the synthesizer consolidates the COMPACT reviews into a single verdict.
///
/// This workflow is single-pass. Callers drive the revision loop — they re-invoke this
/// workflow after applying changes.
///
/// ── Why the response is parsed into separate fields ──────────────────────────────────────
///
/// One text blob used to serve three different jobs: durable reviewer evidence, the
/// synthesizer's input, and (via a downstream delegate step) the text behind an activity
/// notification. Reviewers are deliberately thorough, so that single blob sent thousands of
/// redundant words to the synthesizer and onward.
///
/// The response is now split. <c>ReviewText</c> keeps the full raw response in workflow
/// history, unmodified. Everything downstream sees only the parsed compact fields. The
/// contract is an extension of the marker-line convention already used for <c>VERDICT:</c>,
/// so it needs no changes to the agent transport (RelayMessage, DelegateToAgentActivity,
/// AgentTaskResult) — all parsing lives in this file.
///
/// ── Why blockers are assembled here rather than by the synthesizer ───────────────────────
///
/// A summarizer told to compress is, by construction, choosing what survives — including the
/// option of quietly dropping a requested change. The synthesizer is therefore never given the
/// blocker list; workflow code concatenates it verbatim. A guarantee that depends on an LLM's
/// compliance is not a guarantee.
///
/// This workflow knows nothing about GitHub. Evidence-mirroring behaviour lives entirely in
/// caller-supplied ReviewPrompt text.
/// </summary>
[Workflow]
public class ConsensusReviewWorkflow
{
    /// <summary>
    /// Patch marker gating the compact contract.
    ///
    /// This change alters activity input text and activity ordering, so deterministic string
    /// handling alone does not make an in-flight history replayable. Executions started on the
    /// old code keep the legacy branch; new executions take the compact path. Not deprecated in
    /// this change — removing the legacy branch is a later maintenance step, once no retained
    /// or running history can still need it.
    /// </summary>
    internal const string CompactPatchId = "consensus-review-compact-v1";

    /// <summary>Maximum stored Summary length, including any truncation suffix.</summary>
    internal const int SummaryMaxLength = 500;

    /// <summary>Length of the deterministic fallback slice when a reviewer omits SUMMARY:.</summary>
    internal const int AutoSummaryLength = 280;

    /// <summary>Maximum EVIDENCE: payload accepted. Anything longer is dropped, not truncated.</summary>
    internal const int EvidenceMaxLength = 1000;

    /// <summary>Final cap on ConsolidatedReasoning — the transport budget downstream.</summary>
    internal const int ConsolidatedReasoningMaxLength = 2000;

    internal const string AutoSummaryPrefix = "[auto-summary, reviewer did not provide SUMMARY:] ";
    internal const string SummaryTruncationSuffix = "... [truncated, see ReviewText]";
    internal const string ProseTruncationSuffix = "... [truncated]";

    private const string SummaryMarker  = "SUMMARY:";
    private const string EvidenceMarker = "EVIDENCE:";
    private const string BlockerMarker  = "BLOCKER:";
    private const string VerdictMarker  = "VERDICT:";

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

        // Gate BEFORE the reviewer instruction is assembled or any activity is scheduled —
        // the instruction text itself differs between the two paths, so the decision has to
        // precede it.
        var compact = Workflow.Patched(CompactPatchId);

        var instructionSuffix = compact ? BuildReviewEnvelopeInstruction() : BuildLegacyVerdictInstruction();

        // ── Fan out: all agents review in parallel ────────────────────────────────
        var reviewTasks = reviewers
            .Select(agent =>
            {
                var perspective = input.AgentPerspectives?.GetValueOrDefault(agent);
                var instruction = string.IsNullOrWhiteSpace(perspective)
                    ? input.ReviewPrompt + instructionSuffix
                    : input.ReviewPrompt + "\n\n" + perspective + instructionSuffix;

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

        if (!compact)
            return await RunLegacyAsync(input, reviewers, reviewTasks, synthesizer, workflowId);

        var agentReviews = reviewers
            .Zip(reviewTasks, (agent, task) => ParseReview(agent, task.Result.Text))
            .ToArray();

        // ── Blocker assembly, before any decision about synthesis ─────────────────
        // Overflow short-circuits: there is no point paying for a synthesizer round-trip when
        // the outcome is already pinned to needs_human_review.
        var blockerBlock = RenderBlockerBlock(agentReviews);
        if (blockerBlock.Length > ConsolidatedReasoningMaxLength)
        {
            var blockerCount = agentReviews.Sum(r => r.Blockers.Count);
            var reviewerCount = agentReviews.Count(r => r.Blockers.Count > 0);
            return new ConsensusReviewOutput(
                FinalVerdict: ReviewVerdict.NeedsHumanReview,
                ConsolidatedReasoning: BuildOverflowMessage(blockerCount, reviewerCount),
                PerAgentVerdicts: agentReviews);
        }

        // ── Fail closed: any reviewer needing human review skips synthesis ────────
        if (agentReviews.Any(r => r.Verdict == ReviewVerdict.NeedsHumanReview))
        {
            return ComposeOutput(
                agentReviews,
                ReviewVerdict.NeedsHumanReview,
                RenderCompactReviews(agentReviews));
        }

        // ── Fast path: unanimous approval — skip synthesis ───────────────────────
        if (agentReviews.All(r => r.Verdict == ReviewVerdict.Approved))
        {
            return ComposeOutput(
                agentReviews,
                ReviewVerdict.Approved,
                RenderCompactReviews(agentReviews));
        }

        // ── Synthesis: synthesizer consolidates the COMPACT reviews ──────────────
        var synthesisResult = await Workflow.ExecuteActivityAsync(
            (DelegateToAgentActivity a) => a.DelegateToAgentAsync(
                synthesizer,
                BuildSynthesisInstruction(input, agentReviews),
                $"{workflowId}/synthesis"),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(15),
                HeartbeatTimeout = TimeSpan.FromSeconds(90),
            });

        return ComposeOutput(
            agentReviews,
            ParseSynthesizerVerdict(synthesisResult.Text),
            synthesisResult.Text);
    }

    // ── Legacy path ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The pre-patch behaviour, preserved byte-for-byte in activity inputs and ordering so an
    /// in-flight history started on the old code replays without a nondeterminism error.
    ///
    /// The new additive AgentReview fields are populated with compatibility values only —
    /// nothing here parses markers.
    /// </summary>
    private static async Task<ConsensusReviewOutput> RunLegacyAsync(
        ConsensusReviewInput input,
        string[] reviewers,
        Task<AgentTaskResult>[] reviewTasks,
        string synthesizer,
        string workflowId)
    {
        var agentReviews = reviewers
            .Zip(reviewTasks, (agent, task) =>
                new AgentReview(agent, task.Result.Text, ParseVerdict(task.Result.Text))
                {
                    Summary = task.Result.Text,
                    EvidenceUrl = null,
                    Blockers = [],
                })
            .ToArray();

        var humanFlagged = agentReviews.FirstOrDefault(r => r.Verdict == ReviewVerdict.NeedsHumanReview);
        if (humanFlagged is not null)
        {
            return new ConsensusReviewOutput(
                FinalVerdict: ReviewVerdict.NeedsHumanReview,
                ConsolidatedReasoning: $"{humanFlagged.AgentName} flagged this for human review:\n{humanFlagged.ReviewText}",
                PerAgentVerdicts: agentReviews);
        }

        if (agentReviews.All(r => r.Verdict == ReviewVerdict.Approved))
        {
            var combined = string.Join("\n\n---\n\n", agentReviews.Select(r =>
                $"## {r.AgentName}\n{r.ReviewText}"));
            return new ConsensusReviewOutput(
                FinalVerdict: ReviewVerdict.Approved,
                ConsolidatedReasoning: combined,
                PerAgentVerdicts: agentReviews);
        }

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

    private static string BuildLegacyVerdictInstruction() =>
        $"\n\nEnd your response with exactly one of these verdict lines:\n" +
        $"VERDICT: {ReviewVerdict.Approved}\n" +
        $"VERDICT: {ReviewVerdict.ChangesRequested}\n" +
        $"VERDICT: {ReviewVerdict.NeedsHumanReview}";

    // ── Reviewer contract ─────────────────────────────────────────────────────────

    /// <summary>
    /// The four-marker envelope, appended centrally so every caller — generic or
    /// GitHub-backed — receives it exactly once. Caller prompts must not repeat it.
    /// </summary>
    internal static string BuildReviewEnvelopeInstruction() =>
        "\n\nEnd your response with these marker lines (any order). Each is matched as a whole " +
        "trimmed line, case-insensitively:\n" +
        "SUMMARY: <one to three short sentences, decision-relevant only>\n" +
        "EVIDENCE: <url, or the literal word none>\n" +
        "BLOCKER: <one specific required change; repeat this line once per distinct finding, " +
        "or write BLOCKER: none>\n" +
        $"VERDICT: {ReviewVerdict.Approved} | {ReviewVerdict.ChangesRequested} | {ReviewVerdict.NeedsHumanReview}\n\n" +
        "Rules:\n" +
        "- Do not begin any other line of your detailed review with SUMMARY:, EVIDENCE:, " +
        "BLOCKER: or VERDICT: — use ordinary headings or indentation instead.\n" +
        "- One BLOCKER: line per distinct required change. A BLOCKER: payload is that single " +
        "line only; if a finding needs more explanation, point to it briefly here and leave the " +
        "detail in the body of your review.\n" +
        $"- {ReviewVerdict.ChangesRequested} with no BLOCKER: line is treated as unusable and " +
        $"escalates to {ReviewVerdict.NeedsHumanReview}, because a requested change nobody named " +
        "cannot be acted on.\n" +
        $"- {ReviewVerdict.Approved} together with a BLOCKER: line is contradictory and also " +
        $"escalates to {ReviewVerdict.NeedsHumanReview}.\n" +
        "- Your full response is retained in durable workflow history. Only the marker payloads " +
        "are forwarded onward, so put anything that must reach the implementer on a marker line.";

    // ── Parsing ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits one raw reviewer response into durable text plus the compact fields.
    ///
    /// Deterministic and pure: no clock, no randomness, no I/O — safe under Temporal replay.
    /// </summary>
    internal static AgentReview ParseReview(string agentName, string reviewText)
    {
        var text = reviewText ?? "";
        var lines = text.Split('\n');

        // A marker line is recognised by its prefix alone, regardless of whether its payload
        // later validates. That keeps marker boundaries deterministic: a blank or malformed
        // SUMMARY: still terminates the preceding capture rather than being absorbed into it.
        static bool IsMarkerLine(string trimmed) =>
            trimmed.StartsWith(SummaryMarker, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(EvidenceMarker, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(BlockerMarker, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(VerdictMarker, StringComparison.OrdinalIgnoreCase);

        string? summaryPayload = null;   // last SUMMARY: wins
        string? evidencePayload = null;  // last EVIDENCE: wins
        var blockers = new List<string>();
        var verdictValues = new List<string>();
        var sawVerdictMarker = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith(SummaryMarker, StringComparison.OrdinalIgnoreCase))
            {
                // SUMMARY: captures through to the next marker line or end of text, so a
                // reviewer may wrap it across several lines.
                var sb = new StringBuilder(trimmed[SummaryMarker.Length..].Trim());
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (IsMarkerLine(lines[j].Trim())) break;
                    sb.Append('\n').Append(lines[j]);
                }
                summaryPayload = sb.ToString().Trim();
            }
            else if (trimmed.StartsWith(EvidenceMarker, StringComparison.OrdinalIgnoreCase))
            {
                evidencePayload = trimmed[EvidenceMarker.Length..].Trim();
            }
            else if (trimmed.StartsWith(BlockerMarker, StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately single-line, unlike SUMMARY:. A wrapped explanation must not be
                // absorbed into one enormous blocker, and must not swallow the marker line
                // that follows it.
                var payload = trimmed[BlockerMarker.Length..].Trim();
                if (payload.Length > 0 && !payload.Equals("none", StringComparison.OrdinalIgnoreCase))
                    blockers.Add(payload);
            }
            else if (trimmed.StartsWith(VerdictMarker, StringComparison.OrdinalIgnoreCase))
            {
                sawVerdictMarker = true;
                var value = trimmed[VerdictMarker.Length..].Trim().ToLowerInvariant().Replace(' ', '_');
                if (value is ReviewVerdict.Approved
                          or ReviewVerdict.ChangesRequested
                          or ReviewVerdict.NeedsHumanReview)
                {
                    verdictValues.Add(value);
                }
            }
        }

        var distinctVerdicts = verdictValues.Distinct().ToArray();

        // Unparseable: no valid verdict at all, or two that disagree. Never defaults to
        // approved — a review we could not read is not an approval.
        if (distinctVerdicts.Length != 1)
        {
            _ = sawVerdictMarker;   // recognised the marker but could not use it; same outcome
            return new AgentReview(agentName, text, ReviewVerdict.NeedsHumanReview)
            {
                Summary = $"[unparseable verdict from {agentName}; see ReviewText]",
                EvidenceUrl = null,
                Blockers = blockers,
            };
        }

        var verdict = distinctVerdicts[0];

        // changes_requested with nothing named is not actionable.
        if (verdict == ReviewVerdict.ChangesRequested && blockers.Count == 0)
            verdict = ReviewVerdict.NeedsHumanReview;

        // approved while still naming a required change is a contradiction, not a nuance.
        // The blocker is still preserved and still reaches the output.
        if (verdict == ReviewVerdict.Approved && blockers.Count > 0)
            verdict = ReviewVerdict.NeedsHumanReview;

        return new AgentReview(agentName, text, verdict)
        {
            Summary = BuildSummary(summaryPayload, text),
            EvidenceUrl = NormalizeEvidence(evidencePayload),
            Blockers = blockers,
        };
    }

    /// <summary>
    /// Summary with the documented fallback and truncation. Pure string work.
    /// </summary>
    internal static string BuildSummary(string? payload, string reviewText)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            // Deterministic fallback rather than escalating: most prompts in the wild do not
            // ask for SUMMARY: yet, and escalating every one during rollout would make this
            // un-shippable incrementally.
            var slice = reviewText[..TextTruncation.SafeCutIndex(reviewText, AutoSummaryLength)];
            return AutoSummaryPrefix + slice;
        }

        if (payload.Length <= SummaryMaxLength) return payload;

        // Oversized-but-present is a formatting violation, not evidence of a broken review —
        // truncate, do not escalate.
        var budget = SummaryMaxLength - SummaryTruncationSuffix.Length;
        return payload[..TextTruncation.SafeCutIndex(payload, budget)] + SummaryTruncationSuffix;
    }

    /// <summary>
    /// Absent, blank, <c>none</c> and oversized payloads all map to null. Oversized is dropped
    /// rather than cut, because half a URL is worse than no URL — it looks usable and isn't.
    /// </summary>
    internal static string? NormalizeEvidence(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        var trimmed = payload.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (trimmed.Length > EvidenceMaxLength) return null;

        return trimmed;
    }

    /// <summary>
    /// Synthesizer verdict: same fail-closed rule, but the synthesizer emits no other markers.
    /// </summary>
    internal static string ParseSynthesizerVerdict(string text)
    {
        var values = new List<string>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(VerdictMarker, StringComparison.OrdinalIgnoreCase)) continue;

            var value = trimmed[VerdictMarker.Length..].Trim().ToLowerInvariant().Replace(' ', '_');
            if (value is ReviewVerdict.Approved
                      or ReviewVerdict.ChangesRequested
                      or ReviewVerdict.NeedsHumanReview)
            {
                values.Add(value);
            }
        }

        var distinct = values.Distinct().ToArray();
        return distinct.Length == 1 ? distinct[0] : ReviewVerdict.NeedsHumanReview;
    }

    /// <summary>Legacy verdict parsing — unchanged, still used by the pre-patch branch.</summary>
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

    // ── Compact rendering ─────────────────────────────────────────────────────────

    /// <summary>One reviewer, compact: name, verdict, summary, and evidence URL if present.</summary>
    internal static string RenderCompactReview(AgentReview review)
    {
        var line = $"{review.AgentName} [{review.Verdict}]: {review.Summary}";
        return review.EvidenceUrl is null ? line : line + $"\nevidence: {review.EvidenceUrl}";
    }

    /// <summary>All reviewers, in reviewer order.</summary>
    internal static string RenderCompactReviews(IEnumerable<AgentReview> reviews) =>
        string.Join("\n\n", reviews.Select(RenderCompactReview));

    /// <summary>
    /// The synthesis prompt. Built from the subject, the reviewer count and the COMPACT
    /// reviews only — never ReviewText, and never the blocker list.
    /// </summary>
    internal static string BuildSynthesisInstruction(ConsensusReviewInput input, AgentReview[] reviews) =>
        $"You have received {reviews.Length} independent reviews of: {input.Subject}\n\n" +
        $"## Independent reviews (compact)\n\n{RenderCompactReviews(reviews)}\n\n" +
        "Synthesize these into a single actionable verdict. Give the decision, the smallest " +
        "actionable blocking issue(s), and the verdict — no validation-checklist replay, no " +
        "resolved findings, and no low-severity notes unless they change the decision. " +
        "If all substantive concerns are minor or cosmetic, you may approve.\n" +
        "End your response with exactly one of these verdict lines:\n" +
        $"VERDICT: {ReviewVerdict.Approved}\n" +
        $"VERDICT: {ReviewVerdict.ChangesRequested}\n" +
        $"VERDICT: {ReviewVerdict.NeedsHumanReview}";

    // ── Output assembly ───────────────────────────────────────────────────────────

    /// <summary>Every parsed blocker, in reviewer order, each reviewer's own in parsed order.</summary>
    internal static string RenderBlockerBlock(IEnumerable<AgentReview> reviews) =>
        string.Join("\n", reviews.SelectMany(r => r.Blockers.Select(b => $"- {r.AgentName}: {b}")));

    internal static string BuildOverflowMessage(int blockerCount, int reviewerCount) =>
        $"[blocker overflow: {blockerCount} blockers from {reviewerCount} reviewers exceed the " +
        $"{ConsolidatedReasoningMaxLength}-character limit; see PerAgentVerdicts[].Blockers]";

    /// <summary>
    /// Combines the candidate prose with the verbatim blocker list and applies both guarantees:
    /// the blockers are never lost, and the verdict label never contradicts them.
    ///
    /// The trigger is "any reviewer has blockers", NOT "the verdict says changes_requested".
    /// Keying on the label would make this depend on verdict aggregation the caller controls —
    /// a synthesizer that resolved a lone dissenter into an overall approval would then cause
    /// that reviewer's named blockers to never be assembled at all.
    /// </summary>
    internal static ConsensusReviewOutput ComposeOutput(
        AgentReview[] reviews, string candidateVerdict, string compactProse)
    {
        var blockerBlock = RenderBlockerBlock(reviews);
        var hasBlockers = blockerBlock.Length > 0;

        // An approval still carrying a named required change is a contradiction, not a nuance.
        var finalVerdict = hasBlockers && candidateVerdict == ReviewVerdict.Approved
            ? ReviewVerdict.ChangesRequested
            : candidateVerdict;

        string reasoning;
        if (!hasBlockers)
        {
            reasoning = Truncate(compactProse, ConsolidatedReasoningMaxLength, ProseTruncationSuffix);
        }
        else if (blockerBlock.Length > ConsolidatedReasoningMaxLength)
        {
            // Never a partial blocker list: truncation can cut a required change in half or
            // drop it silently, which is worse than surfacing the overflow.
            var blockerCount = reviews.Sum(r => r.Blockers.Count);
            var reviewerCount = reviews.Count(r => r.Blockers.Count > 0);
            return new ConsensusReviewOutput(
                FinalVerdict: ReviewVerdict.NeedsHumanReview,
                ConsolidatedReasoning: BuildOverflowMessage(blockerCount, reviewerCount),
                PerAgentVerdicts: reviews);
        }
        else
        {
            // The prose is appended only if it fits in full — the blocker list always wins the
            // available space, and a half-sentence of context is worth nothing anyway.
            var remaining = ConsolidatedReasoningMaxLength - blockerBlock.Length - 2;
            reasoning = !string.IsNullOrEmpty(compactProse) && compactProse.Length <= remaining
                ? blockerBlock + "\n\n" + compactProse
                : blockerBlock;
        }

        return new ConsensusReviewOutput(finalVerdict, reasoning, reviews);
    }

    private static string Truncate(string text, int max, string suffix)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
        var budget = max - suffix.Length;
        return text[..TextTruncation.SafeCutIndex(text, budget)] + suffix;
    }
}
