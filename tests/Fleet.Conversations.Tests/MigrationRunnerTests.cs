using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The migration runner's contract (AC26, AC27, AC28, AC29a).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MySqlFixture"/> calls <c>MigrateAsync</c> once as setup, which proves apply-from-empty
/// and nothing else. Every question that matters afterwards — is a re-run a no-op, does an edited
/// script get refused, is a rolled-back binary distinguishable from an un-migrated one, can the
/// runtime account do this at all — needs a database the fixture has not already migrated, so each
/// of these takes its own scratch schema.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class MigrationRunnerTests(MySqlFixture fixture)
{
    // ── AC26: applying twice ─────────────────────────────────────────────────

    /// <summary>
    /// A second run applies nothing and says which version the database is at.
    /// </summary>
    /// <remarks>
    /// The reported version is asserted, not merely the empty result. "Nothing to do" and "nothing
    /// to do, and here is where you are" are different operator experiences, and the second is the
    /// one that lets a deploy log answer "did this deployment get the schema?" without a
    /// round-trip.
    /// </remarks>
    [Fact]
    public async Task A_second_run_applies_nothing_and_reports_the_applied_version()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        var runner = new MigrationRunner(scratch.ConnectionString);

        var first = new List<string>();
        var applied = await runner.MigrateAsync(first.Add);

        Assert.Equal([MigrationRunner.ExpectedVersion], applied);

        var second = new List<string>();
        var again = await runner.MigrateAsync(second.Add);

        Assert.Empty(again);
        Assert.Contains(second, line =>
            line.Contains($"already at version {MigrationRunner.ExpectedVersion}",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Re-running leaves the recorded application ALONE — same checksum, same timestamp.
    /// </summary>
    /// <remarks>
    /// Idempotent is not the same as harmless. A re-run that re-recorded the row would move
    /// <c>applied_at</c>, which is the only evidence of when the schema actually changed.
    /// </remarks>
    [Fact]
    public async Task A_second_run_does_not_rewrite_the_bookkeeping_row()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        var runner = new MigrationRunner(scratch.ConnectionString);

        await runner.MigrateAsync();
        var before = await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString,
            "SELECT version, checksum, applied_at FROM schema_migrations ORDER BY version");

        await runner.MigrateAsync();
        var after = await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString,
            "SELECT version, checksum, applied_at FROM schema_migrations ORDER BY version");

        Assert.Equal(before, after);
    }

    // ── AC27: refusals, and ahead versus behind ──────────────────────────────

    /// <summary>
    /// An applied script whose contents have changed is refused BY NAME, and applies nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recorded checksum is what the runner compares against, so tampering with it is the same
    /// event as editing the script — and it is the one a test can stage without shipping a second
    /// copy of the schema.
    /// </para>
    /// <para>
    /// Refusing matters because applying the difference silently is how two deployments end up
    /// carrying the same version number and different schemas, which nothing downstream can detect.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_edited_migration_is_refused_rather_than_reapplied()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        var runner = new MigrationRunner(scratch.ConnectionString);
        await runner.MigrateAsync();

        await MySqlFixture.ExecuteOnAsync(scratch.ConnectionString,
            "UPDATE schema_migrations SET checksum = REPEAT('0', 64) "
            + $"WHERE version = {MigrationRunner.ExpectedVersion}");

        var refusal = await Assert.ThrowsAsync<MigrationException>(() => runner.MigrateAsync());

        Assert.Contains("contents have changed", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"migration {MigrationRunner.ExpectedVersion}", refusal.Message,
            StringComparison.Ordinal);

        // And it refused rather than half-applied: the tampered row is exactly as it was left.
        Assert.StartsWith($"{MigrationRunner.ExpectedVersion}|{new string('0', 64)}",
            await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString,
                "SELECT version, checksum FROM schema_migrations"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A database AHEAD of this binary is reported as ahead, and distinguishably from behind.
    /// </summary>
    /// <remarks>
    /// The rollback-after-migration case. It is as unhealthy as being behind and needs the opposite
    /// action from the operator — restore or roll forward, not migrate — so reporting both as
    /// "mismatch" would send them to the wrong one.
    /// </remarks>
    [Fact]
    public async Task A_database_ahead_of_the_binary_is_distinguished_from_one_behind_it()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        var runner = new MigrationRunner(scratch.ConnectionString);

        // Never migrated: behind, and reported as having no schema at all.
        var empty = await runner.GetStatusAsync();

        Assert.Null(empty.AppliedVersion);
        Assert.False(empty.Matches);
        Assert.True(empty.IsBehind);
        Assert.False(empty.IsAhead);
        Assert.Contains("no schema applied", empty.Describe(), StringComparison.Ordinal);

        await runner.MigrateAsync();
        var current = await runner.GetStatusAsync();

        Assert.True(current.Matches);
        Assert.False(current.IsAhead);
        Assert.False(current.IsBehind);

        // A newer binary migrated this database and was then rolled back.
        await MySqlFixture.ExecuteOnAsync(scratch.ConnectionString,
            "INSERT INTO schema_migrations (version, script_name, checksum) VALUES "
            + $"({MigrationRunner.ExpectedVersion + 1}, 'from_a_newer_binary', REPEAT('a', 64))");

        var ahead = await runner.GetStatusAsync();

        Assert.Equal(MigrationRunner.ExpectedVersion + 1, ahead.AppliedVersion);
        Assert.True(ahead.IsAhead);
        Assert.False(ahead.IsBehind);
        Assert.False(ahead.Matches);
        Assert.Contains("AHEAD", ahead.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The two unhealthy descriptions are different sentences, not one shared "mismatch".
    /// </summary>
    /// <remarks>
    /// Asserted on the record rather than through a database because this binary carries one script,
    /// so there is no applied version BELOW the expected one that a real schema could be left at.
    /// The distinction is still a property of the type, and this is where it is pinned; the
    /// database-backed halves are above.
    /// </remarks>
    [Fact]
    public void Behind_and_ahead_do_not_share_a_message()
    {
        var behind = new SchemaStatus { AppliedVersion = 4, ExpectedVersion = 7 };
        var ahead = new SchemaStatus { AppliedVersion = 9, ExpectedVersion = 7 };

        Assert.True(behind.IsBehind);
        Assert.False(behind.IsAhead);
        Assert.Contains("BEHIND", behind.Describe(), StringComparison.Ordinal);

        Assert.True(ahead.IsAhead);
        Assert.False(ahead.IsBehind);
        Assert.Contains("AHEAD", ahead.Describe(), StringComparison.Ordinal);

        Assert.NotEqual(behind.Describe(), ahead.Describe());
    }

    // ── AC29a: the runtime account cannot migrate ────────────────────────────

    /// <summary>
    /// The account the service runs as holds no DDL grant, so a migration through it FAILS at the
    /// database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes "startup never migrates" a property of the deployment rather than a
    /// property of the code being careful. Even a process that tried would be refused.
    /// </para>
    /// <para>
    /// The account is created here rather than taken from <c>FLEET_CONVERSATIONS_CONNECTION</c> so
    /// the assertion is real in every environment: a local run without that variable falls back to
    /// the DDL connection, and the test would then pass by testing nothing. CI additionally runs
    /// this entire suite through a DDL-less account, so a store method that quietly needed DDL
    /// fails there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_runtime_account_cannot_apply_a_migration()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        await using var runtime = await fixture.CreateDmlOnlyAccountAsync(scratch.Name);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new MigrationRunner(runtime.ConnectionString).MigrateAsync());

        // Nothing was created. The refusal is at the first DDL statement, not part-way through.
        Assert.Equal("0", await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()"));
    }

    // ── AC28: starting the service never migrates ────────────────────────────

    /// <summary>
    /// Building and starting the south host against an UNMIGRATED database creates no schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted by running it, against the composition <c>Program</c> uses, rather than by reading
    /// the source and concluding nothing calls the runner. A process that migrated on boot would
    /// turn a deployment mistake into an irreversible schema change and remove the operator's
    /// chance to take a backup first.
    /// </para>
    /// <para>
    /// A request is sent too, so this covers a lazily-migrating store as well as an eagerly
    /// migrating one. It fails — there are no tables — and that is the point: it fails rather than
    /// helpfully creating them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Starting_the_service_against_an_unmigrated_database_creates_no_schema()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();

        var store = new MySqlConversationStore(scratch.ConnectionString, new ConversationStoreOptions(),
            NullLogger<MySqlConversationStore>.Instance);

        await using (var host = await SouthTestHost.StartAsync(store))
        {
            // The call fails — there are no tables — and that IS the expected outcome. What is
            // being asserted is what it did NOT do on the way to failing.
            await Record.ExceptionAsync(() => host.PostAsync("/deliveries:claim",
                new ClaimDeliveryRequest { MessageId = "m_absent", Owner = "agent-1" }));
        }

        Assert.Equal("0", await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()"));
    }

    /// <summary>
    /// The migration runner is reachable from the operator subcommands and from nothing else in
    /// <c>Fleet.Comms</c>.
    /// </summary>
    /// <remarks>
    /// The behavioural test above covers the path that exists today. This one covers the path
    /// somebody adds tomorrow: a startup call inserted into <c>Program</c> or into the composition
    /// would not fail any other assertion in this repository, because a correctly-migrated
    /// deployment never notices.
    /// </remarks>
    [Fact]
    public void Only_the_operator_subcommands_reach_the_migration_runner()
    {
        var comms = RepositoryRoot().GetDirectories("src").Single()
            .GetDirectories("Fleet.Comms").Single();

        var mentions = comms.GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file.FullName)
                .Contains(nameof(MigrationRunner), StringComparison.Ordinal))
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["OperatorCommands.cs"], mentions);
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, "could not locate the repository root");
        return directory!;
    }
}
