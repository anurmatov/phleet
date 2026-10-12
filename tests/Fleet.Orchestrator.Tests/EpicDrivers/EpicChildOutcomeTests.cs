using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.EpicDrivers;

public class EpicChildOutcomeTests
{
    [Theory]
    [InlineData("{\"IssueNumber\":42}")]
    [InlineData("\"{\\\"IssueNumber\\\":42}\"")]
    [InlineData("\"  {\\\"IssueNumber\\\":42}  \"")]
    public void Parse_ExactApprovedPayload_MatchesTheRunIssue(string json)
    {
        var result = EpicChildOutcome.Parse("UweDesignWorkflow", "Completed", "json/plain", json, 42, null);
        Assert.Equal("approved", result.Outcome);
        Assert.Equal(42, result.Issue);
        Assert.Equal("approved", EpicChildOutcome.Parse("UweDesignWorkflow", "Completed", "json/plain", json, 42, "42").Outcome);
        Assert.Equal("invalid_result", EpicChildOutcome.Parse("UweDesignWorkflow", "Completed", "json/plain", json, 43, null).Outcome);
        Assert.Equal("invalid_result", EpicChildOutcome.Parse("UweDesignWorkflow", "Completed", "json/plain", json, 42, "43").Outcome);
    }

    [Theory]
    [InlineData("\"APPROVED:42\"")]
    [InlineData("\"REJECTED:x\"")]
    [InlineData("\"ERROR:consensus_exhausted\"")]
    [InlineData("\"[status: idle]\"")]
    [InlineData("\"```json\\n{\\\"IssueNumber\\\":42}\\n```\"")]
    [InlineData("{\"IssueNumber\":\"42\"}")]
    [InlineData("{\"IssueNumber\":0}")]
    [InlineData("{\"IssueNumber\":42.0}")]
    [InlineData("{\"IssueNumber\":42,\"x\":1}")]
    [InlineData("{\"issueNumber\":42}")]
    [InlineData("{\"IssueNumber\":42,\"IssueNumber\":42}")]
    public void Parse_UnexpectedPayload_NeverApproves(string json) =>
        Assert.Equal("invalid_result", EpicChildOutcome.Parse("UweDesignWorkflow", "Completed", "json/plain", json, 42, null).Outcome);

    [Fact]
    public void Parse_MissingAttributeOrUnsupportedEncoding_FailsClosed()
    {
        Assert.Equal("invalid_result", EpicChildOutcome.Parse("UweDesignWorkflow", "Completed", "json/plain", "{\"IssueNumber\":42}", null, null).Outcome);
        Assert.Equal("not_approved", EpicChildOutcome.Parse("UweDesignWorkflow", "Completed", "binary/null", null, 42, null).Outcome);
        Assert.Equal("unknown", EpicChildOutcome.Parse("UweDesignWorkflow", "Completed", "binary/protobuf", null, 42, null).Outcome);
        Assert.Equal("no_result", EpicChildOutcome.Parse("UweDesignToPrWorkflow", "Completed", null, null, null, null).Outcome);
        Assert.Equal("running", EpicChildOutcome.Parse("UweDesignWorkflow", "Running", null, null, 42, null).Outcome);
        foreach (var status in new[] { "Failed", "Terminated", "Canceled", "TimedOut" })
            Assert.Equal("failed", EpicChildOutcome.Parse("UweDesignWorkflow", status, null, null, 42, null).Outcome);
    }
}
