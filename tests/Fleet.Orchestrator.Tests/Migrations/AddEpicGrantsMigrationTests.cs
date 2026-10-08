using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// AC-M1 (#436): AddEpicGrants against real MySQL 8.0. Up creates exactly the two tables and the
/// per-visit unique index; down removes only them; every pre-existing table is untouched.
/// </summary>
public sealed class AddEpicGrantsMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";
    private const string Before = "20261003103013_AddAgentJournalCrossChatEnabled";
    private const string Target = "20261008135432_AddEpicGrants";
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
    public async Task Up_creates_both_tables_the_unique_visit_index_and_the_grant_index()
    {
        await MigrateAsync(Before);
        var existing = await SchemaAsync();

        await MigrateAsync(Target);

        Assert.Equal(1L, await ScalarAsync<long>(TableCount("epic_grants")));
        Assert.Equal(1L, await ScalarAsync<long>(TableCount("epic_grant_decisions")));

        // The at-most-once guard, read from the server's own index catalogue.
        Assert.Equal("0|Namespace,WorkflowId,RunId,Gate,VisitId", await ScalarAsync<string>("""
            SELECT CONCAT(MIN(NON_UNIQUE), '|', GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX))
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'epic_grant_decisions'
              AND INDEX_NAME = 'IX_epic_grant_decisions_Namespace_WorkflowId_RunId_Gate_VisitId'
            """));
        Assert.Equal("1|GrantId", await ScalarAsync<string>("""
            SELECT CONCAT(MIN(NON_UNIQUE), '|', GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX))
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'epic_grant_decisions'
              AND INDEX_NAME = 'IX_epic_grant_decisions_GrantId'
            """));
        Assert.Equal("epic_grants|RESTRICT", await ScalarAsync<string>("""
            SELECT CONCAT(REFERENCED_TABLE_NAME, '|', DELETE_RULE)
            FROM information_schema.REFERENTIAL_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'epic_grant_decisions'
            """));

        Assert.Equal(
            "Id char(36) NO|Status varchar(16) NO|ScopeJson longtext NO|ScopeSha256 char(64) NO|"
            + "DriverNamespace varchar(64) NO|DriverWorkflowId varchar(255) NO|DriverRunId varchar(36) NO|"
            + "CtoAgent varchar(128) NO|CreatedAt datetime(6) NO|ExpiresAt datetime(6) NO|"
            + "RevokedAt datetime(6) YES|RevokeReason varchar(500) YES",
            await ColumnsAsync("epic_grants"));
        Assert.Equal(
            "Id bigint NO|GrantId char(36) NO|Namespace varchar(64) NO|WorkflowId varchar(255) NO|"
            + "RunId varchar(36) NO|Gate varchar(32) NO|VisitId varchar(64) NO|ArtifactRef varchar(64) NO|"
            + "Evidence varchar(500) NO|Caller varchar(128) NO|Status varchar(16) NO|"
            + "CreatedAt datetime(6) NO|UpdatedAt datetime(6) NO",
            await ColumnsAsync("epic_grant_decisions"));
        Assert.Equal("auto_increment", await ScalarAsync<string>("""
            SELECT EXTRA FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'epic_grant_decisions' AND COLUMN_NAME = 'Id'
            """));

        Assert.Equal(existing, await SchemaAsync(exclude: ["epic_grants", "epic_grant_decisions"]));
    }

    [Fact]
    public async Task The_unique_index_rejects_a_second_row_for_the_same_visit()
    {
        await MigrateAsync(Target);
        await ExecAsync(_connectionString, """
            INSERT INTO epic_grants (Id, Status, ScopeJson, ScopeSha256, DriverNamespace, DriverWorkflowId, DriverRunId,
                                     CtoAgent, CreatedAt, ExpiresAt)
            VALUES ('00000000-0000-4000-8000-000000000001', 'active', '{}', REPEAT('a', 64), 'fleet', 'driver', 'run',
                    'agent1', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            """);
        const string insert = """
            INSERT INTO epic_grant_decisions (GrantId, Namespace, WorkflowId, RunId, Gate, VisitId, ArtifactRef, Evidence,
                                              Caller, Status, CreatedAt, UpdatedAt)
            VALUES ('00000000-0000-4000-8000-000000000001', 'fleet', 'wf', 'run', 'merge-approval', 'merge-approval:1',
                    'ref', 'https://example.com/r', 'agent1', 'reserved', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            """;
        await ExecAsync(_connectionString, insert);

        var duplicate = await Assert.ThrowsAsync<MySqlException>(() => ExecAsync(_connectionString, insert));
        Assert.Equal(1062, duplicate.Number);

        // The grant cannot be deleted out from under its decisions.
        var restricted = await Assert.ThrowsAsync<MySqlException>(() => ExecAsync(_connectionString, "DELETE FROM epic_grants"));
        Assert.Equal(1451, restricted.Number);
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
    public async Task Down_drops_only_the_two_tables_and_up_restores_them()
    {
        await MigrateAsync(Before);
        var existing = await SchemaAsync();
        await MigrateAsync(Target);

        await MigrateAsync(Before);

        Assert.Equal(0L, await ScalarAsync<long>(TableCount("epic_grants")));
        Assert.Equal(0L, await ScalarAsync<long>(TableCount("epic_grant_decisions")));
        Assert.Equal(existing, await SchemaAsync());

        await MigrateAsync(Target);
        Assert.Equal(1L, await ScalarAsync<long>(TableCount("epic_grants")));
        Assert.Equal(1L, await ScalarAsync<long>(TableCount("epic_grant_decisions")));
    }

    private static string TableCount(string table) => $"""
        SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'
        """;

    private Task<string> ColumnsAsync(string table) => ScalarAsync<string>($"""
        SELECT GROUP_CONCAT(CONCAT(COLUMN_NAME, ' ', COLUMN_TYPE, ' ', IS_NULLABLE) ORDER BY ORDINAL_POSITION SEPARATOR '|')
        FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'
        """);

    /// <summary>Every table's columns and indexes, as one comparable string (migration history excluded).</summary>
    private async Task<string> SchemaAsync(string[]? exclude = null)
    {
        var skip = string.Join(",", new[] { "__EFMigrationsHistory" }.Concat(exclude ?? []).Select(t => $"'{t}'"));
        var columns = await ScalarAsync<string>($"""
            SELECT GROUP_CONCAT(CONCAT_WS(' ', TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, IFNULL(COLUMN_DEFAULT, '~'), EXTRA)
                                ORDER BY TABLE_NAME, ORDINAL_POSITION SEPARATOR '\n')
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME NOT IN ({skip})
            """);
        var indexes = await ScalarAsync<string>($"""
            SELECT GROUP_CONCAT(CONCAT_WS(' ', TABLE_NAME, INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME)
                                ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX SEPARATOR '\n')
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME NOT IN ({skip})
            """);
        return columns + "\n--\n" + indexes;
    }

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
        await using (var raise = new MySqlCommand("SET SESSION group_concat_max_len = 1048576", connection))
            await raise.ExecuteNonQueryAsync();
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
