using System.Security.Cryptography;
using Fleet.Conversations.Journal;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The three sweep classes (#388), over the real table and a fake bucket, on a clock the test moves.
/// </summary>
/// <remarks>
/// <para>
/// The order each class works in is the invariant under test: <b>bytes first, then the row</b>. A
/// failure between the two leaves a row whose object is gone, which the next tick deletes because
/// deleting a missing object is success. The reverse order leaves an object with no row, and only
/// the weekly listing can find it — which is how a bucket grows forever.
/// </para>
/// <para>
/// ⚠️ These assert the <c>Failures</c> counter and that the row survived, not merely that nothing
/// threw. A sweep that swallowed a bucket error and deleted the row would look identical in a log
/// and would orphan the object permanently.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class JournalObjectSweeperTests : IAsyncLifetime
{
    /// <summary>
    /// A schema of this class's own, not the shared <c>mysql</c> one.
    /// </summary>
    /// <remarks>
    /// The sweep works by age and counts everything in the table, so any row another class left
    /// behind in a shared schema is indistinguishable from a sweep bug. A scratch database is the
    /// only way "1 row remains" means what the AC says it means.
    /// </remarks>
    private static readonly MySqlFixture Shared = new();

    private FakeBucket _bucket = null!;
    private ManualTime _time = null!;
    private MySqlJournalObjectStore _objects = null!;
    private JournalRuntimeStats _stats = null!;
    private ScratchDatabase _scratch = null!;

    public JournalObjectSweeperTests() => _time = new ManualTime(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    public string Db => _scratch.ConnectionString;

    public async Task InitializeAsync()
    {
        await Shared.InitializeAsync();
        _scratch = await Shared.CreateScratchDatabaseAsync();
        await new MigrationRunner(_scratch.ConnectionString).MigrateAsync();

        _bucket = new FakeBucket();
        _objects = new MySqlJournalObjectStore(Db, NullLogger.Instance, _time);
        _stats = new JournalRuntimeStats(_time);
    }

    public async Task DisposeAsync()
    {
        await _bucket.DisposeAsync();
        await _scratch.DisposeAsync();
    }

    private JournalObjectSweeper Sweeper() =>
        new(Db, _bucket, NullLogger.Instance, _stats, _time);

    private async Task<string> Declare(string subject = "agent1", int length = 3)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return await _objects.OpenUploadAsync(
            subject, Convert.ToHexStringLower(SHA256.HashData(bytes)), length, "image/jpeg");
    }

    /// <summary>An object younger than the abandon window survives, whatever its state.</summary>
    [Fact]
    public async Task A_fresh_upload_survives_the_tick()
    {
        var id = await Declare();
        await _objects.MarkUploadedAsync(id, "agent1");

        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(0, swept.Abandoned);
        Assert.Equal(1, await CountAsync("journal_objects"));
    }

    /// <summary>
    /// Exactly one hour short of the window and nothing goes. The window is a promise to the agent
    /// that its spool has a day to come back, not a rough guide.
    /// </summary>
    [Fact]
    public async Task One_hour_short_of_the_window_nothing_is_deleted()
    {
        var id = await Declare();
        await _objects.MarkUploadedAsync(id, "agent1");

        _time.Advance(JournalObjectSweeper.AbandonAfter - TimeSpan.FromHours(1));
        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(0, swept.Abandoned);
        Assert.Equal(1, await CountAsync("journal_objects"));
    }

    [Fact]
    public async Task An_abandoned_upload_loses_its_bytes_then_its_row()
    {
        var id = await Declare();
        var key = await KeyOfAsync(id);
        _bucket.Objects[key] = [1, 2, 3];

        _time.Advance(JournalObjectSweeper.AbandonAfter + TimeSpan.FromMinutes(1));
        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(1, swept.Abandoned);
        Assert.Equal(0, swept.Failures);
        Assert.Empty(_bucket.Keys);
        Assert.Equal(0, await CountAsync("journal_objects"));
    }

    /// <summary>
    /// A bucket that refuses the delete leaves the row exactly where it was and counts the failure.
    /// Deleting the row anyway would create the orphan the sweeper is supposed to remove.
    /// </summary>
    [Fact]
    public async Task A_bucket_error_leaves_the_row_for_the_next_tick()
    {
        var id = await Declare();
        var key = await KeyOfAsync(id);
        _bucket.Objects[key] = [1, 2, 3];
        _bucket.Reachable = false;

        _time.Advance(JournalObjectSweeper.AbandonAfter + TimeSpan.FromMinutes(1));
        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(0, swept.Abandoned);
        Assert.True(swept.Failures > 0);
        Assert.Equal(1, await CountAsync("journal_objects"));

        // The next tick, with the bucket back, finishes the job.
        _bucket.Reachable = true;
        Assert.Equal(1, (await Sweeper().SweepOnceAsync()).Abandoned);
        Assert.Equal(0, await CountAsync("journal_objects"));
    }

    /// <summary>
    /// An object that is already gone from the bucket is a success, not a failure — this is the
    /// state a previous tick left behind between the delete and the row removal.
    /// </summary>
    [Fact]
    public async Task An_object_that_is_already_gone_still_removes_the_row()
    {
        var id = await Declare();

        _time.Advance(JournalObjectSweeper.AbandonAfter + TimeSpan.FromMinutes(1));
        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(1, swept.Abandoned);
        Assert.Equal(0, swept.Failures);
        Assert.Equal(0, await CountAsync("journal_objects"));
    }

    /// <summary>
    /// A dedup loser is <c>aborted</c> and is swept on the same clock as an abandoned upload —
    /// AC1's ending, from the sweeper's side.
    /// </summary>
    [Fact]
    public async Task An_aborted_object_is_swept()
    {
        var id = await Declare();
        await _objects.MarkUploadedAsync(id, "agent1");
        await _objects.MarkAbortedAsync(id, "agent1");

        _time.Advance(JournalObjectSweeper.AbandonAfter + TimeSpan.FromMinutes(1));

        Assert.Equal(1, (await Sweeper().SweepOnceAsync()).Abandoned);
        Assert.Equal(0, await CountAsync("journal_objects"));
    }

    /// <summary>
    /// A <c>committed</c> object is never swept by age, at any age. It is only the retention sweep
    /// that marks one <c>deleting</c>, and that is a decision about a message being deleted.
    /// </summary>
    [Fact]
    public async Task A_committed_object_is_never_swept_by_age()
    {
        var id = await Declare();
        await _objects.MarkUploadedAsync(id, "agent1");
        await CommitAsync(id);

        _time.Advance(TimeSpan.FromDays(400));
        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(0, swept.Abandoned + swept.Retired);
        Assert.Equal(1, await CountAsync("journal_objects"));
    }

    /// <summary>
    /// Class (b): a retired object past its grace window goes. Before it, its bytes stay — that
    /// window is what lets a backup taken before the mark still restore to something whole.
    /// </summary>
    [Fact]
    public async Task A_retired_object_goes_only_past_its_grace()
    {
        var id = await Declare();
        await _objects.MarkUploadedAsync(id, "agent1");
        await CommitAsync(id);
        await MarkDeletingAsync(id, graceHours: 72);
        var key = await KeyOfAsync(id);
        _bucket.Objects[key] = [1, 2, 3];

        _time.Advance(JournalObjectSweeper.DeleteGrace - TimeSpan.FromHours(1));
        Assert.Equal(0, (await Sweeper().SweepOnceAsync()).Retired);
        Assert.True(_bucket.Keys.Contains(key));

        _time.Advance(TimeSpan.FromHours(2));
        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(1, swept.Retired);
        Assert.Equal(0, await CountAsync("journal_objects"));
        Assert.Empty(_bucket.Keys);
    }

    /// <summary>
    /// Class (c): a key under the journal prefix with no row is deleted weekly, and a key the
    /// deployment never wrote is left alone no matter how old it is.
    /// </summary>
    [Fact]
    public async Task An_orphan_key_goes_and_a_foreign_key_does_not()
    {
        _bucket.Objects["j1/01J000000000000000000000FF"] = [1];
        _bucket.Objects["attachments/2026/09/other-app.png"] = [2];

        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(1, swept.Orphans);
        Assert.False(_bucket.Keys.Contains("j1/01J000000000000000000000FF"));
        Assert.True(_bucket.Keys.Contains("attachments/2026/09/other-app.png"));
    }

    /// <summary>
    /// A key that has a row is not an orphan, whatever its state. An <c>uploading</c> row whose
    /// bytes arrived is mid-flight, and deleting its object would fail an upload in progress.
    /// </summary>
    [Fact]
    public async Task A_key_with_a_row_is_never_an_orphan()
    {
        var id = await Declare();
        var key = await KeyOfAsync(id);
        _bucket.Objects[key] = [1, 2, 3];

        var swept = await Sweeper().SweepOnceAsync();

        Assert.Equal(0, swept.Orphans);
        Assert.True(_bucket.Keys.Contains(key));
    }

    /// <summary>
    /// The listing runs weekly, not every tick. It is the one O(bucket) step in the sweep, and the
    /// GC tick is frequent.
    /// </summary>
    [Fact]
    public async Task The_orphan_listing_runs_weekly_not_every_tick()
    {
        _bucket.Objects["j1/01J000000000000000000000FF"] = [1];
        var sweeper = Sweeper();

        Assert.Equal(1, (await sweeper.SweepOnceAsync()).Orphans);

        _bucket.Objects["j1/01J000000000000000000000FE"] = [1];
        Assert.Equal(0, (await sweeper.SweepOnceAsync()).Orphans);

        _time.Advance(JournalObjectSweeper.OrphanInterval + TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await sweeper.SweepOnceAsync()).Orphans);
    }

    /// <summary>
    /// The listing is skipped when a row-driven class failed. Comparing the bucket against a table
    /// that is not settled is how a sweep deletes the wrong object.
    /// </summary>
    [Fact]
    public async Task A_failing_delete_defers_the_orphan_listing()
    {
        var id = await Declare();
        var key = await KeyOfAsync(id);
        _bucket.Objects[key] = [1, 2, 3];
        _bucket.Objects["j1/01J000000000000000000000FF"] = [9];

        _time.Advance(JournalObjectSweeper.AbandonAfter + TimeSpan.FromMinutes(1));
        _bucket.Reachable = false;
        var first = await Sweeper().SweepOnceAsync();

        Assert.True(first.Failures > 0);
        Assert.Equal(0, first.Orphans);
        Assert.True(_bucket.Keys.Contains("j1/01J000000000000000000000FF"));
    }

    /// <summary>The sweep numbers reach the status route, so an operator can see a stalled sweep.</summary>
    [Fact]
    public async Task The_sweep_counts_reach_the_status_snapshot()
    {
        var id = await Declare();
        _time.Advance(JournalObjectSweeper.AbandonAfter + TimeSpan.FromMinutes(1));
        await Sweeper().SweepOnceAsync();

        var snapshot = _stats.Read();

        Assert.Equal(1, snapshot.ObjectsAbandonedDeleted);
        Assert.NotNull(snapshot.ObjectsLastSweepAt);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string> KeyOfAsync(string id) => await MySqlFixture.ScalarRowOnAsync(
        Db, $"SELECT object_key FROM journal_objects WHERE id = '{id}'");

    private async Task<int> CountAsync(string table) => int.Parse(
        await MySqlFixture.ScalarRowOnAsync(Db, $"SELECT COUNT(*) FROM {table}"),
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Marks the row <c>committed</c> the way ingest does, with the unique-key digest. Directly,
    /// because this suite is about the sweep and not about the commit path.
    /// </summary>
    private Task CommitAsync(string id) => ExecuteAsync(
        "UPDATE journal_objects SET state = 'committed', committed_sha256 = sha256, updated_at = @now "
        + "WHERE id = @id",
        ("@id", id),
        ("@now", _time.GetUtcNow().UtcDateTime));

    private Task MarkDeletingAsync(string id, int graceHours) => ExecuteAsync(
        "UPDATE journal_objects SET state = 'deleting', delete_after = @after, updated_at = @now WHERE id = @id",
        ("@id", id),
        ("@after", _time.GetUtcNow().UtcDateTime.AddHours(graceHours)),
        ("@now", _time.GetUtcNow().UtcDateTime));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand(sql, connection);

        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<MySqlConnector.MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnector.MySqlConnection(Db);
        await connection.OpenAsync();
        return connection;
    }
}
