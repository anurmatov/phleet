using System.Net.Http.Json;
using System.Diagnostics;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>The same HTTP checks against real MySQL, plus actual migration and dump/restore queries.</summary>
public sealed class ManagedStockMySqlTests : ManagedStockRuntimeApiTests
{
    private string _admin = "";
    private string _database = "";
    private string _connection = "";
    private string _restoreDatabase = "";
    protected override async Task ConfigureDatabaseAsync(IServiceCollection services)
    {
        var configured = Environment.GetEnvironmentVariable(AddEpicGrantsMigrationTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{AddEpicGrantsMigrationTests.ConnectionVariable} is required; MySQL tests never skip.");
        _admin = new MySqlConnectionStringBuilder(configured) { Database = "" }.ConnectionString;
        _database = "orch_policy_" + Guid.NewGuid().ToString("N");
        await Exec(_admin, $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
        _connection = new MySqlConnectionStringBuilder(configured) { Database = _database }.ConnectionString;
        services.AddDbContext<OrchestratorDbContext>(o => o.UseMySql(_connection, new MySqlServerVersion(new Version(8, 0, 0))));
        using var db = NewContext(_connection);
        await db.Database.MigrateAsync();
    }
    private static OrchestratorDbContext NewContext(string connection) => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 0))).Options);
    protected override async Task DropDatabaseAsync()
    {
        foreach (var name in new[] { _database, _restoreDatabase }.Where(n => n.Length > 0))
            await Exec(_admin, $"DROP DATABASE IF EXISTS `{name}`");
    }
    private static async Task Exec(string connection, string sql)
    {
        await using var c = new MySqlConnection(connection); await c.OpenAsync();
        await using var command = new MySqlCommand(sql, c); await command.ExecuteNonQueryAsync();
    }
    [Fact]
    public async Task Upgrade_preserves_existing_agent_and_grants_with_Docker_defaults()
    {
        await WithDb(async db => { var a = ManagedStockRuntimeTests.Agent(); a.Tools = [new() { ToolName = "existing", IsEnabled = true }]; db.Agents.Add(a); return await db.SaveChangesAsync(); });
        await WithDb(async db =>
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            await db.Database.MigrateAsync();
            return 0;
        });
        var agent = await WithDb(db => db.Agents.Include(a => a.Tools).AsNoTracking().SingleAsync());
        Assert.Equal("agent1", agent.Name);
        Assert.Equal("docker", agent.RuntimeKind); Assert.Equal("docker", agent.AppliedRuntimeKind);
        Assert.Null(agent.SandboxPolicyVersionId); Assert.Null(agent.AppliedRuntimeBinding);
        Assert.Equal("existing", Assert.Single(agent.Tools).ToolName);
    }
    [Fact]
    public async Task Dump_restore_query_preserves_pinned_hash_selection_and_stopped_binding()
    {
        var id = await CreatePolicy();
        var version = await WithDb(db => db.SandboxPolicyVersions.SingleAsync(v => v.PolicyId == id));
        await WithDb(async db =>
        {
            var a = ManagedStockRuntimeTests.Agent(); a.RuntimeKind = "openshell";
            a.SandboxPolicyVersionId = version.Id; a.NativeAuthMode = "api_key"; a.ProviderApiKeyEnvRef = "TEST_API_KEY";
            a.AppliedRuntimeBinding = "synthetic-stopped-docker-binding";
            db.Agents.Add(a); return await db.SaveChangesAsync();
        });
        var dump = await MysqlTool("mysqldump", _database, null, "--single-transaction", "--no-tablespaces", "--set-gtid-purged=OFF");
        _restoreDatabase = "orch_restore_" + Guid.NewGuid().ToString("N");
        await Exec(_admin, $"CREATE DATABASE `{_restoreDatabase}` CHARACTER SET utf8mb4");
        await MysqlTool("mysql", _restoreDatabase, dump);
        using var restored = NewContext(new MySqlConnectionStringBuilder(_connection) { Database = _restoreDatabase }.ConnectionString);
        var row = await restored.SandboxPolicyVersions.SingleAsync();
        var agent = await restored.Agents.SingleAsync();
        Assert.Equal(version.Sha256, row.Sha256); Assert.Equal(version.Yaml, row.Yaml);
        Assert.Equal(version.Id, agent.SandboxPolicyVersionId); Assert.Equal("openshell", agent.RuntimeKind);
        Assert.Equal("docker", agent.AppliedRuntimeKind); Assert.Equal("synthetic-stopped-docker-binding", agent.AppliedRuntimeBinding);
        Assert.Equal("TEST_API_KEY", agent.ProviderApiKeyEnvRef);
        Assert.Empty(await restored.Database.GetPendingMigrationsAsync());
    }
    [Fact]
    public async Task Referenced_version_is_retained_by_real_foreign_key()
    {
        var id = await CreatePolicy();
        var v = await WithDb(db => db.SandboxPolicyVersions.SingleAsync(x => x.PolicyId == id));
        await WithDb(async db => { var a = ManagedStockRuntimeTests.Agent(); a.SandboxPolicyVersionId = v.Id; db.Agents.Add(a); return await db.SaveChangesAsync(); });
        var error = await Assert.ThrowsAsync<MySqlException>(() => Exec(_connection, $"DELETE FROM sandbox_policy_versions WHERE Id={v.Id}"));
        Assert.Equal(1451, error.Number);
    }
    [Fact]
    public async Task Concurrent_append_has_one_winner_and_no_partial_versions()
    {
        var id = await CreatePolicy();
        var url = $"/api/sandbox-policies/{id}/versions";
        var results = await Task.WhenAll(Client.PostAsJsonAsync(url, new { version = 2, yaml = "version: 1" }),
            Client.PostAsJsonAsync(url, new { version = 2, yaml = "version: 1\n" }));
        Assert.Single(results, r => r.StatusCode == System.Net.HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == System.Net.HttpStatusCode.Conflict);
        Assert.Equal(2, await WithDb(db => db.SandboxPolicyVersions.CountAsync()));
    }

    private async Task<string> MysqlTool(string executable, string database, string? input, params string[] extra)
    {
        var cs = new MySqlConnectionStringBuilder(_connection);
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in new[] { "--protocol=TCP", "--default-character-set=utf8mb4", $"--host={cs.Server}", $"--port={cs.Port}", $"--user={cs.UserID}" }.Concat(extra).Append(database))
            start.ArgumentList.Add(arg);
        start.Environment["MYSQL_PWD"] = cs.Password; // never argv or diagnostics
        using var process = Process.Start(start) ?? throw new InvalidOperationException("MySQL client is required for restore verification.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await error; // do not print server/client diagnostics or connection details
            Assert.True(process.ExitCode == 0, "MySQL dump/restore client failed.");
            return await output;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
