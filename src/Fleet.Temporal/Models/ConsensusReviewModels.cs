using System.Text.Json.Serialization;

namespace Fleet.Temporal.Models;

/// <summary>Input for the reusable ConsensusReviewWorkflow child workflow.</summary>
public sealed record ConsensusReviewInput(
    /// <summary>Human-readable subject of what is being reviewed (e.g. "PR on branch feat/issue-157 in repo your-org/your-repo").</summary>
    string Subject,

    /// <summary>Base prompt given to all reviewer agents. Describes what to review.</summary>
    string ReviewPrompt,

    /// <summary>
    /// Agents to fan out reviews to. Each entry is an agent short name.
    /// Required — ConsensusReviewWorkflow will throw ArgumentException if null or empty.
    /// Accepts both JSON array (["linus","developer"]) and comma-separated string
    /// ("linus,developer") forms via FlexibleStringArrayConverter.
    /// </summary>
    [property: JsonConverter(typeof(FlexibleStringArrayConverter))]
    string[]? ReviewerAgents = null,

    /// <summary>
    /// Optional per-agent perspective instructions appended after the base ReviewPrompt.
    /// Keys are agent names; values are the perspective text (e.g. "Your perspective: architecture, correctness...").
    /// Agents without an entry receive only the base ReviewPrompt.
    /// </summary>
    Dictionary<string, string>? AgentPerspectives = null,

    /// <summary>Agent responsible for synthesizing divergent reviews. Required — ConsensusReviewWorkflow will throw ArgumentException if null or empty.</summary>
    string? Synthesizer = null,

    /// <summary>
    /// How long each reviewer (and the synthesizer) may take, in seconds. Optional: when null or
    /// non-positive the workflow reads the deployment's <c>TemporalBridge:AgentTimeoutSeconds</c>
    /// through an activity instead, so the same number drives both the agent's own budget and the
    /// activity's StartToCloseTimeout.
    ///
    /// Set this only to override a deployment-wide value for one review. Raising it does not make
    /// reviewers faster; it stops a slow-but-working review from being killed mid-turn.
    /// </summary>
    int? AgentBudgetSeconds = null,

    /// <summary>
    /// Optional agents that may not review this artifact: its author and the agent that will
    /// decide on it (#436). When any <see cref="ReviewerAgents"/> entry is listed here (trimmed,
    /// case-insensitive, blank entries ignored), the workflow fails non-retryably with
    /// <c>ReviewerNotIndependent</c> before any reviewer is delegated to.
    /// Accepts a JSON array or a comma-separated string, like <see cref="ReviewerAgents"/>.
    /// </summary>
    [property: JsonConverter(typeof(FlexibleStringArrayConverter))]
    string[]? ExcludedAgents = null,

    /// <summary>
    /// Optional exact artifact under review: a commit SHA or an artifact SHA-256 (#436). When
    /// non-blank it is named in front of every reviewer instruction, and it is the only value
    /// <see cref="ConsensusReviewOutput.AttestedRef"/> can ever carry. When blank, reviewer
    /// instructions are byte-identical to the pre-#436 text.
    /// </summary>
    string? ReviewRef = null);

/// <summary>Output produced by the ConsensusReviewWorkflow.</summary>
public sealed record ConsensusReviewOutput(
    /// <summary>Final consensus verdict: approved, changes_requested, or needs_human_review.</summary>
    string FinalVerdict,

    /// <summary>Consolidated reasoning from the synthesizer (or the shared reasoning on fast-path unanimous approval).</summary>
    string ConsolidatedReasoning,

    /// <summary>Per-agent verdicts from the review round.</summary>
    AgentReview[] PerAgentVerdicts,

    /// <summary>
    /// The input <c>ReviewRef</c>, only when the round attests it (#436): the final verdict is
    /// approved, every reviewer approved (unanimous — a synthesizer-approved split round never
    /// attests), and every reviewer's text has the exact line <c>REVIEWED_REF: &lt;ReviewRef&gt;</c>.
    /// Otherwise empty. Defaulted so outputs recorded before #436 deserialize unchanged.
    /// </summary>
    string AttestedRef = "",

    /// <summary>
    /// True when at least one approving reviewer wrote the line <c>PUBLIC_SCRUB: pass</c> and no
    /// reviewer, whatever its verdict, wrote <c>PUBLIC_SCRUB: fail</c> (#436). Defaulted so outputs
    /// recorded before #436 deserialize unchanged.
    /// </summary>
    bool ScrubAttested = false);
