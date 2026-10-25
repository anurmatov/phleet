using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Orchestrator.Tests;

public sealed class StockHeartbeatIdentityTests
{
    [Theory]
    [InlineData("foreign_user")]
    [InlineData("foreign_payload")]
    [InlineData("foreign_route")]
    [InlineData("wrong_exchange")]
    [InlineData("access_request")]
    [InlineData("revoked")]
    public async Task ActualReceiver_DeniesBeforeRegistryMutation(string fault)
    {
        using var fixture = new Fixture();
        var properties = new BasicProperties { UserId = fixture.Identity.Principal("workload") };
        var routingKey = "heartbeat.agent1";
        var exchange = "fleet.orchestrator";
        var payloadAgent = "agent1";
        if (fault == "foreign_user") properties.UserId = "foreign";
        if (fault == "foreign_payload") payloadAgent = "agent2";
        if (fault == "foreign_route") routingKey = "heartbeat.agent2";
        if (fault == "wrong_exchange") exchange = "foreign";
        if (fault == "access_request") routingKey = "access-request.agent1";
        if (fault == "revoked") fixture.Save(fixture.Record with { State = "revoked" });
        await fixture.Receiver.OnMessageReceived(this, new BasicDeliverEventArgs("owned", 1, false, exchange, routingKey, properties,
            JsonSerializer.SerializeToUtf8Bytes(new AgentHeartbeat(payloadAgent, "idle", DateTimeOffset.UtcNow))));
        Assert.Empty(fixture.Registry.GetAll());
        Assert.Empty(fixture.History.GetHistory("agent1"));
    }

    [Fact]
    public async Task ActualReceiver_AdmitsBoundRegistration_ThenRefusesRevocationWithoutDeletingState()
    {
        using var fixture = new Fixture();
        var props = new BasicProperties { UserId = fixture.Identity.Principal("workload") };
        await fixture.Receiver.OnMessageReceived(this, new BasicDeliverEventArgs("owned", 1, false, "fleet.orchestrator", "registration.agent1", props,
            JsonSerializer.SerializeToUtf8Bytes(new AgentHeartbeat("agent1", "idle", DateTimeOffset.UtcNow))));
        Assert.Equal("idle", fixture.Registry.Get("agent1")!.ReportedStatus);
        fixture.Save(fixture.Record with { State = "revoked" });
        await fixture.Receiver.OnMessageReceived(this, new BasicDeliverEventArgs("owned", 2, false, "fleet.orchestrator", "heartbeat.agent1", props,
            JsonSerializer.SerializeToUtf8Bytes(new AgentHeartbeat("agent1", "busy", DateTimeOffset.UtcNow))));
        Assert.Equal("idle", fixture.Registry.Get("agent1")!.ReportedStatus);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "stock-heartbeat-" + Guid.NewGuid().ToString("N"));
        private string Authority => Path.Combine(_directory, "authority.json");
        private string Password => Path.Combine(_directory, "password");
        public StockEnrollmentAuthority Record { get; }
        public StockBrokerIdentity Identity { get; }
        public TaskHistoryStore History { get; } = new();
        public AgentRegistry Registry { get; }
        public HeartbeatConsumerService Receiver { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Record = new()
            {
                State = "active", Agent = "agent1", SandboxId = "synthetic", Epoch = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_directory, "jwks"),
                AssertionPrivateKeyPath = Path.Combine(_directory, "private"), AssertionPublicKeyPath = Path.Combine(_directory, "public"),
                MemoryHost = "memory.example.test", BrokerHost = "broker.example.test"
            };
            Save(Record);
            File.WriteAllText(Password, "synthetic-password");
            Identity = new(Authority, Password, "workload");
            Registry = new(History, NullLogger<AgentRegistry>.Instance);
            var options = Options.Create(new RabbitMqOptions { Host = "broker.example.test", Exchange = "fleet.orchestrator" });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StockOpenShell:AuthorityFile"] = Authority, ["StockOpenShell:BrokerPasswordFile"] = Password
            }).Build();
            Receiver = new(options, Registry, new AgentConfigPublisherService(options, NullLogger<AgentConfigPublisherService>.Instance),
                configuration, NullLogger<HeartbeatConsumerService>.Instance);
        }
        public void Save(StockEnrollmentAuthority record) => File.WriteAllText(Authority, JsonSerializer.Serialize(record));
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
