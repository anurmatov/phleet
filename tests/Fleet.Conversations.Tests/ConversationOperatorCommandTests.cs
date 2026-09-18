using Fleet.Comms.Operations;

namespace Fleet.Conversations.Tests;

/// <summary>
/// <c>conversations migrate</c> and <c>conversations status</c>, driven through the real command
/// dispatcher against a real database (AC26, AC27, AC29a, AC29b).
/// </summary>
/// <remarks>
/// <para>
/// These go through <see cref="OperatorCommands.RunAsync"/> — the entry point <c>Program</c>
/// dispatches to before any web application is built — with configuration supplied through the
/// environment exactly as the container supplies it, and with stdout, stderr and the exit code
/// captured. Calling <see cref="MigrationRunner"/> directly proves the runner works and says
/// nothing about the command: the connection string it picks, what it prints, and what it exits
/// with are the command's contract, and an operator acts on all three.
/// </para>
/// <para>
/// They live in this project rather than beside the other operator-command tests because they need
/// a database. <c>Fleet.Comms.Tests</c> runs without one, and it must keep doing so.
/// </para>
/// <para>
/// ⚠️ The environment is process-wide, so this class is in the <c>mysql</c> collection — xUnit runs
/// a collection's classes one at a time, which is what keeps two of them from resolving each
/// other's connection strings.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class ConversationOperatorCommandTests(MySqlFixture fixture) : IDisposable
{
    private const string MigrationKey = "Comms__ConversationMigrationConnectionString";
    private const string RuntimeKey = "Comms__ConversationConnectionString";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MigrationKey, null);
        Environment.SetEnvironmentVariable(RuntimeKey, null);
    }

    // ── conversations migrate ────────────────────────────────────────────────

    /// <summary>
    /// Applying, then applying again: the second run changes nothing, says the version, and still
    /// exits 0.
    /// </summary>
    /// <remarks>
    /// The exit code is half the assertion. A re-run that reported "already up to date" and exited
    /// non-zero would fail every idempotent deploy script that runs migrations before starting the
    /// service — which is the shape <c>upgrade.sh</c> has.
    /// </remarks>
    [Fact]
    public async Task Migrate_is_idempotent_and_reports_the_version()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        Environment.SetEnvironmentVariable(MigrationKey, scratch.ConnectionString);

        var first = await RunAsync("conversations", "migrate");

        Assert.Equal(0, first.Exit);

        // Every version this binary carries, in order — the command reports what it applied, and
        // applying from empty applies all of them.
        Assert.Contains(
            "applied version(s): "
            + string.Join(", ", MigrationRunner.Scripts.Select(script => script.Version)),
            first.Stdout, StringComparison.Ordinal);

        var second = await RunAsync("conversations", "migrate");

        Assert.Equal(0, second.Exit);
        Assert.Contains("schema already up to date", second.Stdout, StringComparison.Ordinal);
        Assert.Contains($"already at version {MigrationRunner.ExpectedVersion}", second.Stdout,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An edited migration is refused as a message on stderr and exit 1 — never a stack trace.
    /// </summary>
    [Fact]
    public async Task Migrate_refuses_an_edited_migration_with_a_message_and_exit_one()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        Environment.SetEnvironmentVariable(MigrationKey, scratch.ConnectionString);

        await RunAsync("conversations", "migrate");

        await MySqlFixture.ExecuteOnAsync(scratch.ConnectionString,
            "UPDATE schema_migrations SET checksum = REPEAT('0', 64) "
            + $"WHERE version = {MigrationRunner.ExpectedVersion}");

        var refused = await RunAsync("conversations", "migrate");

        Assert.Equal(1, refused.Exit);
        Assert.Contains("contents have changed", refused.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", refused.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC29a: run with the RUNTIME credential, the command fails — the account has no DDL grant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operator-facing half of the property. The runner's own half is in
    /// <see cref="MigrationRunnerTests.The_runtime_account_cannot_apply_a_migration"/>; this asserts
    /// that pointing the <b>command</b> at the runtime account — the plausible mistake, since it is
    /// the connection string the deployment already has — is a failure the operator can see, not a
    /// migration that quietly succeeds because the account turned out to have more rights than
    /// intended.
    /// </para>
    /// <para>
    /// Deliberately not asserted on the database's wording. The message is MySQL's and differs
    /// across versions and forks; what this pins is the exit code and that nothing was created.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Migrate_with_the_runtime_account_fails_and_creates_nothing()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        await using var runtime = await fixture.CreateDmlOnlyAccountAsync(scratch.Name);

        Environment.SetEnvironmentVariable(MigrationKey, runtime.ConnectionString);

        var failure = await Record.ExceptionAsync(() => RunAsync("conversations", "migrate"));

        Assert.NotNull(failure);

        Assert.Equal("0", await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()"));
    }

    /// <summary>
    /// With no DDL connection string configured, the command refuses and says why.
    /// </summary>
    /// <remarks>
    /// It must not silently fall back to <c>ConversationConnectionString</c>. That is the runtime
    /// account, and a fallback would mean the one command allowed to change the schema reaching for
    /// the one credential deliberately unable to.
    /// </remarks>
    [Fact]
    public async Task Migrate_without_a_ddl_connection_string_refuses_rather_than_falling_back()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();

        Environment.SetEnvironmentVariable(MigrationKey, null);
        Environment.SetEnvironmentVariable(RuntimeKey, scratch.ConnectionString);

        var refused = await RunAsync("conversations", "migrate");

        Assert.Equal(1, refused.Exit);
        Assert.Contains("ConversationMigrationConnectionString", refused.Stderr,
            StringComparison.Ordinal);

        Assert.Equal("0", await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()"));
    }

    // ── conversations status ─────────────────────────────────────────────────

    /// <summary>
    /// AC29b: the status output CHANGES after a migration, and the exit code with it.
    /// </summary>
    /// <remarks>
    /// Before and after are captured from the same command against the same database, so this is
    /// the operator's actual before-and-after rather than two independent assertions that happen to
    /// differ. The exit code is asserted because that is what a healthcheck or a deploy script
    /// branches on — a status command that always exits 0 reports a mismatch to nobody.
    /// </remarks>
    [Fact]
    public async Task Status_changes_after_a_migration()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        Environment.SetEnvironmentVariable(MigrationKey, scratch.ConnectionString);

        var before = await RunAsync("conversations", "status");

        Assert.Equal(1, before.Exit);
        Assert.Contains("applied:  none", before.Stdout, StringComparison.Ordinal);
        Assert.Contains("matches:  False", before.Stdout, StringComparison.Ordinal);
        Assert.Contains("no schema applied", before.Stdout, StringComparison.Ordinal);

        await RunAsync("conversations", "migrate");

        var after = await RunAsync("conversations", "status");

        Assert.Equal(0, after.Exit);
        Assert.Contains($"applied:  {MigrationRunner.ExpectedVersion}", after.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("matches:  True", after.Stdout, StringComparison.Ordinal);

        Assert.NotEqual(before.Stdout, after.Stdout);
    }

    /// <summary>
    /// A database ahead of this binary is reported as ahead, with a non-zero exit.
    /// </summary>
    [Fact]
    public async Task Status_reports_a_database_ahead_of_the_binary()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        Environment.SetEnvironmentVariable(MigrationKey, scratch.ConnectionString);

        await RunAsync("conversations", "migrate");

        await MySqlFixture.ExecuteOnAsync(scratch.ConnectionString,
            "INSERT INTO schema_migrations (version, script_name, checksum) VALUES "
            + $"({MigrationRunner.ExpectedVersion + 1}, 'from_a_newer_binary', REPEAT('a', 64))");

        var ahead = await RunAsync("conversations", "status");

        Assert.Equal(1, ahead.Exit);
        Assert.Contains("AHEAD", ahead.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>status</c> reads through the RUNTIME connection string when no DDL one is configured.
    /// </summary>
    /// <remarks>
    /// Unlike <c>migrate</c>, this is correct: reporting the applied version is a read, the runtime
    /// account can do it, and a deployment that keeps the DDL credential off the running container
    /// must still be able to ask what schema it is on.
    /// </remarks>
    [Fact]
    public async Task Status_falls_back_to_the_runtime_connection_string()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        await using var runtime = await fixture.CreateDmlOnlyAccountAsync(scratch.Name);

        Environment.SetEnvironmentVariable(MigrationKey, scratch.ConnectionString);
        await RunAsync("conversations", "migrate");

        Environment.SetEnvironmentVariable(MigrationKey, null);
        Environment.SetEnvironmentVariable(RuntimeKey, runtime.ConnectionString);

        var status = await RunAsync("conversations", "status");

        Assert.Equal(0, status.Exit);
        Assert.Contains($"applied:  {MigrationRunner.ExpectedVersion}", status.Stdout,
            StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await OperatorCommands.RunAsync(args, stdout, stderr);

        return (exit, stdout.ToString(), stderr.ToString());
    }
}
