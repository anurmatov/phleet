using System.Text.Json;
using Fleet.Temporal.Engine;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

public class SetAttributeRequiredTests
{
    [Theory]
    [InlineData(true,"unknown_attribute","value")]
    [InlineData(true,"IssueNumber","not-an-integer")]
    [InlineData(false,"unknown_attribute","value")]
    [InlineData(false,"IssueNumber","not-an-integer")]
    public async Task SetAttribute_InvalidNameOrValue_RequiredFailsOptionalSkips(bool required,string name,string value)
    {
        var root=new SequenceStep { Steps=[
            new SetAttributeStep { Required=required, Attributes=new(){[name]=value} },
            new SetVariableStep { Vars=new(){["_result"]="continued"} }] };
        await using var env=await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue="required-"+Guid.NewGuid().ToString("N");
        using var worker=new TemporalWorker(env.Client,new TemporalWorkerOptions(queue)
            .AddWorkflow<UniversalWorkflow>().AddAllActivities(new AcceptanceEngineActivities(root)));
        await worker.ExecuteAsync(async()=>
        {
            var handle=await env.Client.StartWorkflowAsync("RequiredAttributeTest",[],new WorkflowOptions(queue,queue));
            if (required)
            {
                var ex=await Assert.ThrowsAsync<WorkflowFailedException>(()=>handle.GetResultAsync<string>());
                var cause=Assert.IsType<ApplicationFailureException>(ex.InnerException);
                Assert.Equal("RequiredSearchAttribute",cause.ErrorType);
            }
            else Assert.Equal("continued",await handle.GetResultAsync<string>());
        });
    }
}

internal sealed class AcceptanceEngineActivities(StepDefinition root)
{
    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel Definition(string name)=>new(){Name=name,Namespace="default",TaskQueue="test",Root=root};
    [Activity("LoadWorkflowConfig")]
    public JsonElement Config()=>JsonSerializer.SerializeToElement(new{});
}
