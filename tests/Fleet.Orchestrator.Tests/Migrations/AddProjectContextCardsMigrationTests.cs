using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// <c>AddProjectContextCards</c> (#347) against a real MySQL 8.0: existing assignments become
/// <c>full</c>, the cascade FKs hold, and the migration round-trips.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>These tests FAIL when the database is absent. They never skip.</b> SQLite and InMemory
/// do not run Pomelo migrations, so nothing else in this suite can prove what the migration does
/// to a populated <c>agent_projects</c>. A skipped run would look like a passing one in a summary.
/// </para>
/// <para>
/// The main CI job excludes this namespace; the <c>orchestrator-migrations</c> job runs it beside a
/// <c>mysql:8.0</c> service and refuses a run that skipped anything.
/// </para>
/// </remarks>
public sealed class AddProjectContextCardsMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";

    private const string Before = "20260924105327_AddAgentAnthropicBaseUrl";
    private const string Target = "20260924165651_AddProjectContextCards";

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
    public async Task Existing_assignments_become_full_and_new_tables_cascade()
    {
        await MigrateAsync(Before);

        // Rows written by the pre-#347 schema, the way production holds them today.
        await ExecAsync(_connectionString, """
            INSERT INTO agents (Name, DisplayName, Role, Model, ContainerName, MemoryLimitMb, IsEnabled,
                                GroupDebounceSeconds, PrefixMessages, ProactiveIntervalMinutes, ShowStats, TelegramSendOnly)
            VALUES ('agent-a', 'Agent A', 'dev', 'model-x', 'fleet-agent-a', 1024, 1, 15, 0, 0, 1, 0),
                   ('agent-b', 'Agent B', 'dev', 'model-x', 'fleet-agent-b', 1024, 1, 15, 0, 0, 1, 0);
            INSERT INTO agent_projects (AgentId, ProjectName)
            SELECT Id, 'project-a' FROM agents UNION ALL SELECT Id, 'project-b' FROM agents WHERE Name = 'agent-a';
            INSERT INTO project_contexts (Name, CurrentVersion, IsActive, CreatedAt, UpdatedAt)
            VALUES ('project-a', 1, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP());
            INSERT INTO project_context_versions (ProjectContextId, VersionNumber, Content, CreatedAt)
            SELECT Id, 1, 'Full context.', UTC_TIMESTAMP() FROM project_contexts;
            """);

        await MigrateAsync(Target);

        // AC 2: every pre-existing assignment is full.
        Assert.Equal(3L, await ScalarAsync<long>("SELECT COUNT(*) FROM agent_projects"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM agent_projects WHERE ContextMode <> 'full'"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM project_contexts WHERE CurrentCardVersion IS NOT NULL"));

        // Column shape: an old image that inserts without the column still gets 'full' (rollback path).
        Assert.Equal("varchar(8)|NO|full", await ScalarAsync<string>("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', COLUMN_DEFAULT) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agent_projects' AND COLUMN_NAME = 'ContextMode'
            """));
        await ExecAsync(_connectionString,
            "INSERT INTO agent_projects (AgentId, ProjectName) SELECT Id, 'project-c' FROM agents WHERE Name = 'agent-b'");
        Assert.Equal("full", await ScalarAsync<string>("SELECT ContextMode FROM agent_projects WHERE ProjectName = 'project-c'"));

        // D10: card versions and routes are keyed on the context's Id and die with it.
        await using (var db = NewContext())
        {
            var ctx = await db.ProjectContexts.SingleAsync(p => p.Name == "project-a");
            db.ProjectContextCardVersions.Add(new ProjectContextCardVersion
            {
                ProjectContextId = ctx.Id, VersionNumber = 1, Content = "Card.", BasedOnFullVersion = 1,
            });
            db.ProjectContextRoutes.Add(new ProjectContextRoute
            {
                ProjectContextId = ctx.Id, SignalKind = RouteSignalKind.Repo, SignalValue = "org/app",
            });
            ctx.CurrentCardVersion = 1;
            await db.SaveChangesAsync();
        }

        // The route uniqueness is enforced by the database, not only by the API.
        await Assert.ThrowsAsync<MySqlException>(() => ExecAsync(_connectionString, """
            INSERT INTO project_context_routes (ProjectContextId, SignalKind, SignalValue, CreatedAt)
            SELECT Id, 'repo', 'org/app', UTC_TIMESTAMP() FROM project_contexts
            """));

        // Raw SQL, so the database's FK does the cascading — not EF's change tracker.
        await ExecAsync(_connectionString, "DELETE FROM project_contexts WHERE Name = 'project-a'");
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM project_context_card_versions"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM project_context_routes"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM project_context_versions"));
    }

    [Fact]
    public async Task Down_removes_the_schema_and_up_applies_again()
    {
        await MigrateAsync(Target);
        await MigrateAsync(Before);

        Assert.Equal(0L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME IN ('project_context_card_versions', 'project_context_routes')
            """));
        Assert.Equal(0L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND ((TABLE_NAME = 'agent_projects' AND COLUMN_NAME = 'ContextMode')
              OR (TABLE_NAME = 'project_contexts' AND COLUMN_NAME = 'CurrentCardVersion'))
            """));

        await MigrateAsync(Target);
        Assert.Equal(2L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME IN ('project_context_card_versions', 'project_context_routes')
            """));
    }

    [Fact]
    public async Task Every_migration_applies_to_an_empty_database_and_this_one_is_last()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(Target, (await db.Database.GetAppliedMigrationsAsync()).Last());
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
