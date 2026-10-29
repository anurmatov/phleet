using System.Text.Json;
using Fleet.Agent.Models;
using Fleet.Bridge.Configuration;
using Fleet.Bridge.Services;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Agent.Tests;

/// <summary>
/// #492 managed workflow delegation, agent side: a typed workflow directive is admitted only from the epoch
/// control principal on fleet.group routed to this agent with this enrollment's sandbox and epoch, runs as an
/// ordinary Temporal directive, and its reply goes out as a typed workflow reply on the bridge reply route.
/// </summary>
public sealed class ManagedStockAgentWorkflowDirectiveTests
{
    private static AsyncEventingBasicConsumer Consumer(IChannel channel) =>
        channel.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();

    private static object[][] Publishes(IChannel channel) => channel.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == "BasicPublishAsync").Select(c => c.GetArguments()).ToArray();

    private static byte[] Directive(Guid epoch, string sandbox = "synthetic-sandbox", string? taskId = "wf/task-1",
        string? session = null, string sender = "temporal-bridge") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            ChatId = 0, Sender = sender, Text = "synthetic task", Timestamp = DateTimeOffset.UtcNow, Type = "workflow-directive",
            CorrelationId = session ?? Guid.NewGuid().ToString("N"), TaskId = taskId, Epoch = epoch, Sandbox = sandbox
        });

    [Fact]
    public async Task AdmittedDirective_RunsWithTaskId_AndRepliesOnTheBridgeRouteWithItsContext()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var received = new List<(string Sender, string Type, string? Correlation, string? TaskId)>();
        relay.MessageReceived += (_, sender, _, type, correlation, taskId, _, _) => received.Add((sender, type, correlation, taskId));
        await relay.InitializeAsync(default);
        var session = Guid.NewGuid().ToString("N");

        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1",
            new BasicProperties { UserId = fixture.Binding.Principal("control") }, Directive(fixture.Epoch, session: session));

        var admitted = Assert.Single(received);
        Assert.Equal(("temporal-bridge", RelayMessageType.Directive, (string?)null, (string?)"wf/task-1"), admitted);

        await relay.PublishToAgentAsync("temporal-bridge", 0, "synthetic result", RelayMessageType.Response, taskId: "wf/task-1");
        var publish = Assert.Single(Publishes(fixture.Channel));
        Assert.Equal("fleet.group", publish[0]);
        Assert.Equal("bridge", publish[1]);
        Assert.Equal(fixture.Binding.Principal("workload"), publish.OfType<BasicProperties>().Single().UserId);
        using var reply = JsonDocument.Parse((ReadOnlyMemory<byte>)publish[4]!);
        Assert.Equal("agent1", reply.RootElement.GetProperty("Sender").GetString());
        Assert.Equal("response", reply.RootElement.GetProperty("Type").GetString());
        Assert.Equal("wf/task-1", reply.RootElement.GetProperty("TaskId").GetString());
        Assert.Equal(session, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(fixture.Epoch, reply.RootElement.GetProperty("Epoch").GetGuid());
        Assert.Equal("synthetic-sandbox", reply.RootElement.GetProperty("Sandbox").GetString());

        // The session is consumed: a second reply for the same TaskId is not a workflow reply any more.
        Assert.Equal(0, relay.WorkflowSessionCount);
        await relay.PublishToAgentAsync("temporal-bridge", 0, "again", RelayMessageType.Response, taskId: "wf/task-1");
        Assert.Single(Publishes(fixture.Channel));
    }

    private static void AssertWorkflowReply(byte[] body, string type, string session, Guid epoch)
    {
        using var reply = JsonDocument.Parse(body);
        Assert.Equal("agent1", reply.RootElement.GetProperty("Sender").GetString());
        Assert.Equal(type, reply.RootElement.GetProperty("Type").GetString());
        Assert.Equal("wf/task-1", reply.RootElement.GetProperty("TaskId").GetString());
        Assert.Equal(session, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(epoch, reply.RootElement.GetProperty("Epoch").GetGuid());
        Assert.Equal("synthetic-sandbox", reply.RootElement.GetProperty("Sandbox").GetString());
    }

    // ---- #492 R5: the agent-side session survives a failed reply publication ----

    [Fact]
    public async Task FailedFirstReplyPublication_KeepsTheSession_AndTheRetryPublishesTheOriginalContextOnce()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        var outage = true;
        var published = new List<(string RoutingKey, byte[] Body)>();
        fixture.Channel.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (outage) return ValueTask.FromException(new IOException("synthetic publish outage"));
            published.Add(((string)call[1], ((ReadOnlyMemory<byte>)call[4]).ToArray()));
            return ValueTask.CompletedTask;
        });
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);
        var session = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1",
            new BasicProperties { UserId = fixture.Binding.Principal("control") }, Directive(fixture.Epoch, session: session));

        await relay.PublishToAgentAsync("temporal-bridge", 0, "synthetic result", RelayMessageType.Response, taskId: "wf/task-1");
        Assert.Single(Publishes(fixture.Channel)); // the broker was attempted and failed
        Assert.Empty(published);
        Assert.Equal(1, relay.WorkflowSessionCount); // retained for the retry

        outage = false;
        await relay.PublishToAgentAsync("temporal-bridge", 0, "synthetic result", RelayMessageType.Response, taskId: "wf/task-1");
        var reply = Assert.Single(published);
        Assert.Equal("bridge", reply.RoutingKey);
        AssertWorkflowReply(reply.Body, "response", session, fixture.Epoch);
        Assert.Equal(0, relay.WorkflowSessionCount); // freed only by the successful final publish

        await relay.PublishToAgentAsync("temporal-bridge", 0, "again", RelayMessageType.Response, taskId: "wf/task-1");
        Assert.Single(published);
        Assert.Equal(2, Publishes(fixture.Channel).Length); // no third broker attempt
    }

    [Fact]
    public async Task PartialResponse_DoesNotConsumeTheSession_SoTheFinalReplyCarriesTheContextToo()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);
        var session = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1",
            new BasicProperties { UserId = fixture.Binding.Principal("control") }, Directive(fixture.Epoch, session: session));

        await relay.PublishToAgentAsync("temporal-bridge", 0, "partial", RelayMessageType.PartialResponse, taskId: "wf/task-1");
        Assert.Equal(1, relay.WorkflowSessionCount);
        await relay.PublishToAgentAsync("temporal-bridge", 0, "final", RelayMessageType.Response, taskId: "wf/task-1");
        Assert.Equal(0, relay.WorkflowSessionCount);

        var publishes = Publishes(fixture.Channel);
        Assert.Equal(2, publishes.Length);
        Assert.All(publishes, p => Assert.Equal("bridge", p[1]));
        AssertWorkflowReply(((ReadOnlyMemory<byte>)publishes[0][4]!).ToArray(), "partial-response", session, fixture.Epoch);
        AssertWorkflowReply(((ReadOnlyMemory<byte>)publishes[1][4]!).ToArray(), "response", session, fixture.Epoch);
    }

    public static TheoryData<string> Foreign() => new()
    {
        "workload user", "no user", "foreign route", "foreign exchange", "foreign epoch", "foreign sandbox",
        "no task", "bad session", "foreign sender", "revoked",
    };

    [Theory]
    [MemberData(nameof(Foreign))]
    public async Task ForeignOrStaleDirective_IsRefused_AndNoWorkflowReplyCanBePublished(string fault)
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var received = 0;
        relay.MessageReceived += (_, _, _, _, _, _, _, _) => received++;
        await relay.InitializeAsync(default);

        var user = fault switch { "workload user" => fixture.Binding.Principal("workload"), "no user" => null, _ => fixture.Binding.Principal("control") };
        var body = fault switch
        {
            "foreign epoch" => Directive(Guid.NewGuid()),
            "foreign sandbox" => Directive(fixture.Epoch, sandbox: "other-sandbox"),
            "no task" => Directive(fixture.Epoch, taskId: null),
            "bad session" => Directive(fixture.Epoch, session: "not-a-session"),
            "foreign sender" => Directive(fixture.Epoch, sender: "agent2"),
            _ => Directive(fixture.Epoch),
        };
        if (fault == "revoked") fixture.Save("revoked");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false,
            fault == "foreign exchange" ? "fleet.other" : "fleet.group", fault == "foreign route" ? "agent2" : "agent1",
            new BasicProperties { UserId = user }, body);

        Assert.Equal(0, received);
        await relay.PublishToAgentAsync("temporal-bridge", 0, "must not publish", RelayMessageType.Response, taskId: "wf/task-1");
        Assert.Empty(Publishes(fixture.Channel));
    }

    // ---- #492 W6 lows ----

    [Fact]
    public async Task UnrepliedSessions_AreBounded_SoLaterDirectivesAreStillAdmitted_AndAnswerable()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var received = 0;
        relay.MessageReceived += (_, _, _, _, _, _, _, _) => received++;
        await relay.InitializeAsync(default);
        var props = new BasicProperties { UserId = fixture.Binding.Principal("control") };

        // More unreplied (cancelled or timed-out) tasks than the bound: none may wedge the map.
        for (var i = 0; i < Fleet.Agent.Services.GroupRelayService.MaxWorkflowSessions + 5; i++)
            await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", (ulong)i + 1, false, "fleet.group", "agent1",
                props, Directive(fixture.Epoch, taskId: "wf/unreplied-" + i));
        Assert.Equal(Fleet.Agent.Services.GroupRelayService.MaxWorkflowSessions, relay.WorkflowSessionCount);

        var session = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 9999, false, "fleet.group", "agent1",
            props, Directive(fixture.Epoch, taskId: "wf/fresh", session: session));
        Assert.Equal(Fleet.Agent.Services.GroupRelayService.MaxWorkflowSessions + 6, received);

        await relay.PublishToAgentAsync("temporal-bridge", 0, "fresh result", RelayMessageType.Response, taskId: "wf/fresh");
        var publish = Assert.Single(Publishes(fixture.Channel));
        using var reply = JsonDocument.Parse((ReadOnlyMemory<byte>)publish[4]!);
        Assert.Equal(session, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(Fleet.Agent.Services.GroupRelayService.MaxWorkflowSessions - 1, relay.WorkflowSessionCount); // the reply frees its entry
    }

    [Fact]
    public async Task WorkflowSignal_FromAManagedAgent_IsRefusedByName_NotSwallowed()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            relay.PublishWorkflowSignalAsync("wf-1", "approved", "{}"));
        Assert.StartsWith(Fleet.Agent.Services.GroupRelayService.ManagedWorkflowSignalUnsupported, refused.Message);
        Assert.Empty(Publishes(fixture.Channel));
    }
}

/// <summary>
/// #492 managed workflow delegation, Bridge side: the managed lane (sole consumer of the epoch reply queue)
/// admits a typed workflow reply only with the workload principal and this lane's agent, sandbox and epoch, and
/// forwards it to the Temporal receiver route on the ordinary connection; anything else is refused.
/// </summary>
public sealed class ManagedStockAgentWorkflowReplyBridgeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("managed-wf-bridge-").FullName;
    private readonly IChannel _ordinary = Substitute.For<IChannel>();
    private readonly IChannel _lane = Substitute.For<IChannel>();
    private readonly Guid _epoch = Guid.NewGuid();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Vhost => "stock-" + _epoch.ToString("N");

    private async Task<BridgeRelayService> StartAsync(TimeSpan? retryDelay = null, TimeSpan? confirmTimeout = null)
    {
        File.WriteAllText(Path.Combine(_dir, "agent1.json"), JsonSerializer.Serialize(new StockEnrollmentAuthority
        {
            State = "active", Agent = "agent1", SandboxId = "sandbox-1", Epoch = _epoch, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
            AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
            MemoryHost = "memory.example.test", BrokerHost = "broker.example.test"
        }));
        File.WriteAllText(Path.Combine(_dir, "agent1.control.password"), "control-agent1");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["StockOpenShell:AuthorityDirectory"] = _dir }).Build();
        var ordinary = Substitute.For<IConnection>();
        ordinary.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(_ordinary));
        var bridge = new BridgeRelayService(Options.Create(new BridgeOptions { TargetAgent = "agent1", BridgeChatId = 7 }),
            Options.Create(new RabbitMqOptions { Host = "broker.example.test" }), NullLogger<BridgeRelayService>.Instance, configuration)
        { Connect = (_, _) => Task.FromResult(ordinary) };
        bridge.Lanes!.ForwardRetryDelay = retryDelay ?? TimeSpan.Zero;
        if (confirmTimeout is { } bound) bridge.ForwardConfirmTimeout = bound;
        bridge.Lanes!.Connect = (factory, _) =>
        {
            Assert.Equal(Vhost + "-control", factory.UserName);
            var connection = Substitute.For<IConnection>();
            connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(_lane));
            return Task.FromResult(connection);
        };
        await bridge.InitializeAsync(default);
        return bridge;
    }

    private static object[][] Publishes(IChannel channel) => channel.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == "BasicPublishAsync").Select(c => c.GetArguments()).ToArray();

    private Task Deliver(string? user, string sender = "agent1", Guid? epoch = null, string sandbox = "sandbox-1",
        string? session = null, string? taskId = "wf/task-1", string routingKey = "bridge", ulong tag = 1, bool redelivered = false)
    {
        var consumer = _lane.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ChatId = 7, Sender = sender, Text = "synthetic result", Timestamp = DateTimeOffset.UtcNow, Type = "response",
            CorrelationId = session ?? Guid.NewGuid().ToString("N"), TaskId = taskId, Epoch = epoch ?? _epoch, Sandbox = sandbox
        });
        return consumer.HandleBasicDeliverAsync("owned", tag, redelivered, "fleet.group", routingKey, new BasicProperties { UserId = user }, body);
    }

    private static int Calls(IChannel channel, string method) => channel.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);

    [Fact]
    public async Task AdmittedReply_IsForwardedWithItsContext_ToTheTemporalReceiverOnTheOrdinaryConnection()
    {
        await using var bridge = await StartAsync();
        var session = Guid.NewGuid().ToString("N");
        await Deliver(Vhost + "-workload", session: session);

        var forward = Assert.Single(Publishes(_ordinary));
        Assert.Equal("fleet.group", forward[0]);
        Assert.Equal("temporal-bridge", forward[1]);
        using var reply = JsonDocument.Parse((ReadOnlyMemory<byte>)forward[4]!);
        Assert.Equal("agent1", reply.RootElement.GetProperty("Sender").GetString());
        Assert.Equal("wf/task-1", reply.RootElement.GetProperty("TaskId").GetString());
        Assert.Equal(session, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(_epoch, reply.RootElement.GetProperty("Epoch").GetGuid());
        Assert.Equal("sandbox-1", reply.RootElement.GetProperty("Sandbox").GetString());
        Assert.Empty(Publishes(_lane)); // the lane never publishes a reply back into the epoch vhost
        Assert.True((bool)forward[2]!); // mandatory and confirmed: an unrouted forward fails instead of vanishing
        await _lane.Received(1).BasicAckAsync(1, false, Arg.Any<CancellationToken>()); // acked only after the forward
    }

    [Fact]
    public async Task ForwardOutage_RetainsTheReplyOnTheSameConsumer_AndRecoveryDeliversItOnce()
    {
        var outage = true;
        var forwarded = 0;
        _ordinary.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (outage) return ValueTask.FromException(new IOException("synthetic forward outage"));
            forwarded++;
            return ValueTask.CompletedTask;
        });
        await using var bridge = await StartAsync();
        var session = Guid.NewGuid().ToString("N");

        await Deliver(Vhost + "-workload", session: session, tag: 1);
        await _lane.Received(1).BasicNackAsync(1, false, true, Arg.Any<CancellationToken>()); // requeued, not lost
        Assert.Equal(0, Calls(_lane, "BasicAckAsync"));
        Assert.Equal(0, Calls(_lane, "BasicRejectAsync"));

        outage = false; // the broker redelivers the retained reply to the same (sole) consumer
        await Deliver(Vhost + "-workload", session: session, tag: 2, redelivered: true);
        Assert.Equal(1, forwarded);
        await _lane.Received(1).BasicAckAsync(2, false, Arg.Any<CancellationToken>());
        Assert.Equal(1, Calls(_lane, "BasicNackAsync"));
        Assert.Single(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicConsumeAsync");
        Assert.Contains(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicConsumeAsync" && Equals(c.GetArguments()[1], false));
    }

    [Fact]
    public async Task PreparedReplyQueue_IsRedeclaredIdentically_AndAReplyQueuedBeforeTheLaneOpensIsForwardedOnce()
    {
        // The orchestrator prepares fleet.agent.bridge (durable, non-exclusive, non-auto-delete, no arguments)
        // and its fleet.group/bridge binding before activation (#492 R3/F8), so a reply published before this
        // lane opens is queued; the lane's redeclare must match exactly (no PRECONDITION_FAILED).
        await using var bridge = await StartAsync();
        var declare = Assert.Single(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "QueueDeclareAsync").GetArguments();
        Assert.Equal(new object?[] { "fleet.agent.bridge", true, false, false, null }, declare.Take(5).ToArray());
        var bind = Assert.Single(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "QueueBindAsync").GetArguments();
        Assert.Equal(new object?[] { "fleet.agent.bridge", "fleet.group", "bridge", null }, bind.Take(4).ToArray());
        var exchange = Assert.Single(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "ExchangeDeclareAsync").GetArguments();
        Assert.Equal(new object?[] { "fleet.group", "direct", true, false, null }, exchange.Take(5).ToArray());

        // The broker delivers the reply it retained to the sole consumer as soon as the lane opens.
        await Deliver(Vhost + "-workload", tag: 1);
        Assert.Single(Publishes(_ordinary));
        await _lane.Received(1).BasicAckAsync(1, false, Arg.Any<CancellationToken>());
        Assert.Equal(0, Calls(_lane, "BasicNackAsync"));
    }

    [Fact]
    public async Task UnconfirmedForward_TimesOut_AndItsBackoffRunsOffTheConsumerCallback_ThenRequeues()
    {
        _ordinary.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask(Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(5)))); // no confirm
        await using var bridge = await StartAsync(retryDelay: TimeSpan.FromSeconds(2), confirmTimeout: TimeSpan.FromMilliseconds(100));

        // The confirm wait is bounded (#492 V5), and the callback returns before the backoff elapses.
        await Deliver(Vhost + "-workload", tag: 1).WaitAsync(TimeSpan.FromSeconds(1.5));
        Assert.Equal(0, Calls(_lane, "BasicNackAsync"));
        Assert.Equal(0, Calls(_lane, "BasicAckAsync"));
        // The lane keeps consuming meanwhile: the next delivery is settled while the first awaits its backoff.
        await Deliver(Vhost + "-control", tag: 2).WaitAsync(TimeSpan.FromSeconds(1.5));
        await _lane.Received(1).BasicRejectAsync(2, false, Arg.Any<CancellationToken>());
        Assert.Equal(0, Calls(_lane, "BasicNackAsync"));

        for (var i = 0; i < 100 && Calls(_lane, "BasicNackAsync") == 0; i++) await Task.Delay(50);
        await _lane.Received(1).BasicNackAsync(1, false, true, Arg.Any<CancellationToken>()); // bounded delay, then requeued
        Assert.Equal(0, Calls(_lane, "BasicAckAsync"));
    }

    [Fact]
    public async Task ForeignReplies_AreRefused_AndNeverForwarded()
    {
        await using var bridge = await StartAsync();
        await Deliver(Vhost + "-control");                                     // not the workload principal
        await Deliver(null);                                                   // no validated user-id
        await Deliver(Vhost + "-workload", sender: "agent2");                  // foreign agent
        await Deliver(Vhost + "-workload", epoch: Guid.NewGuid());             // foreign epoch
        await Deliver(Vhost + "-workload", sandbox: "sandbox-2");              // foreign sandbox
        await Deliver(Vhost + "-workload", session: "not-a-session");          // no session nonce
        await Deliver(Vhost + "-workload", taskId: null);                      // no TaskId
        await Deliver(Vhost + "-workload", routingKey: "agent1");              // foreign route
        Assert.Empty(Publishes(_ordinary));
        // Refused replies are rejected without requeue: never redelivered, never acked as forwarded.
        await _lane.Received(8).BasicRejectAsync(1, false, Arg.Any<CancellationToken>());
        Assert.Equal(0, Calls(_lane, "BasicNackAsync"));
        Assert.Equal(0, Calls(_lane, "BasicAckAsync"));
    }
}
