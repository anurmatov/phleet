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

public class HumanInputRequestStartTests
{
    private const string Id = "human-input-123e4567-e89b-42d3-a456-426614174000";
    [Theory]
    [InlineData(null)]
    [InlineData("human-input-ABCDEF00-e89b-42d3-a456-426614174000")]
    [InlineData("other-123e4567-e89b-42d3-a456-426614174000")]
    public void Submit_InvalidId_IsRefused(string? id) => Assert.Throws<ArgumentException>(() =>
        HumanInputRequestContract.Enrich(Json("{\"Question\":\"read\",\"Context\":\"test\",\"AnswerMode\":\"approve-reject\"}"), "agent1", id, "fleet", "fleet", DataConverter.Default.PayloadConverter));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Question\":\" \",\"Context\":\"test\",\"AnswerMode\":\"approve-reject\"}")]
    [InlineData("{\"Question\":\"a\",\"Question\":\"b\",\"Context\":\"test\",\"AnswerMode\":\"approve-reject\"}")]
    [InlineData("{\"question\":\"a\",\"Context\":\"test\",\"AnswerMode\":\"approve-reject\"}")]
    [InlineData("{\"Question\":\"a\",\"Context\":\"test\",\"AnswerMode\":\"approve-reject\",\"RequesterAgent\":\"agent2\"}")]
    public void Submit_InvalidInput_IsRefused(string json) => Assert.Throws<ArgumentException>(() =>
        HumanInputRequestContract.Enrich(Json(json), "agent1", Id, "fleet", "fleet", DataConverter.Default.PayloadConverter));

    [Fact]
    public void Submit_LiteralInput_RoundTripsAndPreflightNeedsMemo()
    {
        var action = " \"read\"\nКыргыз \\ {{config.Secret}} ";
        var input = HumanInputRequestContract.Enrich(JsonSerializer.SerializeToElement(new { Question = action, Context = "test", AnswerMode = "approve-reject" }),
            "agent1", Id, "fleet", "fleet", DataConverter.Default.PayloadConverter);
        Assert.Equal(action, input.GetProperty("Question").GetString());
        Assert.Throws<ArgumentException>(() => HumanInputRequestContract.ValidateStart(input, null, Id, "fleet", "fleet", DataConverter.Default.PayloadConverter));
        HumanInputRequestContract.ValidateStart(input, input.GetProperty("Origin"), Id, "fleet", "fleet", DataConverter.Default.PayloadConverter);
        Assert.Throws<ArgumentException>(() => HumanInputRequestContract.ValidateStart(input, Json("{\"Kind\":\"agent\",\"RequesterAgent\":\"agent2\"}"), Id, "fleet", "fleet", DataConverter.Default.PayloadConverter));
    }

    [Theory]
    [InlineData(null, "fleet", "fleet", 1)]
    [InlineData("agent1", "other", "fleet", 1)]
    [InlineData("agent1", "fleet", "other", 1)]
    [InlineData("agent1", "fleet", "fleet", 1800)]
    public void Submit_MissingIdentityWrongRoutingOrOversize_IsRefused(string? requester, string ns, string queue, int size) =>
        Assert.Throws<ArgumentException>(() => HumanInputRequestContract.Enrich(
            JsonSerializer.SerializeToElement(new { Question = new string('x', size), Context = "test", AnswerMode = "approve-reject" }), requester, Id, ns, queue, DataConverter.Default.PayloadConverter));

    [Theory]
    [InlineData("agent1", "{\"Question\":\"read\",\"Context\":\"test\",\"AnswerMode\":\"approve-reject\"}", true)]
    [InlineData(null, "{\"Question\":\"read\",\"Context\":\"test\",\"AnswerMode\":\"approve-reject\"}", false)]
    [InlineData("agent1", "{\"Question\":\"read\",\"Context\":\"test\",\"AnswerMode\":\"approve-reject\",\"RequesterAgent\":\"agent2\"}", false)]
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
        var response = await tool.StartWorkflowAsync(HumanInputRequestContract.WorkflowType, Id, json);
        if (!accepted)
        {
            Assert.StartsWith("Error", response);
            await client.DidNotReceiveWithAnyArgs().StartWorkflowAsync(default!, default!, default!);
            return;
        }
        Assert.Equal("run-1", Json(response).GetProperty("runId").GetString());
        Assert.Contains("End this initiating turn", response);
        Assert.Equal(WorkflowIdReusePolicy.RejectDuplicate, options!.IdReusePolicy);
        Assert.Equal("agent1", Assert.IsType<JsonElement>(options.Memo![HumanInputRequestContract.OriginMemo]).GetProperty("RequesterAgent").GetString());
        Assert.Equal("agent1", Assert.IsType<JsonElement>(Assert.Single(args!)).GetProperty("Origin").GetProperty("RequesterAgent").GetString());
    }

    [Fact]
    public void Submit_ActualConverterBoundary_1800Accepted1801Refused()
    {
        var converter = DataConverter.Default.PayloadConverter;
        JsonElement Request(int n) => JsonSerializer.SerializeToElement(new { Question = new string('x', n), Context = "c", AnswerMode = "text" });
        var initial = HumanInputRequestContract.Enrich(Request(1), "agent1", Id, "fleet", "fleet", converter);
        var n = 1801 - converter.ToPayload(initial).Data.ToStringUtf8().Length;
        var exact = HumanInputRequestContract.Enrich(Request(n), "agent1", Id, "fleet", "fleet", converter);
        Assert.Equal(1800, converter.ToPayload(exact).Data.ToStringUtf8().Length);
        Assert.Throws<ArgumentException>(() => HumanInputRequestContract.Enrich(Request(n + 1), "agent1", Id, "fleet", "fleet", converter));
    }
    [Fact]
    public void ParentOrigin_MustMatchActualParentAndMemo()
    {
        var converter = DataConverter.Default.PayloadConverter;
        var raw = Json("{\"Question\":\"q\",\"Context\":\"c\",\"AnswerMode\":\"text\"}");
        var input = HumanInputRequestContract.EnrichParent(raw, Id, "fleet", "fleet", "parent", "run-1", converter);
        var origin = input.GetProperty("Origin");
        HumanInputRequestContract.ValidateStart(input, origin, Id, "fleet", "fleet", converter, new("fleet", "run-1", "parent"));
        Assert.Throws<ArgumentException>(() => HumanInputRequestContract.ValidateStart(input, origin, Id, "fleet", "fleet", converter));
        Assert.Throws<ArgumentException>(() => HumanInputRequestContract.ValidateStart(input, origin, Id, "fleet", "fleet", converter, new("fleet", "run-2", "parent")));
        Assert.Throws<ArgumentException>(() => HumanInputRequestContract.EnrichParent(raw, Id, "fleet", "fleet", new string('p', 1800), "run-1", converter));
        Assert.Throws<ArgumentException>(() => HumanInputRequestContract.Enrich(input, "agent1", Id, "fleet", "fleet", converter));
    }

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
}
