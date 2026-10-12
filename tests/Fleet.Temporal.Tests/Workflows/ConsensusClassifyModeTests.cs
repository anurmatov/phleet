using Fleet.Temporal.Models;
using Fleet.Temporal.Workflows.Fleet;

namespace Fleet.Temporal.Tests.Workflows;

public class ConsensusClassifyModeTests
{
    private static AgentReview Review(string label,string verdict,string blockers="none")
        => ConsensusReviewWorkflow.ParseReview("agent1",$"SUMMARY: LABEL: {label}\nEVIDENCE: none\nBLOCKER: {blockers}\nVERDICT: {verdict}");

    [Theory]
    [InlineData("harness","approved","none")]
    [InlineData("environment","approved","none")]
    [InlineData("product_defect","changes_requested","receipt sha: expected x actual y in code")]
    public void Classify_UnanimousConsistentLabel_IsCodeDecided(string label,string verdict,string blockers)
    {
        var reviews=new[]{Review(label,verdict,blockers),Review(label,verdict,blockers)};
        var output=ConsensusReviewWorkflow.Classify(reviews);
        Assert.Equal(label,output.ClassificationLabel);Assert.Equal(verdict,output.FinalVerdict);
    }
    [Theory]
    [InlineData("harness","environment","approved")]
    [InlineData("harness","harness","changes_requested")]
    [InlineData("invalid","invalid","approved")]
    [InlineData("product_defect","product_defect","approved")]
    public void Classify_MixedOrContradictory_NoLabelAndNeedsHuman(string left,string right,string verdict)
    {
        var output=ConsensusReviewWorkflow.Classify([Review(left,verdict),Review(right,verdict)]);
        Assert.Null(output.ClassificationLabel);Assert.Equal("needs_human_review",output.FinalVerdict);
    }
    [Fact]
    public void Classify_LabelOutsideSummary_DoesNotCount()
    {
        var review=ConsensusReviewWorkflow.ParseReview("agent1","LABEL: harness\nSUMMARY: text\nBLOCKER: none\nVERDICT: approved");
        Assert.Null(ConsensusReviewWorkflow.Classify([review]).ClassificationLabel);
    }
    [Fact]
    public void Mode_Absent_IsReviewWithNoClassification()
    {
        var input=new ConsensusReviewInput("subject","prompt");
        Assert.Equal("review",input.Mode);
        Assert.Null(new ConsensusReviewOutput("approved","reason",[]).ClassificationLabel);
    }
}

[Collection("consensus-workflow")]
public class ConsensusClassifyWorkflowTests
{
    [Theory]
    [InlineData("harness","harness","harness","approved")]
    [InlineData("harness","environment",null,"needs_human_review")]
    [InlineData("failure","harness",null,"needs_human_review")]
    [InlineData("product_defect","product_defect","product_defect","changes_requested")]
    public async Task Classify_ActualWorkflow_NeverDelegatesSynthesizerAndReplays(string first,string second,string? expected,string verdict)
    {
        await using var env=await Temporalio.Testing.WorkflowEnvironment.StartTimeSkippingAsync();
        var queue="classify-"+Guid.NewGuid().ToString("N");
        var calls=new System.Collections.Concurrent.ConcurrentQueue<string>();
        var stub=Temporalio.Activities.ActivityDefinition.Create("DelegateToAgent",typeof(AgentTaskResult),
            [typeof(string),typeof(string),typeof(string),typeof(bool),typeof(int),typeof(int)],3,args=>
            {
                var agent=(string)args[0]!;
                calls.Enqueue(agent);
                if (agent=="agent1" && first=="failure")
                    throw new Temporalio.Exceptions.ApplicationFailureException("test reviewer failure", nonRetryable:true);
                Assert.Contains("Classification:",(string)args[1]!);
                var label=agent=="agent1" ? first : second;
                var v=label=="product_defect" ? "changes_requested" : "approved";
                var blocker=label=="product_defect" ? "receipt sha: expected x actual y in path" : "none";
                return new AgentTaskResult($"SUMMARY: LABEL: {label}\nBLOCKER: {blocker}\nVERDICT: {v}","completed");
            });
        using var worker=new Temporalio.Worker.TemporalWorker(env.Client,new Temporalio.Worker.TemporalWorkerOptions(queue)
            .AddWorkflow<ConsensusReviewWorkflow>().AddActivity(stub));
        await worker.ExecuteAsync(async()=>
        {
            var input=new ConsensusReviewInput("attempt","classify receipts",["agent1","agent2"],Synthesizer:"agent3",AgentBudgetSeconds:60,Mode:"classify");
            var handle=await env.Client.StartWorkflowAsync((ConsensusReviewWorkflow w)=>w.RunAsync(input),
                new Temporalio.Client.WorkflowOptions(queue,queue));
            var output=await handle.GetResultAsync();
            Assert.Equal(expected,output.ClassificationLabel);Assert.Equal(verdict,output.FinalVerdict);
        });
        Assert.Equal(new[]{"agent1","agent2"},calls.Order().ToArray());
        var history=await env.Client.GetWorkflowHandle(queue).FetchHistoryAsync();
        var replayer=new Temporalio.Worker.WorkflowReplayer(new Temporalio.Worker.WorkflowReplayerOptions().AddWorkflow<ConsensusReviewWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }
}
