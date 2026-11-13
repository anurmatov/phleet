using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Fleet.WireFixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Client;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests;

/// <summary>
/// What the temporal bridge puts on the broker and sends to the orchestrator, and what it accepts
/// from agents, held to the samples under <c>tests/fixtures/wire/</c>. The agent's and the
/// orchestrator's ends of the same pairs are tested against the same files in their own projects.
/// </summary>
[Collection("fleet-workflow-config-static")]
public sealed class WireContractTests
{
    // ── what the bridge publishes ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("relay.temporal.directive.json", "org/app")]
    [InlineData("relay.temporal.directive.norepo.json", null)]
    public async Task A_delegation_is_published_to_the_agent_as_its_sample(string sample, string? repo)
    {
        var broker = new Broker();
        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions()),
            new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance),
            broker.ConnectionFactory,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<DelegateToAgentActivity>.Instance);

        // The one method every publish of the activity goes through: the first send, the re-send
        // and each continuation. It reads the workflow it runs for from the activity context, so it
        // is called inside one; the test environment's workflow is named "test".
        await new ActivityEnvironment().RunAsync(() => (Task)typeof(DelegateToAgentActivity)
            .GetMethod("PublishDirectiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(activity, ["Reviewer-One", "review the change", "wf-1/review", -100123L, repo, CancellationToken.None])!);

        var message = Assert.Single(broker.Published);
        Assert.Equal("fleet.group", message.Exchange);
        Assert.Equal("reviewer-one", message.RoutingKey);
        Assert.Equal(DeliveryModes.Persistent, message.Properties.DeliveryMode);
        WireGolden.AssertMatches(sample, message.Body);

        await broker.Channel.Received(1).ExchangeDeclareAsync(
            "fleet.group", "direct", Arg.Is(true), Arg.Is(false), Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_token_refresh_is_broadcast_to_every_agent_as_its_sample()
    {
        var broker = new Broker();
        var activity = new BroadcastTokenUpdateActivity(
            broker.ConnectionFactory, NullLogger<BroadcastTokenUpdateActivity>.Instance);

        await new ActivityEnvironment().RunAsync(() => activity.BroadcastAsync(
            provider: "codex", accessToken: "access", refreshToken: "refresh", expiresAt: 1790000000000,
            scopes: ["a", "b"], subscriptionType: "pro", rateLimitTier: null, idToken: "id", accountId: "acct"));

        var message = Assert.Single(broker.Published);
        Assert.Equal("fleet.relay", message.Exchange);
        Assert.Equal("", message.RoutingKey);
        Assert.Equal(DeliveryModes.Persistent, message.Properties.DeliveryMode);
        WireGolden.AssertMatches("relay.temporal.token-update.json", message.Body);

        await broker.Channel.Received(1).ExchangeDeclareAsync(
            "fleet.relay", "fanout", Arg.Is(true), Arg.Is(false), Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_delegated_approval_is_forwarded_to_the_orchestrator_as_its_sample()
    {
        string? path = null;
        string? body = null;
        var client = new HttpClient(new Handler(async request =>
        {
            path = request.RequestUri!.PathAndQuery;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"result":"sent"}""") };
        })) { BaseAddress = new Uri("http://orchestrator.invalid/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(OrchestratorEpicGrantForwarder.HttpClientName).Returns(client);

        var result = await new OrchestratorEpicGrantForwarder(factory).ForwardAsync("grant-1", new EpicGrantDecisionRequest(
            Namespace: "fleet", WorkflowId: "wf-1", Gate: "merge-approval", Decision: "approved",
            VisitId: "merge-approval:1", ArtifactRef: "org/app#12@abc123", Evidence: "consensus", Caller: "co-cto"));

        Assert.Equal("sent", result.Result);
        Assert.Equal("/api/epic-grants/grant-1/decisions", path);
        WireGolden.AssertMatches("epic-grant.decision-request.json", body!);
    }

    // ── a durable delegation: the same directive, and a cancel that names its execution ──────────

    /// <summary>The execution id of the one durable delegation the samples are about.</summary>
    private const string Execution = "e0e1e2e3e4e5e6e7e8e9eaebecedeeef";

    /// <summary>
    /// The record a durable delegation is sent from. Its workflow is named as the activity test
    /// environment names one, so a record without an execution id has to produce the very file the
    /// in-process activity's directive is held to.
    /// </summary>
    private static PendingDelegation Record(string executionId, string? repo) => new()
    {
        TaskId = "wf-1/review", CurrentTaskId = "wf-1/review", Agent = "Reviewer-One",
        CurrentInstruction = "review the change", Repo = repo,
        WorkflowId = "test", WorkflowType = "test", ExecutionId = executionId,
    };

    [Theory]
    // With no execution id the durable path writes what the in-process activity writes: the two
    // samples recorded from the activity, byte for byte, with no ExecutionId member at all.
    [InlineData("", "org/app", "relay.temporal.directive.json")]
    [InlineData("", null, "relay.temporal.directive.norepo.json")]
    // With one, the same directive and the id after it.
    [InlineData(Execution, "org/app", "relay.temporal.directive.execution.json")]
    [InlineData(Execution, null, "relay.temporal.directive.execution.norepo.json")]
    public async Task A_durable_delegation_is_published_to_the_agent_as_its_sample(
        string executionId, string? repo, string sample)
    {
        var broker = new Broker();
        var relay = DurableRelay(broker, Substitute.For<IHttpClientFactory>(), orchestratorUrl: "");

        await relay.PublishDirectiveAsync(Record(executionId, repo), CancellationToken.None);

        var message = Assert.Single(broker.Published);
        Assert.Equal("fleet.group", message.Exchange);
        Assert.Equal("reviewer-one", message.RoutingKey);
        Assert.Equal(DeliveryModes.Persistent, message.Properties.DeliveryMode);
        WireGolden.AssertMatches(sample, message.Body);
    }

    [Theory]
    // No execution id: the request an older bridge and the in-process activity send.
    [InlineData("", "cancel.bridge-request.txt")]
    // A durable delegation's cancel names its execution in one optional query parameter.
    [InlineData(Execution, "cancel.bridge-request.execution.txt")]
    public async Task A_cancel_is_sent_to_the_orchestrator_as_its_sample(string executionId, string sample)
    {
        var requests = new List<string>();
        var client = new HttpClient(new Handler(request =>
        {
            requests.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(WireGolden.Sample("cancel.agent-answer.cancelled.json")),
            });
        }));
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(client);
        var relay = DurableRelay(new Broker(), http, orchestratorUrl: "http://orchestrator.invalid");

        await relay.SendCancelAsync(Record(executionId, repo: null));

        WireGolden.AssertMatches(sample, Assert.Single(requests));
    }

    /// <summary>
    /// The agent's answer comes back through the orchestrator unread. The bridge takes the outcome
    /// from it only when it echoes the execution id the cancel named.
    /// </summary>
    [Theory]
    [InlineData("cancel.agent-answer.cancelled.json", "cancelled")]
    [InlineData("cancel.agent-answer.not-found.json", "not_found")]
    [InlineData("cancel.agent-answer.other-execution.json", "other_execution")]
    [InlineData("cancel.agent-answer.no-execution-id.json", "no_execution_id")]
    public async Task The_agents_answer_to_a_cancel_is_read_for_its_outcome(string sample, string outcome)
    {
        var log = new Entries();
        var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(WireGolden.Sample(sample)),
        })));
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(client);
        var relay = new DelegationRelay(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { OrchestratorUrl = "http://orchestrator.invalid", GroupChatId = -100123 }),
            new Broker().ConnectionFactory, http, new TypedEntries<DelegationRelay>(log));

        await relay.SendCancelAsync(Record(Execution, repo: null));

        Assert.Contains(log.Items, entry =>
            entry.Message == $"Cancel for agent Reviewer-One, taskId=wf-1/review, execution {Execution}: {outcome}");
    }

    private static DelegationRelay DurableRelay(
        Broker broker, IHttpClientFactory http, string orchestratorUrl) => new(
        Options.Create(new RabbitMqOptions()),
        Options.Create(new TemporalBridgeOptions { OrchestratorUrl = orchestratorUrl, GroupChatId = -100123 }),
        broker.ConnectionFactory, http,
        NullLogger<DelegationRelay>.Instance);

    // ── what the bridge accepts from an agent ────────────────────────────────────────────────────

    [Theory]
    [InlineData("relay.agent.response.json", "response", "wf-1/review", null)]
    [InlineData("relay.agent.partial-response.json", "partial-response", "wf-1/review~2", null)]
    // The answer to a durable delegation names the execution it is for.
    [InlineData("relay.agent.response.execution.json", "response", "wf-1/review", Execution)]
    [InlineData("relay.agent.partial-response.execution.json", "partial-response", "wf-1/review~2", Execution)]
    public void An_agents_reply_is_read_with_the_task_it_answers(string sample, string type, string taskId, string? executionId)
    {
        // The call TemporalRelayListener makes on every delivery.
        var message = JsonSerializer.Deserialize<RelayMessage>(WireGolden.Sample(sample))!;

        Assert.Equal(42, message.ChatId);
        Assert.Equal("agent1", message.Sender);
        Assert.Equal("the text", message.Text);
        Assert.Equal(DateTimeOffset.Parse(WireGolden.SampleTimestamp), message.Timestamp);
        Assert.Equal(type, message.Type);
        Assert.Equal(taskId, message.TaskId);
        Assert.Equal(executionId, message.ExecutionId);
        Assert.Null(message.CorrelationId);
        Assert.Null(message.WorkflowId);
        Assert.Null(message.SignalName);
    }

    [Fact]
    public async Task An_agents_workflow_signal_is_delivered_to_the_workflow_it_names()
    {
        var temporal = Substitute.For<ITemporalClient>();
        var handle = Substitute.For<WorkflowHandle>(temporal, "wf-1", null!, null!, null!);
        temporal.GetWorkflowHandle("wf-1", Arg.Any<string?>(), Arg.Any<string?>()).Returns(handle);
        var clients = Substitute.For<ITemporalClientFactory>();
        clients.GetClientAsync("fleet").Returns(temporal);
        var listener = new TemporalRelayListener(
            Options.Create(new RabbitMqOptions()),
            new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance),
            clients,
            NullLogger<TemporalRelayListener>.Instance);

        var message = JsonSerializer.Deserialize<RelayMessage>(WireGolden.Sample("relay.agent.workflow-signal.json"))!;
        var outcome = await listener.HandleWorkflowSignalAsync(message);

        Assert.Equal(RelaySignalOutcome.Delivered, outcome);
        Assert.Equal("workflow-signal", message.Type);
        var call = Assert.Single(handle.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(WorkflowHandle.SignalAsync));
        Assert.Equal("human-review", call.GetArguments()[0]);
        Assert.Equal(["""{"Decision":"approved"}"""], (IReadOnlyCollection<object?>)call.GetArguments()[1]!);
    }

    /// <summary>
    /// Every peer service reads <c>config.changed</c> through this client. A change that names none
    /// of its keys is read and set aside; one it could not read is logged as a warning instead.
    /// </summary>
    [Fact]
    public async Task A_configuration_change_from_the_orchestrator_is_read_by_a_peer()
    {
        var log = new Entries();
        await using var client = new Fleet.Shared.PeerConfigClient(
            "http://orchestrator.invalid", "", "broker", "fleet.orchestrator",
            literalKeys: ["FLEET_GROUP_CHAT_ID"], templateKeys: [], log);

        await (Task)typeof(Fleet.Shared.PeerConfigClient)
            .GetMethod("OnConfigChangedReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(client, [this, new RabbitMQ.Client.Events.BasicDeliverEventArgs(
                consumerTag: "tag", deliveryTag: 1, redelivered: false, exchange: "fleet.orchestrator",
                routingKey: "config.changed", properties: new BasicProperties(),
                body: WireGolden.SampleBytes("orchestrator.config-changed.json"))])!;

        Assert.Equal(
            [(Microsoft.Extensions.Logging.LogLevel.Debug, "config.changed received — no matching keys for this peer")],
            log.Items);
    }

    // ── names the bridge shares with other services ──────────────────────────────────────────────

    /// <summary>
    /// The queue and binding agents publish replies and signals to. The agent's routing key for a
    /// workflow signal is the same literal (<c>GroupRelayService.PublishWorkflowSignalAsync</c>).
    /// </summary>
    [Fact]
    public void The_bridge_listens_on_the_queue_agents_reply_to()
    {
        static string Constant(string name) => (string)typeof(TemporalRelayListener)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

        Assert.Equal("fleet.agent.temporal-bridge", Constant("QueueName"));
        Assert.Equal("temporal-bridge", Constant("RoutingKey"));
    }

    /// <summary>
    /// The approver-only gates. The orchestrator's signal buttons and its epic-grant scope name the
    /// same gates; a name that differs between the two is a gate one side does not protect.
    /// </summary>
    [Fact]
    public void The_approver_only_gates_are_exactly_these()
    {
        Assert.Equal(
            ["acceptance-decision", "advisory-review", "design-approval", "doc-review", "merge-approval",
             "request-approval", "request-input"],
            CeoGateSignals.All.Order(StringComparer.Ordinal));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Sent(string Exchange, string RoutingKey, BasicProperties Properties, byte[] Body);

    private sealed class Broker
    {
        public List<Sent> Published { get; } = [];
        public IChannel Channel { get; } = Substitute.For<IChannel>();
        public IConnectionFactory ConnectionFactory { get; } = Substitute.For<IConnectionFactory>();

        public Broker()
        {
            // The production call is the generic overload with BasicProperties.
            Channel.BasicPublishAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                    Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Published.Add(new Sent(call.ArgAt<string>(0), call.ArgAt<string>(1),
                        call.ArgAt<BasicProperties>(3), call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray()));
                    return ValueTask.CompletedTask;
                });

            var connection = Substitute.For<IConnection>();
            connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Channel));
            ConnectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));
        }
    }

    private sealed class Entries : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Items { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Items.Add((logLevel, formatter(state, exception)));
    }

    private sealed class TypedEntries<T>(Entries entries) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
