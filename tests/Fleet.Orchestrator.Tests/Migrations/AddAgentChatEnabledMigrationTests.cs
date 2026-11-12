using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>Verifies AddAgentChatEnabled against real MySQL 8.0.</summary>
public sealed class AddAgentChatEnabledMigrationTests : IAsyncLifetime
{
    private const string ConnectionVariable = AddAgentJournalEnabledMigrationTests.ConnectionVariable;
    private const string Before = "20261112134638_AddSignIn";
    private const string Target = "20261112141107_AddAgentChatEnabled";
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

    /// <summary>Chat is on by default (#530): for agents that exist at upgrade and for new rows.</summary>
    [Fact]
    public async Task Column_is_non_nullable_bool_defaulting_true_for_existing_and_new_rows()
    {
        await MigrateAsync(Before);
        await InsertAgentAsync("old-agent");

        await MigrateAsync(Target);

        Assert.Equal("tinyint(1)|NO|1", await ScalarAsync<string>("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', COLUMN_DEFAULT)
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'chat_enabled'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM agents WHERE Name = 'old-agent' AND chat_enabled = 1"));

        await InsertAgentAsync("new-agent");
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM agents WHERE Name = 'new-agent' AND chat_enabled = 1"));
    }

    /// <summary>
    /// Through EF, an agent created with chat switched OFF is stored off, and one created without
    /// saying is stored on. Guards against a default-true column turning an explicit false back on.
    /// </summary>
    [Fact]
    public async Task An_agent_saved_with_chat_off_stays_off_and_one_saved_by_default_is_on()
    {
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
            db.Agents.Add(NewAgent("default-agent"));
            db.Agents.Add(NewAgent("off-agent", chatEnabled: false));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT chat_enabled FROM agents WHERE Name = 'default-agent'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT chat_enabled FROM agents WHERE Name = 'off-agent'"));

        await using var read = NewContext();
        Assert.False((await read.Agents.AsNoTracking().SingleAsync(a => a.Name == "off-agent")).ChatEnabled);
        Assert.True((await read.Agents.AsNoTracking().SingleAsync(a => a.Name == "default-agent")).ChatEnabled);
    }

    private static Agent NewAgent(string name, bool? chatEnabled = null)
    {
        var agent = new Agent
        {
            Name = name, DisplayName = name, Role = "dev", Model = "model-x",
            ContainerName = $"fleet-{name}",
        };
        if (chatEnabled is { } value) agent.ChatEnabled = value;
        return agent;
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
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'chat_enabled'
            """));
        await MigrateAsync(Target);
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'agents' AND COLUMN_NAME = 'chat_enabled'
            """));
    }

    [Fact]
    public async Task Push_binding_invalidates_unowned_registrations_and_preserves_agents()
    {
        await MigrateAsync(Target);
        await InsertAgentAsync("preserved-agent");
        await ExecAsync(_connectionString, """
            INSERT INTO push_devices (DeviceId, Token, CreatedAt, UpdatedAt)
            VALUES ('synthetic-install', 'synthetic-token', UTC_TIMESTAMP(), UTC_TIMESTAMP())
            """);
        await MigrateAsync("20261112211417_BindPushSignInDevice");
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM push_devices"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'preserved-agent'"));
        Assert.Equal("varchar(32)|YES", await ScalarAsync<string>("""
            SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE)
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'push_devices' AND COLUMN_NAME = 'AuthDeviceId'
            """));
        await MigrateAsync(Target);
        await MigrateAsync("20261112211417_BindPushSignInDevice");
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM agents WHERE Name = 'preserved-agent'"));
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
