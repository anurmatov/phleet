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
        // SQL from here on: the model has since gained a column this schema does not have yet.
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM secret_bindings"));

        await ExecAsync(_connectionString, InsertBinding("EXAMPLE_API_KEY", "api.example.test"));
        Assert.Equal("static|Authorization|0", await ScalarAsync<string>(
            "SELECT CONCAT(Kind, '|', HeaderName, '|', AgentAttachable) FROM secret_bindings"));
    }

    // Names only the columns without a default, so the defaults are what the migration gave them.
    private static string InsertBinding(string key, string hosts) =>
        $"INSERT INTO secret_bindings (EnvKeyName, Hosts, CreatedAt, UpdatedAt) VALUES ('{key}', '{hosts}', UTC_TIMESTAMP(), UTC_TIMESTAMP())";

    [Fact]
    public async Task OneBindingPerKey_WhateverTheLetterCase()
    {
        await MigrateAsync(Target);
        await ExecAsync(_connectionString, InsertBinding("EXAMPLE_API_KEY", "api.example.test"));

        var duplicate = await Assert.ThrowsAsync<MySqlException>(
            () => ExecAsync(_connectionString, InsertBinding("example_api_key", "other.example.test")));
        Assert.Equal(MySqlErrorCode.DuplicateKeyEntry, duplicate.ErrorCode);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM secret_bindings"));
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
