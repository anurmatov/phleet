using System.Text.Json;
using Fleet.Shared;

namespace Fleet.Agent.Tests.WorkflowOutcomes;

/// <summary>
/// The local parser refuses, before any ack, the decisions the server would refuse anyway, so a
/// turn never acknowledges an item and then loses its decision to a predictable 422.
/// </summary>
public sealed class WorkflowOutcomeTurnOutputParserTests
{
    private static readonly Guid BatchId = Guid.NewGuid();
    private static readonly Guid ItemId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static string Output(string operation, string[] evidence) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        batchId = BatchId,
        generation = 1,
        ack = new[] { new { id = ItemId, version = 1 } },
        decisions = new[] { new { id = ItemId, version = 1, operation, owner = "agent1", reason = "reviewed", evidenceRefs = evidence } },
    });

    private static (bool Ok, string Error) Parse(string raw)
    {
        var ok = WorkflowOutcomeTurnOutputParser.TryParse(raw, BatchId, 1, [new WorkflowOutcomeItemRef(ItemId, 1)], Now, out _, out var error);
        return (ok, error);
    }

    public static TheoryData<string, string[], string> Refused => new()
    {
        { "retry", [], "decision_evidence_empty" },
        { "finish", ["event:7"], "decision_finish_artifact_missing" },
        { "close", ["outcome:" + ItemId, "run:namespace-a/wf/run"], "decision_close_evidence_missing" },
        { "retry", ["outcome:" + Guid.NewGuid()], "decision_evidence_out_of_scope" },
    };

    [Theory]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    [MemberData(nameof(Refused))]
    public void TryParse_DecisionTheServerWouldRefuse_IsRejectedBeforeAck(string operation, string[] evidence, string expected)
    {
        var (ok, error) = Parse(Output(operation, evidence));

        Assert.False(ok);
        Assert.Equal(expected, error);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    [InlineData("finish", "artifact:owner/repo#pr/7")]
    [InlineData("close", "https://example.com/review")]
    [InlineData("retry", "outcome:SELF")]
    public void TryParse_DecisionWithItsRequiredEvidence_Parses(string operation, string evidence)
    {
        var (ok, error) = Parse(Output(operation, [evidence.Replace("SELF", ItemId.ToString("D"))]));

        Assert.True(ok, error);
    }
}
