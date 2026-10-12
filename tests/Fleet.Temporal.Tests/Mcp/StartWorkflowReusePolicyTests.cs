using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Fleet.Temporal.Tests.Mcp;

public class StartWorkflowReusePolicyTests
{
    [Theory]
    [InlineData("allow_duplicate", WorkflowIdReusePolicy.AllowDuplicate)]
    [InlineData("reject_duplicate", WorkflowIdReusePolicy.RejectDuplicate)]
    public async Task Start_ValidPolicy_IsPassedToTemporal(string policy, WorkflowIdReusePolicy expected)
    {
        var (tool, client) = Create();
        WorkflowOptions? captured = null;
        client.StartWorkflowAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<object?>>(), Arg.Do<WorkflowOptions>(o => captured = o))
            .Returns(new WorkflowHandle(client, "id", null!, null!, null!));
        Assert.Contains("started", await tool.StartWorkflowAsync("type", "id", "{}", id_reuse_policy: policy));
        Assert.Equal(expected, captured!.IdReusePolicy);
    }
    [Fact]
    public async Task Start_UnknownPolicy_DoesNotAcquireClientOrStart()
    {
        var (tool, client) = Create();
        Assert.StartsWith("Error:", await tool.StartWorkflowAsync("type", "id", "{}", id_reuse_policy: "unknown"));
        await client.DidNotReceiveWithAnyArgs().StartWorkflowAsync(default!, default!, default!);
    }
    [Fact]
    public async Task Start_RejectDuplicateAfterTermination_IsEnforcedByTemporal()
    {
        await using var env = await Temporalio.Testing.WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new Temporalio.Worker.TemporalWorker(env.Client,
            new Temporalio.Worker.TemporalWorkerOptions("reuse-test").AddWorkflow<ReusePolicyTestWorkflow>());
        await worker.ExecuteAsync(async () =>
        {
            var id = "reuse-" + Guid.NewGuid().ToString("N");
            var first = await env.Client.StartWorkflowAsync("ReusePolicyTestWorkflow", [], new WorkflowOptions(id, "reuse-test"));
            await first.TerminateAsync("test close");
            await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowAlreadyStartedException>(() =>
                env.Client.StartWorkflowAsync("ReusePolicyTestWorkflow", [], new WorkflowOptions(id, "reuse-test")
                { IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate }));
            var next = await env.Client.StartWorkflowAsync("ReusePolicyTestWorkflow", [], new WorkflowOptions(id, "reuse-test")
                { IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate });
            await next.TerminateAsync("test cleanup");
        });
    }

    private static (TemporalWorkflowTools, ITemporalClient) Create()
    {
        var client = Substitute.For<ITemporalClient>(); var factory = Substitute.For<ITemporalClientFactory>();
        factory.GetClientAsync(Arg.Any<string>()).Returns(client);
        var http = Substitute.For<IHttpClientFactory>(); http.CreateClient("orchestrator").Returns(new HttpClient());
        var registry = new WorkflowTypeRegistry(http, Options.Create(new TemporalBridgeOptions()), NullLogger<WorkflowTypeRegistry>.Instance);
        var tool = new TemporalWorkflowTools(factory, registry, Substitute.For<CtoAgentConfigService>(), Substitute.For<IWorkflowGateStateReader>(),
            Substitute.For<IEpicGrantDecisionForwarder>(), Substitute.For<IHttpContextAccessor>(), NullLogger<TemporalWorkflowTools>.Instance);
        return (tool, client);
    }
}

[Temporalio.Workflows.Workflow("ReusePolicyTestWorkflow")]
public class ReusePolicyTestWorkflow
{
    [Temporalio.Workflows.WorkflowRun]
    public async Task RunAsync() => await Temporalio.Workflows.Workflow.DelayAsync(TimeSpan.FromDays(1));
}
