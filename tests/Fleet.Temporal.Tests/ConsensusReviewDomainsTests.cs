using Fleet.Temporal.Workflows.Fleet;

namespace Fleet.Temporal.Tests;

/// <summary>
/// Tests for ConsensusReviewDomains — domain selection, null/unknown fallback, and rubric content.
/// </summary>
public class ConsensusReviewDomainsTests
{
    // ── null / unknown domains return null (no injection) ────────────────────────

    [Fact]
    public void GetRubric_NullDomain_ReturnsNull()
    {
        Assert.Null(ConsensusReviewDomains.GetRubric(null));
    }

    [Fact]
    public void GetRubric_EmptyDomain_ReturnsNull()
    {
        Assert.Null(ConsensusReviewDomains.GetRubric(""));
    }

    [Fact]
    public void GetRubric_UnknownDomain_ReturnsNull()
    {
        Assert.Null(ConsensusReviewDomains.GetRubric("trade_proposal"));
        Assert.Null(ConsensusReviewDomains.GetRubric("doc_update"));
    }

    // ── known domains return their rubric ────────────────────────────────────────

    [Fact]
    public void GetRubric_CodeReview_ReturnsCodeReviewRubric()
    {
        var rubric = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.CodeReview);
        Assert.NotNull(rubric);
        Assert.Contains("implementation match the spec", rubric);
        Assert.Contains("error handling", rubric);
        Assert.Contains("security", rubric);
        Assert.Contains("backward compatibility", rubric);
    }

    [Fact]
    public void GetRubric_DesignReview_ReturnsDesignRubric()
    {
        var rubric = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.DesignReview);
        Assert.NotNull(rubric);
        Assert.Contains("implement", rubric, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("architectural", rubric, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("acceptance criteria", rubric, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRubric_MemoryReview_ReturnsMemoryRubric()
    {
        var rubric = ConsensusReviewDomains.GetRubric(ConsensusReviewDomains.MemoryReview);
        Assert.NotNull(rubric);
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

    // ── KnownDomains and GetRubric stay in sync ──────────────────────────────────

    [Fact]
    public void KnownDomains_EachEntryHasNonNullRubric()
    {
        // Every domain in KnownDomains must produce a non-null rubric.
        // If a new domain is added to KnownDomains without a matching switch arm,
        // this test catches the drift.
        foreach (var domain in ConsensusReviewDomains.KnownDomains)
        {
            var rubric = ConsensusReviewDomains.GetRubric(domain);
            Assert.True(rubric is not null,
                $"KnownDomains contains '{domain}' but GetRubric returns null for it. " +
                "Add a matching case to the GetRubric switch.");
        }
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
