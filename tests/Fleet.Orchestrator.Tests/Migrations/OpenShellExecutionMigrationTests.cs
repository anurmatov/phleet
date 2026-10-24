using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;
using Microsoft.Extensions.Options;
using Fleet.Shared.Security;

namespace Fleet.Orchestrator.Tests.Migrations;

// Requires an explicitly supplied, disposable MySQL target. Never substitutes SQLite or skips.
public sealed class OpenShellExecutionMigrationTests : IAsyncLifetime
{
    private string _admin = "";
    private string _connection = "";
    private string _database = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("FLEET_ORCHESTRATOR_MIGRATION_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("An approved throwaway MySQL target is required; migration acceptance is UNRUN.");
        _admin = new MySqlConnectionStringBuilder(configured) { Database = "" }.ConnectionString;
        _database = "openshell_test_" + Guid.NewGuid().ToString("N")[..16];
        await Sql(_admin, $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
        _connection = new MySqlConnectionStringBuilder(configured) { Database = _database }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_database.Length != 0) await Sql(_admin, $"DROP DATABASE IF EXISTS `{_database}`");
    }

    [Fact]
    public async Task Upgrade_PreservesRuntimeColumns_AndTombstoneSurvivesAgentCleanup()
    {
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20261014141428_RemoveAgentPiImageInput");
        await Sql(_connection, """
            INSERT INTO agents (Name, DisplayName, Role, Model, ContainerName, Provider, MemoryLimitMb,
              IsEnabled, GroupDebounceSeconds, PrefixMessages, ProactiveIntervalMinutes, ShowStats, TelegramSendOnly)
            VALUES ('agent1','Agent 1','test','test','synthetic-runtime','codex',1024,
              0,15,0,0,1,0)
            """);
        await db.Database.MigrateAsync();
        var agent = await db.Agents.SingleAsync();
        Assert.False(agent.IsEnabled);
        Assert.Equal("synthetic-runtime", agent.ContainerName);
        Assert.Equal("codex", agent.Provider);
        Assert.Equal("openshell", db.Entry(agent).Property<string>("ExecutionShell").CurrentValue);
        Assert.Equal("normal", db.Entry(agent).Property<string>("OpenShellPolicy").CurrentValue);
        Assert.Equal("pending", db.Entry(agent).Property<string>("OpenShellMigrationState").CurrentValue);
        var store = new OpenShellLaunchStore(db);
        var now = DateTime.UtcNow;
        var launch = await store.ReserveAsync(agent.Id, agent.ContainerName, now, TimeSpan.FromSeconds(120));
        // This fixture exercises persistence, not a runtime stop. Set the prerequisite phase
        // directly in its owned database; the real reader/driver qualification remains separate.
        launch.Phase = "legacy_stopped";
        await db.SaveChangesAsync();
        await store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", now);
        launch.Workspace = "/workspace/agent1";
        launch.PolicyHash = new string('a', 64);
        launch.ImageHash = new string('b', 64);
        launch.DestinationHash = new string('c', 64);
        launch.ControlUrl = "https://control.example.invalid";
        launch.CapabilitySecretRef = "capability-ref1";
        launch.Error = "compatibility_blocked";
        launch.StopIntentJson = "{\"operation\":\"synthetic-stop\"}";
        launch.DeleteIntentJson = "{\"operation\":\"synthetic-delete\"}";
        launch.UpdatedAt = now;
        launch.BotPollerLeaseOwner = launch.Id;
        launch.BotPollerLeaseExpiresAt = now.AddSeconds(30);
        launch.TelegramOffset = 17;
        db.OpenShellSecretBindings.Add(new() { AgentId = agent.Id, Reference = "native-auth:codex", SourceKind = "native_auth" });
        db.OpenShellIdentitySessions.Add(new() { SessionHash = new string('d', 64), Service = "memory", Route = "/mcp",
            AgentId = agent.Id, LaunchId = launch.Id, IdentityEpoch = launch.IdentityEpoch, ExpiresAt = now.AddMinutes(1) });
        db.OpenShellRequestNonces.Add(new() { Jti = "nonce1", LaunchId = launch.Id, ExpiresAt = now.AddMinutes(1) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var storedLaunch = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("/workspace/agent1", storedLaunch.Workspace);
        Assert.Equal(launch.StopIntentJson, storedLaunch.StopIntentJson);
        Assert.Equal(launch.DeleteIntentJson, storedLaunch.DeleteIntentJson);
        Assert.Equal("unclassified", (await db.OpenShellSecretBindings.SingleAsync()).Classification);
        Assert.Equal(launch.IdentityEpoch, (await db.OpenShellIdentitySessions.SingleAsync()).IdentityEpoch);
        Assert.Single(await db.OpenShellRequestNonces.ToListAsync());
        await store.RetireAsync(launch.Id, now);
        db.ChangeTracker.Clear();
        // Only synthetic persistence cleanup, not a runtime stop/delete acceptance claim.
        await db.OpenShellRequestNonces.ExecuteDeleteAsync();
        await db.OpenShellIdentitySessions.ExecuteDeleteAsync();
        await db.OpenShellSecretBindings.ExecuteDeleteAsync();
        await db.OpenShellLeases.ExecuteDeleteAsync();
        await db.OpenShellLaunches.ExecuteDeleteAsync();
        await db.Agents.ExecuteDeleteAsync();
        Assert.Single(await db.OpenShellRetiredIds.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task FreshDatabase_ContainsNewSchema_WithoutRuntimeOrIdentityAdmission()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.OpenShellLaunches.ToListAsync());
        Assert.Empty(await db.OpenShellLeases.ToListAsync());
        Assert.Empty(await db.OpenShellRetiredIds.ToListAsync());
        Assert.Empty(await db.OpenShellSecretBindings.ToListAsync());
        Assert.Empty(await db.OpenShellIdentitySessions.ToListAsync());
        Assert.Empty(await db.OpenShellRequestNonces.ToListAsync());
        Assert.Empty(await db.Agents.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task DuplicateLease1062_DetachesFailedReservation_BeforeContextReuse()
    {
        await using (var setup = Context())
        {
            await setup.Database.MigrateAsync();
            setup.Agents.Add(new() { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "test" });
            await setup.SaveChangesAsync();
        }
        var collision = new InsertConflictingLease();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(_connection, new MySqlServerVersion(new Version(8, 0, 0))).AddInterceptors(collision).Options);
        var agent = await db.Agents.SingleAsync();
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() => new OpenShellLaunchStore(db)
            .ReserveAsync(agent.Id, null, DateTime.UtcNow, TimeSpan.FromSeconds(30)));
        Assert.Empty(db.ChangeTracker.Entries<OpenShellLaunch>());
        Assert.Empty(db.ChangeTracker.Entries<OpenShellLease>());
        agent.DisplayName = "Updated";
        await db.SaveChangesAsync();
        Assert.Empty(await db.OpenShellLaunches.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OpenShellLeases.AsNoTracking().ToListAsync());
    }

    private sealed class InsertConflictingLease : SaveChangesInterceptor
    {
        private bool _inserted;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            var lease = db.ChangeTracker.Entries<OpenShellLease>().SingleOrDefault(x => x.State == EntityState.Added);
            if (!_inserted && lease is not null)
            {
                _inserted = true;
                // Force a real MySQL 1062 during the actual store SaveChanges, in its owned
                // transaction. This inserted collision must roll back with the failed reserve.
                var owner = Guid.NewGuid();
                var expires = DateTime.UtcNow.AddSeconds(30);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO agent_open_shell_leases (AgentId, Owner, ExpiresAt) VALUES ({lease.Entity.AgentId}, {owner}, {expires})", cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("revoked", false)]
    [InlineData("wrong_epoch", false)]
    [InlineData("missing_nonce", false)]
    public async Task VerifiedLaunchReader_UsesActualMigratedMySqlState(string state, bool expected)
    {
        await using var db = Context(); await db.Database.MigrateAsync();
        var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "test" };
        db.Agents.Add(agent); await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        // Persistence fixture only, not supervisor readiness or actual activation evidence.
        var launch = new OpenShellLaunch { Id = Guid.NewGuid(), AgentId = agent.Id, ActiveAgentId = agent.Id,
            IdentityEpoch = Guid.NewGuid(), GatewayId = "gateway1", SandboxId = "sandbox1", Phase = "activated", CreatedAt = now,
            RevokedAt = state == "revoked" ? now : null };
        db.OpenShellLaunches.Add(launch);
        if (state != "missing_nonce") db.OpenShellRequestNonces.Add(new() { Jti = "nonce1", LaunchId = launch.Id, ExpiresAt = now.AddMinutes(5) });
        await db.SaveChangesAsync();
        var reader = new Fleet.Memory.Services.VerifiedLaunchReader(Options.Create(new Fleet.Memory.Configuration.FleetIdentityOptions { ConnectionString = _connection }));
        var identity = new VerifiedAgentIdentity("agent1", "gateway1", "sandbox1", launch.Id,
            state == "wrong_epoch" ? Guid.NewGuid() : launch.IdentityEpoch, "memory", "/mcp");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, await reader.IsCurrentAsync(identity, "nonce1", deadline.Token));
    }

    private OrchestratorDbContext Context() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseMySql(_connection, new MySqlServerVersion(new Version(8, 0, 0))).Options);

    private static async Task Sql(string connection, string command)
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        await using var sql = new MySqlCommand(command, db);
        await sql.ExecuteNonQueryAsync();
    }
}
