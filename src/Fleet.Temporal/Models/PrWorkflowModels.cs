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
///
/// The three positional members are unchanged, so existing callers and already-recorded
/// payloads keep working. The three init properties are additive: an older serialized payload
/// that lacks them deserializes to the defaults below rather than failing.
///
/// The split matters. <see cref="ReviewText"/> is the durable evidence — the reviewer's full
/// raw response, never rewritten or trimmed. <see cref="Summary"/>, <see cref="EvidenceUrl"/>
/// and <see cref="Blockers"/> are separately-parsed slices of it, and they are what downstream
/// consumers are allowed to see. Nothing downstream reads <see cref="ReviewText"/>.
/// </summary>
public sealed record AgentReview(
    /// <summary>Name of the reviewing agent.</summary>
    string AgentName,

    /// <summary>The agent's full review text. Durable detail — never truncated, never rewritten.</summary>
    string ReviewText,

    /// <summary>The parsed verdict from the review.</summary>
    string Verdict)
{
    /// <summary>
    /// Compact decision-relevant summary parsed from the reviewer's <c>SUMMARY:</c> marker,
    /// or a deterministic fallback derived from <see cref="ReviewText"/>. At most 500 UTF-16
    /// code units including any truncation suffix.
    /// </summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Optional URL from the reviewer's <c>EVIDENCE:</c> marker pointing at a full-detail
    /// mirror (e.g. an issue or PR comment). Null when absent, blank, <c>none</c>, or oversized.
    /// The workflow never fetches or validates it.
    /// </summary>
    public string? EvidenceUrl { get; init; }

    /// <summary>
    /// Verbatim required changes, one per <c>BLOCKER:</c> line, in the order parsed.
    ///
    /// Populated only from explicit markers — never inferred from <see cref="Summary"/> or from
    /// unmarked prose, and never written to by the synthesizer. This list is assembled into the
    /// consolidated output by workflow code precisely so that no summarizer behaviour can drop
    /// a requested change.
    /// </summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];
}
