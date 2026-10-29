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
        await relay.PublishToAgentAsync("temporal-bridge", 0, "again", RelayMessageType.Response, taskId: "wf/task-1");
        Assert.Single(Publishes(fixture.Channel));
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

    private async Task<BridgeRelayService> StartAsync()
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
        string? session = null, string? taskId = "wf/task-1", string routingKey = "bridge")
    {
        var consumer = _lane.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ChatId = 7, Sender = sender, Text = "synthetic result", Timestamp = DateTimeOffset.UtcNow, Type = "response",
            CorrelationId = session ?? Guid.NewGuid().ToString("N"), TaskId = taskId, Epoch = epoch ?? _epoch, Sandbox = sandbox
        });
        return consumer.HandleBasicDeliverAsync("owned", 1, false, "fleet.group", routingKey, new BasicProperties { UserId = user }, body);
    }

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
    }
}
