using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

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
        await store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", now);
        await store.RetireAsync(launch.Id, now);
        db.ChangeTracker.Clear();
        // Only synthetic persistence cleanup, not a runtime stop/delete acceptance claim.
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
        Assert.Empty(await db.Agents.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
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
