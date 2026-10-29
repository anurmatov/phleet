using System.Net;
using System.Text.Json;
using Fleet.Shared.Security;
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
/// Workflow delegation to managed stock OpenShell agents (#492 review F2/S2/F8, R1/F13): managed mode is on only
/// with an authority directory; then a managed target receives a typed directive over its epoch control lane as
/// the control principal (never the ordinary publisher), an ordinary target is unchanged, and an undeterminable
/// runtime fails closed. Default-off installs never consult the orchestrator. The registry completes a managed
/// TaskId only for a reply with the registered agent, sandbox, epoch and session.
/// </summary>
public class ManagedStockDelegationTests : IDisposable
{
    private const string Agent = "worker-one";
    private readonly string _dir = Directory.CreateTempSubdirectory("managed-delegation-").FullName;
    private readonly Guid _epoch = Guid.NewGuid();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Vhost => "stock-" + _epoch.ToString("N");

    private void SaveRecord(string state = "active", bool password = true)
    {
        File.WriteAllText(Path.Combine(_dir, Agent + ".json"), JsonSerializer.Serialize(new StockEnrollmentAuthority
        {
            State = state, Agent = Agent, SandboxId = "sandbox-1", Epoch = _epoch, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
            AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
            MemoryHost = "memory.example.test", BrokerHost = "broker.example.test"
        }));
        if (password) File.WriteAllText(Path.Combine(_dir, Agent + ".control.password"), "control-secret");
    }

    private sealed class StateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private sealed record Published(string Exchange, string RoutingKey, BasicProperties Props, byte[] Body);

    private sealed record Harness(DelegateToAgentActivity Activity, TaskCompletionRegistry Registry,
        IConnectionFactory Factory, List<Published> Ordinary, List<Published> Control, List<ConnectionFactory> ControlFactories,
        StateHandler Orchestrator);

    private static IConnection Recording(List<Published> into)
    {
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (into) into.Add(new(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<BasicProperties>(3),
                    call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray()));
                return ValueTask.CompletedTask;
            });
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));
        return connection;
    }

    private Harness Build(Func<HttpRequestMessage, HttpResponseMessage> respond, bool managedMode = true)
    {
        var ordinary = new List<Published>();
        var control = new List<Published>();
        var controlFactories = new List<ConnectionFactory>();
        var factory = Substitute.For<IConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Recording(ordinary)));

        var handler = new StateHandler(respond);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions
            {
                AgentTimeoutSeconds = 30, OrchestratorUrl = "http://orchestrator.invalid",
                StockAuthorityDirectory = managedMode ? _dir : ""
            }),
            registry, factory, http, NullLogger<DelegateToAgentActivity>.Instance)
        {
            ControlConnect = (f, _) => { lock (controlFactories) controlFactories.Add(f); return Task.FromResult(Recording(control)); }
        };
        return new(activity, registry, factory, ordinary, control, controlFactories, handler);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Body(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(json) };

    private static Func<HttpRequestMessage, HttpResponseMessage> Managed =>
        Body($$"""{"agentName":"{{Agent}}","runtimeKind":"openshell","appliedRuntimeKind":"openshell"}""");

    private static Func<HttpRequestMessage, HttpResponseMessage> Outage => _ => throw new HttpRequestException("connection refused");

    private static async Task<bool> CompleteWhenRegistered(TaskCompletionRegistry registry, string taskId, ManagedCorrelation? context)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (registry.TryComplete(taskId, new AgentTaskResult("done", "completed"), context)) return true;
            await Task.Delay(10);
        }
        return false;
    }

    [Fact]
    public async Task ManagedAgent_GetsTypedDirectiveOverItsControlLane_AsTheControlPrincipal_NeverTheOrdinaryPublisher()
    {
        SaveRecord();
        var h = Build(Managed);

        var run = new ActivityEnvironment().RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/managed"));
        var deadline = Environment.TickCount64 + 5000;
        while (h.Control.Count == 0 && Environment.TickCount64 < deadline) await Task.Delay(10);

        var directive = Assert.Single(h.Control);
        Assert.Equal("fleet.group", directive.Exchange);
        Assert.Equal(Agent, directive.RoutingKey);
        Assert.Equal(Vhost + "-control", directive.Props.UserId);
        var factory = Assert.Single(h.ControlFactories);
        Assert.Equal(Vhost + "-control", factory.UserName);
        Assert.Equal("control-secret", factory.Password);
        Assert.Equal(Vhost, factory.VirtualHost);
        Assert.Equal("broker.example.test", factory.HostName);
        var message = JsonSerializer.Deserialize<RelayMessage>(directive.Body)!;
        Assert.Equal(RelayMessageType.WorkflowDirective, message.Type);
        Assert.Equal("wf/managed", message.TaskId);
        Assert.Equal(_epoch, message.Epoch);
        Assert.Equal("sandbox-1", message.Sandbox);
        Assert.True(Guid.TryParseExact(message.CorrelationId, "N", out _));
        Assert.Empty(h.Ordinary);
        await h.Factory.DidNotReceive().CreateConnectionAsync(Arg.Any<CancellationToken>());

        // Only the registered agent/sandbox/epoch/session completes the TaskId; an ordinary reply never does.
        var good = new ManagedCorrelation(Agent, "sandbox-1", _epoch, message.CorrelationId!);
        var result = new AgentTaskResult("forged", "completed");
        Assert.False(h.Registry.TryComplete("wf/managed", result));
        Assert.False(h.Registry.TryComplete("wf/managed", result, good with { Agent = "worker-two" }));
        Assert.False(h.Registry.TryComplete("wf/managed", result, good with { Epoch = Guid.NewGuid() }));
        Assert.False(h.Registry.TryComplete("wf/managed", result, good with { Sandbox = "sandbox-2" }));
        Assert.False(h.Registry.TryComplete("wf/managed", result, good with { Session = Guid.NewGuid().ToString("N") }));
        Assert.False(h.Registry.TryComplete("wf/other", result, good));
        Assert.True(await CompleteWhenRegistered(h.Registry, "wf/managed", good));
        Assert.Equal("done", (await run).Text);
        Assert.Equal(0, h.Registry.PendingCount);
    }

    [Theory]
    [InlineData("revoked", true)]
    [InlineData("missing", true)]
    [InlineData("active", false)]
    public async Task ManagedAgent_WithoutCurrentRecordOrControlCredential_IsRefused_WithoutAnyPublish(string state, bool password)
    {
        if (state != "missing") SaveRecord(state, password);
        var h = Build(Managed);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => new ActivityEnvironment()
            .RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/denied")));

        Assert.True(failure.NonRetryable);
        Assert.Equal("ManagedAgentContextDenied", failure.ErrorType);
        Assert.StartsWith(DelegateToAgentActivity.ManagedTargetDenied, failure.Message);
        Assert.Empty(h.Control);
        Assert.Empty(h.Ordinary);
        await h.Factory.DidNotReceive().CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrdinaryAgent_IsDelegatedUnchanged_OnTheOrdinaryBroker()
    {
        var h = Build(Body($$"""{"agentName":"{{Agent}}","appliedRuntimeKind":"docker"}"""));

        var run = new ActivityEnvironment().RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/ordinary"));
        Assert.True(await CompleteWhenRegistered(h.Registry, "wf/ordinary", null));

        Assert.True((await run).IsCompleted);
        var published = Assert.Single(h.Ordinary);
        Assert.Equal("fleet.group", published.Exchange);
        Assert.Equal(Agent, published.RoutingKey);
        Assert.Null(published.Props.UserId); // ordinary connection: no stock principal is claimed
        Assert.Equal(RelayMessageType.Directive, JsonSerializer.Deserialize<RelayMessage>(published.Body)!.Type);
        Assert.Empty(h.Control);
    }

    [Fact]
    public async Task DefaultOff_NeverConsultsTheOrchestrator_SoAnOrchestratorOutageCannotFailDelegation()
    {
        SaveRecord(); // even a record on disk is ignored while managed mode is off
        var h = Build(Outage, managedMode: false);

        var run = new ActivityEnvironment().RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/default-off"));
        Assert.True(await CompleteWhenRegistered(h.Registry, "wf/default-off", null));

        Assert.True((await run).IsCompleted);
        Assert.Equal(0, h.Orchestrator.Calls);
        Assert.Single(h.Ordinary);
        Assert.Empty(h.Control);
    }

    [Fact]
    public async Task ManagedOn_OrchestratorOutage_FailsClosed_WithoutOrdinaryFallback()
    {
        SaveRecord();
        var h = Build(Outage);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => new ActivityEnvironment()
            .RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/outage")));

        Assert.False(failure.NonRetryable);
        Assert.Equal("ManagedAgentRuntimeUnknown", failure.ErrorType);
        Assert.Equal(1, h.Orchestrator.Calls);
        Assert.Empty(h.Ordinary);
        Assert.Empty(h.Control);
        await h.Factory.DidNotReceive().CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Registry_OrdinaryRegistration_RefusesAReplyClaimingAManagedContext()
    {
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        registry.Register("wf/plain");
        Assert.False(registry.TryComplete("wf/plain", new AgentTaskResult("x", "completed"),
            new ManagedCorrelation(Agent, "sandbox-1", Guid.NewGuid(), Guid.NewGuid().ToString("N"))));
        Assert.True(registry.TryComplete("wf/plain", new AgentTaskResult("x", "completed")));
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
        Assert.Empty(h.Ordinary);
        Assert.Empty(h.Control);
        await h.Factory.DidNotReceive().CreateConnectionAsync(Arg.Any<CancellationToken>());
    }

    // ---- #492 R5: the workflow session survives a failed publication so the retry reuses it ----

    private static async Task<RelayMessage> NextDirective(List<Published> control, int count)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            lock (control) if (control.Count >= count) return JsonSerializer.Deserialize<RelayMessage>(control[count - 1].Body)!;
            await Task.Delay(10);
        }
        throw new TimeoutException("no directive published");
    }

    [Fact]
    public async Task FailedFirstSend_RetainsTheSession_AndTheRetryReusesIt_SoTheFirstSendsReplyCompletesTheRetry()
    {
        SaveRecord();
        var h = Build(Managed);
        var attempt = 0;
        h.Activity.ControlConnect = (_, _) =>
        {
            // The first send reaches the broker, then the publication reports failure.
            var connection = Recording(h.Control);
            if (Interlocked.Increment(ref attempt) == 1)
                connection.When(c => c.DisposeAsync()).Do(_ => throw new IOException("connection reset after publish"));
            return Task.FromResult(connection);
        };

        await Assert.ThrowsAnyAsync<Exception>(() => new ActivityEnvironment()
            .RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/retry")));
        var first = await NextDirective(h.Control, 1);
        Assert.Equal(0, h.Registry.PendingCount);
        Assert.Equal(1, h.Registry.RetainedSessionCount);

        var retry = new ActivityEnvironment().RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/retry"));
        var second = await NextDirective(h.Control, 2);
        Assert.Equal(first.CorrelationId, second.CorrelationId);

        // The agent answers the directive it already received, with the first send's session.
        Assert.True(await CompleteWhenRegistered(h.Registry, "wf/retry",
            new ManagedCorrelation(Agent, "sandbox-1", _epoch, first.CorrelationId!)));
        Assert.Equal("done", (await retry).Text);
        Assert.Equal(0, h.Registry.RetainedSessionCount);
    }

    [Fact]
    public async Task HealthyOneShot_ReleasesItsSession_AndANewDelegationGetsAFreshOne()
    {
        SaveRecord();
        var h = Build(Managed);

        var run = new ActivityEnvironment().RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/once"));
        var first = await NextDirective(h.Control, 1);
        Assert.True(await CompleteWhenRegistered(h.Registry, "wf/once",
            new ManagedCorrelation(Agent, "sandbox-1", _epoch, first.CorrelationId!)));
        Assert.Equal("done", (await run).Text);
        Assert.Single(h.Control);
        Assert.Equal(0, h.Registry.PendingCount);
        Assert.Equal(0, h.Registry.RetainedSessionCount);

        var again = new ActivityEnvironment().RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/once"));
        var second = await NextDirective(h.Control, 2);
        Assert.NotEqual(first.CorrelationId, second.CorrelationId);
        Assert.True(await CompleteWhenRegistered(h.Registry, "wf/once",
            new ManagedCorrelation(Agent, "sandbox-1", _epoch, second.CorrelationId!)));
        await again;
    }

    // ---- #492 W6: the per-task session map is bounded and released on timeout ----

    [Fact]
    public async Task Timeout_ReleasesTheManagedSession()
    {
        SaveRecord();
        var h = Build(Managed);

        await Assert.ThrowsAsync<TimeoutException>(() => new ActivityEnvironment
            {
                Info = ActivityEnvironment.DefaultInfo with { StartToCloseTimeout = TimeSpan.FromHours(1) }
            }
            .RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/timeout", agentBudgetSeconds: 1)));

        Assert.Single(h.Control);
        Assert.Equal(0, h.Registry.PendingCount);
        Assert.Equal(0, h.Registry.RetainedSessionCount);
    }

    [Fact]
    public void RetainedSessions_AreBounded_OldestFirst_AndReusedOnlyOnTheSameEpoch()
    {
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var first = registry.SessionFor("wf/0", Agent, "sandbox-1", _epoch);
        Assert.Equal(first, registry.SessionFor("wf/0", Agent, "sandbox-1", _epoch));
        var rotated = registry.SessionFor("wf/0", Agent, "sandbox-1", Guid.NewGuid());
        Assert.NotEqual(first.Session, rotated.Session);
        Assert.False(registry.ReleaseSession("wf/0", first)); // a stale owner cannot release the replacement

        for (var i = 1; i <= TaskCompletionRegistry.MaxRetainedSessions + 10; i++)
            registry.SessionFor("wf/" + i, Agent, "sandbox-1", _epoch);
        Assert.Equal(TaskCompletionRegistry.MaxRetainedSessions, registry.RetainedSessionCount);
        Assert.NotEqual(rotated.Session, registry.SessionFor("wf/0", Agent, "sandbox-1", rotated.Epoch).Session); // evicted
    }
}
