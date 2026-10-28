using Fleet.Orchestrator.Services;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests;

public sealed class ManagedStockRuntimeBrokerReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "managed-review-" + Guid.NewGuid().ToString("N"));

    public ManagedStockRuntimeBrokerReviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Broker_grants_use_the_group_exchange_the_agent_and_bridge_declare()
    {
        // GroupRelayService binds the agent queue to fleet.group (read on the exchange); the bridge
        // control lane declares, binds and publishes on fleet.group. fleet.relay is the token fanout.
        var workload = StockBrokerProvisioner.Permissions("workload", "fleet.agent.agent1");
        Assert.Matches(workload.Read, "fleet.group");
        Assert.Matches(workload.Read, "fleet.agent.agent1");
        var control = StockBrokerProvisioner.Permissions("control", "fleet.agent.agent1");
        foreach (var grant in new[] { control.Configure, control.Write, control.Read })
        {
            Assert.Matches(grant, "fleet.group");
            Assert.Matches(grant, "fleet.agent.bridge");
            Assert.DoesNotMatch(grant, "fleet.relay");
        }
    }

    [Theory]
    [InlineData("Agent1")]
    [InlineData("AGENT1")]
    public void Case_changed_name_is_still_held_by_the_lowercase_managed_record(string name)
    {
        var authority = Path.Combine(_root, "managed", "authority");
        Directory.CreateDirectory(authority);
        File.WriteAllText(Path.Combine(authority, "agent1.json"), "{}");
        Assert.True(StockAuthoritySource.HasEntry(authority, name));
        var hold = new StockRuntimeMutationHold(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StockOpenShell:ConfigDir"] = _root }).Build());
        Assert.True(hold.HoldsOrdinaryTraffic(name));
        Assert.False(hold.HoldsOrdinaryTraffic("agent2"));
    }
}
