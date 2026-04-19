using Fleet.Temporal.Workflows.Fleet;

namespace Fleet.Temporal.Tests;

/// <summary>
/// Tests for ConsensusReviewDomains — domain selection, fallback, and rubric content.
/// </summary>
public class ConsensusReviewDomainsTests
{
    // ── domain selection ─────────────────────────────────────────────────────────

    [Fact]
    public void GetRubric_NullDomain_FallsBackToCodeReview()
    {
        var rubric = ConsensusReviewDomains.GetRubric(null);
        Assert.Contains("implementation match the spec", rubric);
    }

    [Fact]
    public void GetRubric_EmptyDomain_FallsBackToCodeReview()
    {
        var rubric = ConsensusReviewDomains.GetRubric("");
        Assert.Contains("implementation match the spec", rubric);
    }

    [Fact]
    public void GetRubric_UnknownDomain_FallsBackToCodeReview()
    {
        var rubric = ConsensusReviewDomains.GetRubric("trade_proposal");
        Assert.Contains("implementation match the spec", rubric);
    }

    [Fact]
    public void GetRubric_CodeReview_ReturnsCodeReviewRubric()
    {
        var rubric = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.CodeReview);
        Assert.Contains("implementation match the spec", rubric);
        Assert.Contains("error handling", rubric);
        Assert.Contains("security", rubric);
        Assert.Contains("backward compatibility", rubric);
    }

    [Fact]
    public void GetRubric_DesignReview_ReturnsDesignRubric()
    {
        var rubric = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.DesignReview);
        Assert.Contains("implement", rubric, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("architectural", rubric, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("acceptance criteria", rubric, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRubric_MemoryReview_ReturnsMemoryRubric()
    {
        var rubric = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.MemoryReview);
        Assert.Contains("title", rubric, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicat", rubric, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scope", rubric, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CODE_REVIEW")]
    [InlineData("Code_Review")]
    [InlineData("DESIGN_REVIEW")]
    [InlineData("MEMORY_REVIEW")]
    public void GetRubric_CaseInsensitive(string domain)
    {
        var rubricLower = ConsensusReviewDomains.GetRubric(domain.ToLowerInvariant());
        var rubricOriginal = ConsensusReviewDomains.GetRubric(domain);
        Assert.Equal(rubricLower, rubricOriginal);
    }

    // ── known domains set ────────────────────────────────────────────────────────

    [Fact]
    public void KnownDomains_ContainsAllThreeInitialDomains()
    {
        Assert.Contains(ConsensusReviewDomains.CodeReview, ConsensusReviewDomains.KnownDomains);
        Assert.Contains(ConsensusReviewDomains.DesignReview, ConsensusReviewDomains.KnownDomains);
        Assert.Contains(ConsensusReviewDomains.MemoryReview, ConsensusReviewDomains.KnownDomains);
    }

    [Fact]
    public void KnownDomains_IsCaseInsensitive()
    {
        Assert.Contains("CODE_REVIEW", ConsensusReviewDomains.KnownDomains);
        Assert.Contains("Design_Review", ConsensusReviewDomains.KnownDomains);
    }

    // ── domain rubrics are distinct ──────────────────────────────────────────────

    [Fact]
    public void AllDomainRubrics_AreDistinct()
    {
        var code = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.CodeReview);
        var design = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.DesignReview);
        var memory = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.MemoryReview);

        Assert.NotEqual(code, design);
        Assert.NotEqual(code, memory);
        Assert.NotEqual(design, memory);
    }
}
