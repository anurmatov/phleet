using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

// Runs against CI's real MySQL service; missing database is a failure, never a skip.
public sealed class AddSecretBindingsMigrationTests : IAsyncLifetime
{
    private const string Before = "20261110112005_RemoveManagedStockRuntime";
    private const string Target = "20261110114347_AddSecretBindings";

    private string _admin = "";
    private string _database = "";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(RemovedProviderColumnMigrationTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{RemovedProviderColumnMigrationTests.ConnectionVariable} is not set, so the migration tests have no MySQL to run against.\n" +
                "  This FAILS rather than skipping on purpose: a skipped migration test reads as a pass.");

        _admin = new MySqlConnectionStringBuilder(configured) { Database = "" }.ConnectionString;
        _database = $"orch_mig_{Guid.NewGuid():N}"[..30];
        await ExecAsync(_admin, $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
        _connectionString = new MySqlConnectionStringBuilder(configured) { Database = _database }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_database.Length > 0)
            await ExecAsync(_admin, $"DROP DATABASE IF EXISTS `{_database}`");
    }

    [Fact]
    public async Task Up_AddsTheTableAndANullColumn_AndLeavesExistingEnvRefsAsTheyWere()
    {
        await MigrateAsync(Before);
        await ExecAsync(_connectionString, """
            INSERT INTO agents (Name, DisplayName, Role, Model, ContainerName, MemoryLimitMb, IsEnabled,
                                GroupDebounceSeconds, PrefixMessages, ProactiveIntervalMinutes, ShowStats, TelegramSendOnly)
            VALUES ('agent-a', 'agent-a', 'dev', 'model-x', 'fleet-agent-a', 1024, 1, 15, 0, 0, 1, 0)
            """);
        await ExecAsync(_connectionString,
            "INSERT INTO agent_env_refs (AgentId, EnvKeyName) SELECT Id, 'EXAMPLE_API_KEY' FROM agents");

        await MigrateAsync(Target);

        await using var db = NewContext();
        var reference = await db.AgentEnvRefs.SingleAsync();
        Assert.Equal("EXAMPLE_API_KEY", reference.EnvKeyName);
        Assert.Null(reference.PlaceholderSha256);
        Assert.Empty(await db.SecretBindings.ToListAsync());

        db.SecretBindings.Add(new SecretBinding { EnvKeyName = "EXAMPLE_API_KEY", Hosts = "api.example.test" });
        await db.SaveChangesAsync();
        var stored = await db.SecretBindings.AsNoTracking().SingleAsync();
        Assert.Equal(("static", "Authorization", false), (stored.Kind, stored.HeaderName, stored.AgentAttachable));
    }

    [Fact]
    public async Task OneBindingPerKey_WhateverTheLetterCase()
    {
        await MigrateAsync(Target);
        await using var db = NewContext();
        db.SecretBindings.Add(new SecretBinding { EnvKeyName = "EXAMPLE_API_KEY", Hosts = "api.example.test" });
        await db.SaveChangesAsync();

        db.SecretBindings.Add(new SecretBinding { EnvKeyName = "example_api_key", Hosts = "other.example.test" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Down_RemovesTheTableAndTheColumn_AndUpReapplies()
    {
        await MigrateAsync(Target);
        await MigrateAsync(Before);

        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'secret_bindings'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agent_env_refs' AND COLUMN_NAME = 'PlaceholderSha256'"));

        await MigrateAsync(Target);
        await using var db = NewContext();
        // Later migrations exist; this one is back in place.
        Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'secret_bindings'"));
    }

    private OrchestratorDbContext NewContext() =>
        new(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(_connectionString, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options);

    private async Task MigrateAsync(string target)
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
