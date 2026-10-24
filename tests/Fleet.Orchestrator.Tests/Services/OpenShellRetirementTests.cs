using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Services;

public class OpenShellRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedLifecycleTools_RevokeBeforeConditionalRpc_ThenVerifyRetirement(bool delete)
    {
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        var reader = new Evidence();
        await using var h = ProvisioningHarness.Create(configure: (services, connection) =>
        {
            services.AddSingleton<IOpenShellGatewayClient>(client);
            services.AddSingleton<IOpenShellRuntimeEvidenceReader>(reader);
            services.AddSingleton<OpenShellRetirementService>(sp => new(() => new OrchestratorDbContext(
                new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(connection).Options),
                sp.GetRequiredService<IServiceScopeFactory>()));
        });
        var launchId = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "legacy", OpenShellMigrationState = "active" };
            db.Agents.Add(agent);
            db.OpenShellLaunches.Add(new() { Id = launchId, Agent = agent, ActiveAgentId = 1,
                GatewayId = "gateway1", SandboxId = "sandbox1", Workspace = "workspace1", Phase = "activated",
                IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        });
        var lifecycle = h.Services.GetRequiredService<IAgentRuntimeLifecycle>();
        wire.BeforeMutation = async () =>
        {
            await using var db = Db(h);
            var row = await db.OpenShellLaunches.SingleAsync();
            Assert.NotNull(row.RevokedAt);
            Assert.NotNull(row.StopIntentJson);
            Assert.Single(await db.OpenShellRetiredIds.ToListAsync());
        };
        if (delete) Assert.True((await h.Service.DeprovisionAsync("agent1")).Success);
        else Assert.Contains("stopped", await new StopAgentTool(lifecycle).StopAgentAsync("agent1"));
        await using var check = Db(h);
        var launch = await check.OpenShellLaunches.SingleAsync();
        Assert.Equal(delete ? "retired" : "stopped", launch.Phase);
        Assert.Equal(delete ? (int?)null : 1, launch.ActiveAgentId);
        Assert.Empty(await check.OpenShellLeases.ToListAsync());
        Assert.NotNull(launch.StopIntentJson);
        Assert.Equal(delete, launch.DeleteIntentJson is not null);
        Assert.Equal(delete ? 2 : 1, wire.Mutations);
        Assert.Equal(0, h.DockerRequestCount);
        Assert.Single(await check.Agents.ToListAsync());
        Assert.True(reader.Reads > 0);
        var stopIntent = System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(launch.StopIntentJson!);
        Assert.Equal(stopIntent!.RequestId.ToString("D"), wire.Stop!.RequestId);
        Assert.Equal(12ul, stopIntent.ResourceVersion);
        if (delete)
        {
            var deleteIntent = System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(launch.DeleteIntentJson!);
            Assert.Equal(deleteIntent!.RequestId.ToString("D"), wire.Delete!.RequestId);
            Assert.Equal(13ul, deleteIntent.ResourceVersion);
        }
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("old")]
    [InlineData("drain")]
    [InlineData("wrong")]
    [InlineData("stale")]
    public async Task FailedOrUnverifiedStop_RetainsIntentSlotAndNeverReissues(string fault)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "legacy", OpenShellMigrationState = "active" };
            db.Agents.Add(agent);
            db.OpenShellLaunches.Add(new() { Id = Guid.NewGuid(), Agent = agent, ActiveAgentId = 1,
                GatewayId = "gateway1", SandboxId = "sandbox1", Workspace = "workspace1", Phase = "activated",
                IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        });
        var wire = new OpenShellConditionalMutationTests.MutationWire(fault is "drain" or "wrong" or "stale" ? null : fault);
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence { Drain = fault != "drain", Wrong = fault == "wrong", Stale = fault == "stale" }).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        var first = await service.StopAsync("agent1");
        Assert.False(first.Success);
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("stop_pending", row.Phase);
        Assert.NotNull(row.StopIntentJson);
        Assert.NotNull(row.RevokedAt);
        Assert.Equal(1, row.ActiveAgentId);
        var before = wire.Mutations;
        Assert.False((await service.StopAsync("agent1")).Success);
        Assert.Equal(before, wire.Mutations);
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Theory]
    [InlineData("peers")]
    [InlineData("lease")]
    public async Task MissingPeersOrLiveLease_DoNotRevokeOrMutate(string fault)
    {
        await using var h = ProvisioningHarness.Create();
        await SeedActiveAsync(h);
        if (fault == "lease") await h.SeedAsync(db => db.OpenShellLeases.Add(new()
            { AgentId = 1, Owner = Guid.NewGuid(), ExpiresAt = DateTime.UtcNow.AddMinutes(1) }));
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        var services = new ServiceCollection();
        if (fault != "peers") services.AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence());
        using var peers = services.BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        Assert.False((await service.StopAsync("agent1")).Success);
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("activated", row.Phase);
        Assert.Null(row.RevokedAt);
        Assert.Null(row.StopIntentJson);
        Assert.Empty(await db.OpenShellRetiredIds.ToListAsync());
        Assert.Equal(0, wire.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedDelete_PreservesCleanupIntentAndSlotWithoutResending(bool missingAbsence)
    {
        await using var h = ProvisioningHarness.Create();
        await SeedActiveAsync(h);
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence { ProveAbsence = !missingAbsence }).BuildServiceProvider();
        wire.BeforeMutation = () => wire.Stop is not null && !missingAbsence
            ? Task.FromException(new IOException("owned synthetic lost delete response")) : Task.CompletedTask;
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        Assert.False((await service.DeprovisionAsync("agent1")).Success);
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("cleanup_pending", row.Phase);
        Assert.NotNull(row.DeleteIntentJson);
        Assert.NotNull(row.RevokedAt);
        Assert.Equal(1, row.ActiveAgentId);
        Assert.Single(await db.OpenShellLeases.ToListAsync());
        var calls = wire.Calls.Count;
        Assert.False((await service.DeprovisionAsync("agent1")).Success);
        Assert.Equal(calls, wire.Calls.Count);
        Assert.Equal(0, h.DockerRequestCount);
    }

    private static Task SeedActiveAsync(ProvisioningHarness h) => h.SeedAsync(db =>
    {
        var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test",
            ContainerName = "legacy", OpenShellMigrationState = "active" };
        db.Agents.Add(agent);
        db.OpenShellLaunches.Add(new() { Id = Guid.NewGuid(), Agent = agent, ActiveAgentId = 1,
            GatewayId = "gateway1", SandboxId = "sandbox1", Workspace = "workspace1", Phase = "activated",
            IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
    });

    private static OrchestratorDbContext Db(ProvisioningHarness h) => new(
        new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(h.Connection).Options);

    private sealed class Evidence : IOpenShellRuntimeEvidenceReader
    {
        public bool Drain { get; init; } = true;
        public bool ProveAbsence { get; init; } = true;
        public bool Wrong { get; init; }
        public bool Stale { get; init; }
        public int Reads { get; private set; }
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget target, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new OpenShellRuntimeEvidence(Wrong ? Guid.NewGuid() : target.LaunchId, target.IdentityEpoch,
                target.GatewayId, target.RuntimeId, true, target.RequireAbsence && ProveAbsence, Drain ? 0 : 1, 0, DateTime.UtcNow.AddSeconds(Stale ? -6 : 0)));
        }
    }
}
