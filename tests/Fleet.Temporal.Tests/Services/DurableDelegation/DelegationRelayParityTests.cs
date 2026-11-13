using System.Text;
using System.Text.Json.Nodes;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// <see cref="DelegationRelay"/> is a second copy of what <see cref="DelegateToAgentActivity"/>
/// sends, kept separate so the activity's own code is untouched while durable delegation is
/// optional. A copy can drift. These run the real activity and the relay against the same recording
/// broker and orchestrator and require the same bytes (the timestamp aside) on the same routing
/// key: an agent must not be able to tell which of the two sent a directive.
/// </summary>
[Collection("fleet-workflow-config-static")]
public sealed class DelegationRelayParityTests : IDisposable
{
    private const string Agent = "Reviewer-One";

    // The escalation target comes from a process-wide static that FleetWorkflowConfigTests resets;
    // the shared collection keeps the two from running at once, and this puts it back as found.
    private static void SetEscalationTarget(string? agent) =>
        typeof(FleetWorkflowConfig)
            .GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, agent is null ? null : new FleetWorkflowOptions { CtoAgent = agent });

    public void Dispose() => SetEscalationTarget(null);

    private sealed class Wire
    {
        public List<(string Exchange, string RoutingKey, bool Persistent, string Body)> Published { get; } = [];
        public List<(string Method, string Url, string? Authorization)> Http { get; } = [];
        public IConnectionFactory ConnectionFactory { get; }
        public IHttpClientFactory HttpClientFactory { get; }

        public Wire()
        {
            var channel = Substitute.For<IChannel>();
            channel.BasicPublishAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                    Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var body = JsonNode.Parse(Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span))!.AsObject();
                    // The one field that is a clock reading.
                    Assert.True(body.Remove("Timestamp"));
                    lock (Published)
                        Published.Add((
                            call.ArgAt<string>(0), call.ArgAt<string>(1),
                            call.ArgAt<BasicProperties>(3).DeliveryMode == DeliveryModes.Persistent,
                            body.ToJsonString()));
                    return ValueTask.CompletedTask;
                });

            var connection = Substitute.For<IConnection>();
            connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(channel));
            ConnectionFactory = Substitute.For<IConnectionFactory>();
            ConnectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));

            HttpClientFactory = Substitute.For<IHttpClientFactory>();
            HttpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Recorder(Http)));
        }

        private sealed class Recorder(List<(string, string, string?)> requests) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (requests)
                    requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }
        }
    }

    private static TemporalBridgeOptions Bridge(string orchestratorUrl, int agentTimeoutSeconds = 30) => new()
    {
        AgentTimeoutSeconds = agentTimeoutSeconds,
        GroupChatId = -100123,
        OrchestratorUrl = orchestratorUrl,
        OrchestratorAuthToken = orchestratorUrl.Length > 0 ? "token-for-the-test" : "",
    };

    private static (DelegateToAgentActivity Activity, TaskCompletionRegistry Registry) Activity(Wire wire, TemporalBridgeOptions bridge)
    {
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        return (new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()), Options.Create(bridge), registry,
            wire.ConnectionFactory, wire.HttpClientFactory, NullLogger<DelegateToAgentActivity>.Instance), registry);
    }

    private static DelegationRelay Relay(Wire wire, TemporalBridgeOptions bridge) => new(
        Options.Create(new RabbitMqOptions()), Options.Create(bridge),
        wire.ConnectionFactory, wire.HttpClientFactory, NullLogger<DelegationRelay>.Instance);

    /// <summary>The record the durable path would hold for the same delegation, in the same activity.</summary>
    private static PendingDelegation Record(string taskId, string instruction, string? repo) => new()
    {
        TaskId = taskId,
        CurrentTaskId = taskId,
        Agent = Agent,
        CurrentInstruction = instruction,
        Repo = repo,
        WorkflowId = ActivityEnvironment.DefaultInfo.WorkflowId,
        WorkflowType = ActivityEnvironment.DefaultInfo.WorkflowType,
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.True(condition(), "the activity never got that far");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("owner/name")]
    public async Task ADirective_IsTheSameBytesOnTheSameRoutingKey(string? repo)
    {
        const string instruction = "review the change\nsecond line — “quoted”";
        var legacy = new Wire();
        var (activity, registry) = Activity(legacy, Bridge(""));
        var run = new ActivityEnvironment().RunAsync(() => activity.DelegateToAgentAsync(Agent, instruction, "wf/step~1.0", repo: repo));
        await WaitForAsync(() => legacy.Published.Count == 1);
        Assert.True(registry.TryComplete("wf/step~1.0", new AgentTaskResult("ok", "completed")));
        await run;

        var durable = new Wire();
        await Relay(durable, Bridge("")).PublishDirectiveAsync(Record("wf/step~1.0", instruction, repo), default);

        Assert.Equal(legacy.Published, durable.Published);
        Assert.Equal("reviewer-one", durable.Published[0].RoutingKey);
        Assert.True(durable.Published[0].Persistent);
    }

    [Fact]
    public async Task ATargetedCancel_IsTheSameRequestToTheOrchestrator()
    {
        var legacy = new Wire();
        var bridge = Bridge("http://orchestrator.invalid/");
        var (activity, _) = Activity(legacy, bridge);
        using var cts = new CancellationTokenSource();
        var run = new ActivityEnvironment { CancellationTokenSource = cts }
            .RunAsync(() => activity.DelegateToAgentAsync(Agent, "do it", "wf/step one"));
        await WaitForAsync(() => legacy.Published.Count == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var durable = new Wire();
        await Relay(durable, bridge).SendCancelAsync(Record("wf/step one", "do it", null));

        Assert.Equal(legacy.Http, durable.Http);
        var request = Assert.Single(durable.Http);
        Assert.Equal("POST", request.Method);
        Assert.EndsWith("/api/agents/Reviewer-One/cancel/wf%2Fstep%20one", request.Url);
        Assert.Equal("Bearer token-for-the-test", request.Authorization);
        Assert.Empty(durable.Published);
    }

    [Fact]
    public async Task TheFallbackCancel_IsTheSameBroadcast_WhenThereIsNoOrchestratorUrl()
    {
        var legacy = new Wire();
        var (activity, _) = Activity(legacy, Bridge(""));
        using var cts = new CancellationTokenSource();
        var run = new ActivityEnvironment { CancellationTokenSource = cts }
            .RunAsync(() => activity.DelegateToAgentAsync(Agent, "do it", "wf/step"));
        await WaitForAsync(() => legacy.Published.Count == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var durable = new Wire();
        await Relay(durable, Bridge("")).SendCancelAsync(Record("wf/step", "do it", null));

        Assert.Equal(legacy.Published[1], Assert.Single(durable.Published));
        Assert.Contains("\"TaskId\":\"wf/step/cancel\"", durable.Published[0].Body);
    }

    [Fact]
    public async Task ATimeoutReport_IsTheSameMessageToTheEscalationTarget()
    {
        SetEscalationTarget("Escalation-Agent");
        var legacy = new Wire();
        var (activity, _) = Activity(legacy, Bridge("", agentTimeoutSeconds: 1));
        var timeout = await Assert.ThrowsAsync<TimeoutException>(
            () => new ActivityEnvironment().RunAsync(() => activity.DelegateToAgentAsync(Agent, "do it", "wf/step")));
        Assert.Equal(2, legacy.Published.Count);

        var durable = new Wire();
        await Relay(durable, Bridge("")).ReportTimeoutAsync(Record("wf/step", "do it", null), timeout.Message);

        Assert.Equal(legacy.Published[1], Assert.Single(durable.Published));
        Assert.Equal("escalation-agent", durable.Published[0].RoutingKey);
        Assert.Contains("wf/step/timeout-report", durable.Published[0].Body);
    }
}
