using System.Text.Json;
using Fleet.Temporal.Engine;
using Temporalio.Converters;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Client;
using Temporalio.Api.Enums.V1;

namespace Fleet.Temporal.Tests.Mcp;

public class HumanApprovalRequestStartTests
{
    private const string Id = "human-approval-123e4567-e89b-42d3-a456-426614174000";
    [Theory]
    [InlineData(null)]
    [InlineData("human-approval-ABCDEF00-e89b-42d3-a456-426614174000")]
    [InlineData("other-123e4567-e89b-42d3-a456-426614174000")]
    public void Submit_InvalidId_IsRefused(string? id) => Assert.Throws<ArgumentException>(() =>
        HumanApprovalRequestContract.Enrich(Json("{\"Action\":\"read\",\"Scope\":\"test\"}"), "agent1", id, "fleet", "fleet", DataConverter.Default.PayloadConverter));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Action\":\" \",\"Scope\":\"test\"}")]
    [InlineData("{\"Action\":\"a\",\"Action\":\"b\",\"Scope\":\"test\"}")]
    [InlineData("{\"action\":\"a\",\"Scope\":\"test\"}")]
    [InlineData("{\"Action\":\"a\",\"Scope\":\"test\",\"RequesterAgent\":\"agent2\"}")]
    public void Submit_InvalidInput_IsRefused(string json) => Assert.Throws<ArgumentException>(() =>
        HumanApprovalRequestContract.Enrich(Json(json), "agent1", Id, "fleet", "fleet", DataConverter.Default.PayloadConverter));

    [Fact]
    public void Submit_LiteralInput_RoundTripsAndPreflightNeedsMemo()
    {
        var action = " \"read\"\nКыргыз \\ {{config.Secret}} ";
        var input = HumanApprovalRequestContract.Enrich(JsonSerializer.SerializeToElement(new { Action = action, Scope = "test" }),
            "agent1", Id, "fleet", "fleet", DataConverter.Default.PayloadConverter);
        Assert.Equal(action, input.GetProperty("Action").GetString());
        Assert.Throws<ArgumentException>(() => HumanApprovalRequestContract.ValidateStart(input, null, Id, "fleet", "fleet", DataConverter.Default.PayloadConverter));
        HumanApprovalRequestContract.ValidateStart(input, "agent1", Id, "fleet", "fleet", DataConverter.Default.PayloadConverter);
        Assert.Throws<ArgumentException>(() => HumanApprovalRequestContract.ValidateStart(input, "agent2", Id, "fleet", "fleet", DataConverter.Default.PayloadConverter));
    }

    [Theory]
    [InlineData(null, "fleet", "fleet", 1)]
    [InlineData("agent1", "other", "fleet", 1)]
    [InlineData("agent1", "fleet", "other", 1)]
    [InlineData("agent1", "fleet", "fleet", 1800)]
    public void Submit_MissingIdentityWrongRoutingOrOversize_IsRefused(string? requester, string ns, string queue, int size) =>
        Assert.Throws<ArgumentException>(() => HumanApprovalRequestContract.Enrich(
            JsonSerializer.SerializeToElement(new { Action = new string('x', size), Scope = "test" }), requester, Id, ns, queue, DataConverter.Default.PayloadConverter));

    [Theory]
    [InlineData("agent1", "{\"Action\":\"read\",\"Scope\":\"test\"}", true)]
    [InlineData(null, "{\"Action\":\"read\",\"Scope\":\"test\"}", false)]
    [InlineData("agent1", "{\"Action\":\"read\",\"Scope\":\"test\",\"RequesterAgent\":\"agent2\"}", false)]
    public async Task Tool_AttributedStart_StampsMemoAndForcesRejectDuplicate(string? requester, string json, bool accepted)
    {
        var client = Substitute.For<ITemporalClient>();
        client.Options.Returns(new TemporalClientOptions { DataConverter = DataConverter.Default });
        var factory = Substitute.For<ITemporalClientFactory>(); factory.GetClientAsync(Arg.Any<string>()).Returns(client);
        var http = Substitute.For<IHttpClientFactory>(); http.CreateClient("orchestrator").Returns(new HttpClient());
        var registry = new WorkflowTypeRegistry(http, Options.Create(new TemporalBridgeOptions()), NullLogger<WorkflowTypeRegistry>.Instance);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (requester != null) accessor.HttpContext.Request.QueryString = new QueryString("?agent=" + requester);
        WorkflowOptions? options = null;
        IReadOnlyCollection<object?>? args = null;
        client.StartWorkflowAsync(Arg.Any<string>(), Arg.Do<IReadOnlyCollection<object?>>(a => args = a), Arg.Do<WorkflowOptions>(o => options = o))
            .Returns(new WorkflowHandle(client, Id, null, "run-1", null));
        var tool = new TemporalWorkflowTools(factory, registry, Substitute.For<CtoAgentConfigService>(), Substitute.For<IWorkflowGateStateReader>(),
            Substitute.For<IEpicGrantDecisionForwarder>(), accessor, NullLogger<TemporalWorkflowTools>.Instance);
        var response = await tool.StartWorkflowAsync(HumanApprovalRequestContract.WorkflowType, Id, json);
        if (!accepted)
        {
            Assert.StartsWith("Error", response);
            await client.DidNotReceiveWithAnyArgs().StartWorkflowAsync(default!, default!, default!);
            return;
        }
        Assert.Equal("run-1", Json(response).GetProperty("runId").GetString());
        Assert.Contains("End this initiating turn", response);
        Assert.Equal(WorkflowIdReusePolicy.RejectDuplicate, options!.IdReusePolicy);
        Assert.Equal("agent1", options.Memo![HumanApprovalRequestContract.RequesterMemo]);
        Assert.Equal("agent1", Assert.IsType<JsonElement>(Assert.Single(args!)).GetProperty("RequesterAgent").GetString());
    }

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
}
