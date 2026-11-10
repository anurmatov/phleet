using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>The fixed reason codes of the removal preflight. Needs no database.</summary>
public sealed class ManagedRuntimeRemovalPreflightTests
{
    [Theory]
    [InlineData("docker", "docker", null)]
    [InlineData("docker", "docker", "")]
    [InlineData("docker", "docker", """{"v":1,"state":"none","errors":[],"applied":null,"candidate":null,"operation":null}""")]
    [InlineData("docker", "docker", """{"v":1,"state":"revoked","applied":{"sandboxId":"synthetic"},"candidate":null,"operation":null}""")]
    public void DockerAgent_WithNoOrRevokedRecord_DoesNotBlock(string kind, string applied, string? binding) =>
        Assert.Null(ManagedRuntimeRemovalPreflight.DescribeFault(kind, applied, binding));

    [Theory]
    [InlineData("openshell", "docker", null, "selected_runtime_not_docker")]
    [InlineData(null, "docker", null, "selected_runtime_not_docker")]
    [InlineData("docker", "openshell", null, "applied_runtime_not_docker")]
    [InlineData("docker", "docker", """{"state":"applied","applied":{"sandboxId":"synthetic"}}""", "runtime_record_not_revoked")]
    [InlineData("docker", "docker", """{"state":"stopped","applied":{"sandboxId":"synthetic"}}""", "runtime_record_not_revoked")]
    [InlineData("docker", "docker", """{"state":"needs_attention","errors":["x"]}""", "runtime_record_not_revoked")]
    [InlineData("docker", "docker", """{"state":"applying"}""", "runtime_record_not_revoked")]
    [InlineData("docker", "docker", """{"applied":null}""", "runtime_record_not_revoked")]
    [InlineData("docker", "docker", """{"state":"none","applied":{"sandboxId":"synthetic"}}""", "runtime_record_not_revoked")]
    [InlineData("docker", "docker", """{"state":"revoked","operation":"apply:1"}""", "runtime_record_in_progress")]
    [InlineData("docker", "docker", """{"state":"none","candidate":{"sandboxId":"synthetic"}}""", "runtime_record_in_progress")]
    [InlineData("docker", "docker", "not json", "runtime_record_unreadable")]
    [InlineData("docker", "docker", "[]", "runtime_record_unreadable")]
    public void AnythingElse_Blocks_WithAFixedReason(string? kind, string applied, string? binding, string reason) =>
        Assert.Equal(reason, ManagedRuntimeRemovalPreflight.DescribeFault(kind, applied, binding));

    [Fact]
    public void Reason_NeverEchoesTheStoredRecord()
    {
        const string marker = "synthetic-marker-7f3a";
        var reason = ManagedRuntimeRemovalPreflight.DescribeFault(
            "docker", "docker", """{"state":"applied","applied":{"sandboxId":"MARKER"}}""".Replace("MARKER", marker));
        Assert.NotNull(reason);
        Assert.DoesNotContain(marker, reason);
    }
}

// Runs against CI's real MySQL service; missing database is a failure, never a skip.
public sealed class RemoveManagedStockRuntimeMigrationTests : IAsyncLifetime
{
    private const string Before = ManagedRuntimeRemovalPreflight.AddedMigration;
    private const string Target = ManagedRuntimeRemovalPreflight.RemovalMigration;

    private static readonly string[] ManagedColumns =
    [
        "RuntimeKind", "SandboxPolicyVersionId", "NativeAuthMode", "ProviderApiKeyEnvRef",
        "AppliedRuntimeKind", "AppliedSandboxPolicyVersionId", "AppliedRuntimeBinding",
    ];

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
    public void MigrationIds_MatchTheMigrationsInTheAssembly()
    {
        using var db = NewContext();
        var ids = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToArray();
        // The removal directly follows the migration it undoes; later migrations may follow it.
        Assert.Equal(Array.IndexOf(ids, Before) + 1, Array.IndexOf(ids, Target));
        Assert.True(Array.IndexOf(ids, Before) >= 0);
    }

    [Fact]
    public async Task Removal_UpDropsColumnsAndTables_DownRestoresDefaults_UpReapplies()
    {
        await MigrateAsync(Before);
        await InsertAgentAsync("agent-a");
        await ExecAsync(_connectionString, "INSERT INTO sandbox_policies (Name) VALUES ('synthetic-policy')");

        await MigrateAsync(Target);
        Assert.Equal(0L, await ManagedColumnCountAsync());
        Assert.Equal(0L, await ManagedTableCountAsync());
        await using (var db = NewContext())
        {
            // This explicit target is historical; later additive migrations may remain pending.
            Assert.Equal(Target, (await db.Database.GetAppliedMigrationsAsync()).Last());
            Assert.DoesNotContain(Target, await db.Database.GetPendingMigrationsAsync());
            Assert.Equal("agent-a", await ScalarAsync<string>("SELECT Name FROM agents"));
        }

        await MigrateAsync(Before);
        Assert.Equal((long)ManagedColumns.Length, await ManagedColumnCountAsync());
        Assert.Equal(2L, await ManagedTableCountAsync());
        Assert.Equal("docker|docker", await ScalarAsync<string>(
            "SELECT CONCAT(RuntimeKind, '|', AppliedRuntimeKind) FROM agents WHERE Name = 'agent-a'"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM sandbox_policies"));

        await MigrateAsync(Target);
        Assert.Equal(0L, await ManagedColumnCountAsync());
    }

    [Theory]
    [InlineData("RuntimeKind = 'openshell'", "selected_runtime_not_docker")]
    [InlineData("AppliedRuntimeKind = 'openshell'", "applied_runtime_not_docker")]
    [InlineData("""AppliedRuntimeBinding = '{"state":"applied","applied":{"sandboxId":"synthetic"}}'""", "runtime_record_not_revoked")]
    [InlineData("""AppliedRuntimeBinding = '{"state":"needs_attention"}'""", "runtime_record_not_revoked")]
    [InlineData("AppliedRuntimeBinding = 'not json'", "runtime_record_unreadable")]
    public async Task Startup_ManagedAgent_RefusesBeforeMigration_PreservesRollbackSchema(string set, string reason)
    {
        await MigrateAsync(Before);
        await InsertAgentAsync("agent-a");
        await InsertAgentAsync("agent-b");
        await ExecAsync(_connectionString, $"UPDATE agents SET IsEnabled = 0, {set} WHERE Name = 'agent-b'");
        await using var db = NewContext();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();

        var error = await Assert.ThrowsAsync<ManagedRuntimeRemovalBlockedException>(() => SeedStartupAsync(db));

        Assert.Contains($"agent-b: {reason}", error.Message);
        Assert.DoesNotContain("agent-a", error.Message);
        Assert.Contains("docs/upgrading/remove-managed-runtime.md", error.Message);
        Assert.Equal(applied, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Contains(Target, await db.Database.GetPendingMigrationsAsync());
        Assert.Equal((long)ManagedColumns.Length, await ManagedColumnCountAsync());
        Assert.Equal(2L, await ManagedTableCountAsync());

        // The previous release can still read its columns and move this agent back to Docker.
        await ExecAsync(_connectionString,
            "UPDATE agents SET RuntimeKind = 'docker', AppliedRuntimeKind = 'docker', AppliedRuntimeBinding = NULL WHERE Name = 'agent-b'");
        await SeedStartupAsync(db);
        Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0L, await ManagedColumnCountAsync());
    }

    [Fact]
    public async Task Startup_DockerAgentsWithSavedPoliciesAndAnEmptyRecord_MigratesAndKeepsTheAgents()
    {
        await MigrateAsync(Before);
        await InsertAgentAsync("agent-a");
        await InsertAgentAsync("agent-b");
        await ExecAsync(_connectionString, "INSERT INTO sandbox_policies (Name) VALUES ('synthetic-policy')");
        await ExecAsync(_connectionString, """
            INSERT INTO sandbox_policy_versions (PolicyId, Version, Yaml, Sha256, ProviderProfileHashes, CreatedAt)
            SELECT Id, 1, 'version: 1', REPEAT('a', 64), '{}', UTC_TIMESTAMP(6) FROM sandbox_policies
            """);
        // A pending selection that was saved but never applied, and a record left empty after a revoke.
        await ExecAsync(_connectionString, """
            UPDATE agents SET SandboxPolicyVersionId = (SELECT MIN(Id) FROM sandbox_policy_versions) WHERE Name = 'agent-a'
            """);
        await ExecAsync(_connectionString, """
            UPDATE agents SET AppliedRuntimeBinding = '{"v":1,"state":"none","errors":[],"applied":null,"candidate":null,"operation":null}'
            WHERE Name = 'agent-b'
            """);

        await using var db = NewContext();
        await SeedStartupAsync(db);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0L, await ManagedColumnCountAsync());
        Assert.Equal(0L, await ManagedTableCountAsync());
        Assert.Equal(["agent-a", "agent-b"], await db.Agents.OrderBy(a => a.Name).Select(a => a.Name).ToListAsync());
    }

    [Fact]
    public async Task Startup_FreshDatabase_SkipsThePreflight_AndMigratesNormally()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());

        await SeedStartupAsync(db);

        Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0L, await ManagedColumnCountAsync());
    }

    [Fact]
    public async Task Startup_DatabaseFromBeforeTheManagedSchema_SkipsThePreflight()
    {
        // The migration just before the managed schema: its columns do not exist, so the preflight's
        // raw query must not run.
        await MigrateAsync("20261014141428_RemoveAgentPiImageInput");
        await InsertAgentAsync("agent-a");
        await using var db = NewContext();

        await SeedStartupAsync(db);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("agent-a", (await db.Agents.SingleAsync()).Name);
    }

    [Fact]
    public async Task Startup_AfterTheRemoval_DoesNotRunThePreflightAgain()
    {
        await MigrateAsync(Target);
        await InsertAgentAsync("agent-a");
        await using var db = NewContext();

        await SeedStartupAsync(db);
        await SeedStartupAsync(db);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static Task SeedStartupAsync(OrchestratorDbContext db)
    {
        var absent = Path.Combine(Path.GetTempPath(), $"no-seed-{Guid.NewGuid():N}");
        return DbSeeder.SeedAsync(db, absent, outputStylesDir: absent);
    }

    private Task<long> ManagedColumnCountAsync() => ScalarAsync<long>($"""
        SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME = 'agents' AND COLUMN_NAME IN ({string.Join(", ", ManagedColumns.Select(c => $"'{c}'"))})
        """);

    private Task<long> ManagedTableCountAsync() => ScalarAsync<long>("""
        SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME IN ('sandbox_policies', 'sandbox_policy_versions')
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
