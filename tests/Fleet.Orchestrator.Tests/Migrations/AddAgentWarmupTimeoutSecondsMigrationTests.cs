using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// <c>AddAgentWarmupTimeoutSeconds</c> (#357) against a real MySQL 8.0: existing agent rows resolve
/// to 60 with no backfill, the column is non-null with default 60, and the migration round-trips.
/// </summary>
/// <remarks>
/// ⚠️ <b>These tests FAIL when the database is absent. They never skip.</b> The main CI job excludes
/// this namespace; the <c>orchestrator-migrations</c> job runs it beside a <c>mysql:8.0</c> service.
/// </remarks>
public sealed class AddAgentWarmupTimeoutSecondsMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";

    private const string Before = "20260925105427_RemoveProjectContextCards";
    private const string Target = "20260925150402_AddAgentWarmupTimeoutSeconds";

    private string _admin = "";
    private string _database = "";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{ConnectionVariable} is not set, so the migration tests have no MySQL to run against.\n" +
                "  This FAILS rather than skipping on purpose: a skipped migration test reads as a pass.\n" +
                "  CI supplies a mysql:8.0 service. Locally, point it at any MySQL 8.0 account with DDL rights:\n" +
                $"    export {ConnectionVariable}='Server=127.0.0.1;Port=3306;User ID=root;Password=…;'");

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
    public async Task Existing_rows_resolve_to_sixty_without_backfill()
    {
        await MigrateAsync(Before);

        // A row written by the pre-#357 schema: no WarmupTimeoutSeconds anywhere in the INSERT.
        await ExecAsync(_connectionString, """
            INSERT INTO agents (Name, DisplayName, Role, Model, ContainerName, MemoryLimitMb, IsEnabled,
                                GroupDebounceSeconds, PrefixMessages, ProactiveIntervalMinutes, ShowStats, TelegramSendOnly)
            VALUES ('agent-a', 'Agent A', 'dev', 'model-x', 'fleet-agent-a', 1024, 1, 15, 0, 0, 1, 0);
            """);

        await MigrateAsync(Target);

        Assert.Equal(60L, await ScalarAsync<long>("SELECT WarmupTimeoutSeconds FROM agents WHERE Name = 'agent-a'"));
    }

    [Fact]
    public async Task Column_is_non_null_with_default_sixty()
    {
        await MigrateAsync(Target);

        Assert.Equal("int|NO|60", await ScalarAsync<string>("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', COLUMN_DEFAULT) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'WarmupTimeoutSeconds'
            """));

        // An old image inserting without the column still gets 60 (rollback path).
        await ExecAsync(_connectionString, """
            INSERT INTO agents (Name, DisplayName, Role, Model, ContainerName, MemoryLimitMb, IsEnabled,
                                GroupDebounceSeconds, PrefixMessages, ProactiveIntervalMinutes, ShowStats, TelegramSendOnly)
            VALUES ('agent-b', 'Agent B', 'dev', 'model-x', 'fleet-agent-b', 1024, 1, 15, 0, 0, 1, 0);
            """);
        Assert.Equal(60L, await ScalarAsync<long>("SELECT WarmupTimeoutSeconds FROM agents WHERE Name = 'agent-b'"));
    }

    [Fact]
    public async Task Every_migration_applies_and_this_one_is_last()
    {
        // Moved from RemoveProjectContextCardsMigrationTests (#357 review): each migration tip
        // owns the "I am last" assertion, so appending a migration never breaks its predecessor.
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(Target, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Fact]
    public async Task Down_drops_the_column_and_up_applies_again()
    {
        await MigrateAsync(Target);
        await MigrateAsync(Before);

        Assert.Equal(0L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'WarmupTimeoutSeconds'
            """));

        await MigrateAsync(Target);
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'WarmupTimeoutSeconds'
            """));
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
