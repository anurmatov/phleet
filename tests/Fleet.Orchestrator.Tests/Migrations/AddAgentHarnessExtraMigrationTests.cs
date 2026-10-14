using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// <c>AddAgentHarnessExtra</c> (#460 Part 3b) against a real MySQL 8.0: existing agent rows read null
/// with no backfill, the column is a nullable <c>text</c>, a 4,096-character multi-line value
/// round-trips byte for byte through the model, and the migration round-trips. Rollback leaves the
/// column unused: an older image that inserts without it gets null.
/// </summary>
/// <remarks>
/// ⚠️ <b>These tests FAIL when the database is absent. They never skip.</b> The main CI job excludes
/// this namespace; the <c>orchestrator-migrations</c> job runs it beside a <c>mysql:8.0</c> service.
/// </remarks>
public sealed class AddAgentHarnessExtraMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";

    private const string Before = "20261014051428_AddAgentPiImageInput";
    private const string Target = "20261014131235_AddAgentHarnessExtra";

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
    public async Task Existing_rows_read_null_without_backfill()
    {
        await MigrateAsync(Before);

        // A row written by the pre-#460 schema: no harness_extra anywhere in the INSERT.
        await InsertAgentAsync("agent-a");

        await MigrateAsync(Target);

        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'agent-a' AND harness_extra IS NULL"));
    }

    [Fact]
    public async Task Column_is_nullable_text()
    {
        await MigrateAsync(Target);

        Assert.Equal("text|YES", await ScalarAsync<string>("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'harness_extra'
            """));

        // An old image inserting without the column still gets null (the rollback path).
        await InsertAgentAsync("agent-b");
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'agent-b' AND harness_extra IS NULL"));
    }

    [Fact]
    public async Task Model_round_trips_the_largest_value_byte_for_byte()
    {
        // The model is the latest schema's, so this migrates all the way rather than to Target.
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        await InsertAgentAsync("agent-c");

        // 4,096 characters, multi-line, with a non-ASCII comment: the stored text is returned verbatim.
        var line = "# ünïcode comment\r\nMAX_MCP_OUTPUT_TOKENS=12000\n";
        var value = string.Concat(Enumerable.Repeat(line, 100))[..Fleet.Shared.HarnessExtra.MaxChars];
        var agent = await db.Agents.SingleAsync(a => a.Name == "agent-c");
        Assert.Null(agent.HarnessExtra);
        agent.HarnessExtra = value;
        await db.SaveChangesAsync();

        await using var fresh = NewContext();
        Assert.Equal(value, (await fresh.Agents.AsNoTracking().SingleAsync(a => a.Name == "agent-c")).HarnessExtra);
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
            AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'harness_extra'
            """));

        await MigrateAsync(Target);
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'harness_extra'
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
