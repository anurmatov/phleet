using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Mcp;

public class GetWorkflowResultRunIdTests
{
    [Fact]
    public async Task Result_ReusedId_BindsChosenRunAndKeepsLegacyLatestOutput()
    {
        await using var env=await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue="result-binding-"+Guid.NewGuid().ToString("N");
        using var worker=new TemporalWorker(env.Client,new TemporalWorkerOptions(queue).AddWorkflow<AcceptanceResultWorkflow>());
        var tool=Create(env.Client);
        await worker.ExecuteAsync(async()=>
        {
            var r1=await env.Client.StartWorkflowAsync((AcceptanceResultWorkflow w)=>w.RunAsync(1,false),new WorkflowOptions(queue,queue));
            await r1.GetResultAsync();
            var r2=await env.Client.StartWorkflowAsync((AcceptanceResultWorkflow w)=>w.RunAsync(2,false),new WorkflowOptions(queue,queue){IdReusePolicy=WorkflowIdReusePolicy.AllowDuplicate});
            await r2.GetResultAsync();
            using var bound=JsonDocument.Parse(await tool.GetWorkflowResultAsync(queue,run_id:r1.ResultRunId));
            Assert.Equal(queue,bound.RootElement.GetProperty("workflowId").GetString());
            Assert.Equal(r1.ResultRunId,bound.RootElement.GetProperty("runId").GetString());
            Assert.Equal(1,bound.RootElement.GetProperty("result").GetProperty("IssueNumber").GetInt32());
            using var latest=JsonDocument.Parse(await tool.GetWorkflowResultAsync(queue));
            Assert.Equal(2,latest.RootElement.GetProperty("IssueNumber").GetInt32());
            Assert.Equal(latest.RootElement.GetRawText(),await tool.GetWorkflowResultAsync(queue,run_id:""));
            Assert.StartsWith("Error getting workflow result:",await tool.GetWorkflowResultAsync(queue,run_id:Guid.NewGuid().ToString()));

            var empty=await env.Client.StartWorkflowAsync((AcceptanceResultWorkflow w)=>w.RunAsync(0,false),new WorkflowOptions(queue+"-null",queue));
            await empty.GetResultAsync();
            var later=await env.Client.StartWorkflowAsync((AcceptanceResultWorkflow w)=>w.RunAsync(441,false),new WorkflowOptions(queue+"-null",queue){IdReusePolicy=WorkflowIdReusePolicy.AllowDuplicate});
            await later.GetResultAsync();
            using var noApproval=JsonDocument.Parse(await tool.GetWorkflowResultAsync(empty.Id,run_id:empty.ResultRunId));
            Assert.Equal(JsonValueKind.Null,noApproval.RootElement.GetProperty("result").ValueKind);

            var continued=await env.Client.StartWorkflowAsync((AcceptanceResultWorkflow w)=>w.RunAsync(1,true),new WorkflowOptions(queue+"-continue",queue));
            await continued.GetResultAsync();
            Assert.StartsWith("Error getting workflow result:",await tool.GetWorkflowResultAsync(continued.Id,run_id:continued.ResultRunId));
        });
    }

    [Theory]
    [InlineData("agent1")]
    [InlineData("cto-agent")]
    public async Task AcceptanceDecision_NoAgentException_NoTemporalCall(string caller)
    {
        var client=Substitute.For<ITemporalClient>();
        var tool=Create(client,caller);
        Assert.StartsWith("Error:",await tool.SignalWorkflowAsync("wf-1","acceptance-decision","{\"Decision\":\"continue\"}"));
        client.DidNotReceiveWithAnyArgs().GetWorkflowHandle(default!,default!,default!);
    }

    private static TemporalWorkflowTools Create(ITemporalClient client,string caller="agent1")
    {
        var factory=Substitute.For<ITemporalClientFactory>();factory.GetClientAsync(Arg.Any<string>()).Returns(client);
        var http=Substitute.For<IHttpClientFactory>();http.CreateClient("orchestrator").Returns(new HttpClient());
        var registry=new WorkflowTypeRegistry(http,Options.Create(new TemporalBridgeOptions()),NullLogger<WorkflowTypeRegistry>.Instance);
        var cto=Substitute.For<CtoAgentConfigService>();cto.GetCtoAgent().Returns("cto-agent");
        var accessor=Substitute.For<IHttpContextAccessor>();accessor.HttpContext.Returns(new DefaultHttpContext());
        accessor.HttpContext!.Request.QueryString=new QueryString("?agent="+caller);
        return new TemporalWorkflowTools(factory,registry,cto,Substitute.For<IWorkflowGateStateReader>(),
            Substitute.For<IEpicGrantDecisionForwarder>(),accessor,NullLogger<TemporalWorkflowTools>.Instance);
    }
}

[Workflow("AcceptanceResultWorkflow")]
public class AcceptanceResultWorkflow
{
    [WorkflowRun]
    public Task<JsonElement> RunAsync(int issue,bool continueRun)
    {
        if (continueRun)
            throw Workflow.CreateContinueAsNewException((AcceptanceResultWorkflow w)=>w.RunAsync(2,false));
        return Task.FromResult(issue==0 ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(new {IssueNumber=issue}));
    }
}
