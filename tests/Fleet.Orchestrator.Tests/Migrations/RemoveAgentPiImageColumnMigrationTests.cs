using System.Reflection;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// <c>RemoveAgentPiImageColumn</c> (#458) against a real MySQL 8.0: Up drops the per-agent image
/// column #455 added, and Down puts it back as a non-null bool with default false.
/// </summary>
/// <remarks>
/// <para>
/// The column and the previous migration are read from the migrations themselves, so the old
/// setting's name appears only in the migration files (#458 acceptance 3).
/// </para>
/// <para>
/// ⚠️ <b>These tests FAIL when the database is absent. They never skip.</b> The main CI job excludes
/// this namespace; the <c>orchestrator-migrations</c> job runs it beside a <c>mysql:8.0</c> service.
/// </para>
/// </remarks>
public sealed class RemoveAgentPiImageColumnMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";

    private static readonly string Target = typeof(RemoveAgentPiImageColumn).GetCustomAttribute<MigrationAttribute>()!.Id;
    private static readonly DropColumnOperation Drop =
        new RemoveAgentPiImageColumn().UpOperations.OfType<DropColumnOperation>().Single();

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
    public void The_migration_drops_exactly_one_agents_column()
    {
        Assert.IsType<DropColumnOperation>(Assert.Single(new RemoveAgentPiImageColumn().UpOperations));
        Assert.Equal("agents", Drop.Table);
        var add = Assert.IsType<AddColumnOperation>(Assert.Single(new RemoveAgentPiImageColumn().DownOperations));
        Assert.Equal((Drop.Name, "agents", typeof(bool), false, (object?)false),
            (add.Name, add.Table, add.ClrType, add.IsNullable, add.DefaultValue));
    }

    [Fact]
    public async Task Up_drops_the_column_and_keeps_the_rows()
    {
        await MigrateAsync(await PreviousAsync());
        await InsertAgentAsync("agent-a");
        await ExecAsync(_connectionString, $"UPDATE agents SET `{Drop.Name}` = 1 WHERE Name = 'agent-a'");

        await MigrateAsync(Target);

        Assert.Equal("", await ColumnAsync());
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'agent-a'"));
    }

    [Fact]
    public async Task Down_adds_the_column_back_non_null_with_default_false()
    {
        await MigrateAsync(Target);
        await InsertAgentAsync("agent-b");

        await MigrateAsync(await PreviousAsync());

        Assert.Equal("tinyint(1)|NO|0", await ColumnAsync());
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT COUNT(*) FROM agents WHERE Name = 'agent-b' AND `{Drop.Name}` = 0"));

        // An insert that does not name the column gets false, as before #455.
        await InsertAgentAsync("agent-c");
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT COUNT(*) FROM agents WHERE Name = 'agent-c' AND `{Drop.Name}` = 0"));

        await MigrateAsync(Target);
        Assert.Equal("", await ColumnAsync());
    }

    [Fact]
    public async Task Every_migration_applies_and_the_model_has_no_such_column()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync());
        Assert.Null(db.Model.FindEntityType(typeof(Agent))!.GetProperties()
            .FirstOrDefault(p => p.GetColumnName() == Drop.Name));

        // The model reads and writes an agent with the column gone.
        await InsertAgentAsync("agent-d");
        var agent = await db.Agents.SingleAsync(a => a.Name == "agent-d");
        agent.MemoryLimitMb = 2048;
        await db.SaveChangesAsync();
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'agent-d' AND MemoryLimitMb = 2048"));
    }

    /// <summary>The migration just before <see cref="Target"/>: the schema that still has the column.</summary>
    private async Task<string> PreviousAsync()
    {
        await using var db = NewContext();
        var all = db.Database.GetMigrations().ToList();
        return all[all.IndexOf(Target) - 1];
    }

    /// <summary>"type|nullable|default" of the column, or "" when it does not exist.</summary>
    private async Task<string> ColumnAsync()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', COLUMN_DEFAULT) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = @column
            """, connection);
        command.Parameters.AddWithValue("@column", Drop.Name);
        return (string?)await command.ExecuteScalarAsync() ?? "";
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
