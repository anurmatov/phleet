using System.Text.Json;
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

/// <summary>#492 managed Bridge control: one epoch-bound lane per managed target, ordinary targets unchanged.</summary>
public sealed class ManagedStockAgentBridgeLaneTests : IDisposable
{
    private const string Denied = "Error: stock control admission denied.";
    private readonly string _dir = Directory.CreateTempSubdirectory("managed-bridge-").FullName;
    private readonly IChannel _ordinary = Substitute.For<IChannel>();
    private readonly List<(ConnectionFactory Factory, IConnection Connection, IChannel Channel)> _lanes = [];
    private readonly Dictionary<string, Guid> _epochs = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Save(string agent, string sandbox, string state = "active", Guid? epoch = null)
    {
        _epochs[agent] = epoch ?? Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, agent + ".json"), JsonSerializer.Serialize(new StockEnrollmentAuthority
        {
            State = state, Agent = agent, SandboxId = sandbox, Epoch = _epochs[agent], ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
            AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
            MemoryHost = "memory.example.test", BrokerHost = "broker.example.test"
        }));
        File.WriteAllText(Path.Combine(_dir, agent + ".control.password"), "control-" + agent);
    }

    private string Vhost(string agent) => "stock-" + _epochs[agent].ToString("N");

    // Current lanes open at initialization (#492 workflow replies), so select a lane by its epoch vhost.
    private (ConnectionFactory Factory, IConnection Connection, IChannel Channel) Lane(string agent) =>
        _lanes.Last(l => l.Factory.VirtualHost == Vhost(agent));

    private BridgeRelayService Bridge(Func<ConnectionFactory, IConnection>? lane = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["StockOpenShell:AuthorityDirectory"] = _dir }).Build();
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(_ordinary));
        var bridge = new BridgeRelayService(Options.Create(new BridgeOptions { TargetAgent = "agent3", BridgeChatId = 7 }),
            Options.Create(new RabbitMqOptions { Host = "broker.example.test" }), NullLogger<BridgeRelayService>.Instance, configuration)
        {
            Connect = (factory, _) =>
            {
                Assert.Equal("/", factory.VirtualHost); // the ordinary connection keeps its ordinary factory
                Assert.True(factory.AutomaticRecoveryEnabled);
                return Task.FromResult(connection);
            }
        };
        bridge.Lanes!.Connect = (factory, _) =>
        {
            var channel = Substitute.For<IChannel>();
            var laneConnection = lane?.Invoke(factory) ?? Substitute.For<IConnection>();
            laneConnection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
            lock (_lanes) _lanes.Add((factory, laneConnection, channel));
            return Task.FromResult(laneConnection);
        };
        return bridge;
    }

    private static object[] Published(IChannel channel) =>
        channel.ReceivedCalls().Last(c => c.GetMethodInfo().Name == "BasicPublishAsync").GetArguments();

    private static int Publishes(IChannel channel) => channel.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "BasicPublishAsync");

    private static AsyncEventingBasicConsumer Consumer(IChannel channel) =>
        channel.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Last();

    /// <summary>Completes the last request published on <paramref name="channel"/> as <paramref name="sender"/>.</summary>
    private static async Task Complete(IChannel channel, string sender, string? user, string text = "synthetic completion")
    {
        using var request = JsonDocument.Parse((ReadOnlyMemory<byte>)Published(channel)[4]!);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ChatId = 7, Sender = sender, Text = text, Timestamp = DateTimeOffset.UtcNow, Type = "bridge-response",
            CorrelationId = request.RootElement.GetProperty("CorrelationId").GetString()
        });
        await Consumer(channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "bridge", new BasicProperties { UserId = user }, body);
    }

    [Fact]
    public async Task TwoManagedTargets_UseTheirOwnEpochLanes_OrdinaryTargetUsesOrdinaryConnection()
    {
        Save("agent1", "sandbox-1");
        Save("agent2", "sandbox-2");
        await using var bridge = Bridge();
        await bridge.InitializeAsync(default);

        foreach (var agent in new[] { "agent1", "agent2" })
        {
            bridge.TargetAgent = agent;
            var response = bridge.AskAsync("synthetic question", "operator", null, 5, default);
            var (factory, _, channel) = Lane(agent);
            Assert.Equal(Vhost(agent), factory.VirtualHost);
            Assert.Equal(Vhost(agent) + "-control", factory.UserName);
            Assert.Equal("control-" + agent, factory.Password);
            Assert.Equal("broker.example.test", factory.HostName);
            Assert.Equal(5672, factory.Port);
            Assert.False(factory.AutomaticRecoveryEnabled);
            var sent = Published(channel);
            Assert.Equal(agent, sent[1]);
            Assert.Equal(Vhost(agent) + "-control", sent.OfType<BasicProperties>().Single().UserId);
            // The existing principal/route/correlation checks still hold inside each lane.
            var other = agent == "agent1" ? "agent2" : "agent1";
            await Complete(channel, agent, Vhost(other) + "-workload", "foreign epoch");
            await Complete(channel, other, Vhost(agent) + "-workload", "foreign sender");
            Assert.False(response.IsCompleted);
            await Complete(channel, agent, Vhost(agent) + "-workload");
            Assert.Equal("synthetic completion", await response.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.Equal(2, _lanes.Count);
        Assert.Equal(0, Publishes(_ordinary));

        bridge.TargetAgent = "agent3";
        var ordinary = bridge.AskAsync("ordinary question", "operator", null, 5, default);
        var args = Published(_ordinary);
        Assert.Equal("agent3", args[1]);
        Assert.Null(args.OfType<BasicProperties>().Single().UserId);
        await Complete(_ordinary, "agent3", null, "ordinary completion");
        Assert.Equal("ordinary completion", await ordinary.WaitAsync(TimeSpan.FromSeconds(3)));

        // A lane is reused while its record binding is unchanged.
        // Startup opens lanes in directory-listing order, so select agent1's lane by its epoch vhost (#492 C1).
        bridge.TargetAgent = "Agent1";
        var agent1Lane = Lane("agent1");
        var again = bridge.AskAsync("again", "operator", null, 5, default);
        Assert.Equal(2, Publishes(agent1Lane.Channel)); // reused: the second ask publishes on the same lane channel
        Assert.Equal("agent1", Published(agent1Lane.Channel)[1]);
        Assert.Equal(Vhost("agent1") + "-control", Published(agent1Lane.Channel).OfType<BasicProperties>().Single().UserId);
        await Complete(agent1Lane.Channel, "agent1", Vhost("agent1") + "-workload");
        Assert.Equal("synthetic completion", await again.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, _lanes.Count);
        Assert.Equal(1, Publishes(_ordinary));
    }

    [Fact]
    public async Task EpochChange_RecreatesTheLane_AndRemovedRecordRefusesWithoutFallback()
    {
        Save("agent1", "sandbox-1");
        await using var bridge = Bridge();
        await bridge.InitializeAsync(default);
        bridge.TargetAgent = "agent1";
        var first = bridge.AskAsync("first", "operator", null, 5, default);
        await Complete(_lanes[0].Channel, "agent1", Vhost("agent1") + "-workload");
        await first;
        var oldVhost = Vhost("agent1");

        Save("agent1", "sandbox-1");
        var second = bridge.AskAsync("second", "operator", null, 5, default);
        Assert.Equal(2, _lanes.Count);
        Assert.Contains(_lanes[0].Connection.ReceivedCalls(), c => c.GetMethodInfo().Name == "CloseAsync");
        Assert.NotEqual(oldVhost, _lanes[1].Factory.VirtualHost);
        Assert.Equal(Vhost("agent1") + "-control", _lanes[1].Factory.UserName);
        Assert.Equal(1, Publishes(_lanes[0].Channel));
        await Complete(_lanes[1].Channel, "agent1", Vhost("agent1") + "-workload");
        await second;

        File.Delete(Path.Combine(_dir, "agent1.json"));
        Assert.Equal(Denied, await bridge.AskAsync("removed", "operator", null, 1, default));
        Assert.Equal("Error: stock status route is not enrolled.", await bridge.CheckStatusAsync(default));
        Assert.Contains(_lanes[1].Connection.ReceivedCalls(), c => c.GetMethodInfo().Name == "CloseAsync");
        Assert.Equal(2, _lanes.Count);
        Assert.Equal(0, Publishes(_ordinary));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("duplicate_sandbox")]
    [InlineData("password_link")]
    public async Task InactiveOrAmbiguousManagedRecord_RefusesAndNeverUsesOrdinaryConnection(string fault)
    {
        Save("agent1", "sandbox-1", fault == "revoked" ? "revoked" : "active");
        if (fault == "duplicate_sandbox") Save("agent2", "sandbox-1");
        if (fault == "password_link")
        {
            var password = Path.Combine(_dir, "agent1.control.password");
            File.Move(password, password + ".real");
            File.CreateSymbolicLink(password, password + ".real");
        }
        await using var bridge = Bridge();
        await bridge.InitializeAsync(default);
        bridge.TargetAgent = "agent1";
        Assert.Equal(Denied, await bridge.AskAsync("refused", "operator", null, 1, default));
        Assert.Equal("Error: stock status route is not enrolled.", await bridge.CheckStatusAsync(default));
        Assert.Empty(_lanes);
        Assert.Equal(0, Publishes(_ordinary));
    }

    [Fact]
    public async Task FailedLane_StaysHeldUntilReenrollment_OtherLanesAndOrdinaryUnaffected()
    {
        Save("agent1", "sandbox-1");
        Save("agent2", "sandbox-2");
        var failing = true;
        await using var bridge = Bridge(factory => factory.UserName == Vhost("agent1") + "-control" && failing
            ? throw new IOException("synthetic connect failure") : Substitute.For<IConnection>());
        await bridge.InitializeAsync(default);
        bridge.TargetAgent = "agent1";
        Assert.Equal(Denied, await bridge.AskAsync("fails", "operator", null, 1, default));
        failing = false;
        Assert.Equal(Denied, await bridge.AskAsync("held", "operator", null, 1, default));
        Assert.DoesNotContain(_lanes, l => l.Factory.VirtualHost == Vhost("agent1")); // no reconnect before the backoff

        bridge.TargetAgent = "agent2";
        var other = bridge.AskAsync("other", "operator", null, 5, default);
        await Complete(Lane("agent2").Channel, "agent2", Vhost("agent2") + "-workload");
        Assert.Equal("synthetic completion", await other.WaitAsync(TimeSpan.FromSeconds(3)));

        Save("agent1", "sandbox-1");
        bridge.TargetAgent = "agent1";
        var reenrolled = bridge.AskAsync("reenrolled", "operator", null, 5, default);
        Assert.Equal(Vhost("agent1"), _lanes[^1].Factory.VirtualHost);
        await Complete(Lane("agent1").Channel, "agent1", Vhost("agent1") + "-workload");
        Assert.Equal("synthetic completion", await reenrolled.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, Publishes(_ordinary));
    }

    private (ConnectionFactory Factory, IConnection Connection, IChannel Channel)[] Snapshot()
    {
        lock (_lanes) return [.. _lanes];
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    private static bool Closed(IConnection connection) =>
        connection.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "CloseAsync");

    private static int Calls(IChannel channel, string method, object? first = null) => channel.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == method && (first is null || Equals(c.GetArguments()[0], first)));

    [Fact]
    public async Task LateEnrollment_OpensTheReplyLaneAndItsSoleConsumer_WithoutAnAsk_AndForwardsItsReply()
    {
        Save("agent1", "sandbox-1");
        await using var bridge = Bridge();
        bridge.Lanes!.ReconcileDelay = TimeSpan.FromMilliseconds(20);
        await bridge.InitializeAsync(default);
        Assert.Single(Snapshot());

        Save("agent2", "sandbox-2"); // enrolled after the Bridge started; no bridge ask, no restart
        await WaitFor(() => Snapshot().Any(l => l.Factory.VirtualHost == Vhost("agent2")));
        var (factory, _, channel) = Snapshot().Single(l => l.Factory.VirtualHost == Vhost("agent2"));
        Assert.Equal(Vhost("agent2") + "-control", factory.UserName);
        Assert.Equal(1, Calls(channel, "QueueDeclareAsync", "fleet.agent.bridge")); // the epoch reply queue exists
        Assert.Equal(1, Calls(channel, "BasicConsumeAsync", "fleet.agent.bridge")); // with its one consumer
        Assert.Equal(2, Snapshot().Length);

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ChatId = 7, Sender = "agent2", Text = "synthetic result", Timestamp = DateTimeOffset.UtcNow, Type = "response",
            CorrelationId = Guid.NewGuid().ToString("N"), TaskId = "wf/task-2", Epoch = _epochs["agent2"], Sandbox = "sandbox-2"
        });
        await Consumer(channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "bridge",
            new BasicProperties { UserId = Vhost("agent2") + "-workload" }, body);
        Assert.Equal("temporal-bridge", Published(_ordinary)[1]);
        Assert.Equal(1, Publishes(_ordinary));
    }

    [Fact]
    public async Task ReEpoch_ReplacesTheLaneWithoutAnAsk_AndRevocationClosesIt()
    {
        Save("agent1", "sandbox-1");
        await using var bridge = Bridge();
        bridge.Lanes!.ReconcileDelay = TimeSpan.FromMilliseconds(20);
        await bridge.InitializeAsync(default);
        var old = Snapshot().Single();

        Save("agent1", "sandbox-1"); // new epoch
        await WaitFor(() => Snapshot().Any(l => l.Factory.VirtualHost == Vhost("agent1")) && Closed(old.Connection));
        var current = Snapshot().Single(l => l.Factory.VirtualHost == Vhost("agent1"));
        Assert.NotEqual(old.Factory.VirtualHost, current.Factory.VirtualHost);
        Assert.Equal(1, Calls(current.Channel, "BasicConsumeAsync", "fleet.agent.bridge"));

        Save("agent1", "sandbox-1", state: "revoked", epoch: _epochs["agent1"]);
        await WaitFor(() => Closed(current.Connection));
        bridge.TargetAgent = "agent1";
        Assert.Equal(Denied, await bridge.AskAsync("revoked", "operator", null, 1, default));
        Assert.Equal(2, Snapshot().Length);
        Assert.Equal(0, Publishes(_ordinary));
    }

    [Fact]
    public async Task FailedLane_IsRetriedWithoutAnAsk_ABoundedNumberOfTimes()
    {
        Save("agent1", "sandbox-1");
        Save("agent2", "sandbox-2");
        var attempts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        await using var bridge = Bridge(factory =>
        {
            var count = attempts.AddOrUpdate(factory.VirtualHost, 1, (_, n) => n + 1);
            // agent1 recovers after its first failed open; agent2 never does.
            return factory.VirtualHost == Vhost("agent2") || count == 1
                ? throw new IOException("synthetic connect failure") : Substitute.For<IConnection>();
        });
        bridge.Lanes!.LaneRetryDelay = TimeSpan.FromMilliseconds(20);
        await bridge.InitializeAsync(default);

        await WaitFor(() => Snapshot().Any(l => l.Factory.VirtualHost == Vhost("agent1")));
        Assert.Equal(2, attempts[Vhost("agent1")]);
        await WaitFor(() => attempts.GetValueOrDefault(Vhost("agent2")) == BridgeControlLanes.MaxLaneAttempts);
        await Task.Delay(800); // past the next backoff: the held lane is not reopened again
        Assert.Equal(BridgeControlLanes.MaxLaneAttempts, attempts[Vhost("agent2")]);
        bridge.TargetAgent = "agent2";
        Assert.Equal(Denied, await bridge.AskAsync("held", "operator", null, 1, default));
        Assert.Equal(BridgeControlLanes.MaxLaneAttempts, attempts[Vhost("agent2")]);
        Assert.Equal(0, Publishes(_ordinary));
    }

    [Fact]
    public async Task StaleReconcileSnapshot_NeverClosesALaneOpenedForANewEnrollment()
    {
        Save("agent1", "sandbox-1");
        BridgeRelayService? bridge = null;
        Task<string>? ask = null;
        await using var created = Bridge(factory =>
        {
            // While the startup reconcile (snapshot: agent1 only) opens agent1's lane, agent2 enrolls and an
            // ask to agent2 queues on the gate ahead of that reconcile's close pass (#492 V1).
            if (factory.VirtualHost == Vhost("agent1") && ask is null)
            {
                Save("agent2", "sandbox-2");
                bridge!.TargetAgent = "agent2";
                ask = bridge.AskAsync("new enrollment", "operator", null, 5, default);
            }
            return Substitute.For<IConnection>();
        });
        bridge = created;
        bridge.Lanes!.ReconcileDelay = TimeSpan.FromHours(1); // no watcher reconcile can mask a wrong close
        await bridge.InitializeAsync(default);

        var lane = Snapshot().Single(l => l.Factory.VirtualHost == Vhost("agent2"));
        Assert.False(Closed(lane.Connection));
        Assert.Equal("agent2", Published(lane.Channel)[1]);
        await Complete(lane.Channel, "agent2", Vhost("agent2") + "-workload");
        Assert.Equal("synthetic completion", await ask!.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, Snapshot().Length);
    }

    [Fact]
    public void SingleFileMode_IsUnchanged_AndNeverCombinesWithTheDirectory()
    {
        Save("agent1", "sandbox-1");
        var options = Options.Create(new BridgeOptions { TargetAgent = "agent1" });
        var rabbit = Options.Create(new RabbitMqOptions { Host = "broker.example.test" });
        var legacy = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StockOpenShell:AuthorityFile"] = Path.Combine(_dir, "agent1.json"),
            ["StockOpenShell:BrokerPasswordFile"] = Path.Combine(_dir, "agent1.control.password")
        }).Build();
        Assert.Null(new BridgeRelayService(options, rabbit, NullLogger<BridgeRelayService>.Instance, legacy).Lanes);
        Assert.Null(new BridgeRelayService(options, rabbit, NullLogger<BridgeRelayService>.Instance).Lanes);
        legacy["StockOpenShell:AuthorityDirectory"] = _dir;
        Assert.Equal("broker_configuration", Assert.Throws<StockIdentityDenied>(() =>
            new BridgeRelayService(options, rabbit, NullLogger<BridgeRelayService>.Instance, legacy)).Message);
        var relative = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["StockOpenShell:AuthorityDirectory"] = "managed/authority" }).Build();
        Assert.Equal("broker_configuration", Assert.Throws<StockIdentityDenied>(() =>
            new BridgeRelayService(options, rabbit, NullLogger<BridgeRelayService>.Instance, relative)).Message);
    }
}
