using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

// Runs against CI's real MySQL service; missing database is a failure, never a skip.
public sealed class RemovedProviderColumnMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";

    private const string Before = "20261014131235_AddAgentHarnessExtra";
    private const string Target = "20261014141428_RemoveAgentPiImageInput";

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
    public async Task Removal_UpDropsColumn_DownRestoresFalse_UpReapplies()
    {
        await MigrateAsync(Before);
        await InsertAgentAsync("agent-a");
        var migration = new RemoveAgentPiImageInput { ActiveProvider = "Pomelo.EntityFrameworkCore.MySql" };
        var column = Assert.IsType<DropColumnOperation>(Assert.Single(migration.UpOperations)).Name;
        await ExecAsync(_connectionString, $"UPDATE agents SET `{column}` = 1");

        await MigrateAsync(Target);
        Assert.Equal(0L, await ColumnCountAsync(column));
        await using (var db = NewContext())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            await RemovedProviderGuard.EnsureAsync(db);
        }

        await MigrateAsync(Before);
        Assert.Equal(1L, await ColumnCountAsync(column));
        Assert.Equal("tinyint(1)|NO|0", await ScalarAsync<string>($"""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', COLUMN_DEFAULT) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = '{column}'
            """));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT `{column}` FROM agents WHERE Name = 'agent-a'"));
        await MigrateAsync(Target);
        Assert.Equal(0L, await ColumnCountAsync(column));
    }

    [Fact]
    public async Task Removal_PiRow_GuardNamesAgent_ChangingProviderClearsGuard()
    {
        await MigrateAsync(Target);
        await InsertAgentAsync("agent-a");
        await ExecAsync(_connectionString, "UPDATE agents SET Provider = 'pi', IsEnabled = 0 WHERE Name = 'agent-a'");
        await using var db = NewContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RemovedProviderGuard.EnsureAsync(db));
        Assert.Contains("agent-a", error.Message);
        await ExecAsync(_connectionString, "UPDATE agents SET Provider = 'codex' WHERE Name = 'agent-a'");
        await RemovedProviderGuard.EnsureAsync(db);
    }

    [Fact]
    public async Task Startup_PiRow_RefusesBeforeMigration_PreservesRollbackSchema()
    {
        await MigrateAsync(Before);
        await InsertAgentAsync("agent-a");
        var migration = new RemoveAgentPiImageInput { ActiveProvider = "Pomelo.EntityFrameworkCore.MySql" };
        var column = Assert.IsType<DropColumnOperation>(Assert.Single(migration.UpOperations)).Name;
        await ExecAsync(_connectionString,
            $"UPDATE agents SET Provider = 'pi', IsEnabled = 0, `{column}` = 1 WHERE Name = 'agent-a'");
        await using var db = NewContext();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedStartupAsync(db));

        Assert.Contains("agent-a", error.Message);
        Assert.Contains("reprovision before upgrading", error.Message);
        Assert.Equal(applied, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Contains(Target, await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(1L, await ColumnCountAsync(column));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT `{column}` FROM agents WHERE Name = 'agent-a'"));

        // The previous release can still read its column and move this agent off Pi.
        await ExecAsync(_connectionString, "UPDATE agents SET Provider = 'codex' WHERE Name = 'agent-a'");
        await SeedStartupAsync(db);
        Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0L, await ColumnCountAsync(column));
    }

    [Fact]
    public async Task Startup_FreshDatabase_SkipsLegacyRowQuery_AndMigratesNormally()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());

        await SeedStartupAsync(db);

        Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty(await db.Agents.ToListAsync());
    }

    private static Task SeedStartupAsync(OrchestratorDbContext db)
    {
        var absent = Path.Combine(Path.GetTempPath(), $"no-seed-{Guid.NewGuid():N}");
        return DbSeeder.SeedAsync(db, absent, outputStylesDir: absent);
    }

    private Task<long> ColumnCountAsync(string column) => ScalarAsync<long>($"""
        SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME = 'agents' AND COLUMN_NAME = '{column}'
        """);

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
