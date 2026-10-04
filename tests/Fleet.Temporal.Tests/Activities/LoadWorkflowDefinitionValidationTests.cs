using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using NSubstitute;

namespace Fleet.Temporal.Tests.Activities;

/// <summary>
/// #280 — proof that definition validation is actually WIRED, not merely written.
///
/// <para>
/// <see cref="WorkflowDefinitionValidator"/> has its own unit tests, but those call it directly:
/// delete the one line in the load activity that invokes it and every one of them still passes.
/// The claim "a bad definition fails at load" is a claim about this activity, so it is tested
/// here, against the real HTTP-to-step-tree path, with only the socket stubbed.
/// </para>
/// </summary>
public sealed class LoadWorkflowDefinitionValidationTests
{
    [Fact]
    public async Task ADefinitionWithBindToButNoOutputVar_FailsToLoad()
    {
        // bindTo with nowhere to write its verdict. Runtime would silently produce an uncorrelated
        // park; load time names the step.
        var definition = """
            {"type":"sequence","steps":[
              {"type":"wait_for_signal","name":"park_on_gate","signalName":"blocker-resolved",
               "bindTo":"{{vars.blocker_ref}}"}
            ]}
            """;

        var activity = BuildActivity(definition);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => activity.LoadAsync("ExampleWorkflow"));

        Assert.Contains("park_on_gate", ex.Message);
    }

    [Fact]
    public async Task AValidDefinition_LoadsNormally()
    {
        // The positive control: without it, an activity that threw on everything would pass above.
        var definition = """
            {"type":"sequence","steps":[
              {"type":"wait_for_signal","name":"park_on_gate","signalName":"blocker-resolved",
               "bindTo":"{{vars.blocker_ref}}","outputVar":"resume"}
            ]}
            """;

        var model = await BuildActivity(definition).LoadAsync("ExampleWorkflow");

        var root = Assert.IsType<SequenceStep>(model.Root);
        var wait = Assert.IsType<WaitForSignalStep>(root.Steps[0]);
        Assert.Equal("resume", wait.OutputVar);
    }

    // ── #424: statusVar and fail ─────────────────────────────────────────────

    [Theory]
    [InlineData("""{"type":"delegate","name":"named_step","target":"agent1","instruction":"x","statusVar":""}""")]
    [InlineData("""{"type":"delegate","name":"named_step","target":"agent1","instruction":"x","statusVar":"  "}""")]
    [InlineData("""{"type":"delegate","name":"named_step","target":"agent1","instruction":"x","statusVar":"{{vars.x}}"}""")]
    [InlineData("""{"type":"fail","name":"named_step"}""")]
    [InlineData("""{"type":"fail","name":"named_step","message":" "}""")]
    [InlineData("""{"type":"fail","name":"named_step","message":"stop","ignoreFailure":true}""")]
    public async Task AnInvalidStatusVarOrFailStep_FailsToLoad_NamingTheStep(string step)
    {
        var definition = $$"""{"type":"sequence","steps":[{{step}}]}""";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildActivity(definition).LoadAsync("ExampleWorkflow"));

        Assert.Contains("named_step", ex.Message);
    }

    [Fact]
    public async Task ValidStatusVarAndFailSteps_Load()
    {
        var definition = """
            {"type":"sequence","steps":[
              {"type":"delegate","name":"work","target":"agent1","instruction":"x","statusVar":"work_status"},
              {"type":"branch","on":"{{vars.work_status}}","cases":{
                "failed":{"type":"fail","name":"stop","message":"work reported {{vars.work_status}}"}}}
            ]}
            """;

        var model = await BuildActivity(definition).LoadAsync("ExampleWorkflow");

        var root = Assert.IsType<SequenceStep>(model.Root);
        Assert.Equal("work_status", Assert.IsType<DelegateStep>(root.Steps[0]).StatusVar);
        var fail = Assert.IsType<FailStep>(Assert.IsType<BranchStep>(root.Steps[1]).Cases["failed"]);
        Assert.Equal("work reported {{vars.work_status}}", fail.Message);
    }

    private static LoadWorkflowDefinitionActivity BuildActivity(string definitionJson)
    {
        var body = JsonSerializer.Serialize(new
        {
            name = "ExampleWorkflow",
            @namespace = "default",
            taskQueue = "test",
            definition = definitionJson,
            version = 1,
        });

        var client = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://orchestrator.invalid/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("orchestrator").Returns(client);

        return new LoadWorkflowDefinitionActivity(factory);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
