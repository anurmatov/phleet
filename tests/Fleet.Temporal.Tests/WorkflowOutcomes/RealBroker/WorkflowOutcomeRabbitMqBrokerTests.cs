using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Shared;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Fleet.Temporal.Tests.WorkflowOutcomes.RealBroker;

/// <summary>
/// #476 AC17 against a REAL RabbitMQ: publisher confirms, mandatory returns, the reject-publish
/// quota, a refused queue declaration, redelivery on channel close and consumer restart — through
/// the production <see cref="RabbitMqWorkflowOutcomeBroker"/>, <see cref="TemporalRelayListener"/>
/// and <see cref="WorkflowOutcomeLateResultConsumer"/>.
///
/// <para>
/// ⚠️ FAILS when <see cref="Variable"/> is unset or the broker is unreachable. It never skips: a
/// skipped broker suite would read as passing evidence. Point it at a throwaway broker, e.g.
/// <c>export FLEET_WORKFLOW_OUTCOMES_BROKER=amqp://guest:guest@127.0.0.1:5672/</c>. Every queue it
/// touches has a per-test random name and is deleted afterwards; nothing else is declared.
/// </para>
/// </summary>
[Trait("WorkflowOutcomeSuite", "late-results")]
public sealed class WorkflowOutcomeRabbitMqBrokerTests : IAsyncLifetime
{
    public const string Variable = "FLEET_WORKFLOW_OUTCOMES_BROKER";
    private const string TaskId = "wf-real/implement";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private string _uri = "";
    private TemporalBridgeOptions _options = null!;
    private string Source => "wo-test.relay." + _suffix;
    private string Late => _options.WorkflowOutcomeLateQueue;
    private string Quarantine => _options.WorkflowOutcomeQuarantineQueue;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{Variable} is not set, so the real-broker late-result suite has no broker to run against. " +
                "It FAILS rather than skipping on purpose. Point it at a throwaway broker, e.g. " +
                $"export {Variable}='amqp://guest:guest@127.0.0.1:5672/'");
        _uri = configured;
        _options = new TemporalBridgeOptions
        {
            WorkflowOutcomeLateCaptureEnabled = true,
            WorkflowOutcomeLateQueue = "wo-test.late." + _suffix,
            WorkflowOutcomeQuarantineQueue = "wo-test.quarantine." + _suffix,
            OrchestratorUrl = "https://orchestrator.example",
            OrchestratorAuthToken = WorkflowOutcomeTestValues.AdminToken,
        };

        // An unreachable broker is a missing prerequisite, not a failing assertion.
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(Source, durable: true, exclusive: false, autoDelete: false);
    }

    public async Task DisposeAsync()
    {
        if (_uri.Length == 0) return;
        await using var connection = await Factory().CreateConnectionAsync();
        if (_ownsRelayTopology)
        {
            try
            {
                await using var channel = await connection.CreateChannelAsync();
                await channel.ExchangeDeleteAsync(Exchange);
            }
            catch (Exception)
            {
                // Already gone is the desired end state.
            }
        }

        foreach (var queue in _ownsRelayTopology ? new[] { Source, Late, Quarantine, TemporalBridgeOptions.RelayQueue } : new[] { Source, Late, Quarantine })
        {
            try
            {
                await using var channel = await connection.CreateChannelAsync();
                await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
            }
            catch (Exception)
            {
                // Already gone is the desired end state.
            }
        }
    }

    private string Exchange => "wo-test.exchange." + _suffix;
    private bool _ownsRelayTopology;

    /// <summary>
    /// The listener binds the fixed relay queue. It must not exist yet — that is how this suite
    /// knows it is on a throwaway broker and may delete the queue afterwards.
    /// </summary>
    private async Task ClaimRelayQueueAsync()
    {
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        try
        {
            await channel.QueueDeclarePassiveAsync(TemporalBridgeOptions.RelayQueue);
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            _ownsRelayTopology = true;
            return;
        }

        throw new InvalidOperationException(
            $"{TemporalBridgeOptions.RelayQueue} already exists on this broker: run this suite against a throwaway broker only");
    }

    private TemporalRelayListener RunningListener(TemporalBridgeOptions options, IWorkflowOutcomeBroker? broker, TaskCompletionRegistry registry,
        RecordingLogger<TemporalRelayListener> logger, TimeProvider time) =>
        new(
            Options.Create(new RabbitMqOptions { Host = "127.0.0.1", Exchange = Exchange }),
            registry,
            new TemporalClientFactory(Options.Create(new TemporalBridgeOptions())),
            logger,
            Options.Create(options),
            broker,
            time)
        {
            ConnectionFactoryOverride = new ConnectionFactory
            {
                Uri = new Uri(_uri),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            },
        };

    private async Task PublishReplyToRelayAsync(byte[] body)
    {
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.BasicPublishAsync(Exchange, "temporal-bridge", mandatory: true, new BasicProperties { Persistent = true }, body);
    }

    private async Task<uint?> ReadyOrNullAsync(string queue)
    {
        try
        {
            return await ReadyAsync(queue);
        }
        catch (OperationInterruptedException)
        {
            return null;
        }
    }

    private ConnectionFactory Factory() => new() { Uri = new Uri(_uri), AutomaticRecoveryEnabled = false };

    private RabbitMqWorkflowOutcomeBroker Broker() =>
        new(Factory(), _options, NullLogger<RabbitMqWorkflowOutcomeBroker>.Instance);

    private TemporalRelayListener Listener(IWorkflowOutcomeBroker broker) => new(
        Options.Create(new RabbitMqOptions()),
        new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance),
        new TemporalClientFactory(Options.Create(new TemporalBridgeOptions())),
        NullLogger<TemporalRelayListener>.Instance,
        Options.Create(_options),
        broker);

    private WorkflowOutcomeLateResultConsumer Consumer(IWorkflowOutcomeBroker broker, FakeLateResultsApi api, TimeProvider time) => new(
        new RabbitMqOptions(), _options, broker, api.Factory(WorkflowOutcomeLateResultConsumer.HttpClientName),
        NullLogger<WorkflowOutcomeLateResultConsumer>.Instance, time, Factory());

    private async Task<uint> ReadyAsync(string queue)
    {
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    private async Task PublishAsync(string queue, byte[] body, BasicProperties properties)
    {
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, properties, body);
    }

    /// <summary>Takes one relay delivery and lets the REAL listener settle it on a real channel.</summary>
    private async Task<(List<string> Pauses, uint SourceAfterClose)> CaptureOneAsync(IWorkflowOutcomeBroker broker)
    {
        var pauses = new List<string>();
        await using (var connection = await Factory().CreateConnectionAsync())
        {
            var channel = await connection.CreateChannelAsync();
            var delivery = await channel.BasicGetAsync(Source, autoAck: false);
            Assert.NotNull(delivery);
            await Listener(broker).HandleDeliveryAsync(
                delivery!.Body, delivery.BasicProperties,
                new RabbitMqOutcomeDelivery(channel, delivery.DeliveryTag, (_, reason) => pauses.Add(reason)));

            // Closing returns anything left unacknowledged to the relay queue.
            await channel.CloseAsync();
        }

        return (pauses, await ReadyAsync(Source));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException("timed out waiting for " + what);
    }

    private static string? HeaderString(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers is not null && properties.Headers.TryGetValue(name, out var raw) && raw is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    [Fact]
    public async Task Activate_DeclaresDurableQuotaQueues_WithRejectPublish()
    {
        await using var broker = Broker();

        Assert.Null(await broker.ActivateAsync(CancellationToken.None));

        await using var connection = await Factory().CreateConnectionAsync();
        foreach (var queue in new[] { Late, Quarantine })
        {
            // Same arguments: idempotent. Any other arguments: the broker refuses (406), which
            // proves the declared ones are exactly the quota and reject-publish, durable.
            await using (var same = await connection.CreateChannelAsync())
                await same.QueueDeclareAsync(queue, true, false, false, WorkflowOutcomeLateEnvelope.QueueArguments());

            await using var other = await connection.CreateChannelAsync();
            var refused = await Assert.ThrowsAsync<OperationInterruptedException>(() =>
                other.QueueDeclareAsync(queue, true, false, false, new Dictionary<string, object?>()));
            Assert.Equal(406, refused.ShutdownReason!.ReplyCode);
        }
    }

    /// <summary>Relay reply → confirmed late copy → original ACKed → stored by the API → late copy ACKed.</summary>
    [Fact]
    public async Task Capture_ThenStore_EachAckFollowsItsConfirmedCommit()
    {
        await using var broker = Broker();
        var body = WorkflowOutcomeTestValues.Reply(TaskId);
        await PublishAsync(Source, body, new BasicProperties { Persistent = true });

        var (pauses, sourceAfter) = await CaptureOneAsync(broker);

        Assert.Empty(pauses);
        Assert.Equal(0u, sourceAfter);
        Assert.Equal(1u, await ReadyAsync(Late));

        var api = new FakeLateResultsApi();
        var consumer = Consumer(broker, api, TimeProvider.System);
        await consumer.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(api.Stored.Count == 1), "the late result to be stored");
        await consumer.StopAsync(CancellationToken.None);

        Assert.Equal(0u, await ReadyAsync(Late));          // acknowledged, not merely held
        using var request = JsonDocument.Parse(Assert.Single(api.Requests).Body);
        Assert.Equal(WorkflowOutcomeHashes.Sha256Hex(body), request.RootElement.GetProperty("messageId").GetString());
        Assert.Equal(TaskId, request.RootElement.GetProperty("taskId").GetString());
        Assert.Equal(0, request.RootElement.GetProperty("attempt").GetInt32());
    }

    /// <summary>
    /// The API is down: replacements 1/2/3 go back through the real broker with confirms (the
    /// consumer's waits run on a fake clock), and the third delayed failure lands in quarantine.
    /// </summary>
    [Fact]
    public async Task Consumer_ApiOutage_ReplacesThenQuarantines()
    {
        await using var broker = Broker();
        Assert.Null(await broker.ActivateAsync(CancellationToken.None));
        // Whole milliseconds, like the dueAt header, so the waits compare exactly.
        var time = new AutoAdvanceTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var body = WorkflowOutcomeTestValues.Reply(TaskId);
        await PublishAsync(Late, body, new BasicProperties
        {
            Persistent = true,
            MessageId = "real-message-1",
            Headers = new Dictionary<string, object?>(WorkflowOutcomeLateEnvelope.LateHeaders(0, time.GetUtcNow(), time.GetUtcNow(), TaskId)),
        });

        var api = new FakeLateResultsApi { AlwaysFail = () => HttpStatusCode.ServiceUnavailable };
        var consumer = Consumer(broker, api, time);
        await consumer.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () => await ReadyAsync(Quarantine) == 1, "the quarantined message");
        await consumer.StopAsync(CancellationToken.None);

        Assert.Equal(0u, await ReadyAsync(Late));
        Assert.Equal([0, 1, 2, 3], api.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("attempt").GetInt32()));
        Assert.Equal(new[] { 5, 30, 60 }.Select(s => TimeSpan.FromSeconds(s)), time.Delays.Where(d => d > TimeSpan.Zero).Take(3));
        Assert.Equal(1, consumer.Alarm.Counts["store_failed_after_retries"]);

        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var quarantined = await channel.BasicGetAsync(Quarantine, autoAck: true);
        Assert.NotNull(quarantined);
        Assert.Equal(body, quarantined!.Body.ToArray());
        Assert.Equal("real-message-1", quarantined.BasicProperties.MessageId);
        Assert.Equal(DeliveryModes.Persistent, quarantined.BasicProperties.DeliveryMode);
        Assert.Equal("store_failed_after_retries", HeaderString(quarantined.BasicProperties, WorkflowOutcomeLateEnvelope.QuarantineReasonHeader));
    }

    /// <summary>The late queue is at its 256 MiB quota: reject-publish nacks the copy, the reply stays unacknowledged.</summary>
    [Fact]
    public async Task Capture_QueueAtQuota_RejectPublishPausesAndLeavesTheReplyQueued()
    {
        await using var broker = Broker();
        Assert.Null(await broker.ActivateAsync(CancellationToken.None));

        var filler = new byte[16 * 1024 * 1024];
        var nacked = false;
        await using (var connection = await Factory().CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true)))
        {
            for (var i = 0; i < 20 && !nacked; i++)
            {
                try
                {
                    await channel.BasicPublishAsync(string.Empty, Late, mandatory: true, new BasicProperties(), filler);
                }
                catch (PublishException ex) when (!ex.IsReturn)
                {
                    nacked = true;
                }
            }
        }

        Assert.True(nacked, "the quota never refused a publish");

        await PublishAsync(Source, WorkflowOutcomeTestValues.Reply(TaskId), new BasicProperties { Persistent = true });
        var (pauses, sourceAfter) = await CaptureOneAsync(broker);

        Assert.Contains("Nacked", Assert.Single(pauses));
        Assert.Equal(1u, sourceAfter);                     // still with the broker, never dropped

        // Once the operator drains the late queue, the very same reply is captured.
        await using (var connection = await Factory().CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
            await channel.QueuePurgeAsync(Late);
        var (retryPauses, retrySource) = await CaptureOneAsync(broker);
        Assert.Empty(retryPauses);
        Assert.Equal(0u, retrySource);
        Assert.Equal(1u, await ReadyAsync(Late));
    }

    /// <summary>The late queue vanished: the mandatory copy is returned, nothing is ACKed, the next attempt re-declares it.</summary>
    [Fact]
    public async Task Capture_QueueMissing_ReturnIsNotAcked_AndTheNextAttemptRedeclares()
    {
        await using var broker = Broker();
        Assert.Null(await broker.ActivateAsync(CancellationToken.None));
        await using (var connection = await Factory().CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
            await channel.QueueDeleteAsync(Late, ifUnused: false, ifEmpty: false);

        await PublishAsync(Source, WorkflowOutcomeTestValues.Reply(TaskId), new BasicProperties { Persistent = true });
        var (pauses, sourceAfter) = await CaptureOneAsync(broker);

        Assert.Contains("Returned", Assert.Single(pauses));
        Assert.Equal(1u, sourceAfter);

        var (retryPauses, retrySource) = await CaptureOneAsync(broker);
        Assert.Empty(retryPauses);
        Assert.Equal(0u, retrySource);
        Assert.Equal(1u, await ReadyAsync(Late));
    }

    /// <summary>A queue already declared with other arguments blocks activation; capture then holds instead of dropping.</summary>
    [Fact]
    public async Task Activate_ConflictingQueueArguments_BlocksCapture_AndTheReplyIsHeld()
    {
        await using (var connection = await Factory().CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
            await channel.QueueDeclareAsync(Late, durable: true, exclusive: false, autoDelete: false);

        await using var broker = Broker();
        Assert.Equal("queue_declare_refused_406", await broker.ActivateAsync(CancellationToken.None));

        await PublishAsync(Source, WorkflowOutcomeTestValues.Reply(TaskId), new BasicProperties { Persistent = true });
        var (pauses, sourceAfter) = await CaptureOneAsync(broker);

        Assert.Contains("Unavailable", Assert.Single(pauses));
        Assert.Equal(1u, sourceAfter);
    }

    /// <summary>A consumer stopped while holding a not-yet-due message returns it; the restarted one stores it.</summary>
    [Fact]
    public async Task Consumer_RestartWhileHolding_RedeliversAndStores()
    {
        await using var broker = Broker();
        Assert.Null(await broker.ActivateAsync(CancellationToken.None));
        var now = DateTimeOffset.UtcNow;
        await PublishAsync(Late, WorkflowOutcomeTestValues.Reply(TaskId), new BasicProperties
        {
            Persistent = true,
            MessageId = "restart-1",
            Headers = new Dictionary<string, object?>(WorkflowOutcomeLateEnvelope.LateHeaders(1, now.AddSeconds(60), now, TaskId)),
        });

        var api = new FakeLateResultsApi();
        var first = Consumer(broker, api, TimeProvider.System);
        await first.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () => await ReadyAsync(Late) == 0, "the first consumer to hold the message");
        await first.StopAsync(CancellationToken.None);

        Assert.Empty(api.Requests);
        Assert.Equal(1u, await ReadyAsync(Late));

        var restarted = Consumer(broker, api, new AutoAdvanceTimeProvider(now));
        await restarted.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(api.Stored.Count == 1), "the restarted consumer to store it");
        await restarted.StopAsync(CancellationToken.None);

        Assert.Equal(0u, await ReadyAsync(Late));
        Assert.Equal(1, JsonDocument.Parse(Assert.Single(api.Requests).Body).RootElement.GetProperty("attempt").GetInt32());
    }

    /// <summary>Headers as they really arrive (strings as AMQP long strings): a wrong attempt is quarantined, not posted.</summary>
    [Fact]
    public async Task Consumer_InvalidAttemptFromTheWire_IsQuarantined()
    {
        await using var broker = Broker();
        Assert.Null(await broker.ActivateAsync(CancellationToken.None));
        var headers = new Dictionary<string, object?>(WorkflowOutcomeLateEnvelope.LateHeaders(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TaskId))
        {
            [WorkflowOutcomeLateEnvelope.AttemptHeader] = "seven",
        };
        await PublishAsync(Late, WorkflowOutcomeTestValues.Reply(TaskId), new BasicProperties { Persistent = true, MessageId = "bad-1", Headers = headers });

        var api = new FakeLateResultsApi();
        var consumer = Consumer(broker, api, new AutoAdvanceTimeProvider(DateTimeOffset.UtcNow));
        await consumer.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () => await ReadyAsync(Quarantine) == 1, "the quarantined message");
        await consumer.StopAsync(CancellationToken.None);

        Assert.Empty(api.Requests);
        Assert.Equal(0u, await ReadyAsync(Late));
        Assert.Equal(1, consumer.Alarm.Counts["invalid_attempt"]);
    }

    /// <summary>
    /// The listener's own consumer glue on a real broker: the late queue is missing, so the first
    /// handoff is returned — the reply is not acknowledged, the consumer is cancelled and its
    /// channel closed (the reply goes back to the relay queue) — and after the backoff the consumer
    /// reopens, the reply is redelivered and captured, and only then acknowledged.
    /// </summary>
    [Fact]
    public async Task Listener_RealConsumer_ReturnedHandoffPausesWithoutAck_ThenResumesAndCaptures()
    {
        await ClaimRelayQueueAsync();
        await using var broker = Broker();
        Assert.Null(await broker.ActivateAsync(CancellationToken.None));
        await using (var connection = await Factory().CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
            await channel.QueueDeleteAsync(Late, ifUnused: false, ifEmpty: false);

        var logger = new RecordingLogger<TemporalRelayListener>();
        var listener = RunningListener(_options, broker, new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance),
            logger, new AutoAdvanceTimeProvider(DateTimeOffset.UtcNow));
        await listener.StartAsync(CancellationToken.None);
        try
        {
            await PublishReplyToRelayAsync(WorkflowOutcomeTestValues.Reply(TaskId));

            await WaitUntilAsync(async () => await ReadyOrNullAsync(Late) == 1 && await ReadyAsync(TemporalBridgeOptions.RelayQueue) == 0,
                "the reply to be captured after the pause");
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }

        Assert.Contains(logger.Entries, e => e.Message.Contains("not captured (Returned"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("Relay consumer resumed"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("captured to " + Late));
        Assert.Equal(0u, await ReadyAsync(TemporalBridgeOptions.RelayQueue));
        Assert.DoesNotContain(WorkflowOutcomeTestValues.SecretText, logger.AllText);
    }

    /// <summary>Matched replies are acknowledged with nothing captured; with capture off an orphan is discarded as before.</summary>
    [Fact]
    public async Task Listener_RealConsumer_MatchedReplyAndCaptureOff_KeepTheLegacyBehaviour()
    {
        await ClaimRelayQueueAsync();
        await using var broker = Broker();
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var waiter = registry.Register("wf-real/matched");

        var captureOn = RunningListener(_options, broker, registry, new RecordingLogger<TemporalRelayListener>(), TimeProvider.System);
        await captureOn.StartAsync(CancellationToken.None);
        try
        {
            await PublishReplyToRelayAsync(WorkflowOutcomeTestValues.Reply("wf-real/matched"));
            Assert.Equal("completed", (await waiter.Task.WaitAsync(TimeSpan.FromSeconds(30))).Status);
            await WaitUntilAsync(async () => await ReadyAsync(TemporalBridgeOptions.RelayQueue) == 0, "the matched reply to be acknowledged");
        }
        finally
        {
            await captureOn.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0u, await ReadyAsync(Late));

        var off = new TemporalBridgeOptions
        {
            WorkflowOutcomeLateQueue = _options.WorkflowOutcomeLateQueue,
            WorkflowOutcomeQuarantineQueue = _options.WorkflowOutcomeQuarantineQueue,
        };
        var offLogger = new RecordingLogger<TemporalRelayListener>();
        var captureOff = RunningListener(off, broker: null, new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance), offLogger, TimeProvider.System);
        await captureOff.StartAsync(CancellationToken.None);
        try
        {
            await PublishReplyToRelayAsync(WorkflowOutcomeTestValues.Reply(TaskId));
            await WaitUntilAsync(() => Task.FromResult(offLogger.AllText.Contains("Orphan response for TaskId=" + TaskId)), "the legacy orphan discard");
            await WaitUntilAsync(async () => await ReadyAsync(TemporalBridgeOptions.RelayQueue) == 0, "the orphan to be discarded");
        }
        finally
        {
            await captureOff.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0u, await ReadyAsync(Late));
    }
}
