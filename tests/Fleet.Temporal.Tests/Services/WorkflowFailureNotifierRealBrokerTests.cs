using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Fleet.Temporal.Tests.Workflows;
using Fleet.Temporal.Workflows.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// Opt-in: runs only when <c>FLEET_TEST_RABBITMQ_HOST</c> names a DISPOSABLE RabbitMQ (port 5672,
/// default guest account). Without it the test is reported as skipped with an UNRUN reason, never as
/// passed. Never point it at a broker real agents use.
/// </summary>
public sealed class RealBrokerFactAttribute : FactAttribute
{
    public const string HostVariable = "FLEET_TEST_RABBITMQ_HOST";

    public RealBrokerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostVariable)))
            Skip = $"UNRUN: real-broker acceptance needs {HostVariable} (see docs/workflow-failure-notifier.md)";
    }
}

/// <summary>
/// The #488 real-AMQP acceptance seam. Unlike <see cref="WorkflowFailureNotifierLocalTests"/>, nothing on
/// the delivery path is substituted: a real local Temporal server, the production
/// <see cref="TemporalWorkflowFailureSource"/>/<see cref="TemporalFailureNotificationSender"/>, NotifyCtoWorkflow,
/// <see cref="DelegateToAgentActivity"/> with a real <see cref="ConnectionFactory"/>, and
/// <see cref="TemporalRelayListener"/> receiving the coordinator's reply over AMQP. The coordinator is a
/// synthetic AMQP consumer on a per-run exchange; no real agent or human is involved.
///
/// The first attempt for every failure runs against an unreachable broker endpoint (a real
/// <see cref="ConnectionFactory"/> on a closed port, bounded connect timeout), so each notification run
/// closes Failed; the next attempt, on the real broker, is answered and delivered. A restarted notifier
/// on the same state file then sends nothing, and no synthetic secret reaches the coordinator.
/// </summary>
[Collection("workflow-failure-real-broker")]
public sealed class WorkflowFailureNotifierRealBrokerTests : IClassFixture<LocalTemporalFixture>, IDisposable
{
    private const string UnreachablePortVariable = "FLEET_TEST_RABBITMQ_UNREACHABLE_PORT";
    private const string SecretType = "synthetic-secret-123";

    private readonly LocalTemporalFixture _fixture;
    private readonly string _dir = Directory.CreateTempSubdirectory("wf-failure-real-broker-").FullName;

    public WorkflowFailureNotifierRealBrokerTests(LocalTemporalFixture fixture) => _fixture = fixture;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [RealBrokerFact]
    public async Task RealAmqp_UnreachableFirstAttemptThenDelivery_RestartSendsNothing_NoSecrets()
    {
        var host = Environment.GetEnvironmentVariable(RealBrokerFactAttribute.HostVariable)!;
        var unreachablePort = int.TryParse(Environment.GetEnvironmentVariable(UnreachablePortVariable), out var p) ? p : 1;
        var run = Guid.NewGuid().ToString("N")[..10];
        var coordinatorName = $"test-coordinator-{run}";
        var rabbit = Options.Create(new RabbitMqOptions { Host = host, Exchange = $"phleet488-{run}" });
        var bridge = Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = 30, OrchestratorUrl = "" });
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var queue = $"wf-failure-real-{run}";
        var ns = _fixture.Client.Options.Namespace;
        var factory = new FixedClientFactory(_fixture.Client);
        var source = new TemporalWorkflowFailureSource(factory);
        var statePath = Path.Combine(_dir, "workflow-failures.json");

        DelegateToAgentActivity Activity(IConnectionFactory connections) => new(
            rabbit, bridge, registry, connections, new PlainHttpClientFactory(), NullLogger<DelegateToAgentActivity>.Instance);

        TemporalWorker Worker(IConnectionFactory connections) => new(_fixture.Client, new TemporalWorkerOptions(queue)
            .AddAllActivities(Activity(connections))
            .AddWorkflow<NotifyCtoWorkflow>()
            .AddWorkflow<SyntheticOutcomeWorkflow>());

        WorkflowFailureNotifier Notifier()
        {
            var options = Options.Create(new WorkflowFailureNotifierOptions
            {
                Enabled = true,
                CoordinatorAgent = coordinatorName,
                NotificationNamespace = ns,
                NotificationTaskQueue = queue,
                StateFile = statePath,
            });
            return new WorkflowFailureNotifier(
                options, Options.Create(new TemporalBridgeOptions { Namespaces = [ns] }), new CtoAgentConfigService(),
                source, new TemporalFailureNotificationSender(factory, options), NullLogger<WorkflowFailureNotifier>.Instance);
        }

        await using var coordinator = await SyntheticCoordinator.StartAsync(host, rabbit.Value.Exchange, coordinatorName);
        var listener = new TemporalRelayListener(
            rabbit, registry, new TemporalClientFactory(bridge), NullLogger<TemporalRelayListener>.Instance);
        await listener.StartAsync(CancellationToken.None);

        var handles = new Dictionary<string, WorkflowHandle>();
        var notifier = Notifier();
        var closedPort = new ConnectionFactory
        {
            HostName = host, Port = unreachablePort, RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
            AutomaticRecoveryEnabled = false,
        };

        // Phase 1: the broker endpoint is unreachable for every first attempt.
        using (var unreachable = Worker(closedPort))
        {
            await unreachable.ExecuteAsync(async () =>
            {
                handles["explicit"] = await CloseAsync($"real-explicit-{run}", "explicit", queue);
                handles["error"] = await CloseAsync($"real-error-{run}", "error", queue);
                handles["timeout"] = await CloseAsync($"real-timeout-{run}", "hold", queue, TimeSpan.FromSeconds(2));
                handles["success"] = await CloseAsync($"real-success-{run}", "success", queue);
                handles["canceled"] = await CloseAsync($"real-hold-{run}", "hold", queue, cancel: true);
                await WaitUntilListedAsync(source, ns, 3, run);

                await notifier.RunCycleAsync(CancellationToken.None);
                Assert.Equal(3, notifier.Snapshot().InFlight.Count);
                foreach (var key in new[] { "explicit", "error", "timeout" })
                    await WaitForStatusAsync(AttemptId(ns, handles[key], 1), WorkflowExecutionStatus.Failed);
            });
        }
        Assert.Empty(coordinator.Directives);

        // Phase 2: the real broker; the coordinator answers over AMQP through TemporalRelayListener.
        using (var reachable = Worker(new ConnectionFactory { HostName = host }))
        {
            await reachable.ExecuteAsync(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (notifier.Snapshot().Delivered < 3 && DateTime.UtcNow < deadline)
                {
                    await notifier.RunCycleAsync(CancellationToken.None);
                    await Task.Delay(500);
                }
                Assert.Equal(3, notifier.Snapshot().Delivered);
                Assert.Empty(notifier.Snapshot().Parked);

                foreach (var key in new[] { "explicit", "error", "timeout" })
                {
                    Assert.Equal(WorkflowExecutionStatus.Failed, await StatusAsync(AttemptId(ns, handles[key], 1)));
                    Assert.Equal(WorkflowExecutionStatus.Completed, await StatusAsync(AttemptId(ns, handles[key], 2)));
                }

                var received = coordinator.Directives.ToList();
                Assert.Equal(3, received.Count);
                Assert.Contains(received, d => d.Contains($"workflowId: {handles["explicit"].Id}\n") && d.Contains("cause: explicit_fail"));
                Assert.Contains(received, d => d.Contains($"workflowId: {handles["error"].Id}\n") && d.Contains("cause: application_error\n"));
                Assert.Contains(received, d => d.Contains($"workflowId: {handles["timeout"].Id}\n") && d.Contains("status: TimedOut"));
                Assert.DoesNotContain(received, d => d.Contains(handles["success"].Id) || d.Contains(handles["canceled"].Id));
                Assert.All(received, d => Assert.DoesNotContain(SecretType, d));
                Assert.All(received, d => Assert.DoesNotContain("hunter2", d));
                Assert.DoesNotContain(SecretType, File.ReadAllText(statePath));

                // A restart on the same state file: nothing is sent again.
                var restarted = Notifier();
                await restarted.RunCycleAsync(CancellationToken.None);
                await restarted.RunCycleAsync(CancellationToken.None);
                await Task.Delay(1000);
                Assert.Equal(3, coordinator.Directives.Count);
                Assert.Equal(3, restarted.Snapshot().Delivered);
            });
        }

        await listener.StopAsync(CancellationToken.None);
    }

    private static string AttemptId(string ns, WorkflowHandle failed, int attempt) =>
        $"{WorkflowFailureNotifier.NotificationIdPrefix}{ns}-{failed.ResultRunId}-{attempt}";

    private async Task<WorkflowExecutionStatus> StatusAsync(string workflowId) =>
        (await _fixture.Client.GetWorkflowHandle(workflowId).DescribeAsync()).Status;

    private async Task WaitForStatusAsync(string workflowId, WorkflowExecutionStatus expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await StatusAsync(workflowId) == expected) return;
            await Task.Delay(250);
        }
        Assert.Equal(expected, await StatusAsync(workflowId));
    }

    private async Task<WorkflowHandle> CloseAsync(string id, string mode, string queue, TimeSpan? timeout = null, bool cancel = false)
    {
        var handle = await _fixture.Client.StartWorkflowAsync(
            (SyntheticOutcomeWorkflow wf) => wf.RunAsync(mode),
            new WorkflowOptions(id, queue) { ExecutionTimeout = timeout });
        if (cancel)
        {
            await Task.Delay(500);
            await handle.CancelAsync();
        }
        try { await handle.GetResultAsync(); } catch (WorkflowFailedException) { }
        return handle;
    }

    private static async Task WaitUntilListedAsync(IWorkflowFailureSource source, string ns, int count, string tag)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var runs = await source.ListFailedAsync(ns, DateTime.UtcNow.AddHours(-1), CancellationToken.None);
            if (runs.Count(r => r.WorkflowId.Contains(tag)) >= count) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("failed runs never appeared in visibility");
    }

    private sealed class FixedClientFactory(ITemporalClient client) : ITemporalClientFactory
    {
        public Task<ITemporalClient> GetClientAsync(string @namespace) => Task.FromResult(client);
    }

    /// <summary>Cancellation is the only HTTP use and is not exercised here.</summary>
    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// The test coordinator: an AMQP consumer bound to its agent routing key on the run's exchange. It
    /// answers each directive the way an agent does, a <c>response</c> with a <c>[status: completed]</c>
    /// prefix to the <c>temporal-bridge</c> routing key.
    /// </summary>
    private sealed class SyntheticCoordinator : IAsyncDisposable
    {
        private readonly IConnection _connection;
        private readonly IChannel _channel;
        public ConcurrentQueue<string> Directives { get; } = new();

        private SyntheticCoordinator(IConnection connection, IChannel channel)
        {
            _connection = connection;
            _channel = channel;
        }

        public static async Task<SyntheticCoordinator> StartAsync(string host, string exchange, string name)
        {
            var connection = await new ConnectionFactory { HostName = host, ClientProvidedName = name }.CreateConnectionAsync();
            var channel = await connection.CreateChannelAsync();
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true, autoDelete: false);
            var queue = await channel.QueueDeclareAsync($"{name}.inbox", durable: false, exclusive: true, autoDelete: true);
            await channel.QueueBindAsync(queue.QueueName, exchange, routingKey: name.ToLowerInvariant());

            var coordinator = new SyntheticCoordinator(connection, channel);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                var directive = JsonSerializer.Deserialize<RelayMessage>(Encoding.UTF8.GetString(ea.Body.Span))!;
                coordinator.Directives.Enqueue(directive.Text);
                var reply = new RelayMessage(directive.ChatId, name, "[status: completed] noted", DateTimeOffset.UtcNow,
                    Type: RelayMessageType.Response, TaskId: directive.TaskId);
                await channel.BasicPublishAsync(exchange, "temporal-bridge", mandatory: false,
                    basicProperties: new BasicProperties { DeliveryMode = DeliveryModes.Persistent },
                    body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply)));
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            };
            await channel.BasicConsumeAsync(queue.QueueName, autoAck: false, consumer: consumer);
            return coordinator;
        }

        public async ValueTask DisposeAsync()
        {
            await _channel.CloseAsync();
            await _connection.CloseAsync();
        }
    }
}
