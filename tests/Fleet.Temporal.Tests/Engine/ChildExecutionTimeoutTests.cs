using System.Text.Json;
using Fleet.Temporal.Engine;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Engine;

public class ChildExecutionTimeoutTests
{
    [Fact]
    public void Timeout_OnFireAndForget_IsRejectedAtLoad()
    {
        var step=new FireAndForgetStep{WorkflowType="StuckAcceptanceChild",ExecutionTimeoutMinutes=60};
        Assert.Throws<InvalidOperationException>(()=>WorkflowDefinitionValidator.Validate(step,"parent"));
    }
    [Fact]
    public void Timeout_Absent_FireAndForgetIsUnchanged()
        => WorkflowDefinitionValidator.Validate(new FireAndForgetStep{WorkflowType="child"},"parent");

    [Theory]
    [InlineData("\"60\"")]
    [InlineData("60.5")]
    [InlineData("\"{{input.MaxDuration}}\"")]
    public void Timeout_MustBeLiteralInteger(string number)
        => Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<StepDefinition>(
            "{\"type\":\"child_workflow\",\"workflowType\":\"child\",\"executionTimeoutMinutes\":"+number+"}",new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Timeout_Nonpositive_IsRejected(int minutes)
        => Assert.Throws<InvalidOperationException>(()=>WorkflowDefinitionValidator.Validate(new ChildWorkflowStep{WorkflowType="child",ExecutionTimeoutMinutes=minutes},"parent"));

    [Theory]
    [InlineData(60)]
    [InlineData(null)]
    public async Task Timeout_RealChildCommand_BindsDeadlineOrPreservesAbsence(int? minutes)
    {
        await using var env=await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue="child-deadline-"+Guid.NewGuid().ToString("N");
        var root=new ChildWorkflowStep {WorkflowType="StuckAcceptanceChild",ExecutionTimeoutMinutes=minutes};
        using var worker=new TemporalWorker(env.Client,new TemporalWorkerOptions(queue)
            .AddWorkflow<UniversalWorkflow>().AddWorkflow<StuckAcceptanceChild>()
            .AddAllActivities(new AcceptanceEngineActivities(root)));
        await worker.ExecuteAsync(async()=>
        {
            var handle=await env.Client.StartWorkflowAsync("ChildDeadlineParent",[],new WorkflowOptions(queue,queue));
            if (minutes is not null)
            {
                var ex=await Assert.ThrowsAsync<WorkflowFailedException>(()=>handle.GetResultAsync());
                var child=Assert.IsType<ChildWorkflowFailureException>(ex.InnerException);
                Assert.IsType<TimeoutFailureException>(child.InnerException);
                var history=await handle.FetchHistoryAsync();
                var started=Assert.Single(history.Events.Where(e=>e.EventType==EventType.StartChildWorkflowExecutionInitiated));
                Assert.Equal(TimeSpan.FromMinutes(60),started.StartChildWorkflowExecutionInitiatedEventAttributes.WorkflowExecutionTimeout.ToTimeSpan());
                var timed=Assert.Single(history.Events.Where(e=>e.EventType==EventType.ChildWorkflowExecutionTimedOut));
                // The command carries exactly 60 minutes; server event recording adds milliseconds.
                Assert.InRange(timed.EventTime.ToDateTime()-started.EventTime.ToDateTime(),
                    TimeSpan.FromMinutes(60),TimeSpan.FromMinutes(60)+TimeSpan.FromSeconds(1));
            }
            else
            {
                // Inspect the actual initiated command before closing our owned run.
                await handle.SignalAsync("probe",[]);
                var history=await handle.FetchHistoryAsync();
                for(var i=0;i<100 && !history.Events.Any(e=>e.EventType==EventType.StartChildWorkflowExecutionInitiated);i++)
                { await Task.Delay(10); history=await handle.FetchHistoryAsync(); }
                var started=Assert.Single(history.Events.Where(e=>e.EventType==EventType.StartChildWorkflowExecutionInitiated));
                Assert.True(started.StartChildWorkflowExecutionInitiatedEventAttributes.WorkflowExecutionTimeout is null
                    || started.StartChildWorkflowExecutionInitiatedEventAttributes.WorkflowExecutionTimeout.ToTimeSpan()==TimeSpan.Zero);
                await handle.TerminateAsync("owned test cleanup");
            }
        });
    }
}

[Workflow("StuckAcceptanceChild")]
public class StuckAcceptanceChild
{
    [WorkflowRun]
    public async Task RunAsync()=>await Workflow.WaitConditionAsync(()=>false);
}
