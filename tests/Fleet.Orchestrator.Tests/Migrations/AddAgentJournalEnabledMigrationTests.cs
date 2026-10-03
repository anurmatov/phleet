using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>Verifies AddAgentJournalEnabled against real MySQL 8.0.</summary>
public sealed class AddAgentJournalEnabledMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";
    private const string Before = "20260926173913_AddAgentContextWindow";
    private const string Target = "20260927130818_AddAgentJournalEnabled";
    private string _admin = "";
    private string _database = "";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{ConnectionVariable} is required; migration tests never skip");
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
    public async Task Column_is_non_nullable_bool_defaulting_false_for_existing_and_new_rows()
    {
        await MigrateAsync(Before);
        await InsertAgentAsync("old-agent");

        await MigrateAsync(Target);

        Assert.Equal("tinyint(1)|NO|0", await ScalarAsync<string>("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', COLUMN_DEFAULT)
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'journal_enabled'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM agents WHERE Name = 'old-agent' AND journal_enabled = 0"));

        await InsertAgentAsync("new-agent");
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM agents WHERE Name = 'new-agent' AND journal_enabled = 0"));
    }

    [Fact]
    public async Task Every_migration_applies_and_contains_this_one()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Down_drops_the_column_and_up_restores_it()
    {
        await MigrateAsync(Target);
        await MigrateAsync(Before);
        Assert.Equal(0L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'journal_enabled'
            """));
        await MigrateAsync(Target);
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'journal_enabled'
            """));
    }

    private Task InsertAgentAsync(string name) => ExecAsync(_connectionString, $"""
        INSERT INTO agents (Name, DisplayName, Role, Model, ContainerName, MemoryLimitMb, IsEnabled,
                            GroupDebounceSeconds, PrefixMessages, ProactiveIntervalMinutes, ShowStats, TelegramSendOnly)
        VALUES ('{name}', '{name}', 'dev', 'model-x', 'fleet-{name}', 1024, 1, 15, 0, 0, 1, 0)
        """);

    private OrchestratorDbContext NewContext() => new(
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(_connectionString, new MySqlServerVersion(new Version(8, 0, 0))).Options);

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
