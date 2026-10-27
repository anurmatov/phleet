using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Models;
using Fleet.Bridge.Services;
using Microsoft.Extensions.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Agent.Tests;

public sealed class StockBrokerTransportTests
{
    [Theory]
    [InlineData("workload")]
    [InlineData("control")]
    [InlineData("observer")]
    public void Configure_AllRolesSelectPrivateAmqpAndRetainEpochAuthentication(string role)
    {
        using var fixture = new Fixture();
        var identity = new StockBrokerIdentity(fixture.Authority, fixture.Password, role);
        var factory = new ConnectionFactory
        {
            HostName = "broker.example.test", Port = 5671,
            Ssl = new SslOption { Enabled = true },
            UserName = "foreign", Password = "foreign", VirtualHost = "foreign",
            AutomaticRecoveryEnabled = true, TopologyRecoveryEnabled = true
        };
        identity.Configure(factory);
        Assert.Equal("broker.example.test", factory.HostName);
        Assert.Equal(5672, factory.Port);
        Assert.False(factory.Ssl.Enabled);
        Assert.Null(factory.Ssl.CertificateValidationCallback);
        Assert.Equal("stock-" + fixture.Epoch.ToString("N"), factory.VirtualHost);
        Assert.Equal(identity.Principal(role), factory.UserName);
        Assert.Equal("synthetic-password", factory.Password);
        Assert.Equal(identity.Principal(role), identity.Properties().UserId);
        Assert.False(factory.AutomaticRecoveryEnabled);
        Assert.False(factory.TopologyRecoveryEnabled);
        fixture.Save("revoked");
        Assert.Throws<StockIdentityDenied>(() => identity.Configure(factory));
        Assert.Throws<StockIdentityDenied>(() => identity.Properties());
    }

    [Theory]
    [InlineData("workload")]
    [InlineData("control")]
    [InlineData("observer")]
    public void Configure_WrongHostRefusesBeforeChangingFactory(string role)
    {
        using var fixture = new Fixture();
        var identity = new StockBrokerIdentity(fixture.Authority, fixture.Password, role);
        var factory = new ConnectionFactory { HostName = "foreign.example.test", Port = 5671, Ssl = new SslOption { Enabled = true } };
        var error = Assert.Throws<StockIdentityDenied>(() => identity.Configure(factory));
        Assert.Equal("broker_target", error.Message);
        Assert.Equal(5671, factory.Port);
        Assert.True(factory.Ssl.Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("foreign")]
    public void Configure_UnknownRoleNeverCreatesAnIdentity(string role)
    {
        using var fixture = new Fixture();
        Assert.Throws<StockIdentityDenied>(() => new StockBrokerIdentity(fixture.Authority, fixture.Password, role));
    }

    [Fact]
    public async Task OrdinaryBridge_StillUsesItsExistingFactoryWithoutStockOverrides()
    {
        var channel = Substitute.For<IChannel>();
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
        await using var bridge = new BridgeRelayService(
            Options.Create(new Fleet.Bridge.Configuration.BridgeOptions { TargetAgent = "agent1" }),
            Options.Create(new Fleet.Bridge.Configuration.RabbitMqOptions { Host = "broker.example.test" }),
            NullLogger<BridgeRelayService>.Instance)
        {
            Connect = (factory, _) =>
            {
                var defaults = new ConnectionFactory();
                Assert.Equal(defaults.Port, factory.Port);
                Assert.Equal(defaults.Ssl.Enabled, factory.Ssl.Enabled);
                Assert.Equal(defaults.UserName, factory.UserName);
                Assert.Equal(defaults.VirtualHost, factory.VirtualHost);
                Assert.True(factory.AutomaticRecoveryEnabled);
                Assert.True(factory.TopologyRecoveryEnabled);
                return Task.FromResult(connection);
            }
        };
        await bridge.InitializeAsync(default);
        Assert.Contains(channel.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicConsumeAsync");
    }

    [Fact]
    public async Task ActualRelay_RequiresBrokerValidatedControlIdentity_AndPublishesBoundCompletion()
    {
        using var fixture = new Fixture();
        await using var relay = fixture.Relay();
        var received = 0;
        relay.MessageReceived += (_, _, _, _, _, _, _, _) => received++;
        await relay.InitializeAsync(default);
        Assert.True(relay.IsInitializedForTesting);
        Assert.Equal(5672, fixture.Factory!.Port);
        Assert.False(fixture.Factory.Ssl.Enabled);
        Assert.Equal(fixture.Binding.Principal("workload"), fixture.Factory.UserName);
        Assert.Equal(fixture.Binding.VirtualHost, fixture.Factory.VirtualHost);
        var consumer = fixture.Channel.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();
        var correlation = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new { ChatId = 0, Sender = "bridge:operator", Text = "synthetic task", Type = "bridge-request", CorrelationId = correlation });
        var props = new BasicProperties { UserId = fixture.Binding.Principal("control") };
        await Deliver(consumer, props, body);
        Assert.Equal(1, received);
        props.UserId = null;
        await Deliver(consumer, props, body);
        Assert.Equal(1, received);
        props.UserId = "foreign";
        await Deliver(consumer, props, body);
        Assert.Equal(1, received);
        props.UserId = fixture.Binding.Principal("control");
        await Deliver(consumer, props, body, "agent2");
        Assert.Equal(1, received);
        await relay.PublishToAgentAsync("bridge", 0, "synthetic result", "bridge-response", correlation);
        var publishes = fixture.Channel.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "BasicPublishAsync").ToArray();
        Assert.Single(publishes);
        Assert.Equal(fixture.Binding.Principal("workload"), publishes[0].GetArguments().OfType<BasicProperties>().Single().UserId);
        fixture.Save("revoked");
        await Deliver(consumer, props, body);
        await relay.PublishToAgentAsync("bridge", 0, "must not publish", "bridge-response", correlation);
        Assert.Equal(1, received);
        Assert.Single(fixture.Channel.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicPublishAsync");
    }

    [Fact]
    public async Task ActualRelay_DoesNotBindTokenBroadcasts_OrPublishUnverifiedRoutes()
    {
        using var fixture = new Fixture();
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);
        Assert.DoesNotContain(fixture.Channel.ReceivedCalls(), c => c.GetMethodInfo().Name == "QueueBindAsync" && c.GetArguments().Contains("fleet.relay"));
        await relay.PublishToAgentAsync("agent2", 0, "foreign", "directive");
        await relay.PublishToAgentAsync("bridge", 0, "no correlation", "bridge-response");
        await relay.PublishAccessRequestAsync(new());
        Assert.DoesNotContain(fixture.Channel.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicPublishAsync");
    }

    [Theory]
    [InlineData("changed_host")]
    [InlineData("revoked")]
    [InlineData("missing_password")]
    public async Task ActualRelay_InvalidBindingRefusesBeforeConnect(string fault)
    {
        using var fixture = new Fixture();
        await using var relay = fixture.Relay();
        if (fault == "changed_host") fixture.Save("active", "other.example.test");
        if (fault == "revoked") fixture.Save("revoked");
        if (fault == "missing_password") File.Delete(fixture.Password);
        await Assert.ThrowsAsync<StockIdentityDenied>(() => relay.InitializeAsync(default));
        Assert.Null(fixture.Factory);
    }

    [Fact]
    public async Task ActualBridge_ControlToRelay_CompletionPublisherToMatchingPendingRequest()
    {
        using var fixture = new Fixture();
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);
        var controlChannel = Substitute.For<IChannel>();
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(controlChannel));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StockOpenShell:AuthorityFile"] = fixture.Authority, ["StockOpenShell:BrokerPasswordFile"] = fixture.Password
        }).Build();
        await using var bridge = new BridgeRelayService(
            Options.Create(new Fleet.Bridge.Configuration.BridgeOptions { TargetAgent = "agent1" }),
            Options.Create(new Fleet.Bridge.Configuration.RabbitMqOptions { Host = "broker.example.test" }),
            NullLogger<BridgeRelayService>.Instance, configuration)
        {
            Connect = (factory, _) =>
            {
                Assert.Equal(fixture.Binding.Principal("control"), factory.UserName);
                Assert.Equal(5672, factory.Port);
                Assert.False(factory.Ssl.Enabled);
                return Task.FromResult(connection);
            }
        };
        await bridge.InitializeAsync(default);
        var response = bridge.AskAsync("synthetic question", "operator", null, 5, default);
        var sent = controlChannel.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "BasicPublishAsync").GetArguments();
        var controlProps = sent.OfType<BasicProperties>().Single();
        var request = sent.OfType<ReadOnlyMemory<byte>>().Single();
        using var json = JsonDocument.Parse(request);
        var correlation = json.RootElement.GetProperty("CorrelationId").GetString()!;
        Assert.Equal(fixture.Binding.Principal("control"), controlProps.UserId);
        string? receivedCorrelation = null;
        relay.MessageReceived += (_, _, _, _, value, _, _, _) => receivedCorrelation = value;
        var workloadConsumer = fixture.Channel.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();
        // Exercise actual task intake and completion. Only inference is replaced, not the routing/event chain.
        var options = Options.Create(new AgentOptions { Name = "agent1", Role = "synthetic", WorkDir = fixture.WorkDir });
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(), Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Completed());
        var manager = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: new MessageSinkHolder());
        var telegram = Options.Create(new TelegramOptions());
        var behavior = new GroupBehavior(options, telegram, new AllowlistHolder(telegram), executor, relay, manager,
            new CommandDispatcher(manager, executor, options, NullLogger<CommandDispatcher>.Instance),
            new PromptAssembler(executor), NullLogger<GroupBehavior>.Instance);
        relay.MessageReceived += behavior.OnRelayMessage;
        using var publisher = new RelayCompletionPublisher(manager, relay, NullLogger<RelayCompletionPublisher>.Instance);
        var published = new TaskCompletionSource<(BasicProperties, ReadOnlyMemory<byte>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Channel.BasicPublishAsync<BasicProperties>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask).AndDoes(call => published.TrySetResult((call.ArgAt<BasicProperties>(3), call.ArgAt<ReadOnlyMemory<byte>>(4))));
        await Deliver(workloadConsumer, controlProps, request.ToArray());
        Assert.Equal(correlation, receivedCorrelation);
        var completion = await published.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var controlConsumer = controlChannel.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();
        completion.Item1.UserId = "foreign";
        await controlConsumer.HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "bridge", completion.Item1, completion.Item2);
        Assert.False(response.IsCompleted);
        completion.Item1.UserId = fixture.Binding.Principal("workload");
        await controlConsumer.HandleBasicDeliverAsync("owned", 2, false, "fleet.group", "bridge", completion.Item1,
            JsonSerializer.SerializeToUtf8Bytes(new { Sender = "agent2", Type = "bridge-response", CorrelationId = correlation, Text = "foreign" }));
        Assert.False(response.IsCompleted);
        await controlConsumer.HandleBasicDeliverAsync("owned", 3, false, "fleet.group", "bridge", completion.Item1,
            JsonSerializer.SerializeToUtf8Bytes(new { Sender = "agent1", Type = "bridge-response", CorrelationId = Guid.NewGuid().ToString("N"), Text = "unrelated" }));
        Assert.False(response.IsCompleted);
        await controlConsumer.HandleBasicDeliverAsync("owned", 2, false, "fleet.group", "bridge", completion.Item1, completion.Item2);
        Assert.Equal("synthetic completion", await response.WaitAsync(TimeSpan.FromSeconds(3)));
        fixture.Save("revoked");
        await Deliver(workloadConsumer, controlProps, request.ToArray());
        Assert.Single(executor.ReceivedCalls(), c => c.GetMethodInfo().Name == "ExecuteAsync");
        Assert.Equal("Error: stock control admission denied.", await bridge.AskAsync("must not send", "operator", null, 1, default));
        Assert.Single(controlChannel.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicPublishAsync");
    }

    [Fact]
    public async Task ActualRegistrationPublisher_UsesTheSameEnrolledPrivateNetworkIdentity()
    {
        using var fixture = new Fixture();
        var options = Options.Create(new AgentOptions { Name = "agent1", ShortName = "agent1", Role = "synthetic", WorkDir = fixture.WorkDir });
        var manager = new TaskManager(options, Substitute.For<IAgentExecutor>(), new SessionManager(), NullLogger<TaskManager>.Instance);
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(fixture.Channel));
        await using var heartbeat = new OrchestratorHeartbeatService(options, Options.Create(new RabbitMqOptions { Host = "broker.example.test" }),
            manager, Substitute.For<IFleetConnectionState>(), NullLogger<OrchestratorHeartbeatService>.Instance, stockBroker: fixture.Binding)
        {
            Connect = (factory, _) =>
            {
                Assert.Equal(fixture.Binding.Principal("workload"), factory.UserName);
                Assert.Equal(fixture.Binding.VirtualHost, factory.VirtualHost);
                Assert.Equal(5672, factory.Port);
                Assert.False(factory.Ssl.Enabled);
                return Task.FromResult(connection);
            }
        };
        try
        {
            await heartbeat.StartAsync(default);
            var args = fixture.Channel.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "BasicPublishAsync").GetArguments();
            Assert.Equal("registration.agent1", args[1]);
            using var body = JsonDocument.Parse(args.OfType<ReadOnlyMemory<byte>>().Single());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("ContainerName").ValueKind);
            Assert.Equal(fixture.Binding.Principal("workload"), args.OfType<BasicProperties>().Single().UserId);
            Assert.Equal("60000", args.OfType<BasicProperties>().Single().Expiration);
        }
        finally { await heartbeat.StopAsync(default); }
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("sandbox")]
    [InlineData("policy")]
    public void ReenrollmentCannotRebindExistingConnection(string fault)
    {
        using var fixture = new Fixture();
        var binding = fixture.Binding;
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Authority));
        var record = document.RootElement.Deserialize<StockEnrollmentAuthority>()!;
        record = fault switch
        {
            "epoch" => record with { Epoch = Guid.NewGuid() },
            "policy" => record with { PolicySha256 = new string('c', 64) },
            _ => record with { SandboxId = "another-sandbox" }
        };
        File.WriteAllText(fixture.Authority, JsonSerializer.Serialize(record));
        Assert.Throws<StockIdentityDenied>(() => binding.Configure(new ConnectionFactory { HostName = "broker.example.test" }));
        Assert.Throws<StockIdentityDenied>(() => binding.Properties());
        Assert.Throws<StockIdentityDenied>(() => binding.RequireDelivery(binding.Principal("workload"), "workload", "group", "group", "bridge", "bridge"));
    }

    private static async IAsyncEnumerable<AgentProgress> Completed()
    {
        await Task.Yield();
        yield return new AgentProgress { EventType = "result", Summary = "synthetic completion", FinalResult = "synthetic completion" };
    }

    private static Task Deliver(AsyncEventingBasicConsumer consumer, BasicProperties props, byte[] body, string target = "agent1")
        => consumer.HandleBasicDeliverAsync("owned", 1, false, "fleet.group", target, props, body);

    internal sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "stock-broker-" + Guid.NewGuid().ToString("N"));
        public string WorkDir => _directory;
        public string Authority => Path.Combine(_directory, "authority.json");
        public string Password => Path.Combine(_directory, "password");
        public readonly Guid Epoch = Guid.NewGuid();
        public IChannel Channel { get; } = Substitute.For<IChannel>();
        public ConnectionFactory? Factory;
        public StockBrokerIdentity Binding => new(Authority, Password, "workload");
        public Fixture() { Directory.CreateDirectory(_directory); File.WriteAllText(Password, "synthetic-password"); Save("active"); }
        public void Save(string state, string host = "broker.example.test") => File.WriteAllText(Authority, JsonSerializer.Serialize(new StockEnrollmentAuthority
        {
            State = state, Agent = "agent1", SandboxId = "synthetic-sandbox", Epoch = Epoch, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_directory, "jwks"),
            AssertionPrivateKeyPath = Path.Combine(_directory, "private"), AssertionPublicKeyPath = Path.Combine(_directory, "public"),
            MemoryHost = "memory.example.test", BrokerHost = host
        }));
        public GroupRelayService Relay()
        {
            var connection = Substitute.For<IConnection>();
            connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Channel));
            return new(Options.Create(new AgentOptions { Name = "agent1", ShortName = "agent1", Role = "synthetic", WorkDir = _directory }),
                Options.Create(new RabbitMqOptions { Host = "broker.example.test" }), NullLogger<GroupRelayService>.Instance, Binding)
            { Connect = (factory, _) => { Factory = factory; return Task.FromResult(connection); } };
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
