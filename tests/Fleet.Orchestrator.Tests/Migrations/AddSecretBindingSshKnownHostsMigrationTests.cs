using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

// Runs against CI's real MySQL service; missing database is a failure, never a skip.
public sealed class AddSecretBindingSshKnownHostsMigrationTests : IAsyncLifetime
{
    private const string Before = "20261110114347_AddSecretBindings";
    private const string Target = "20261111064715_AddSecretBindingSshKnownHosts";

    private string _admin = "";
    private string _database = "";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(RemovedProviderColumnMigrationTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{RemovedProviderColumnMigrationTests.ConnectionVariable} is not set, so the migration tests have no MySQL to run against.\n" +
                "  This FAILS rather than skipping on purpose: a skipped migration test reads as a pass.");

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
    public async Task Up_AddsANullColumn_AndLeavesExistingBindingsAsTheyWere()
    {
        await MigrateAsync(Before);
        await ExecAsync(_connectionString, """
            INSERT INTO secret_bindings (EnvKeyName, Kind, Hosts, HeaderName, AgentAttachable, CreatedAt, UpdatedAt)
            VALUES ('EXAMPLE_API_KEY', 'static', 'api.example.test', 'Authorization', 0, UTC_TIMESTAMP(), UTC_TIMESTAMP())
            """);

        await MigrateAsync(Target);

        await using var db = NewContext();
        var existing = await db.SecretBindings.SingleAsync();
        Assert.Equal(("EXAMPLE_API_KEY", "static", "api.example.test"), (existing.EnvKeyName, existing.Kind, existing.Hosts));
        Assert.Null(existing.SshKnownHosts);
        Assert.Null(existing.KnownHostList);

        // Longer than any varchar the table has: several RSA host keys.
        var lines = string.Join('\n', Enumerable.Range(0, 8).Select(i => $"build.example.test ssh-rsa {new string('A', 700)}{i}"));
        db.SecretBindings.Add(new SecretBinding
        {
            EnvKeyName = "SSH_KEY_EXAMPLE", Kind = SecretBindingRules.KindSshKey,
            Hosts = "deploy@build.example.test", SshKnownHosts = lines,
        });
        await db.SaveChangesAsync();

        await using var read = NewContext();
        var ssh = await read.SecretBindings.SingleAsync(b => b.EnvKeyName == "SSH_KEY_EXAMPLE");
        Assert.Equal(lines, ssh.SshKnownHosts);
        Assert.Equal(8, ssh.KnownHostList!.Count);
    }

    [Fact]
    public async Task Down_DropsTheColumn_AndKeepsTheBindings()
    {
        await MigrateAsync(Target);
        await ExecAsync(_connectionString, """
            INSERT INTO secret_bindings (EnvKeyName, Kind, Hosts, HeaderName, AgentAttachable, CreatedAt, UpdatedAt, SshKnownHosts)
            VALUES ('SSH_KEY_EXAMPLE', 'ssh_key', 'deploy@build.example.test', 'Authorization', 0, UTC_TIMESTAMP(), UTC_TIMESTAMP(),
                    'build.example.test ssh-ed25519 synthetic')
            """);

        await MigrateAsync(Before);

        Assert.Equal(0L, await ScalarAsync(
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'secret_bindings' AND COLUMN_NAME = 'SshKnownHosts'"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM secret_bindings WHERE EnvKeyName = 'SSH_KEY_EXAMPLE'"));
    }

    private OrchestratorDbContext NewContext() => new(
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(_connectionString, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options);

    private async Task MigrateAsync(string target)
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
