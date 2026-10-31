using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Orchestrator.Tests;

/// <summary>#492 managed observer: one epoch-bound <c>-observer</c> receiver per active managed record.</summary>
public sealed class ManagedStockRuntimeObserverLaneTests : IDisposable
{
    // Review W4: records in managed/authority, observer credentials only in the private managed/observer sibling.
    private readonly string _root = Directory.CreateTempSubdirectory("managed-observer-").FullName;
    private readonly string _dir;
    private readonly string _observerDir;
    private readonly Dictionary<string, Guid> _epochs = [];
    private readonly List<(ConnectionFactory Factory, IConnection Connection, IChannel Channel)> _lanes = [];
    private readonly AgentRegistry _registry = new(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance);
    private readonly IOptions<RabbitMqOptions> _options = Options.Create(new RabbitMqOptions { Host = "ordinary.example.test" });
    private readonly IConfiguration _config;

    public ManagedStockRuntimeObserverLaneTests()
    {
        _dir = Directory.CreateDirectory(Path.Combine(_root, "managed", "authority")).FullName;
        _observerDir = Directory.CreateDirectory(Path.Combine(_root, "managed", "observer")).FullName;
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StockOpenShell:AuthorityDirectory"] = _dir }).Build();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Save(string agent, string sandbox, string state = "active", string file = "")
    {
        _epochs[agent] = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, (file == "" ? agent : file) + ".json"), JsonSerializer.Serialize(new StockEnrollmentAuthority
        {
            State = state, Agent = agent, SandboxId = sandbox, Epoch = _epochs[agent], ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
            AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
            MemoryHost = "memory.example.test", BrokerHost = "broker-" + agent + ".example.test"
        }));
        File.WriteAllText(Path.Combine(_observerDir, agent + ".observer.password"), "observer-" + agent);
    }

    private string Vhost(string agent) => "stock-" + _epochs[agent].ToString("N");

    private StockHeartbeatObserverService Observer(Func<ConnectionFactory, bool>? fails = null, TimeSpan? interval = null) =>
        new(_options, _registry, new AgentConfigPublisherService(_options, NullLogger<AgentConfigPublisherService>.Instance),
            _config, new StockRuntimeMutationHold(_config), NullLogger<HeartbeatConsumerService>.Instance,
            NullLogger<StockHeartbeatObserverService>.Instance)
        {
            ReconcileInterval = interval ?? TimeSpan.FromSeconds(30),
            Connect = (factory, _) =>
            {
                if (fails?.Invoke(factory) == true) throw new IOException("synthetic connect failure");
                var channel = Substitute.For<IChannel>();
                var connection = Substitute.For<IConnection>();
                connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
                lock (_lanes) _lanes.Add((factory, connection, channel));
                return Task.FromResult(connection);
            }
        };

    private (ConnectionFactory Factory, IConnection Connection, IChannel Channel) Lane(string agent) =>
        _lanes.Last(l => l.Factory.VirtualHost == Vhost(agent));

    private static bool Closed(IConnection connection) => connection.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "CloseAsync");

    private static Task Deliver(IChannel channel, string route, string agent, string status, string? user) =>
        ((AsyncEventingBasicConsumer)channel.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "BasicConsumeAsync").GetArguments()
            .Single(a => a is AsyncEventingBasicConsumer)!).HandleBasicDeliverAsync("consumer", 1, false, "fleet.orchestrator", route,
            new BasicProperties { UserId = user }, JsonSerializer.SerializeToUtf8Bytes(new AgentHeartbeat(agent, status, DateTimeOffset.UtcNow)), default);

    [Fact]
    public async Task ObserverLane_ReadsItsPasswordFromThePrivateSibling_NeverFromTheSharedAuthorityDirectory()
    {
        // Review W4: an observer password left in the shared (Telegram/Temporal/Bridge-mounted) directory is never used.
        Save("agent1", "sandbox-1");
        Save("agent2", "sandbox-2");
        File.Move(Path.Combine(_observerDir, "agent2.observer.password"), Path.Combine(_dir, "agent2.observer.password"));
        using var observer = Observer();
        await observer.ReconcileAsync(_dir, default);
        Assert.Equal("observer-agent1", Lane("agent1").Factory.Password);
        Assert.DoesNotContain(_lanes, l => l.Factory.Password == "observer-agent2");
    }

    [Fact]
    public async Task StockObserver_LegacySharedPassword_IsMovedOnceIntoThePrivateSibling_AndTheLaneStarts()
    {
        // Review V3: a pre-W4 enrollment left its observer password in the shared mount and had no private copy.
        Save("agent1", "sandbox-1");
        Save("agent2", "sandbox-2");
        File.Move(Path.Combine(_observerDir, "agent1.observer.password"), Path.Combine(_dir, "agent1.observer.password"));
        // agent2 already has its current private password; a stale shared copy is just removed.
        File.WriteAllText(Path.Combine(_dir, "agent2.observer.password"), "stale-agent2");

        Assert.Equal(2, StockHeartbeatObserverService.MigrateLegacyObserverPasswords(_dir, NullLogger.Instance));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir, "*.observer.password"));
        var moved = Path.Combine(_observerDir, "agent1.observer.password");
        Assert.Equal("observer-agent1", File.ReadAllText(moved));
        Assert.Equal("observer-agent2", File.ReadAllText(Path.Combine(_observerDir, "agent2.observer.password")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(moved));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_observerDir));
        }
        // Once: a second pass finds nothing to do.
        Assert.Equal(0, StockHeartbeatObserverService.MigrateLegacyObserverPasswords(_dir, NullLogger.Instance));
        using var observer = Observer();
        await observer.ReconcileAsync(_dir, default);
        Assert.Equal("observer-agent1", Lane("agent1").Factory.Password);
        Assert.Equal("observer-agent2", Lane("agent2").Factory.Password);
    }

    [Fact]
    public void StockObserver_LegacyPasswordMigration_RacesNeverSkipEntries_AndFilesCreatedMidMoveAreMoved()
    {
        // Review X5: a concurrent apply writes one agent's private sibling, another shared copy vanishes and a new legacy
        // file appears, all while the first move is in flight. None of that may skip the remaining entries.
        foreach (var a in new[] { "agent1", "agent2", "agent3" }) File.WriteAllText(Path.Combine(_dir, a + ".observer.password"), "legacy-" + a);
        var order = new List<string>();
        var seams = new StockHeartbeatObserverService.LegacyMigrationSeams
        {
            BeforeMove = agent =>
            {
                order.Add(agent);
                if (order.Count != 1) return;
                File.WriteAllText(Path.Combine(_observerDir, agent + ".observer.password"), "current-" + agent);
                var other = new[] { "agent1", "agent2", "agent3" }.First(a => a != agent && File.Exists(Path.Combine(_dir, a + ".observer.password")));
                File.Delete(Path.Combine(_dir, other + ".observer.password"));
                order.Add("vanished:" + other);
                File.WriteAllText(Path.Combine(_dir, "agent4.observer.password"), "legacy-agent4");
            },
        };

        var migrated = StockHeartbeatObserverService.MigrateLegacyObserverPasswords(_dir, NullLogger.Instance, seams);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir, "*.observer.password"));
        var raced = order[0];
        var vanished = order[1]["vanished:".Length..];
        var plain = new[] { "agent1", "agent2", "agent3" }.Single(a => a != raced && a != vanished);
        // The concurrent apply's current password is never overwritten by the stale shared copy.
        Assert.Equal("current-" + raced, File.ReadAllText(Path.Combine(_observerDir, raced + ".observer.password")));
        Assert.False(File.Exists(Path.Combine(_observerDir, vanished + ".observer.password")));
        Assert.Equal("legacy-" + plain, File.ReadAllText(Path.Combine(_observerDir, plain + ".observer.password")));
        Assert.Equal("legacy-agent4", File.ReadAllText(Path.Combine(_observerDir, "agent4.observer.password")));
        Assert.Equal(3, migrated);
    }

    [Fact]
    public void StockObserver_LegacyPasswordMigration_CrossDeviceRaceLeavesNoTempAndKeepsTheCurrentPassword()
    {
        foreach (var a in new[] { "agent1", "agent2" }) File.WriteAllText(Path.Combine(_dir, a + ".observer.password"), "legacy-" + a);
        string? raced = null;
        var seams = new StockHeartbeatObserverService.LegacyMigrationSeams
        {
            ForceCrossDevice = true,
            // Review X5: a concurrent apply publishes the sibling between the temp copy and its rename.
            BeforePublish = agent =>
            {
                if (raced is not null) return;
                raced = agent;
                File.WriteAllText(Path.Combine(_observerDir, agent + ".observer.password"), "current-" + agent);
            },
        };

        Assert.Equal(2, StockHeartbeatObserverService.MigrateLegacyObserverPasswords(_dir, NullLogger.Instance, seams));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir, "*.observer.password"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_observerDir, "*.tmp"));
        var other = raced == "agent1" ? "agent2" : "agent1";
        Assert.Equal("current-" + raced, File.ReadAllText(Path.Combine(_observerDir, raced + ".observer.password")));
        var copied = Path.Combine(_observerDir, other + ".observer.password");
        Assert.Equal("legacy-" + other, File.ReadAllText(copied));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(copied));
    }

    [Fact]
    public void StockObserver_LegacyPasswordMigration_RefusesLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        Save("agent1", "sandbox-1");
        File.Delete(Path.Combine(_observerDir, "agent1.observer.password"));
        var outside = Path.Combine(_root, "outside-secret");
        File.WriteAllText(outside, "not-a-password");
        var link = Path.Combine(_dir, "agent1.observer.password");
        File.CreateSymbolicLink(link, outside);

        Assert.Equal(0, StockHeartbeatObserverService.MigrateLegacyObserverPasswords(_dir, NullLogger.Instance));

        // Never followed: the link stays, the target is untouched, nothing is published to the private sibling.
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("not-a-password", File.ReadAllText(outside));
        Assert.False(File.Exists(Path.Combine(_observerDir, "agent1.observer.password")));

        // A linked observer directory stops the migration entirely.
        File.Delete(link);
        File.WriteAllText(link, "observer-agent1");
        Directory.Delete(_observerDir, recursive: true);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        Directory.CreateSymbolicLink(_observerDir, elsewhere);
        Assert.Equal(0, StockHeartbeatObserverService.MigrateLegacyObserverPasswords(_dir, NullLogger.Instance));
        Assert.True(File.Exists(link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public async Task OneObserverLanePerActiveRecord_EachInItsOwnEpoch_RemovedAndChangedRecordsReconciled()
    {
        Save("agent1", "sandbox-1");
        Save("agent2", "sandbox-2");
        Save("agent3", "sandbox-3", state: "revoked");
        using var observer = Observer();
        await observer.ReconcileAsync(_dir, default);
        Assert.Equal(new[] { "agent1", "agent2" }, observer.Lanes.Keys.Order());
        Assert.Equal(2, _lanes.Count);
        foreach (var agent in new[] { "agent1", "agent2" })
        {
            var factory = Lane(agent).Factory;
            Assert.Equal("broker-" + agent + ".example.test", factory.HostName);
            Assert.Equal(Vhost(agent) + "-observer", factory.UserName);
            Assert.Equal("observer-" + agent, factory.Password);
            Assert.Equal(5672, factory.Port);
            Assert.False(factory.AutomaticRecoveryEnabled);
        }
        Assert.Equal("ordinary.example.test", _options.Value.Host);

        // Each lane admits only its own agent under its own epoch's workload principal.
        var lane1 = Lane("agent1");
        await Deliver(lane1.Channel, "heartbeat.agent1", "agent1", "busy", Vhost("agent1") + "-workload");
        await Deliver(lane1.Channel, "heartbeat.agent2", "agent2", "busy", Vhost("agent2") + "-workload");
        await Deliver(Lane("agent2").Channel, "heartbeat.agent2", "agent2", "idle", Vhost("agent1") + "-workload");
        Assert.Equal("busy", _registry.Get("agent1")!.ReportedStatus);
        Assert.Null(_registry.Get("agent2"));

        // Unchanged records are kept without reconnecting.
        await observer.ReconcileAsync(_dir, default);
        Assert.Equal(2, _lanes.Count);

        var lane2 = Lane("agent2");
        File.Delete(Path.Combine(_dir, "agent2.json"));
        Save("agent1", "sandbox-1");
        await observer.ReconcileAsync(_dir, default);
        Assert.Equal(new[] { "agent1" }, observer.Lanes.Keys);
        Assert.True(Closed(lane2.Connection));
        Assert.True(Closed(lane1.Connection));
        Assert.Equal(3, _lanes.Count);
        Assert.Equal(Vhost("agent1") + "-observer", _lanes[^1].Factory.UserName);
        // The retired epoch's receiver no longer updates the registry.
        await Deliver(lane1.Channel, "heartbeat.agent1", "agent1", "idle", lane1.Factory.VirtualHost + "-workload");
        Assert.Equal("busy", _registry.Get("agent1")!.ReportedStatus);

        await observer.StopLanesAsync();
        Assert.True(Closed(_lanes[^1].Connection));
        Assert.Empty(observer.Lanes);
    }

    [Fact]
    public async Task FailingLane_IsHeldUntilItsRecordChanges_WithoutStoppingOtherLanes()
    {
        Save("agent1", "sandbox-1");
        Save("agent2", "sandbox-2");
        var failing = true;
        using var observer = Observer(f => failing && f.HostName == "broker-agent2.example.test");
        await observer.ReconcileAsync(_dir, default);
        Assert.True(observer.Lanes["agent1"]);
        Assert.False(observer.Lanes["agent2"]);
        failing = false;
        await observer.ReconcileAsync(_dir, default);
        Assert.False(observer.Lanes["agent2"]); // no stock reconnect loop for the same binding
        Assert.Single(_lanes);
        Save("agent2", "sandbox-2");
        await observer.ReconcileAsync(_dir, default);
        Assert.True(observer.Lanes["agent2"]);
        Assert.Equal(Vhost("agent2") + "-observer", _lanes[^1].Factory.UserName);
        Assert.False(Closed(Lane("agent1").Connection));
        await observer.StopLanesAsync();
    }

    [Theory]
    [InlineData("duplicate_agent")]
    [InlineData("duplicate_sandbox")]
    [InlineData("misnamed")]
    [InlineData("link")]
    [InlineData("password_link")]
    public async Task AmbiguousOrLinkedRecords_StartNoLaneForThatAgent(string fault)
    {
        Save("agent1", "sandbox-1");
        if (fault == "duplicate_agent") Save("agent2", "sandbox-x", file: "other");
        Save("agent2", "sandbox-2");
        if (fault == "duplicate_sandbox") Save("agent3", "sandbox-2");
        if (fault == "misnamed") { File.Move(Path.Combine(_dir, "agent2.json"), Path.Combine(_dir, "renamed.json")); }
        var outside = Directory.CreateTempSubdirectory("managed-outside-").FullName;
        try
        {
            if (fault == "link")
            {
                File.Move(Path.Combine(_dir, "agent2.json"), Path.Combine(outside, "agent2.json"));
                File.CreateSymbolicLink(Path.Combine(_dir, "agent2.json"), Path.Combine(outside, "agent2.json"));
            }
            if (fault == "password_link")
            {
                File.Move(Path.Combine(_observerDir, "agent2.observer.password"), Path.Combine(outside, "password"));
                File.CreateSymbolicLink(Path.Combine(_observerDir, "agent2.observer.password"), Path.Combine(outside, "password"));
            }
            var active = StockAuthoritySource.Active(_dir);
            Assert.NotNull(active["agent1"]);
            using var observer = Observer();
            await observer.ReconcileAsync(_dir, default);
            Assert.True(observer.Lanes["agent1"]);
            Assert.False(observer.Lanes.TryGetValue("agent2", out var started) && started);
            Assert.Single(_lanes);
            await observer.StopLanesAsync();
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task HostedObserver_ReconcilesOnItsInterval_OrdinaryConsumerAndHostUnaffected()
    {
        Save("agent1", "sandbox-1");
        Save("agent2", "sandbox-2");
        var ordinaryChannel = Substitute.For<IChannel>();
        var ordinaryConnection = Substitute.For<IConnection>();
        ordinaryConnection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ordinaryChannel));
        var ordinary = new HeartbeatConsumerService(_options, _registry,
            new AgentConfigPublisherService(_options, NullLogger<AgentConfigPublisherService>.Instance), _config,
            NullLogger<HeartbeatConsumerService>.Instance) { Connect = (_, _) => Task.FromResult(ordinaryConnection) };
        using var observer = Observer(f => f.HostName == "broker-agent2.example.test", TimeSpan.FromMilliseconds(20));
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddHostedService(_ => ordinary);
            services.AddHostedService(_ => observer);
        }).Build();
        await host.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_lanes.Count == 0) await Task.Delay(5, timeout.Token);
        Save("agent3", "sandbox-3");
        while (_lanes.Count < 2) await Task.Delay(5, timeout.Token);
        Assert.Equal(Vhost("agent3") + "-observer", _lanes[^1].Factory.UserName);
        Assert.True(ordinary.IsConnected);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync();
        Assert.All(_lanes, l => Assert.True(Closed(l.Connection)));
        Assert.False(observer.ExecuteTask!.IsFaulted);
    }

    [Theory]
    [InlineData("agent1", false)] // managed record present: ordinary telemetry cannot speak for it
    [InlineData("agent9", true)]  // ordinary agent unaffected
    public async Task OrdinaryConsumer_DropsHeartbeatsClaimingAManagedAgent(string agent, bool accepted)
    {
        Save("agent1", "sandbox-1");
        var channel = Substitute.For<IChannel>();
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
        await using var ordinary = new HeartbeatConsumerService(_options, _registry,
            new AgentConfigPublisherService(_options, NullLogger<AgentConfigPublisherService>.Instance), _config,
            NullLogger<HeartbeatConsumerService>.Instance, new StockRuntimeMutationHold(_config)) { Connect = (_, _) => Task.FromResult(connection) };
        await ordinary.StartAsync(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (channel.ReceivedCalls().All(c => c.GetMethodInfo().Name != "BasicConsumeAsync")) await Task.Delay(1, timeout.Token);
        var consumer = (AsyncEventingBasicConsumer)channel.ReceivedCalls().First(c => c.GetMethodInfo().Name == "BasicConsumeAsync")
            .GetArguments().Single(a => a is AsyncEventingBasicConsumer)!;

        await consumer.HandleBasicDeliverAsync("consumer", 1, false, "fleet.orchestrator", "heartbeat." + agent,
            new BasicProperties(), JsonSerializer.SerializeToUtf8Bytes(new AgentHeartbeat(agent, "busy", DateTimeOffset.UtcNow)), default);

        Assert.Equal(accepted, _registry.Get(agent)?.ReportedStatus == "busy");
        await ordinary.StopAsync(default);
    }
}
