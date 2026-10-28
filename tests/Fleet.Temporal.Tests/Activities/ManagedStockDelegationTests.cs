using System.Net;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests.Activities;

/// <summary>
/// Workflow delegation to managed stock OpenShell agents (#492 review F2/D1/F8, L2): a managed target is
/// refused by name and never published on the ordinary broker, an ordinary target is unchanged, and an
/// undeterminable runtime fails closed (retryably) instead of falling back to the ordinary broker.
/// </summary>
public class ManagedStockDelegationTests
{
    private const string Agent = "worker-one";

    private sealed class StateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private sealed record Harness(DelegateToAgentActivity Activity, TaskCompletionRegistry Registry,
        IConnectionFactory Factory, List<(string Exchange, string RoutingKey, BasicProperties Props)> Published);

    private static Harness Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var published = new List<(string, string, BasicProperties)>();
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (published) published.Add((call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<BasicProperties>(3)));
                return ValueTask.CompletedTask;
            });
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));
        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));

        var handler = new StateHandler(respond);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = 30, OrchestratorUrl = "http://orchestrator.invalid" }),
            registry, factory, http, NullLogger<DelegateToAgentActivity>.Instance);
        return new(activity, registry, factory, published);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Body(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(json) };

    [Fact]
    public async Task ManagedAgent_IsRefusedByName_AndNeverReachesTheOrdinaryPublisher()
    {
        var h = Build(Body($$"""{"agentName":"{{Agent}}","runtimeKind":"openshell","appliedRuntimeKind":"openshell"}"""));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => new ActivityEnvironment()
            .RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/managed")));

        Assert.True(failure.NonRetryable);
        Assert.Equal("ManagedAgentWorkflowUnsupported", failure.ErrorType);
        Assert.StartsWith(DelegateToAgentActivity.ManagedTargetUnsupported, failure.Message);
        Assert.Empty(h.Published);
        await h.Factory.DidNotReceive().CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrdinaryAgent_IsDelegatedUnchanged_OnTheOrdinaryBroker()
    {
        var h = Build(Body($$"""{"agentName":"{{Agent}}","appliedRuntimeKind":"docker"}"""));

        var run = new ActivityEnvironment().RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/ordinary"));
        var deadline = Environment.TickCount64 + 5000;
        while (!h.Registry.TryComplete("wf/ordinary", new AgentTaskResult("done", "completed")) && Environment.TickCount64 < deadline)
            await Task.Delay(10);

        Assert.True((await run).IsCompleted);
        var (exchange, routingKey, props) = Assert.Single(h.Published);
        Assert.Equal("fleet.group", exchange);
        Assert.Equal(Agent, routingKey);
        Assert.Null(props.UserId); // ordinary connection: no stock principal is claimed
    }

    public static TheoryData<string, Func<HttpRequestMessage, HttpResponseMessage>> Unknown() => new()
    {
        { "status 500", Body("{}", HttpStatusCode.InternalServerError) },
        { "status 401", Body("{}", HttpStatusCode.Unauthorized) },
        { "status 404", Body("{}", HttpStatusCode.NotFound) },
        { "unreachable", _ => throw new HttpRequestException("connection refused") },
        { "timed out", _ => throw new TaskCanceledException("timeout") },
        { "empty body", Body("") },
        { "invalid json", Body("{not json") },
        { "array body", Body("[]") },
        { "missing field", Body("""{"agentName":"worker-one"}""") },
        { "non-string field", Body("""{"appliedRuntimeKind":1}""") },
        { "duplicate field", Body("""{"appliedRuntimeKind":"docker","AppliedRuntimeKind":"openshell"}""") },
        { "unknown runtime", Body("""{"appliedRuntimeKind":"podman"}""") },
    };

    [Theory]
    [MemberData(nameof(Unknown))]
    public async Task UndeterminableRuntime_FailsClosed_WithoutPublishing(string label, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var h = Build(respond);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => new ActivityEnvironment()
            .RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/unknown")));

        Assert.False(failure.NonRetryable, label); // transient: a retry repeats the check before any publish
        Assert.Equal("ManagedAgentRuntimeUnknown", failure.ErrorType);
        Assert.StartsWith(DelegateToAgentActivity.ManagedTargetUnknown, failure.Message);
        Assert.Empty(h.Published);
        await h.Factory.DidNotReceive().CreateConnectionAsync(Arg.Any<CancellationToken>());
    }
}
