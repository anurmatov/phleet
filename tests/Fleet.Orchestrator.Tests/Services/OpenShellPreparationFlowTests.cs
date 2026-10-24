using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Openshell.V1;

namespace Fleet.Orchestrator.Tests.Services;

// This exercises the production coordinator and relational store with explicitly owned peers.
// NOT an outside hook/agent/TLS/broker integration pass: those concrete prerequisites remain absent.
public sealed class OpenShellPreparationFlowTests
{
    [Theory]
    [InlineData("held")]
    [InlineData("probe")]
    [InlineData("create_lost")]
    [InlineData("readiness")]
    [InlineData("prior_retired")]
    [InlineData("drift_id")]
    [InlineData("drift_version")]
    [InlineData("drift_environment")]
    [InlineData("drift_configuration")]
    [InlineData("drift_policy")]
    [InlineData("missing_provider")]
    [InlineData("foreign_provider")]
    public async Task Preparation_UsesSingleCreate_AndRevokesBeforeExactHeldCleanup(string fault)
    {
        await using var sql = new SqliteConnection("Data Source=:memory:"); await sql.OpenAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(sql).Options;
        await using (var db = new OrchestratorDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Agents.Add(new() { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "unused" });
            await db.SaveChangesAsync();
        }
        if (fault == "prior_retired")
        {
            await using var history = new OrchestratorDbContext(options);
            history.OpenShellLaunches.Add(new() { Id = Guid.NewGuid(), AgentId = 1, IdentityEpoch = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow.AddHours(-1), RevokedAt = DateTime.UtcNow.AddHours(-1), Phase = "retired" });
            await history.SaveChangesAsync();
        }
        var plan = OpenShellPolicyCompilerTests.Plan();
        var selection = new OpenShellPreparationScope(1, "start", null, plan.Hash, DateTime.UtcNow.AddSeconds(100), plan);
        var wire = new OwnedGateway(fault, options);
        var services = new ServiceCollection();
        services.AddSingleton<IOpenShellGatewayClient>(wire);
        services.AddSingleton<IOpenShellRuntimeEvidenceReader>(new OwnedEvidence());
        services.AddSingleton<IOpenShellPreparationPreflight>(new OwnedPreflight());
        services.AddSingleton<IOpenShellBootstrapProbe>(new OwnedProbe(fault));
        using var peers = services.BuildServiceProvider();
        var driver = new OpenShellProvisioningService(() => new(options), new OwnedAuthority(selection), peers.GetRequiredService<IServiceScopeFactory>());
        var result = await driver.TryPrepareAsync("agent1", "start", false, default);
        Assert.NotNull(result); Assert.False(result.Success); Assert.Equal(1, wire.Creates);
        await using var read = new OrchestratorDbContext(options);
        var row = await read.OpenShellLaunches.SingleAsync(l => l.CreateRequestId != null);
        Assert.NotNull(row.PreparationJson); Assert.NotNull(row.CreateRequestId); Assert.NotNull(row.CreateAttemptedAt);
        Assert.Equal(selection.Deadline, row.PreparationDeadline); Assert.NotNull(row.RevokedAt);
        if (fault == "create_lost")
        {
            Assert.Null(row.SandboxId); Assert.Equal("cleanup_pending", row.Phase); Assert.NotNull(row.ActiveAgentId);
            Assert.Equal(0, wire.Stops); Assert.Equal(0, wire.Deletes); // No name/label/ID guess or blind resend.
        }
        else
        {
            Assert.Equal(1, wire.Stops); Assert.Equal(1, wire.Deletes); Assert.Equal("retired", row.Phase);
            Assert.Null(row.ActiveAgentId); Assert.Empty(await read.OpenShellLeases.ToListAsync());
            Assert.Single(await read.OpenShellRetiredIds.ToListAsync());
            if (fault.StartsWith("drift_") || fault is "missing_provider" or "foreign_provider") Assert.Null(row.SecurityReadyJson);
            if (fault is "held" or "prior_retired") { Assert.NotNull(row.SecurityReadyJson); Assert.Contains("activation_held", result.Message); }
        }
    }

    private sealed class OwnedAuthority(OpenShellPreparationScope selection) : IOpenShellPreparationAuthority
    {
        public Task<OpenShellPreparationScope?> SelectAsync(string name, string intent, CancellationToken ct) => Task.FromResult<OpenShellPreparationScope?>(selection);
    }
    private sealed class OwnedPreflight : IOpenShellPreparationPreflight
    {
        public Task VerifyAsync(OpenShellPreparationScope scope, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyLegacyOwnershipAsync(OpenShellPreparationScope scope, CancellationToken ct) => Task.CompletedTask;
        public Task StopLegacyExactAsync(string expectedId, CancellationToken ct) => throw new Exception("No legacy runtime is owned by this fresh fixture.");
    }
    private sealed class OwnedProbe(string fault) : IOpenShellBootstrapProbe
    {
        public Task VerifyAsync(Guid id, Guid epoch, OpenShellSandboxSnapshot target, IReadOnlyList<OpenShellSupervisorProof> proofs, CancellationToken ct)
        { if (fault == "probe") throw new Exception("owned probe failure"); return Task.CompletedTask; }
    }
    private sealed class OwnedEvidence : IOpenShellRuntimeEvidenceReader
    {
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget t, CancellationToken ct) =>
            Task.FromResult(new OpenShellRuntimeEvidence(t.LaunchId, t.IdentityEpoch, t.GatewayId, t.RuntimeId,
                true, t.RequireAbsence, 0, 0, DateTime.UtcNow, true, 0));
    }
    private sealed class OwnedGateway(string fault, DbContextOptions<OrchestratorDbContext> options) : IOpenShellGatewayClient
    {
        public int Creates, Stops, Deletes;
        public Task<OpenShellSandboxSnapshot> CreateAsync(OpenShellLaunchPlan plan, Guid launchId, Guid epoch, Guid requestId, DateTime bootstrapDeadline, CancellationToken ct)
        { Creates++; if (fault == "create_lost") throw new Exception("ambiguous owned reply"); return Task.FromResult(OpenShellPreparationTests.Target()); }
        public Task<OpenShellSupervisorProof> ReadCurrentSupervisorAsync(OpenShellSandboxSnapshot target, string provider, CancellationToken ct)
        {
            if (fault == "readiness") throw new Exception("unavailable current supervisor");
            var status = OpenShellPreparationTests.Ready();
            switch (fault)
            {
                case "drift_id": status.Receipt.Desired.ProviderId = "replacement1"; break;
                case "drift_version": status.Receipt.Desired.ProviderResourceVersion++; break;
                case "drift_environment": status.Receipt.Desired.ProviderEnvRevision++; status.Observed.ProviderEnvRevision++; break;
                case "drift_configuration": status.Receipt.Desired.ConfigRevision++; status.Observed.ConfigRevision++; break;
                case "drift_policy": status.Receipt.Desired.PolicyHash = new string('c', 64); status.Observed.PolicyHash = new string('c', 64); break;
                case "missing_provider": status.Receipt.Desired.ProviderId = ""; break;
                case "foreign_provider": status.Receipt.Provider = "foreign1"; break;
            }
            return Task.FromResult(OpenShellSupervisorProof.Verify(target, provider, status, DateTime.UtcNow));
        }
        public Task<IReadOnlyList<OpenShellSandboxSnapshot>> InventoryAsync(CancellationToken ct) => throw new Exception("No ambiguous inventory guess permitted.");
        public Task<OpenShellSandboxSnapshot?> ReadExactAsync(string id, CancellationToken ct) => Task.FromResult<OpenShellSandboxSnapshot?>(OpenShellPreparationTests.Target());
        public async Task<OpenShellSandboxSnapshot> StopExactAsync(OpenShellSandboxSnapshot target, Guid requestId, CancellationToken ct)
        {
            await AssertRevoked(ct); Stops++; Assert.Equal("sandbox1", target.Id); Assert.NotEqual(Guid.Empty, requestId);
            return target;
        }
        public async Task<OpenShellDeleteAcknowledgement> DeleteExactAsync(OpenShellSandboxSnapshot target, Guid requestId, bool allowMissing, CancellationToken ct)
        {
            await AssertRevoked(ct); Deletes++; Assert.Equal("sandbox1", target.Id); Assert.NotEqual(Guid.Empty, requestId);
            return new(target.Id, DeletionOutcome.Completed);
        }
        private async Task AssertRevoked(CancellationToken ct)
        {
            await using var db = new OrchestratorDbContext(options);
            Assert.NotNull((await db.OpenShellLaunches.AsNoTracking().SingleAsync(l => l.CreateRequestId != null, ct)).RevokedAt);
            Assert.Single(await db.OpenShellRetiredIds.ToListAsync(ct));
        }
    }
}
