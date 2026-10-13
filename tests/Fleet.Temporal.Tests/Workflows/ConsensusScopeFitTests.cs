using System.Text.Json;
using Fleet.Temporal.Models;
using Fleet.Temporal.Workflows.Fleet;

namespace Fleet.Temporal.Tests.Workflows;

public sealed class ConsensusScopeFitTests
{
    private static readonly string Ref = new('a', 40);
    private static AgentReview Review(string fit, string verdict = "approved", string? reviewedRef = null) =>
        ConsensusReviewWorkflow.ParseReview("agent1", $"SUMMARY: checked\nBLOCKER: {(verdict == "approved" ? "none" : "revision required")}\nVERDICT: {verdict}\nREVIEWED_REF: {reviewedRef ?? Ref}\n{fit}");

    [Theory]
    [InlineData("SCOPE_FIT: pass", "approved", true)]
    [InlineData("  SCOPE_FIT: pass  \r", "approved", true)]
    [InlineData("", "approved", false)]
    [InlineData("SCOPE_FIT: n/a", "approved", false)]
    [InlineData("SCOPE_FIT: fail", "approved", false)]
    [InlineData("SCOPE_FIT: pass\nSCOPE_FIT: fail", "approved", false)]
    [InlineData("scope_fit: pass", "approved", false)]
    [InlineData("SCOPE_FIT: PASS", "approved", false)]
    [InlineData("SCOPE_FIT: pass extra", "approved", false)]
    [InlineData("SCOPE_FIT: pass", "changes_requested", false)]
    public void ScopeFit_RequiresAnExactPassFromEveryIndependentlyAttestedReviewer(string fit, string verdict, bool expected)
    {
        var reviews = new[] { Review("SCOPE_FIT: pass"), Review(fit, verdict) };
        var output = ConsensusReviewWorkflow.ApplyAttestation(new("approved", "review", reviews), Ref);
        Assert.Equal(expected, output.ScopeFitAttested);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("different-ref")]
    public void ScopeFit_WithoutExactArtifactAttestationIsFalse(string? expectedRef)
    {
        var output = ConsensusReviewWorkflow.ApplyAttestation(new("approved", "review", [Review("SCOPE_FIT: pass")]), expectedRef);
        Assert.False(output.ScopeFitAttested);
        Assert.Equal("", output.AttestedRef);
    }

    [Fact]
    public void ScopeFit_EmptyRoundCannotAttestAndOldPayloadDefaultsFalse()
    {
        var output = ConsensusReviewWorkflow.ApplyAttestation(new("approved", "review", []), Ref);
        Assert.False(output.ScopeFitAttested);
        var old = JsonSerializer.Deserialize<ConsensusReviewOutput>("""{"FinalVerdict":"approved","ConsolidatedReasoning":"review","PerAgentVerdicts":[]} """)!;
        Assert.False(old.ScopeFitAttested);
    }
}

[Collection("consensus-workflow")]
public sealed class ConsensusScopeFitWorkflowTests
{
    [Theory]
    [InlineData("SCOPE_FIT: pass", true)]
    [InlineData("SCOPE_FIT: n/a", false)]
    [InlineData("", false)]
    public async Task ScopeFit_ActualIndependentRoundDerivesOutputAndReplays(string fit, bool expected)
    {
        var artifact = new string('a', 40);
        await using var env = await Temporalio.Testing.WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = "scope-fit-" + Guid.NewGuid().ToString("N");
        var activity = Temporalio.Activities.ActivityDefinition.Create("DelegateToAgent", typeof(AgentTaskResult),
            [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int), typeof(int)], 3, args =>
            {
                Assert.Contains((string)args[0]!, new[] { "agent1", "agent2" });
                return new AgentTaskResult($"SUMMARY: checked\nBLOCKER: none\nVERDICT: approved\nREVIEWED_REF: {artifact}\n{fit}", "completed");
            });
        using var worker = new Temporalio.Worker.TemporalWorker(env.Client, new Temporalio.Worker.TemporalWorkerOptions(queue)
            .AddWorkflow<ConsensusReviewWorkflow>().AddActivity(activity));
        await worker.ExecuteAsync(async () =>
        {
            var input = new ConsensusReviewInput("scope", "review", ["agent1", "agent2"], Synthesizer: "agent3",
                AgentBudgetSeconds: 60, ExcludedAgents: ["agent3", "agent4"], ReviewRef: artifact);
            var handle = await env.Client.StartWorkflowAsync((ConsensusReviewWorkflow w) => w.RunAsync(input), new Temporalio.Client.WorkflowOptions(queue, queue));
            var output = await handle.GetResultAsync();
            Assert.Equal(artifact, output.AttestedRef);
            Assert.Equal(expected, output.ScopeFitAttested);
        });
        var history = await env.Client.GetWorkflowHandle(queue).FetchHistoryAsync();
        var replayer = new Temporalio.Worker.WorkflowReplayer(new Temporalio.Worker.WorkflowReplayerOptions().AddWorkflow<ConsensusReviewWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }
}
