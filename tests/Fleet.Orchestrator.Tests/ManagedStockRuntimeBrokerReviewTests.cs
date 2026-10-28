using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    [Theory]
    [InlineData("applying", "managed_runtime_unavailable")]
    [InlineData("needs_attention", "managed_runtime_unavailable")]
    [InlineData("revoked", null)]
    public async Task Docker_start_is_refused_while_a_managed_record_is_unresolved(string state, string? expected)
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseInMemoryDatabase(dbName));
        using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var agent = ManagedStockRuntimeTests.Agent();
            agent.AppliedRuntimeKind = "docker"; // Docker→OpenShell candidate still in flight
            agent.AppliedRuntimeBinding = new ManagedRecord { State = state }.Serialize();
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
        }
        var hold = new StockRuntimeMutationHold(new ConfigurationBuilder().Build(), provider.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(expected, await hold.DescribeAppliedFaultAsync("agent1"));
        if (expected is not null) // no registry or Docker service: any legacy Docker dispatch would throw
            Assert.Equal(expected, await new Fleet.Orchestrator.Tools.StartAgentTool(null!, null!, hold).StartAgentAsync("agent1"));
    }

    [Fact]
    public void Verified_docker_retirement_releases_the_ordinary_hold_but_a_revoked_or_linked_record_does_not()
    {
        var authority = Path.Combine(_root, "managed", "authority");
        Directory.CreateDirectory(authority);
        var record = Path.Combine(authority, "agent1.json");
        var hold = new StockRuntimeMutationHold(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StockOpenShell:AuthorityDirectory"] = authority }).Build());
        File.WriteAllText(record, """{"State":"revoked","Agent":"agent1"}""");
        Assert.True(hold.HoldsOrdinaryTraffic("agent1"));
        Assert.False(StockAuthoritySource.IsRetired(authority, "agent1"));
        File.WriteAllText(record, """{"State":"retired","Agent":"agent1"}""");
        Assert.False(hold.HoldsOrdinaryTraffic("agent1"));
        Assert.False(hold.HoldsOrdinaryTraffic("Agent1"));
        Assert.True(StockAuthoritySource.IsRetired(authority, "AGENT1"));
        Assert.True(File.Exists(record)); // custody: the record stays in place
        // A link is never trusted as retirement, even to a retired record.
        var elsewhere = Path.Combine(_root, "retired.json");
        File.Move(record, elsewhere);
        File.CreateSymbolicLink(record, elsewhere);
        Assert.True(hold.HoldsOrdinaryTraffic("agent1"));
        Assert.False(StockAuthoritySource.IsRetired(authority, "agent1"));
    }

}
