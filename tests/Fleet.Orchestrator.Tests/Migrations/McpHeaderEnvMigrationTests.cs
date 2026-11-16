using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// <c>AddMcpHeaderEnvRefs</c> (#593 A1) against a real MySQL 8.0: an existing endpoint reads null (no
/// mappings) with no backfill, the column is a nullable <c>text</c>, two synthetic mappings round-trip
/// through the model as names only, and the migration round-trips.
/// </summary>
/// <remarks>
/// ⚠️ <b>These tests FAIL when the database is absent. They never skip.</b> The main CI job excludes
/// this namespace; the <c>orchestrator-migrations</c> job runs it beside a <c>mysql:8.0</c> service.
/// </remarks>
public sealed class McpHeaderEnvMigrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION";

    private const string Before = "20261112211417_BindPushSignInDevice";
    private const string Target = "20261116133031_AddMcpHeaderEnvRefs";

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
    public async Task Existing_endpoints_read_null_without_backfill()
    {
        await MigrateAsync(Before);

        // Rows written by the pre-#593 schema: no HeaderEnvRefsJson anywhere in the INSERT.
        await InsertAgentAsync("agent-a");
        await InsertEndpointAsync("agent-a");

        await MigrateAsync(Target);

        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agent_mcp_endpoints WHERE McpName = 'example' AND HeaderEnvRefsJson IS NULL"));
        await using var db = NewContext();
        var endpoint = await db.AgentMcpEndpoints.AsNoTracking().SingleAsync();
        Assert.True(McpHeaderMappings.TryRead(endpoint, out var mappings, out _));
        Assert.Empty(mappings);
    }

    [Fact]
    public async Task Column_is_nullable_text()
    {
        await MigrateAsync(Target);

        Assert.Equal("text|YES", await ScalarAsync<string>("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agent_mcp_endpoints' AND COLUMN_NAME = 'HeaderEnvRefsJson'
            """));

        // An older image inserting without the column still gets null (the rollback path).
        await InsertAgentAsync("agent-b");
        await InsertEndpointAsync("agent-b");
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agent_mcp_endpoints WHERE HeaderEnvRefsJson IS NULL"));
    }

    [Fact]
    public async Task Model_round_trips_two_mappings_as_names_only()
    {
        // The model is the latest schema's, so this migrates all the way rather than to Target.
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
            await InsertAgentAsync("agent-c");
            var agent = await db.Agents.SingleAsync(a => a.Name == "agent-c");
            db.AgentMcpEndpoints.Add(new AgentMcpEndpoint
            {
                AgentId = agent.Id, McpName = "example", Url = "https://mcp.example.test/mcp", TransportType = "http",
                HeaderEnvRefsJson = McpHeaderMappings.Serialize(
                    [new McpHeaderEnvRef("X-Example-Key", "EXAMPLE_KEY"), new McpHeaderEnvRef("X-Example-Secret", "EXAMPLE_SECRET")]),
            });
            await db.SaveChangesAsync();
        }

        await using var fresh = NewContext();
        var endpoint = await fresh.AgentMcpEndpoints.AsNoTracking().SingleAsync();
        Assert.True(McpHeaderMappings.TryRead(endpoint, out var mappings, out _));
        Assert.Equal([new McpHeaderEnvRef("X-Example-Key", "EXAMPLE_KEY"), new McpHeaderEnvRef("X-Example-Secret", "EXAMPLE_SECRET")], mappings);
        Assert.Equal(
            """[{"headerName":"X-Example-Key","envRefName":"EXAMPLE_KEY"},{"headerName":"X-Example-Secret","envRefName":"EXAMPLE_SECRET"}]""",
            await ScalarAsync<string>("SELECT HeaderEnvRefsJson FROM agent_mcp_endpoints"));
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
            AND TABLE_NAME = 'agent_mcp_endpoints' AND COLUMN_NAME = 'HeaderEnvRefsJson'
            """));

        await MigrateAsync(Target);
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'agent_mcp_endpoints' AND COLUMN_NAME = 'HeaderEnvRefsJson'
            """));
    }

    private Task InsertEndpointAsync(string agent) => ExecAsync(_connectionString, $"""
        INSERT INTO agent_mcp_endpoints (AgentId, McpName, Url, TransportType)
        SELECT Id, 'example', 'https://mcp.example.test/mcp', 'http' FROM agents WHERE Name = '{agent}'
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
