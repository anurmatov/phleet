namespace Fleet.Temporal.Workflows.Fleet;

/// <summary>
/// Per-domain review rubrics for ConsensusReviewWorkflow.
///
/// Each domain returns a checklist that is injected into the base ReviewPrompt so
/// reviewers evaluate against consistent, domain-appropriate criteria instead of
/// improvising their own.
///
/// Callers pass the domain string via <see cref="Fleet.Temporal.Models.ConsensusReviewInput.ReviewDomain"/>.
/// Unrecognised or null values fall back to <see cref="CodeReview"/> (backward compat).
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
    /// Returns the rubric checklist for the given domain.
    /// Falls back to the code-review rubric for null or unrecognised values.
    /// </summary>
    public static string GetRubric(string? domain) =>
        domain?.ToLowerInvariant() switch
        {
            DesignReview => DesignReviewRubric,
            MemoryReview => MemoryReviewRubric,
            _ => CodeReviewRubric, // covers null, code_review, and any unknown value
        };

    private const string CodeReviewRubric =
        """
        Evaluate using this rubric (one point per criterion):
        1. Does the implementation match the spec / issue requirements?
        2. Do all new code paths have appropriate error handling?
        3. Are there any security concerns (injection, auth bypass, data leak)?
        4. Is backward compatibility preserved for existing callers?
        5. Are the edge cases described in the spec covered?
        """;

    private const string DesignReviewRubric =
        """
        Evaluate using this rubric (one point per criterion):
        1. Is the spec complete enough for a developer to implement without ambiguity?
        2. Are architectural trade-offs called out and reasoned about?
        3. Does the design align with existing patterns in the codebase?
        4. Are open questions or risks identified and owned?
        5. Is the acceptance criteria specific and verifiable?
        """;

    private const string MemoryReviewRubric =
        """
        Evaluate using this rubric (one point per criterion):
        1. Is the title short, specific, and searchable (would a future search surface it)?
        2. Does the content avoid duplicating an existing memory (check for overlaps)?
        3. Is the content actionable — does it tell a future agent what to do differently?
        4. Is the scope correct (right project, right type, right agent)?
        5. Are related memory IDs linked where relevant?
        """;
}
