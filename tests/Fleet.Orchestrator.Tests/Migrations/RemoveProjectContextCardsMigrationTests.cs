using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Tests.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// <c>RemoveProjectContextCards</c> and its <see cref="ContextRemovalPreflight"/> (#346) against a real
/// MySQL 8.0: the card schema and reserved rows go, the canonical tables are never written, <c>Down</c>
/// restores the <c>AddProjectContextCards</c> schema empty, and a blocked preflight migrates nothing.
/// </summary>
/// <remarks>
/// ⚠️ <b>These tests FAIL when the database is absent. They never skip</b> — see
/// <see cref="AddProjectContextCardsMigrationTests"/>. The <c>orchestrator-migrations</c> CI job runs
/// them beside a <c>mysql:8.0</c> service and refuses a run that skipped anything.
/// </remarks>
public sealed class RemoveProjectContextCardsMigrationTests : IAsyncLifetime
{
    private const string Cards = ContextRemovalPreflight.CardsMigration;
    private const string Removal = ContextRemovalPreflight.RemovalMigration;

    private string _admin = "";
    private readonly List<string> _databases = [];
    private string _connectionString = "";
    private string _emptyDir = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(AddProjectContextCardsMigrationTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{AddProjectContextCardsMigrationTests.ConnectionVariable} is not set, so the migration tests have no MySQL " +
                "to run against. This FAILS rather than skipping on purpose: a skipped migration test reads as a pass.");

        _admin = new MySqlConnectionStringBuilder(configured) { Database = "" }.ConnectionString;
        _connectionString = await CreateDatabaseAsync(configured);
        _emptyDir = Directory.CreateTempSubdirectory("orch-preflight-").FullName;
    }

    public async Task DisposeAsync()
    {
        foreach (var database in _databases)
            await ExecAsync(_admin, $"DROP DATABASE IF EXISTS `{database}`");
        if (_emptyDir.Length > 0) Directory.Delete(_emptyDir, recursive: true);
    }

    private async Task<string> CreateDatabaseAsync(string configured)
    {
        var database = $"orch_mig_{Guid.NewGuid():N}"[..30];
        await ExecAsync(_admin, $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4");
        _databases.Add(database);
        return new MySqlConnectionStringBuilder(configured) { Database = database }.ConnectionString;
    }

    /// <summary>
    /// The pre-removal state the way production holds it: two card assignments (agent-a → project-a,
    /// agent-b → project-a), a card on project-b with no card assignment, a full assignment, three
    /// routes, the reserved fleet-context endpoint and tool rows, and unrelated tool and endpoint rows.
    /// </summary>
    private async Task SeedCardsAsync(string connectionString)
    {
        await ExecAsync(connectionString, """
            INSERT INTO agents (Name, DisplayName, Role, Model, ContainerName, MemoryLimitMb, IsEnabled,
                                GroupDebounceSeconds, PrefixMessages, ProactiveIntervalMinutes, ShowStats, TelegramSendOnly)
            VALUES ('agent-a', 'Agent A', 'dev', 'model-x', 'fleet-agent-a', 1024, 1, 15, 0, 0, 1, 0),
                   ('agent-b', 'Agent B', 'dev', 'model-x', 'fleet-agent-b', 1024, 1, 15, 0, 0, 1, 0);
            INSERT INTO project_contexts (Name, CurrentVersion, IsActive, CreatedAt, UpdatedAt)
            VALUES ('project-a', 2, 1, '2026-01-01 00:00:00', '2026-01-02 00:00:00'),
                   ('project-b', 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00'),
                   ('project-c', 1, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            INSERT INTO project_context_versions (ProjectContextId, VersionNumber, Content, CreatedAt, CreatedBy, Reason)
            SELECT Id, 1, CONCAT(Name, ' full v1 <!-- keep:rule-one -->'), '2026-01-01 00:00:00', 'seed', NULL FROM project_contexts;
            INSERT INTO project_context_versions (ProjectContextId, VersionNumber, Content, CreatedAt, CreatedBy, Reason)
            SELECT Id, 2, 'Полный v2.', '2026-01-02 00:00:00', 'seed', 'edit' FROM project_contexts WHERE Name = 'project-a';
            INSERT INTO project_context_card_versions (ProjectContextId, VersionNumber, Content, BasedOnFullVersion, CreatedAt)
            SELECT Id, 1, 'Card.', 1, '2026-01-01 00:00:00' FROM project_contexts WHERE Name IN ('project-a', 'project-b');
            UPDATE project_contexts SET CurrentCardVersion = 1 WHERE Name IN ('project-a', 'project-b');
            INSERT INTO project_context_routes (ProjectContextId, SignalKind, SignalValue, CreatedAt)
            SELECT Id, 'repo', 'org/app', UTC_TIMESTAMP() FROM project_contexts WHERE Name = 'project-a'
            UNION ALL SELECT Id, 'workflow', 'ExampleWorkflow', UTC_TIMESTAMP() FROM project_contexts WHERE Name = 'project-a'
            UNION ALL SELECT Id, 'chat', '-100000000001', UTC_TIMESTAMP() FROM project_contexts WHERE Name = 'project-b';
            INSERT INTO agent_projects (AgentId, ProjectName, ContextMode)
            SELECT Id, 'project-a', 'card' FROM agents
            UNION ALL SELECT Id, 'project-c', 'full' FROM agents WHERE Name = 'agent-a';
            INSERT INTO agent_mcp_endpoints (AgentId, McpName, Url, TransportType)
            SELECT Id, 'fleet-context', 'http://fleet-orchestrator:3600/mcp/context?agent=agent-a', 'http' FROM agents WHERE Name = 'agent-a'
            UNION ALL SELECT Id, 'fleet-memory', 'http://fleet-memory:3100/mcp', 'http' FROM agents WHERE Name = 'agent-a';
            INSERT INTO agent_tools (AgentId, ToolName, IsEnabled)
            SELECT Id, 'mcp__fleet-context__get_project_context', 1 FROM agents WHERE Name = 'agent-a'
            UNION ALL SELECT Id, 'mcp__fleet-memory__memory_search', 1 FROM agents WHERE Name = 'agent-a';
            """);
    }

    private const string VersionsRows = """
        SELECT CONCAT_WS('|', Id, ProjectContextId, VersionNumber, HEX(Content), CreatedAt, IFNULL(CreatedBy, '-'), IFNULL(Reason, '-'))
        FROM project_context_versions ORDER BY Id
        """;

    private const string ContextRows = """
        SELECT CONCAT_WS('|', Id, Name, CurrentVersion, IsActive, CreatedAt, UpdatedAt) FROM project_contexts ORDER BY Id
        """;

    // ── the migration ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Up_drops_the_card_schema_and_reserved_rows_and_never_writes_canonical_contexts()
    {
        await MigrateAsync(_connectionString, Cards);
        await SeedCardsAsync(_connectionString);
        var versionsBefore = await RowsAsync(_connectionString, VersionsRows);
        var contextsBefore = await RowsAsync(_connectionString, ContextRows);

        await MigrateAsync(_connectionString, Removal);

        Assert.Equal(0L, await ScalarAsync<long>(_connectionString, """
            SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME IN ('project_context_card_versions', 'project_context_routes')
            """));
        Assert.Equal(0L, await ScalarAsync<long>(_connectionString, """
            SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND ((TABLE_NAME = 'agent_projects' AND COLUMN_NAME = 'ContextMode')
              OR (TABLE_NAME = 'project_contexts' AND COLUMN_NAME = 'CurrentCardVersion'))
            """));

        Assert.Equal(["fleet-memory"], await RowsAsync(_connectionString, "SELECT McpName FROM agent_mcp_endpoints ORDER BY McpName"));
        Assert.Equal(["mcp__fleet-memory__memory_search"], await RowsAsync(_connectionString, "SELECT ToolName FROM agent_tools ORDER BY ToolName"));
        Assert.Equal(3L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM agent_projects"));

        Assert.Equal(versionsBefore, await RowsAsync(_connectionString, VersionsRows));
        Assert.Equal(contextsBefore, await RowsAsync(_connectionString, ContextRows));
    }

    [Fact]
    public async Task Down_recreates_the_AddProjectContextCards_schema_empty()
    {
        await MigrateAsync(_connectionString, Cards);
        await SeedCardsAsync(_connectionString);
        await MigrateAsync(_connectionString, Removal);

        await MigrateAsync(_connectionString, Cards);

        // Byte for byte the schema a database migrated straight to AddProjectContextCards has.
        var reference = await CreateDatabaseAsync(_admin);
        await MigrateAsync(reference, Cards);
        Assert.Equal(await CardSchemaAsync(reference), await CardSchemaAsync(_connectionString));
        Assert.Contains(await CardSchemaAsync(_connectionString),
            line => line.StartsWith("agent_projects.ContextMode|varchar(8)|NO|full|", StringComparison.Ordinal));

        // No data comes back.
        Assert.Equal(0L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM project_context_card_versions"));
        Assert.Equal(0L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM project_context_routes"));
        Assert.Equal(0L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM project_contexts WHERE CurrentCardVersion IS NOT NULL"));
        Assert.Equal(0L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM agent_projects WHERE ContextMode <> 'full'"));
    }

    [Fact]
    public async Task Every_migration_applies_to_an_empty_database_with_no_preflight_lines()
    {
        var logs = new ProvisioningLogSink();
        await using var db = NewContext(_connectionString);

        await ContextRemovalPreflight.MigrateAsync(db, logs.For<RemoveProjectContextCardsMigrationTests>(), 10_000);

        // Deliberately not "this one is last": later migrations (e.g. #357's
        // AddAgentWarmupTimeoutSeconds) append after it, and each new tip owns that assertion in
        // its own migration tests. Here only the apply-without-preflight behaviour is pinned.
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(Removal, await db.Database.GetAppliedMigrationsAsync());
        Assert.DoesNotContain(logs.Entries, e => e.Message.StartsWith("Context removal", StringComparison.Ordinal));
    }

    // ── the preflight ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Preflight_passes_with_an_exact_report_then_applies_the_drop()
    {
        await MigrateAsync(_connectionString, Cards);
        await SeedCardsAsync(_connectionString);
        await ExecAsync(_connectionString, """
            INSERT INTO instructions (Name, CurrentVersion, IsActive) VALUES ('role-x', 1, 1), ('role-y', 1, 1);
            INSERT INTO instruction_versions (InstructionId, VersionNumber, Content, CreatedAt)
            SELECT Id, 1, IF(Name = 'role-x', 'Call mcp__fleet-context__get_project_context.', 'Nothing here.'), UTC_TIMESTAMP() FROM instructions;
            INSERT INTO workflow_definitions (Name, Namespace, TaskQueue, Definition, Version, IsActive, CreatedAt, UpdatedAt)
            VALUES ('ExampleWorkflow', 'fleet', 'fleet', '{"instruction":"use mcp__fleet-context__get_project_context"}', 1, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP()),
                   ('RetiredWorkflow', 'fleet', 'fleet', '{"instruction":"use mcp__fleet-context__get_project_context"}', 1, 0, UTC_TIMESTAMP(), UTC_TIMESTAMP());
            """);
        var logs = new ProvisioningLogSink();

        await using (var db = NewContext(_connectionString))
            await ContextRemovalPreflight.MigrateAsync(db, logs.For<RemoveProjectContextCardsMigrationTests>(), projectContextLimitBytes: 10);

        var lines = logs.Entries.Where(e => e.Message.StartsWith("Context removal preflight:", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [
                (LogLevel.Information, "Context removal preflight: cardAssignments=2 projectsWithCards=2 routes=3 fleetContextEndpointRows=1 fleetContextToolRows=1 result=pass"),
                // project-a's canonical is v2, "Полный v2." = 16 bytes; its card "Card." = 5 bytes.
                (LogLevel.Information, "Context removal preflight: agent=agent-a project=project-a fullVersion=2 fullBytes=16 cardBytes=5 residentDelta=+11 overProjectContextLimit=yes"),
                (LogLevel.Information, "Context removal preflight: agent=agent-b project=project-a fullVersion=2 fullBytes=16 cardBytes=5 residentDelta=+11 overProjectContextLimit=yes"),
                (LogLevel.Warning, "Context removal preflight: still mention mcp__fleet-context__ (remove the reference; the tool is gone): instruction role-x, workflow ExampleWorkflow"),
            ],
            lines);
        Assert.Contains(Removal, await RowsAsync(_connectionString, "SELECT MigrationId FROM __EFMigrationsHistory"));
    }

    [Fact]
    public async Task Preflight_blocks_through_SeedAsync_migrates_nothing_then_applies_after_the_fix()
    {
        await MigrateAsync(_connectionString, Cards);
        await SeedCardsAsync(_connectionString);
        await ExecAsync(_connectionString, """
            UPDATE project_context_versions SET Content = ' '
            WHERE ProjectContextId = (SELECT Id FROM project_contexts WHERE Name = 'project-a') AND VersionNumber = 2;
            INSERT INTO agent_projects (AgentId, ProjectName, ContextMode) SELECT Id, 'project-gone', 'card' FROM agents WHERE Name = 'agent-b';
            """);
        var historyBefore = await RowsAsync(_connectionString, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId");
        var cardsBefore = await RowsAsync(_connectionString,
            "SELECT CONCAT_WS('|', Id, ProjectContextId, VersionNumber, Content) FROM project_context_card_versions ORDER BY Id");
        var logs = new ProvisioningLogSink();

        await using (var db = NewContext(_connectionString))
        {
            await Assert.ThrowsAsync<ContextRemovalBlockedException>(() =>
                DbSeeder.SeedAsync(db, _emptyDir, logger: logs.For<RemoveProjectContextCardsMigrationTests>(), outputStylesDir: _emptyDir));
        }

        Assert.Equal(
            [
                "Context removal blocked: project=project-a agents=agent-a,agent-b reason=empty canonical content",
                "Context removal blocked: project=project-gone agents=agent-b reason=no project context",
                ContextRemovalPreflight.NotAppliedMessage,
            ],
            logs.Entries.Where(e => e.Level == LogLevel.Critical).Select(e => e.Message).ToList());
        Assert.Equal(historyBefore, await RowsAsync(_connectionString, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId"));
        Assert.Equal(cardsBefore, await RowsAsync(_connectionString,
            "SELECT CONCAT_WS('|', Id, ProjectContextId, VersionNumber, Content) FROM project_context_card_versions ORDER BY Id"));
        Assert.Equal(3L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM agent_projects WHERE ContextMode = 'card'"));

        // Fix both through the current build's own paths, then start again.
        await ExecAsync(_connectionString, """
            UPDATE project_context_versions SET Content = 'Fixed.'
            WHERE ProjectContextId = (SELECT Id FROM project_contexts WHERE Name = 'project-a') AND VersionNumber = 2;
            DELETE FROM agent_projects WHERE ProjectName = 'project-gone';
            """);
        await using (var db = NewContext(_connectionString))
            await DbSeeder.SeedAsync(db, _emptyDir, logger: logs.For<RemoveProjectContextCardsMigrationTests>(), outputStylesDir: _emptyDir);

        Assert.Contains(Removal, await RowsAsync(_connectionString, "SELECT MigrationId FROM __EFMigrationsHistory"));
        Assert.Equal(0L, await ScalarAsync<long>(_connectionString, """
            SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'project_context_card_versions'
            """));
    }

    [Fact]
    public async Task Preflight_blocks_on_a_carded_project_without_versions()
    {
        await MigrateAsync(_connectionString, Cards);
        await SeedCardsAsync(_connectionString);
        await ExecAsync(_connectionString,
            "DELETE FROM project_context_versions WHERE ProjectContextId = (SELECT Id FROM project_contexts WHERE Name = 'project-b')");
        var logs = new ProvisioningLogSink();

        await using var db = NewContext(_connectionString);
        await Assert.ThrowsAsync<ContextRemovalBlockedException>(() =>
            ContextRemovalPreflight.MigrateAsync(db, logs.For<RemoveProjectContextCardsMigrationTests>(), 10_000));

        // project-b has a card and no card assignment: it still blocks, naming no agent.
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical &&
            e.Message == "Context removal blocked: project=project-b agents=none reason=no versions");
        Assert.DoesNotContain(Removal, await RowsAsync(_connectionString, "SELECT MigrationId FROM __EFMigrationsHistory"));
    }

    [Fact]
    public async Task Preflight_sql_error_propagates_and_the_drop_is_not_applied()
    {
        await MigrateAsync(_connectionString, Cards);
        await SeedCardsAsync(_connectionString);
        // A half-dropped schema, as a part-way DDL failure would leave it.
        await ExecAsync(_connectionString, "DROP TABLE project_context_routes");

        await using (var db = NewContext(_connectionString))
            await Assert.ThrowsAsync<MySqlException>(() => ContextRemovalPreflight.MigrateAsync(db, null, 10_000));

        Assert.DoesNotContain(Removal, await RowsAsync(_connectionString, "SELECT MigrationId FROM __EFMigrationsHistory"));
        Assert.Equal(2L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM project_context_card_versions"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Columns, indexes and FKs of the four card schema objects, one sorted line each.</summary>
    private static async Task<List<string>> CardSchemaAsync(string connectionString)
    {
        var columns = await RowsAsync(connectionString, """
            SELECT CONCAT_WS('|', CONCAT(TABLE_NAME, '.', COLUMN_NAME), COLUMN_TYPE, IS_NULLABLE, IFNULL(COLUMN_DEFAULT, '-'), EXTRA, IFNULL(CHARACTER_SET_NAME, '-'))
            FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND (TABLE_NAME IN ('project_context_card_versions', 'project_context_routes')
              OR (TABLE_NAME = 'agent_projects' AND COLUMN_NAME = 'ContextMode')
              OR (TABLE_NAME = 'project_contexts' AND COLUMN_NAME = 'CurrentCardVersion'))
            """);
        var indexes = await RowsAsync(connectionString, """
            SELECT CONCAT_WS('|', TABLE_NAME, INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME)
            FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME IN ('project_context_card_versions', 'project_context_routes')
            """);
        var fks = await RowsAsync(connectionString, """
            SELECT CONCAT_WS('|', r.TABLE_NAME, r.CONSTRAINT_NAME, r.REFERENCED_TABLE_NAME, r.DELETE_RULE, k.COLUMN_NAME, k.REFERENCED_COLUMN_NAME)
            FROM information_schema.REFERENTIAL_CONSTRAINTS r
            JOIN information_schema.KEY_COLUMN_USAGE k
              ON k.CONSTRAINT_SCHEMA = r.CONSTRAINT_SCHEMA AND k.CONSTRAINT_NAME = r.CONSTRAINT_NAME AND k.TABLE_NAME = r.TABLE_NAME
            WHERE r.CONSTRAINT_SCHEMA = DATABASE() AND r.TABLE_NAME IN ('project_context_card_versions', 'project_context_routes')
            """);
        return [.. columns.Concat(indexes).Concat(fks).Order(StringComparer.Ordinal)];
    }

    private static OrchestratorDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options);

    private static async Task MigrateAsync(string connectionString, string target)
    {
        await using var db = NewContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<List<string>> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        return rows;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
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
