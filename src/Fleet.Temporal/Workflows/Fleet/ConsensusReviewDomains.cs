namespace Fleet.Temporal.Workflows.Fleet;

/// <summary>
/// Per-domain review rubrics for ConsensusReviewWorkflow.
///
/// Callers pass the domain string via <see cref="Fleet.Temporal.Models.ConsensusReviewInput.ReviewDomain"/>.
/// When ReviewDomain is null or absent the workflow injects nothing, preserving exact backward
/// compatibility with callers that embed their own checklist in ReviewPrompt.
/// Unrecognised non-null values also return null (no injection) rather than silently
/// substituting the code-review rubric.
/// </summary>
public static class ConsensusReviewDomains
{
    /// <summary>Default domain for code PR reviews.</summary>
    public const string CodeReview = "code_review";

    /// <summary>Design spec / GitHub issue review.</summary>
    public const string DesignReview = "design_review";

    /// <summary>Fleet-memory entry review (title, content, scope, duplication).</summary>
    public const string MemoryReview = "memory_review";

    /// <summary>All known domain identifiers.</summary>
    public static readonly IReadOnlySet<string> KnownDomains =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CodeReview,
            DesignReview,
            MemoryReview,
        };

    /// <summary>
    /// Returns the rubric checklist for the given domain, or null when domain is null/unrecognised.
    /// The workflow only injects the rubric when the return value is non-null, so callers that
    /// omit ReviewDomain see no change in reviewer prompt.
    /// </summary>
    public static string? GetRubric(string? domain) =>
        domain?.ToLowerInvariant() switch
        {
            CodeReview => CodeReviewRubric,
            DesignReview => DesignReviewRubric,
            MemoryReview => MemoryReviewRubric,
            _ => null, // null domain or unrecognised value → no rubric injected
        };

    public const string CodeReviewRubric =
        """
        Evaluate using this rubric (one point per criterion):
        1. Does the implementation match the spec / issue requirements?
        2. Do all new code paths have appropriate error handling?
        3. Are there any security concerns (injection, auth bypass, data leak)?
        4. Is backward compatibility preserved for existing callers?
        5. Are the edge cases described in the spec covered?
        """;

    public const string DesignReviewRubric =
        """
        Evaluate using this rubric (one point per criterion):
        1. Is the spec complete enough for a developer to implement without ambiguity?
        2. Are architectural trade-offs called out and reasoned about?
        3. Does the design align with existing patterns in the codebase?
        4. Are open questions or risks identified and owned?
        5. Is the acceptance criteria specific and verifiable?
        """;

    public const string MemoryReviewRubric =
        """
        Evaluate using this rubric (one point per criterion):
        1. Is the title short, specific, and searchable (would a future search surface it)?
        2. Does the content avoid duplicating an existing memory (check for overlaps)?
        3. Is the content actionable — does it tell a future agent what to do differently?
        4. Is the scope correct (right project, right type, right agent)?
        5. Are related memory IDs linked where relevant?
        """;
}
