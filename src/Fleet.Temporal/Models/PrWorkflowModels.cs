namespace Fleet.Temporal.Models;

/// <summary>Constants for verdicts a reviewer returns after reviewing a PR.</summary>
public static class ReviewVerdict
{
    /// <summary>Reviewer approved the PR — proceed to merge.</summary>
    public const string Approved = "approved";

    /// <summary>Reviewer requested changes — implementer must revise and re-submit.</summary>
    public const string ChangesRequested = "changes_requested";

    /// <summary>Reviewer determined human review is required before proceeding.</summary>
    public const string NeedsHumanReview = "needs_human_review";
}

/// <summary>
/// An individual review from one agent in the multi-agent consensus review phase.
/// </summary>
public sealed record AgentReview(
    /// <summary>Name of the reviewing agent.</summary>
    string AgentName,

    /// <summary>The agent's full review text.</summary>
    string ReviewText,

    /// <summary>The parsed verdict from the review.</summary>
    string Verdict);
