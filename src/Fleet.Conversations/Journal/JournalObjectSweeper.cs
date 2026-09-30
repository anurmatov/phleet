using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations.Journal;

/// <summary>
/// The journal object sweeper: three classes of bytes that must not stay in the bucket.
/// </summary>
/// <remarks>
/// <para>
/// <b>(a) Abandoned uploads.</b> <c>uploading</c>, <c>uploaded</c> and <c>aborted</c> older than
/// the abandon window. An upload whose subject never committed, and a dedup loser, both end here.
/// </para>
/// <para>
/// <b>(b) Retired objects.</b> <c>deleting</c> past <c>delete_after</c>. Retention removes the
/// message and marks the object; the bytes wait out a grace window so a backup taken before the
/// mark still restores to something whole.
/// </para>
/// <para>
/// <b>(c) Orphans.</b> Weekly: keys under the journal prefix with no row at all. A delete whose row
/// removal succeeded but whose bucket call failed, a row deleted by hand, a partial restore — this
/// is the sweep that makes the bucket converge on the table when the two disagree.
/// </para>
/// <para>
/// ⚠️ <b>An S3 error leaves the row in place, is counted, and is retried next tick.</b> Never the
/// other order: deleting the row because the bucket was unreachable would orphan the object
/// permanently, and (c) cannot find it afterwards because (c) works from keys that have no row —
/// which is exactly what that would create.
/// </para>
/// <para>
/// ⚠️ Class (c) never trusts a truncated listing. <see cref="IJournalObjectStore.ListAsync"/>
/// follows pagination to the end and throws rather than returning a partial page; a partial page
/// is the one input that would delete live objects.
/// </para>
/// </remarks>
public sealed class JournalObjectSweeper(
    string connectionString,
    IJournalObjectStore objects,
    ILogger logger,
    JournalRuntimeStats? stats = null,
    TimeProvider? time = null)
{
    /// <summary>How long an uncommitted object is kept.</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a retired object's bytes are kept. Defined once, on
    /// <see cref="JournalRetention.DeleteGrace"/>, which is the code that sets the deadline.
    /// </summary>
    public static readonly TimeSpan DeleteGrace = JournalRetention.DeleteGrace;

    /// <summary>Upper bound on rows touched per class per tick, so a backlog cannot run unbounded.</summary>
    private const int MaxRowsPerClass = 500;

    /// <summary>How often class (c) runs. A full listing is the one O(bucket) step here.</summary>
    public static readonly TimeSpan OrphanInterval = TimeSpan.FromDays(7);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>What one tick removed, by class.</summary>
    public sealed record SweepResult(int Abandoned, int Retired, int Orphans, int Failures)
    {
        public static SweepResult Empty { get; } = new(0, 0, 0, 0);
    }

    private DateTimeOffset _nextOrphanSweep = DateTimeOffset.MinValue;

    public async Task<SweepResult> SweepOnceAsync(CancellationToken ct = default)
    {
        var (abandonedCount, abandonedFailures) = await SweepClassAsync(
            "SELECT id, object_key FROM journal_objects "
            + "WHERE state IN ('uploading','uploaded','aborted') AND created_at < @cutoff "
            + "ORDER BY created_at, id LIMIT @limit",
            _time.GetUtcNow().UtcDateTime - AbandonAfter, "abandoned", ct);

        var (retiredCount, retiredFailures) = await SweepClassAsync(
            "SELECT id, object_key FROM journal_objects "
            + "WHERE state = 'deleting' AND delete_after IS NOT NULL AND delete_after < @cutoff "
            + "ORDER BY delete_after, id LIMIT @limit",
            _time.GetUtcNow().UtcDateTime, "retired", ct);

        var orphans = 0;
        var failures = abandonedFailures + retiredFailures;

        // Weekly, and only when the two row-driven sweeps are healthy. Running the listing behind
        // a failing delete would compare the bucket against a table that is not yet settled.
        if (failures == 0 && _time.GetUtcNow() >= _nextOrphanSweep)
        {
            _nextOrphanSweep = _time.GetUtcNow() + OrphanInterval;
            var (count, failed) = await SweepOrphansAsync(ct);
            orphans = count;
            failures += failed;
        }

        if (abandonedCount + retiredCount + orphans > 0)
        {
            logger.LogInformation(
                "journal object sweep removed {Abandoned} abandoned, {Retired} retired "
                + "and {Orphans} orphan object(s)",
                abandonedCount, retiredCount, orphans);
        }

        stats?.RecordObjectSweep(abandonedCount, retiredCount, orphans, failures);

        return new SweepResult(abandonedCount, retiredCount, orphans, failures);
    }

    /// <summary>
    /// One row-driven class: is it still referenced, then bytes, then the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference guard comes first precisely because a referenced object is not doomed. Bytes
    /// before the row is still the order for the two steps that ARE a deletion: a failure between
    /// them leaves a row with no object, which the next tick deletes, because
    /// <see cref="IJournalObjectStore.DeleteAsync"/> treats a missing object as success. Row before
    /// bytes would leave an object with no row, findable only by the weekly orphan sweep.
    /// </para>
    /// <para>
    /// Neither order protects an object that an attachment still points at, so that question is
    /// answered before any of it. See the guard in the loop.
    /// </para>
    /// </remarks>
    private async Task<(int Removed, int Failures)> SweepClassAsync(
        string sql, DateTime cutoff, string metricClass, CancellationToken ct)
    {
        var doomed = new List<(string Id, string Key)>();

        await using (var connection = await OpenAsync(ct))
        await using (var command = new MySqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@cutoff", cutoff);
            command.Parameters.AddWithValue("@limit", MaxRowsPerClass);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                doomed.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (doomed.Count == 0) return (0, 0);

        var removed = 0;
        var failures = 0;

        foreach (var (id, key) in doomed)
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            // Share commit/PUT row locks and recheck the candidate after acquiring the lock.
            var eligible = metricClass == "abandoned"
                ? "state IN ('uploading','uploaded','aborted') AND created_at < @cutoff"
                : "state = 'deleting' AND delete_after IS NOT NULL AND delete_after < @cutoff";
            await using (var lockRow = new MySqlCommand(
                $"SELECT object_key FROM journal_objects WHERE id = @id AND {eligible} FOR UPDATE", connection, transaction))
            {
                lockRow.Parameters.AddWithValue("@id", id);
                lockRow.Parameters.AddWithValue("@cutoff", cutoff);
                if (await lockRow.ExecuteScalarAsync(ct) is null) continue;
            }

            // ⚠️ A live attachment is an absolute stop, for every class, and it is checked BEFORE
            //    the bucket delete. A dedup loser is exactly the case that makes this load-bearing:
            //    its own subject committed against it, lost dedup, and its attachment row now points
            //    at the WINNER while the loser's row goes stale.
            //
            //    The previous order was bytes-first, and it was wrong: once DeleteAsync succeeds
            //    there is nothing left to skip. The row survives (the FK refuses it), the guard logs
            //    "leaving it for retention", and the next tick repeats the delete on an object that
            //    is already gone — permanently un-downloadable media behind a row that reads healthy.
            //    That is the one failure this sweep must never produce, and the "row then bytes"
            //    trade-off below does not apply here: a referenced object is not doomed at all, so
            //    there is no orphan to create by refusing early.
            await using (var referenced = new MySqlCommand(
                "SELECT 1 FROM journal_attachments WHERE object_id = @id LIMIT 1", connection, transaction))
            {
                referenced.Parameters.AddWithValue("@id", id);
                if (await referenced.ExecuteScalarAsync(ct) is not null)
                {
                    logger.LogWarning(
                        "journal object {Id} passed its {Class} deadline but is still referenced "
                        + "by an attachment; leaving it and its bytes for retention", id, metricClass);
                    continue;
                }
            }

            try
            {
                await objects.DeleteAsync(key, ct);
            }
            catch (JournalObjectStoreUnavailableException)
            {
                // Row untouched. Counted, retried next tick.
                failures++;
                continue;
            }

            await using var delete = new MySqlCommand(
                "DELETE FROM journal_objects WHERE id = @id", connection, transaction);
            delete.Parameters.AddWithValue("@id", id);
            removed += await delete.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }

        if (failures > 0)
            logger.LogWarning("journal object sweep deferred {Failures} {Class} object(s) on a store error",
                failures, metricClass);

        ConversationMetrics.JournalOrphansDeleted.Add(
            removed, new KeyValuePair<string, object?>("class", metricClass));

        return (removed, failures);
    }

    /// <summary>
    /// Keys under the journal prefix with no row, older than the abandon window.
    /// </summary>
    /// <remarks>
    /// ⚠️ The age bound is not an optimisation. An object created between the listing and the delete
    /// has no row yet — the row is inserted by <c>OpenUploadAsync</c> before the PUT, but a listing
    /// that raced a restore or a write could still miss it. Deleting a young key is how a sweep
    /// eats an upload in flight.
    /// </remarks>
    private async Task<(int Count, int Failures)> SweepOrphansAsync(CancellationToken ct)
    {
        IReadOnlyList<JournalObjectListing> keys;
        try
        {
            keys = await objects.ListAsync(JournalObjectKeys.Prefix, ct);
        }
        catch (JournalObjectStoreUnavailableException)
        {
            logger.LogWarning("journal orphan sweep deferred on a store error");
            return (0, 1);
        }

        var cutoff = _time.GetUtcNow() - AbandonAfter;
        var candidates = keys
            .Where(k => JournalObjectKeys.IsJournalKey(k.Key) && k.LastModified < cutoff)
            .Select(k => k.Key)
            .ToList();

        if (candidates.Count == 0) return (0, 0);

        // One query for the whole candidate set rather than one per key: a bucket with thousands of
        // orphans must not cost thousands of round trips on the maintenance tick.
        var known = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = await OpenAsync(ct))
        {
            foreach (var batch in candidates.Chunk(200))
            {
                var names = batch
                    .Select((_, i) => $"@k{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                    .ToList();

                await using var command = new MySqlCommand(
                    $"SELECT object_key FROM journal_objects WHERE object_key IN ({string.Join(", ", names)})",
                    connection);

                for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue(names[i], batch[i]);

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) known.Add(reader.GetString(0));
            }
        }

        var removed = 0;
        var failures = 0;

        foreach (var key in candidates.Where(k => !known.Contains(k)))
        {
            try
            {
                await objects.DeleteAsync(key, ct);
                removed++;
            }
            catch (JournalObjectStoreUnavailableException)
            {
                failures++;
            }
        }

        if (removed > 0)
            ConversationMetrics.JournalOrphansDeleted.Add(
                removed, new KeyValuePair<string, object?>("class", "orphan"));

        return (removed, failures);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        builder.ConnectionTimeout = Math.Min(builder.ConnectionTimeout, 5);
        if (builder.DefaultCommandTimeout is 0 or > 10) builder.DefaultCommandTimeout = 10;

        var connection = new MySqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
