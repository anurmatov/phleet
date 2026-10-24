using System.Data.Common;
using System.Net;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellAgentDeletionTests
{
    [Theory]
    [InlineData("none", 200)]
    [InlineData("deprovision-proof", 409)]
    [InlineData("delete-proof", 409)]
    [InlineData("unknown-inventory", 409)]
    [InlineData("complete-inventory", 200)]
    [InlineData("sql", 503)]
    [InlineData("notification", 503)]
    public async Task ActualDeleteEndpoint_RequiresProofAndAtomicCleanup_PreservesTombstones(string fault, int expectedStatus)
    {
        var wire = new OpenShellConditionalMutationTests.MutationWire();
        using var gateway = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);
        var evidence = new Evidence(fault);
        var interceptor = new FailAgentDelete(fault == "sql");
        await using var h = ProvisioningHarness.Create(configure: (services, connection) =>
        {
            services.AddSingleton<IOpenShellGatewayClient>(gateway);
            services.AddSingleton<IOpenShellRuntimeEvidenceReader>(evidence);
            services.AddSingleton<OpenShellRetirementService>(sp => new(() => new OrchestratorDbContext(
                new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options),
                sp.GetRequiredService<IServiceScopeFactory>()));
        });
        var id = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Agents.Add(new() { Id = 1, Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test",
                ContainerName = "legacy", OpenShellMigrationState = "active",
                Projects = [new() { ProjectName = "project1" }], Tools = [new() { ToolName = "tool1" }] });
            db.OpenShellLaunches.Add(new() { Id = id, AgentId = 1, ActiveAgentId = 1, GatewayId = "gateway1",
                Workspace = "workspace1", SandboxId = "sandbox1", Phase = "activated", IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
            db.CredentialFiles.Add(new() { Id = 1, Name = "synthetic-file", FileName = "synthetic.txt", FilePath = "/synthetic/shared-file" });
            db.AgentCredentialMounts.Add(new() { AgentId = 1, CredentialFileId = 1, MountPath = "/synthetic/mount" });
            db.OpenShellSecretBindings.Add(new() { AgentId = 1, Reference = "synthetic-reference", SourceKind = "env_ref" });
            db.OpenShellIdentitySessions.Add(new() { AgentId = 1, LaunchId = id, IdentityEpoch = Guid.NewGuid(),
                SessionHash = new string('a', 64), Service = "memory", Route = "/mcp", ExpiresAt = DateTime.UtcNow.AddMinutes(1) });
            db.OpenShellRequestNonces.Add(new() { LaunchId = id, Jti = "synthetic-nonce", ExpiresAt = DateTime.UtcNow.AddMinutes(1) });
            db.AgentProjectAccess.AddRange(
                new() { AgentName = "agent1", Project = "project1", Source = AgentProjectAccessSource.Assignment },
                new() { AgentName = "agent1", Project = "manual", Source = AgentProjectAccessSource.Manual },
                new() { AgentName = "agent1", Project = "*", Source = AgentProjectAccessSource.Manual });
            if (fault is "unknown-inventory" or "complete-inventory") db.OpenShellLaunches.Add(new() { Id = Guid.NewGuid(), AgentId = 1,
                Phase = "retired", IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow.AddDays(-1), RevokedAt = DateTime.UtcNow });
        });
        var marker = Path.Combine(h.BaseDir, "retained-workspace-marker");
        await File.WriteAllTextAsync(marker, "synthetic retained workspace");
        var registry = new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance);
        await using (var db = Db(h)) registry.PreloadFromDbConfig(await db.Agents.SingleAsync());
        var notifier = new Notifier(fault == "notification");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(h.Connection));
        builder.Services.AddSingleton(h.Services.GetRequiredService<OpenShellRetirementService>());
        builder.Services.AddSingleton(h.Services.GetRequiredService<IAgentRuntimeLifecycle>());
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IAclChangeNotifier>(notifier);
        await using var app = builder.Build();
        app.UseOpenShellOperatorAuthorization("synthetic-operator");
        // Turn an unexpected persistence exception into the same failed HTTP boundary as a real host.
        Exception? unexpected = null;
        app.Use(async (ctx, next) => { try { await next(ctx); } catch (Exception ex) { unexpected = ex; ctx.Response.StatusCode = 500; } });
        app.MapAgentDeleteEndpoint();
        await app.StartAsync();
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-operator");
        var response = await http.DeleteAsync("/api/agents/agent1");
        Assert.Null(unexpected);
        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        var deleted = fault is "none" or "notification" or "complete-inventory";
        await using var check = Db(h);
        Assert.Equal(!deleted, await check.Agents.AnyAsync());
        Assert.Equal(!deleted, await check.OpenShellLaunches.AnyAsync());
        Assert.Equal(!deleted, await check.OpenShellSecretBindings.AnyAsync());
        Assert.Equal(!deleted, await check.OpenShellIdentitySessions.AnyAsync());
        Assert.Equal(!deleted, await check.OpenShellRequestNonces.AnyAsync());
        Assert.Equal(!deleted, await check.AgentProjects.AnyAsync());
        Assert.Equal(!deleted, await check.AgentTools.AnyAsync());
        Assert.Equal(!deleted, await check.AgentCredentialMounts.AnyAsync());
        Assert.Single(await check.CredentialFiles.ToListAsync());
        Assert.Equal(!deleted, await check.AgentProjectAccess.AnyAsync(x => x.Source == AgentProjectAccessSource.Assignment));
        Assert.Equal(2, await check.AgentProjectAccess.CountAsync(x => x.Source == AgentProjectAccessSource.Manual));
        Assert.Single(await check.OpenShellRetiredIds.ToListAsync());
        Assert.Equal(!deleted, registry.Get("agent1") is not null);
        Assert.Equal(deleted ? 1 : 0, notifier.Calls);
        Assert.Equal(2, wire.Mutations);
        Assert.Equal(0, h.DockerRequestCount);
        Assert.True(File.Exists(marker));
        if (fault == "sql") Assert.True(interceptor.Attempted);
        if (fault == "notification") Assert.Contains("deleted", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("active")]
    [InlineData("lease")]
    [InlineData("mapping")]
    [InlineData("session")]
    [InlineData("replacement")]
    public async Task ConfigurationCleanup_ReloadsAuthoritativeOwnership_AndNeverDeletesUnknownRows(string fault)
    {
        await using var h = ProvisioningHarness.Create();
        var id = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Agents.AddRange(
                new() { Id = 1, Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "legacy", OpenShellMigrationState = "active" },
                new() { Id = 2, Name = "agent2", DisplayName = "Agent 2", Role = "test", Model = "test", ContainerName = "other" });
            db.OpenShellLaunches.Add(new() { Id = id, AgentId = 1, GatewayId = "gateway1", Workspace = "workspace1",
                SandboxId = "sandbox1", Phase = "retired", RevokedAt = DateTime.UtcNow, IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
            if (fault != "mapping") db.OpenShellRetiredIds.Add(new() { GatewayId = "gateway1", SandboxId = "sandbox1", RetiredAt = DateTime.UtcNow });
            if (fault == "active") db.OpenShellLaunches.Add(new() { Id = Guid.NewGuid(), AgentId = 1, ActiveAgentId = 1,
                Phase = "prepared", IdentityEpoch = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
            if (fault == "lease") db.OpenShellLeases.Add(new() { AgentId = 1, Owner = Guid.NewGuid(), ExpiresAt = DateTime.UtcNow.AddSeconds(-1) });
            if (fault == "session") db.OpenShellIdentitySessions.Add(new() { AgentId = 2, LaunchId = id,
                SessionHash = new string('b', 64), Service = "memory", Route = "/mcp", IdentityEpoch = Guid.NewGuid(), ExpiresAt = DateTime.UtcNow.AddMinutes(1) });
        });
        using var peers = new ServiceCollection().AddSingleton<IOpenShellRuntimeEvidenceReader>(new Evidence("none")).BuildServiceProvider();
        var service = new OpenShellRetirementService(() => Db(h), peers.GetRequiredService<IServiceScopeFactory>());
        var result = await service.CompleteAgentDeletionAsync("agent1", fault == "replacement" ? 2 : 1);
        Assert.False(result.Result.Success);
        Assert.Equal(409, result.Result.StatusCode);
        Assert.False(result.AclChanged);
        await using var db = Db(h);
        Assert.Equal(2, await db.Agents.CountAsync());
        Assert.True(await db.OpenShellLaunches.AnyAsync(x => x.Id == id));
        Assert.Equal(fault == "session", await db.OpenShellIdentitySessions.AnyAsync());
        Assert.Equal(fault == "lease", await db.OpenShellLeases.AnyAsync());
    }

    [Fact]
    public async Task ConfigurationCleanup_MissingEvidence_NeverOpensStorage()
    {
        var writes = 0;
        using var peers = new ServiceCollection().BuildServiceProvider();
        var service = new OpenShellRetirementService(() => { writes++; throw new InvalidOperationException(); }, peers.GetRequiredService<IServiceScopeFactory>());
        var result = await service.CompleteAgentDeletionAsync("agent1", 1);
        Assert.False(result.Result.Success);
        Assert.Equal(503, result.Result.StatusCode);
        Assert.Equal(0, writes);
    }

    private static OrchestratorDbContext Db(ProvisioningHarness h) => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(h.Connection).Options);

    private sealed class Evidence(string fault) : IOpenShellRuntimeEvidenceReader
    {
        private int _absenceReads;
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget target, CancellationToken cancellationToken)
        {
            if (target.RequireAbsence) _absenceReads++;
            var absent = target.RequireAbsence && fault != "deprovision-proof" && !(fault == "delete-proof" && _absenceReads >= 2);
            return Task.FromResult(new OpenShellRuntimeEvidence(target.LaunchId, target.IdentityEpoch, target.GatewayId,
                target.RuntimeId, true, absent, 0, 0, DateTime.UtcNow, fault == "complete-inventory", fault == "complete-inventory" ? 0 : -1));
        }
    }

    private sealed class Notifier(bool fail) : IAclChangeNotifier
    {
        public int Calls { get; private set; }
        public Task PublishAclChangedAsync(CancellationToken ct = default)
        { Calls++; return fail ? Task.FromException(new IOException("owned synthetic notification failure")) : Task.CompletedTask; }
    }

    private sealed class FailAgentDelete(bool fail) : DbCommandInterceptor
    {
        public bool Attempted { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (fail && command.CommandText.Contains("DELETE FROM \"agents\"", StringComparison.Ordinal))
            { Attempted = true; throw new IOException("owned synthetic transaction failure"); }
            return ValueTask.FromResult(result);
        }
    }
}
