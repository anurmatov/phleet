using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// <c>AddAgentPiImageInput</c> (#455 A1) against a real MySQL 8.0: existing agent rows read false
/// with no backfill, the column is a non-null bool with default false, and the migration
/// round-trips. The default is also R1: an older image that inserts without the column gets false.
/// </summary>
/// <remarks>
/// ⚠️ <b>These tests FAIL when the database is absent. They never skip.</b> The main CI job excludes
/// this namespace; the <c>orchestrator-migrations</c> job runs it beside a <c>mysql:8.0</c> service.
/// </remarks>
public sealed class AddAgentPiImageInputMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";

    private const string Before = "20261012223148_AddAutonomyCharters";
    private const string Target = "20261014051428_AddAgentPiImageInput";

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
    public async Task Existing_rows_read_false_without_backfill()
    {
        await MigrateAsync(Before);

        // A row written by the pre-#455 schema: no pi_image_input anywhere in the INSERT.
        await InsertAgentAsync("agent-a");

        await MigrateAsync(Target);

        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'agent-a' AND pi_image_input = 0"));
    }

    [Fact]
    public async Task Column_is_non_null_bool_with_default_false()
    {
        await MigrateAsync(Target);

        Assert.Equal("tinyint(1)|NO|0", await ScalarAsync<string>("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', COLUMN_DEFAULT) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'pi_image_input'
            """));

        // An old image inserting without the column still gets false (R1, the rollback path).
        await InsertAgentAsync("agent-b");
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'agent-b' AND pi_image_input = 0"));
    }

    [Fact]
    public async Task Model_reads_false_and_writes_true()
    {
        // The model is the latest schema's, so this migrates all the way rather than to Target.
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        await InsertAgentAsync("agent-c");

        var agent = await db.Agents.SingleAsync(a => a.Name == "agent-c");
        Assert.False(agent.PiImageInput);
        agent.PiImageInput = true;
        await db.SaveChangesAsync();

        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'agent-c' AND pi_image_input = 1"));
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
    public async Task Down_drops_the_column_and_up_applies_again()
    {
        await MigrateAsync(Target);
        await MigrateAsync(Before);

        Assert.Equal(0L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'pi_image_input'
            """));

        await MigrateAsync(Target);
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'pi_image_input'
            """));
    }

    private Task InsertAgentAsync(string name) => ExecAsync(_connectionString, $"""
        INSERT INTO agents (Name, DisplayName, Role, Model, ContainerName, MemoryLimitMb, IsEnabled,
                            GroupDebounceSeconds, PrefixMessages, ProactiveIntervalMinutes, ShowStats, TelegramSendOnly)
        VALUES ('{name}', '{name}', 'dev', 'model-x', 'fleet-{name}', 1024, 1, 15, 0, 0, 1, 0)
        """);

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
