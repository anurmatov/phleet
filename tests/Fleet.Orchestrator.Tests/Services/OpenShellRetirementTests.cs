using System.Net;
using System.Net.Http.Json;
using Fleet.Orchestrator.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
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
        var observedCalls = wire.Calls.Count;
        if (delete) Assert.True((await h.Service.DeprovisionAsync("agent1")).Success);
        else Assert.Contains("stopped", await new StopAgentTool(lifecycle).StopAgentAsync("agent1"));
        Assert.Equal(observedCalls, wire.Calls.Count); // Terminal idempotency re-verifies proof, not a new mutation.

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
        Assert.InRange(first.StatusCode, 400, 599);
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("stop_pending", row.Phase);
        Assert.NotNull(row.StopIntentJson);
        Assert.Contains(System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(row.StopIntentJson!)!.RequestId.ToString("D"), first.Message);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnboundActiveLaunch_CannotBeHiddenByRetiredHistory(bool delete)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            db.Agents.Add(new() { Id = 1, Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "legacy",
                OpenShellMigrationState = "active" });
            db.OpenShellLaunches.Add(new() { Id = Guid.NewGuid(), AgentId = 1, ActiveAgentId = 1,
                Phase = "prepared", IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
            db.OpenShellLaunches.Add(new() { Id = Guid.NewGuid(), AgentId = 1, Phase = "retired",
                IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow.AddMinutes(-10), RevokedAt = DateTime.UtcNow,
                GatewayId = "gateway1", SandboxId = "retired1", Workspace = "workspace1" });
            db.OpenShellRetiredIds.Add(new() { GatewayId = "gateway1", SandboxId = "retired1", RetiredAt = DateTime.UtcNow });
        });
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        var evidence = new Evidence();
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(evidence).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        var result = delete ? await service.DeprovisionAsync("agent1") : await service.StopAsync("agent1");
        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Empty(wire.Calls);
        Assert.Equal(0, evidence.Reads);
        await using var db = Db(h);
        Assert.Equal("prepared", (await db.OpenShellLaunches.SingleAsync(x => x.ActiveAgentId != null)).Phase);
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OperatorReconcile_UsesOriginalIntentAndRequiresFreshProof(bool delete, bool replay)
    {
        await using var h = ProvisioningHarness.Create();
        await SeedActiveAsync(h);
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        var evidence = new Evidence { Drain = delete, ProveAbsence = false };
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(evidence).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        Assert.False((delete ? await service.DeprovisionAsync("agent1") : await service.StopAsync("agent1")).Success);
        await using var db = Db(h);
        var launch = await db.OpenShellLaunches.SingleAsync();
        var original = delete ? launch.DeleteIntentJson : launch.StopIntentJson;
        var intent = System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(original!)!;
        var originalStop = wire.Stop?.Clone(); var originalDelete = wire.Delete?.Clone();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(service);
        await using var app = builder.Build();
        app.UseOpenShellOperatorAuthorization("synthetic-operator");
        app.MapOpenShellReconciliationEndpoint();
        await app.StartAsync();
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-operator");
        var input = new OpenShellReconciliationRequest(launch.Id, intent.RequestId, replay);
        var calls = wire.Calls.Count;
        // An in-flight retirement still owns its lease; an operator cannot steal it.
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/agents/agent1/openshell/reconcile", input)).StatusCode);
        Assert.Equal(calls, wire.Calls.Count);
        await h.SeedAsync(x => x.OpenShellLeases.Single().ExpiresAt = DateTime.UtcNow.AddSeconds(-1));
        foreach (var mismatch in new[] { input with { ExpectedLaunchId = Guid.NewGuid() }, input with { ExpectedRequestId = Guid.NewGuid() } })
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/agents/agent1/openshell/reconcile", mismatch)).StatusCode);
        Assert.Equal(calls, wire.Calls.Count);
        evidence.Drain = true; evidence.ProveAbsence = true;
        var response = await http.PostAsJsonAsync("/api/agents/agent1/openshell/reconcile", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(calls + (replay ? 2 : 0), wire.Calls.Count); // Health + the identical mutation, no fresh Get/name lookup.
        if (delete) Assert.Equal(originalDelete, wire.Delete);
        else Assert.Equal(originalStop, wire.Stop);
        db.ChangeTracker.Clear();
        launch = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal(delete ? "retired" : "stopped", launch.Phase);
        Assert.Equal(original, delete ? launch.DeleteIntentJson : launch.StopIntentJson);
        Assert.Equal(delete ? null : (int?)1, launch.ActiveAgentId);
        Assert.Empty(await db.OpenShellLeases.ToListAsync());
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Theory]
    [InlineData("", "synthetic-operator", 503)]
    [InlineData("synthetic-operator", "wrong", 401)]
    [InlineData("synthetic-operator", "synthetic-operator", 503)]
    public async Task ReconcileRoute_RequiresOperatorAndTrustedPeersBeforeStorage(string configured, string supplied, int status)
    {
        var reads = 0;
        using var peers = new ServiceCollection().BuildServiceProvider();
        var service = new OpenShellRetirementService(() => { reads++; throw new InvalidOperationException(); },
            peers.GetRequiredService<IServiceScopeFactory>());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(service);
        await using var app = builder.Build();
        app.UseOpenShellOperatorAuthorization(configured);
        app.MapOpenShellReconciliationEndpoint();
        await app.StartAsync();
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", supplied);
        var response = await http.PostAsJsonAsync("/api/agents/agent1/openshell/reconcile",
            new OpenShellReconciliationRequest(Guid.NewGuid(), Guid.NewGuid(), true));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostMutationReply_ReconcilesOnlyWithFreshProof_WithoutResend(bool delete)
    {
        await using var h = ProvisioningHarness.Create();
        await SeedActiveAsync(h);
        var wire = new OpenShellConditionalMutationTests.MutationWire { FailNextMutationReply = !delete };
        if (delete) wire.BeforeMutation = () => { if (wire.Stop is not null) wire.FailNextMutationReply = true; return Task.CompletedTask; };
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence()).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        var result = delete ? await service.DeprovisionAsync("agent1") : await service.StopAsync("agent1");
        Assert.False(result.Success); Assert.Equal(503, result.StatusCode);
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        var json = delete ? row.DeleteIntentJson : row.StopIntentJson;
        var intent = System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(json!)!;
        Assert.Contains(intent.RequestId.ToString("D"), result.Message);
        await h.SeedAsync(x => x.OpenShellLeases.Single().ExpiresAt = DateTime.UtcNow.AddSeconds(-1));
        var calls = wire.Calls.Count;
        Assert.True((await service.ReconcileAsync("agent1", row.Id, intent.RequestId, false)).Success);
        Assert.Equal(calls, wire.Calls.Count);
        db.ChangeTracker.Clear();
        Assert.Equal(delete ? "retired" : "stopped", (await db.OpenShellLaunches.SingleAsync()).Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_RejectsCapabilityDowngradeOrDifferentLeaseOwner(bool differentOwner)
    {
        await using var h = ProvisioningHarness.Create();
        await SeedActiveAsync(h);
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        var evidence = new Evidence { Drain = false };
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(evidence).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        Assert.False((await service.StopAsync("agent1")).Success);
        await using var db = Db(h);
        var launch = await db.OpenShellLaunches.SingleAsync();
        var intent = System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(launch.StopIntentJson!)!;
        await h.SeedAsync(x =>
        {
            var lease = x.OpenShellLeases.Single(); lease.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            if (differentOwner) lease.Owner = Guid.NewGuid();
        });
        wire.Supported = false; evidence.Drain = true;
        var mutations = wire.Mutations;
        var result = await service.ReconcileAsync("agent1", launch.Id, intent.RequestId, true);
        Assert.False(result.Success);
        Assert.Equal(differentOwner ? 409 : 503, result.StatusCode);
        Assert.Contains(intent.RequestId.ToString("D"), result.Message);
        Assert.Equal(mutations, wire.Mutations);
        db.ChangeTracker.Clear();
        Assert.Equal("stop_pending", (await db.OpenShellLaunches.SingleAsync()).Phase);
        Assert.Single(await db.OpenShellLeases.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationRecovery_LostStopReply_CompletesChainKeepingOriginalStopIdentity(bool replay)
    {
        var launchId = Guid.NewGuid();
        var stopIntent = new OpenShellRetirementService.Intent(Guid.NewGuid(), "gateway1", "workspace1",
            "sandbox1", "name1", 12);
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test",
                ContainerName = "held", OpenShellMigrationState = "pending" };
            db.Agents.Add(agent);
            db.OpenShellLaunches.Add(new() { Id = launchId, Agent = agent, ActiveAgentId = 1,
                GatewayId = "gateway1", SandboxId = "sandbox1", Workspace = "workspace1",
                Phase = "cleanup_pending", RevokedAt = DateTime.UtcNow, IdentityEpoch = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow.AddSeconds(-30),
                StopIntentJson = System.Text.Json.JsonSerializer.Serialize(stopIntent) });
            db.OpenShellRetiredIds.Add(new() { GatewayId = "gateway1", SandboxId = "sandbox1", RetiredAt = DateTime.UtcNow });
        });
        // The preparation lease is launch-owned (Reserve) and expired, unlike retirement leases.
        await h.SeedAsync(db => db.OpenShellLeases.Add(new() { AgentId = 1, Owner = launchId, ExpiresAt = DateTime.UtcNow.AddSeconds(-1) }));
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence()).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        var result = await service.ReconcilePreparationAsync("agent1", launchId, replay);
        Assert.True(result.Success);
        Assert.Equal(replay, wire.Stop is not null); // without replay the lost Stop is never resent
        if (replay) Assert.Equal(stopIntent.RequestId.ToString("D"), wire.Stop!.RequestId);
        Assert.NotNull(wire.Delete); // the never-issued Delete continues the chain with its own identity
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("retired", row.Phase);
        Assert.Null(row.ActiveAgentId);
        Assert.Equal(stopIntent.RequestId,
            System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(row.StopIntentJson!)!.RequestId);
        var minted = System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(row.DeleteIntentJson!)!;
        Assert.NotEqual(stopIntent.RequestId, minted.RequestId);
        Assert.Equal(minted.RequestId.ToString("D"), wire.Delete!.RequestId);
        Assert.Equal("pending", (await db.Agents.SingleAsync()).OpenShellMigrationState); // never promoted
        Assert.Empty(await db.OpenShellLeases.ToListAsync());
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Fact]
    public async Task PreparationRecovery_LostDeleteReply_ReplaysOnlyOriginalDeleteIdentity()
    {
        var launchId = Guid.NewGuid();
        var stopIntent = new OpenShellRetirementService.Intent(Guid.NewGuid(), "gateway1", "workspace1", "sandbox1", "name1", 12);
        var deleteIntent = new OpenShellRetirementService.Intent(Guid.NewGuid(), "gateway1", "workspace1", "sandbox1", "name1", 13);
        var stopJson = System.Text.Json.JsonSerializer.Serialize(stopIntent);
        var deleteJson = System.Text.Json.JsonSerializer.Serialize(deleteIntent);
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test",
                ContainerName = "held", OpenShellMigrationState = "pending" };
            db.Agents.Add(agent);
            db.OpenShellLaunches.Add(new() { Id = launchId, Agent = agent, ActiveAgentId = 1,
                GatewayId = "gateway1", SandboxId = "sandbox1", Workspace = "workspace1",
                Phase = "cleanup_pending", RevokedAt = DateTime.UtcNow, IdentityEpoch = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow.AddSeconds(-30), StopIntentJson = stopJson, DeleteIntentJson = deleteJson });
            db.OpenShellRetiredIds.Add(new() { GatewayId = "gateway1", SandboxId = "sandbox1", RetiredAt = DateTime.UtcNow });
        });
        await h.SeedAsync(db => db.OpenShellLeases.Add(new() { AgentId = 1, Owner = launchId, ExpiresAt = DateTime.UtcNow.AddSeconds(-1) }));
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence()).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        Assert.True((await service.ReconcilePreparationAsync("agent1", launchId, replay: true)).Success);
        Assert.Null(wire.Stop); // Delete already owns the pending cleanup; the finished Stop is never replayed.
        Assert.Equal(deleteIntent.RequestId.ToString("D"), wire.Delete!.RequestId);
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("retired", row.Phase);
        Assert.Null(row.ActiveAgentId);
        Assert.Equal(stopJson, row.StopIntentJson);
        Assert.Equal(deleteJson, row.DeleteIntentJson); // original identity byte-identical, never re-minted
        Assert.Equal("pending", (await db.Agents.SingleAsync()).OpenShellMigrationState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationRecovery_LostCreateReply_CompletesFromInventoryProofWithoutMutation(bool replay)
    {
        var launchId = Guid.NewGuid();
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test",
                ContainerName = "held", OpenShellMigrationState = "pending" };
            db.Agents.Add(agent);
            db.OpenShellLaunches.Add(new() { Id = launchId, Agent = agent, ActiveAgentId = 1, Workspace = "workspace1",
                Phase = "cleanup_pending", RevokedAt = DateTime.UtcNow, IdentityEpoch = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow.AddSeconds(-30) });
        });
        await h.SeedAsync(db => db.OpenShellLeases.Add(new() { AgentId = 1, Owner = launchId, ExpiresAt = DateTime.UtcNow.AddSeconds(-1) }));
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence()).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        var result = await service.ReconcilePreparationAsync("agent1", launchId, replay);
        if (replay)
        {
            // Nothing was ever persisted to replay for an unbound ambiguous Create.
            Assert.False(result.Success);
            Assert.Equal(409, result.StatusCode);
            Assert.Equal("preparation launch " + launchId.ToString("D") + " conflicts with current ownership or proof; recovery state retained", result.Message);
            await using var kept = Db(h);
            Assert.Equal("cleanup_pending", (await kept.OpenShellLaunches.SingleAsync()).Phase);
            Assert.Single(await kept.OpenShellLeases.ToListAsync());
            Assert.Empty(wire.Calls);
            return;
        }
        Assert.True(result.Success);
        Assert.Empty(wire.Calls); // absence/drain inventory is the only gate; zero gateway mutations.
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("retired", row.Phase);
        Assert.Null(row.ActiveAgentId);
        Assert.Null(row.CreateRequestId); // no Create was ever recorded or retried
        Assert.Equal("pending", (await db.Agents.SingleAsync()).OpenShellMigrationState);
        Assert.Empty(await db.OpenShellLeases.ToListAsync());
    }

    [Fact]
    public async Task PreparationRecovery_ReplayedStopWithLostReply_RetainsPendingWithoutDeleteIntent()
    {
        var launchId = Guid.NewGuid();
        var stopIntent = new OpenShellRetirementService.Intent(Guid.NewGuid(), "gateway1", "workspace1", "sandbox1", "name1", 12);
        var stopJson = System.Text.Json.JsonSerializer.Serialize(stopIntent);
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test",
                ContainerName = "held", OpenShellMigrationState = "pending" };
            db.Agents.Add(agent);
            db.OpenShellLaunches.Add(new() { Id = launchId, Agent = agent, ActiveAgentId = 1,
                GatewayId = "gateway1", SandboxId = "sandbox1", Workspace = "workspace1",
                Phase = "cleanup_pending", RevokedAt = DateTime.UtcNow, IdentityEpoch = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow.AddSeconds(-30), StopIntentJson = stopJson });
            db.OpenShellRetiredIds.Add(new() { GatewayId = "gateway1", SandboxId = "sandbox1", RetiredAt = DateTime.UtcNow });
        });
        await h.SeedAsync(db => db.OpenShellLeases.Add(new() { AgentId = 1, Owner = launchId, ExpiresAt = DateTime.UtcNow.AddSeconds(-1) }));
        var wire = new OpenShellConditionalMutationTests.MutationWire { FailNextMutationReply = true };
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence()).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        var result = await service.ReconcilePreparationAsync("agent1", launchId, replay: true);
        Assert.False(result.Success);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal(stopIntent.RequestId.ToString("D"), wire.Stop!.RequestId); // sent under the original identity only
        Assert.Null(wire.Delete);
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("cleanup_pending", row.Phase);
        Assert.Equal(stopJson, row.StopIntentJson);
        Assert.Null(row.DeleteIntentJson); // the continuation stops at proof, never past a lost reply
        Assert.Equal(1, row.ActiveAgentId);
        Assert.Equal("pending", (await db.Agents.SingleAsync()).OpenShellMigrationState);
    }

    [Theory]
    [InlineData("live_lease")]
    [InlineData("foreign_lease")]
    [InlineData("missing_lease")]
    [InlineData("promoted_state")]
    [InlineData("second_open_launch")]
    [InlineData("wrong_launch")]
    [InlineData("intent_mismatch")]
    [InlineData("missing_tombstone")]
    [InlineData("not_pending")]
    public async Task PreparationRecovery_RefusesEveryMismatch_WithoutMutationOrPromotion(string fault)
    {
        var launchId = Guid.NewGuid();
        var stopIntent = fault == "intent_mismatch"
            ? new OpenShellRetirementService.Intent(Guid.NewGuid(), "gateway1", "workspace1", "other-sandbox", "name1", 12)
            : new OpenShellRetirementService.Intent(Guid.NewGuid(), "gateway1", "workspace1", "sandbox1", "name1", 12);
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test",
                ContainerName = "held", OpenShellMigrationState = fault == "promoted_state" ? "active" : "pending" };
            db.Agents.Add(agent);
            db.OpenShellLaunches.Add(new() { Id = launchId, Agent = agent, ActiveAgentId = 1,
                GatewayId = "gateway1", SandboxId = "sandbox1", Workspace = "workspace1",
                Phase = fault == "not_pending" ? "prepared" : "cleanup_pending",
                RevokedAt = fault == "not_pending" ? null : DateTime.UtcNow, IdentityEpoch = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow.AddSeconds(-30),
                StopIntentJson = System.Text.Json.JsonSerializer.Serialize(stopIntent) });
            if (fault != "missing_tombstone")
                db.OpenShellRetiredIds.Add(new() { GatewayId = "gateway1", SandboxId = "sandbox1", RetiredAt = DateTime.UtcNow });
        });
        if (fault == "second_open_launch")
            await h.SeedAsync(db => db.OpenShellLaunches.Add(new() { Id = Guid.NewGuid(), AgentId = 1,
                Phase = "prepared", IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow.AddSeconds(-60) }));
        if (fault != "missing_lease")
            await h.SeedAsync(db => db.OpenShellLeases.Add(new()
            {
                AgentId = 1,
                Owner = fault == "foreign_lease" ? Guid.NewGuid() : launchId,
                ExpiresAt = fault == "live_lease" ? DateTime.UtcNow.AddMinutes(1) : DateTime.UtcNow.AddSeconds(-1),
            }));
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        using var peers = new ServiceCollection().AddSingleton<IOpenShellGatewayClient>(client)
            .AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence()).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        var result = await service.ReconcilePreparationAsync("agent1",
            fault == "wrong_launch" ? Guid.NewGuid() : launchId, replay: true);
        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Empty(wire.Calls);
        await using var db = Db(h);
        var row = await db.OpenShellLaunches.SingleAsync(l => l.Id == launchId);
        Assert.Equal(fault == "not_pending" ? "prepared" : "cleanup_pending", row.Phase);
        Assert.Equal(1, row.ActiveAgentId);
        Assert.Equal(fault == "promoted_state" ? "active" : "pending",
            (await db.Agents.AsNoTracking().SingleAsync()).OpenShellMigrationState); // never promoted here
        var expectedLeases = fault is "missing_lease" ? 0 : 1;
        Assert.Equal(expectedLeases, (await db.OpenShellLeases.ToListAsync()).Count);
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Theory]
    [InlineData("", "synthetic-operator", 503)]
    [InlineData("synthetic-operator", "wrong", 401)]
    [InlineData("synthetic-operator", "synthetic-operator", 503)]
    public async Task PreparationReconcileRoute_RequiresOperatorAndTrustedPeersBeforeStorage(string configured, string supplied, int status)
    {
        var reads = 0;
        using var peers = new ServiceCollection().BuildServiceProvider();
        var service = new OpenShellRetirementService(() => { reads++; throw new InvalidOperationException(); },
            peers.GetRequiredService<IServiceScopeFactory>());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(service);
        await using var app = builder.Build();
        app.UseOpenShellOperatorAuthorization(configured);
        app.MapOpenShellReconciliationEndpoint();
        await app.StartAsync();
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", supplied);
        var response = await http.PostAsJsonAsync("/api/agents/agent1/openshell/reconcile-preparation",
            new OpenShellPreparationReconciliationRequest(Guid.NewGuid(), true));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, reads);
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
        public bool Drain { get; set; } = true;
        public bool ProveAbsence { get; set; } = true;
        public bool Inventory { get; set; } = true;
        public bool Wrong { get; init; }
        public bool Stale { get; init; }
        public int Reads { get; private set; }
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget target, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new OpenShellRuntimeEvidence(Wrong ? Guid.NewGuid() : target.LaunchId, target.IdentityEpoch,
                target.GatewayId, target.RuntimeId, true, target.RequireAbsence && ProveAbsence, Drain ? 0 : 1, 0,
                DateTime.UtcNow.AddSeconds(Stale ? -6 : 0), Inventory, Inventory ? 0 : -1));
        }
    }
}
