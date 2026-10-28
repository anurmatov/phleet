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

public sealed class StockObserverIsolationTests
{
    [Theory]
    [InlineData("active", "broker.example.test")]
    [InlineData("revoked", "broker.example.test")]
    [InlineData("active", "127.0.0.1")]
    [InlineData("revoked", "127.0.0.1")]
    public async Task SeparateSubscriptions_OrdinaryRegistrationHeartbeatAndAccessContinue(string state, string ordinaryHost)
    {
        using var f = new Fixture();
        f.Options.Value.Host = ordinaryHost;
        var ordinaryChannel = Substitute.For<IChannel>();
        var stockChannel = Substitute.For<IChannel>();
        var publisherChannel = Substitute.For<IChannel>();
        var publisher = f.Publisher(publisherChannel);
        var stockConnection = Connection(stockChannel);
        ConnectionFactory? ordinaryFactory = null;
        ConnectionFactory? stockFactory = null;
        var ordinary = new HeartbeatConsumerService(f.Options, f.Registry, publisher, f.Config,
            NullLogger<HeartbeatConsumerService>.Instance, f.Hold)
        {
            Connect = (factory, _) => { ordinaryFactory = factory; return Task.FromResult(Connection(ordinaryChannel)); }
        };
        using var observer = f.Observer(publisher, (factory, _) =>
        {
            stockFactory = factory;
            return Task.FromResult(stockConnection);
        });
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(ordinary);
            services.AddSingleton<IRabbitMqStatus>(ordinary);
            services.AddHostedService(sp => sp.GetRequiredService<HeartbeatConsumerService>());
            services.AddHostedService(_ => observer);
        }).Build();
        await host.StartAsync();
        // Wait only for the in-process BackgroundService, never for a broker.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (stockChannel.ReceivedCalls().All(c => c.GetMethodInfo().Name != "BasicConsumeAsync"))
            await Task.Delay(1, timeout.Token);
        Assert.Equal(ordinaryHost, ordinaryFactory!.HostName);
        Assert.Equal(ordinaryHost, f.Options.Value.Host);
        Assert.Equal(f.Record.BrokerHost, stockFactory!.HostName);
        Assert.Equal("/", ordinaryFactory.VirtualHost);
        Assert.Equal("guest", ordinaryFactory.UserName);
        Assert.True(ordinaryFactory.AutomaticRecoveryEnabled);
        Assert.Equal("stock-" + f.Record.Epoch.ToString("N"), stockFactory!.VirtualHost);
        Assert.Equal("stock-" + f.Record.Epoch.ToString("N") + "-observer", stockFactory.UserName);
        Assert.Equal(5672, stockFactory.Port);
        Assert.False(stockFactory.Ssl.Enabled);
        Assert.False(stockFactory.AutomaticRecoveryEnabled);
        Assert.Same(ordinary, host.Services.GetRequiredService<IRabbitMqStatus>());
        Assert.True(ordinary.IsConnected);

        var stock = Consumer(stockChannel);
        await Deliver(stock, "registration.agent1", new AgentHeartbeat("agent1", "idle", DateTimeOffset.UtcNow),
            "stock-" + f.Record.Epoch.ToString("N") + "-workload");
        Assert.Equal("idle", f.Registry.Get("agent1")!.ReportedStatus);
        if (state == "revoked") f.Save(f.Record with { State = "revoked" });
        await Deliver(stock, "heartbeat.agent1", new AgentHeartbeat("agent1", "busy", DateTimeOffset.UtcNow), "foreign");
        Assert.Equal("idle", f.Registry.Get("agent1")!.ReportedStatus);
        if (state == "revoked")
        {
            await Deliver(stock, "heartbeat.agent1", new AgentHeartbeat("agent1", "busy", DateTimeOffset.UtcNow),
                "stock-" + f.Record.Epoch.ToString("N") + "-workload");
            Assert.Equal("idle", f.Registry.Get("agent1")!.ReportedStatus);
        }
        stockConnection.ConnectionShutdownAsync += Raise.Event<AsyncEventHandler<ShutdownEventArgs>>(
            stockConnection, new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "synthetic stock shutdown"));
        await ExerciseOrdinary(Consumer(ordinaryChannel), f, publisherChannel);
        Assert.True(ordinary.IsConnected);
        Assert.True(f.Hold.Denies("agent1"));
        Assert.False(f.Hold.Denies("agent2"));
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("channel")]
    [InlineData("revoked")]
    [InlineData("authority_missing")]
    [InlineData("password_missing")]
    [InlineData("cleanup")]
    public async Task StockStartupFailure_DoesNotStopHostOrOrdinaryFlows(string fault)
    {
        using var f = new Fixture();
        var ordinaryChannel = Substitute.For<IChannel>();
        var publisherChannel = Substitute.For<IChannel>();
        var publisher = f.Publisher(publisherChannel);
        if (fault == "revoked") f.Save(f.Record with { State = "revoked" });
        if (fault == "authority_missing") File.Delete(f.Authority);
        if (fault == "password_missing") File.Delete(f.Password);
        var hold = new StockRuntimeMutationHold(f.Config);
        var ordinary = new HeartbeatConsumerService(f.Options, f.Registry, publisher, f.Config,
            NullLogger<HeartbeatConsumerService>.Instance, hold)
        { Connect = (_, _) => Task.FromResult(Connection(ordinaryChannel)) };
        var stockConnection = Connection(Substitute.For<IChannel>());
        stockConnection.CreateChannelAsync(Arg.Any<CreateChannelOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IChannel>>(_ => throw new IOException("synthetic channel failure"));
        if (fault == "cleanup")
            stockConnection.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("synthetic cleanup failure"));
        using var observer = f.Observer(publisher, (_, _) => fault == "connect"
            ? throw new IOException("synthetic connection failure") : Task.FromResult(stockConnection));
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddHostedService(_ => ordinary);
            services.AddHostedService(_ => observer);
        }).Build();
        await host.StartAsync();
        await observer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        if (fault is "channel" or "cleanup")
            Assert.Contains(stockConnection.ReceivedCalls(), c => c.GetMethodInfo().Name == "CloseAsync");
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.True(ordinary.IsConnected);
        await ExerciseOrdinary(Consumer(ordinaryChannel), f, publisherChannel, fault != "authority_missing");
        Assert.True(hold.Denies("agent1"));
        if (fault == "authority_missing") Assert.True(hold.Denies("agent2"));
        await host.StopAsync();
    }

    [Fact]
    public void ObserverIdentity_WrongHostRefusesBeforeChangingFactory()
    {
        using var f = new Fixture();
        var identity = new StockBrokerIdentity(f.Authority, f.Password, "observer");
        var factory = new ConnectionFactory { HostName = "foreign.example.test" };
        var error = Assert.Throws<StockIdentityDenied>(() => identity.Configure(factory));
        Assert.Equal("broker_target", error.Message);
        Assert.Equal("foreign.example.test", factory.HostName);
        Assert.Equal("/", factory.VirtualHost);
        Assert.Equal("guest", factory.UserName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("partial")]
    public void UnavailableEnrollment_HoldsAllMutations_WithoutThrowing(string fault)
    {
        using var f = new Fixture();
        if (fault == "missing") File.Delete(f.Authority);
        if (fault == "malformed") File.WriteAllText(f.Authority, "not json");
        if (fault == "duplicate") File.WriteAllText(f.Authority, "{\"Agent\":\"agent1\",\"Agent\":\"agent2\"}");
        if (fault == "partial") f.Config["StockOpenShell:AuthorityFile"] = null;
        var hold = new StockRuntimeMutationHold(f.Config);
        Assert.True(hold.HasEnrollment);
        Assert.True(hold.Denies("agent1"));
        Assert.True(hold.Denies("agent2"));
    }

    [Fact]
    public async Task DefaultOff_ObserverDoesNotConnect()
    {
        using var f = new Fixture();
        f.Config["StockOpenShell:AuthorityFile"] = null;
        f.Config["StockOpenShell:BrokerPasswordFile"] = null;
        var calls = 0;
        using var observer = f.Observer(f.Publisher(Substitute.For<IChannel>()), (_, _) =>
        { calls++; throw new Exception("must not connect"); });
        await observer.StartAsync(default);
        await observer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, calls);
        Assert.False(new StockRuntimeMutationHold(f.Config).HasEnrollment);
        var channel = Substitute.For<IChannel>();
        var publisherChannel = Substitute.For<IChannel>();
        await using var ordinary = new HeartbeatConsumerService(f.Options, f.Registry, f.Publisher(publisherChannel), f.Config,
            NullLogger<HeartbeatConsumerService>.Instance)
        { Connect = (_, _) => Task.FromResult(Connection(channel)) };
        await ordinary.StartAsync(default);
        await ExerciseOrdinary(Consumer(channel), f, publisherChannel, knownEnrollment: false);
    }

    private static async Task ExerciseOrdinary(AsyncEventingBasicConsumer consumer, Fixture f, IChannel publisher, bool knownEnrollment = true)
    {
        await Deliver(consumer, "registration.agent2", new AgentHeartbeat("agent2", "idle", DateTimeOffset.UtcNow));
        Assert.Equal("idle", f.Registry.Get("agent2")!.ReportedStatus);
        await Deliver(consumer, "heartbeat.agent2", new AgentHeartbeat("agent2", "busy", DateTimeOffset.UtcNow));
        Assert.Equal("busy", f.Registry.Get("agent2")!.ReportedStatus);
        await Deliver(consumer, "access-request.agent2", new
        { request_id = "request1", target_agent = "agent2", user_id = 1, message_text = "synthetic request" });
        var publish = Assert.Single(publisher.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicPublishAsync");
        var args = publish.GetArguments();
        Assert.Equal("fleet.group", args[0]);
        Assert.Equal("agent3", args[1]);
        using var body = JsonDocument.Parse((ReadOnlyMemory<byte>)args[4]!);
        Assert.Equal("access.request", body.RootElement.GetProperty("Type").GetString());
        Assert.Contains("request1", body.RootElement.GetProperty("Text").GetString());
        Assert.Contains("Never auto-approve", body.RootElement.GetProperty("Text").GetString());
        if (!knownEnrollment) return;
        // An ordinary delivery must not impersonate or grant access to the held enrollment.
        await Deliver(consumer, "heartbeat.agent1", new AgentHeartbeat("agent1", "offline", DateTimeOffset.UtcNow));
        await Deliver(consumer, "access-request.agent1", new { target_agent = "agent1", user_id = 1 });
        Assert.NotEqual("offline", f.Registry.Get("agent1")?.ReportedStatus);
        Assert.Single(publisher.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicPublishAsync");
    }

    private static AsyncEventingBasicConsumer Consumer(IChannel channel) =>
        (AsyncEventingBasicConsumer)Assert.Single(channel.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicConsumeAsync").GetArguments()
            .Single(a => a is AsyncEventingBasicConsumer)!;

    private static Task Deliver(AsyncEventingBasicConsumer consumer, string route, object payload, string? user = null) =>
        consumer.HandleBasicDeliverAsync("consumer", 1, false, "fleet.orchestrator", route,
            new BasicProperties { UserId = user }, JsonSerializer.SerializeToUtf8Bytes(payload), default);

    private static IConnection Connection(IChannel channel)
    {
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));
        return connection;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "stock-observer-" + Guid.NewGuid().ToString("N"));
        public string Authority => Path.Combine(_directory, "authority.json");
        public string Password => Path.Combine(_directory, "password");
        public StockEnrollmentAuthority Record { get; }
        public IConfiguration Config { get; }
        public StockRuntimeMutationHold Hold { get; }
        public AgentRegistry Registry { get; } = new(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance);
        public IOptions<RabbitMqOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(
            new RabbitMqOptions { Host = "broker.example.test", Exchange = "fleet.orchestrator" });
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Record = new()
            {
                State = "active", Agent = "agent1", SandboxId = "synthetic", Epoch = Guid.NewGuid(),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), GatewayIssuer = "issuer", GatewayAudience = "audience",
                GatewayJwksPath = Path.Combine(_directory, "jwks"), AssertionPrivateKeyPath = Path.Combine(_directory, "private"),
                AssertionPublicKeyPath = Path.Combine(_directory, "public"), MemoryHost = "memory.example.test",
                BrokerHost = "broker.example.test"
            };
            Save(Record);
            File.WriteAllText(Password, "synthetic-password");
            Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StockOpenShell:AuthorityFile"] = Authority, ["StockOpenShell:BrokerPasswordFile"] = Password,
                ["FLEET_CTO_AGENT"] = "agent3"
            }).Build();
            Hold = new(Config);
        }
        public AgentConfigPublisherService Publisher(IChannel channel) =>
            new(Options, NullLogger<AgentConfigPublisherService>.Instance)
            { Connect = (_, _) => Task.FromResult(Connection(channel)) };
        public StockHeartbeatObserverService Observer(AgentConfigPublisherService publisher,
            Func<ConnectionFactory, CancellationToken, Task<IConnection>> connect) =>
            new(Options, Registry, publisher, Config, Hold, NullLogger<HeartbeatConsumerService>.Instance,
                NullLogger<StockHeartbeatObserverService>.Instance) { Connect = connect };
        public void Save(StockEnrollmentAuthority record) => File.WriteAllText(Authority, JsonSerializer.Serialize(record));
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
