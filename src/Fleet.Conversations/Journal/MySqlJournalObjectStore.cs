using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations.Journal;

/// <summary>One <c>journal_objects</c> row, as the upload and commit paths need it.</summary>
public sealed record JournalObjectRow
{
    public required string Id { get; init; }
    public required string ObjectKey { get; init; }
    public required string Owner { get; init; }
    public required string Sha256 { get; init; }
    public required long ByteSize { get; init; }
    public required string MimeType { get; init; }
    public required JournalObjectState State { get; init; }
}

/// <summary>What the status route reports about stored objects.</summary>
public readonly record struct JournalMediaStats(long Objects, long BytesStored);

/// <summary>An upload could not be opened.</summary>
public enum JournalUploadOpenRefusal { MediaDisabled, TooLarge }

/// <summary>
/// The <c>journal_objects</c> table: who may complete an upload, and which object a committed
/// attachment points at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two facts this class exists to hold, and the whole design follows from them.</b>
/// </para>
/// <list type="bullet">
///   <item><c>owner</c> binds who may <i>complete and commit</i> an upload. It is never consulted
///   for a read: read access is authorised by message observership. A subject that can name an
///   object id and cannot name the message it is attached to reads nothing.</item>
///   <item>Dedup happens <b>after</b> proof. Every upload carries its bytes, so a subject can never
///   attach — and therefore never reach — an object it never held. When a second subject proves the
///   same digest, its own object becomes the loser of dedup and is aborted; the attachment points
///   at the object that was already committed.</item>
/// </list>
/// <para>
/// ⚠️ Object keys never leave this process. A response naming an id names the row id, which is a
/// ULID with no meaning outside the database.
/// </para>
/// </remarks>
public sealed class MySqlJournalObjectStore(string connectionString, ILogger logger, TimeProvider? time = null)
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("A connection string is required.", nameof(connectionString))
        : connectionString;

    private readonly ILogger _logger = logger;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// The bucket, when media is configured. Set once by the composition that builds the media
    /// bundle; a store without it still reads and writes rows, which is what a media-less
    /// deployment does.
    /// </summary>
    public IJournalObjectStore? Bytes { get; init; }

    /// <summary>
    /// Opens an upload: a new <c>uploading</c> row owned by <paramref name="subject"/>.
    /// </summary>
    /// <remarks>
    /// <b>No dedup here, deliberately.</b> Deduplicating at declaration time would tell a caller
    /// that a digest already exists before it had proven anything, and that answer is an oracle on
    /// who has uploaded what. Two subjects declaring the same bytes get two rows and both upload.
    /// </remarks>
    public async Task<string> OpenUploadAsync(
        string subject, string sha256, long byteSize, string mimeType, CancellationToken ct = default)
    {
        var id = Ulid.NewUlid(_time.GetUtcNow());
        var now = _time.GetUtcNow().UtcDateTime;

        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            """
            INSERT INTO journal_objects
                (id, object_key, owner, sha256, committed_sha256, byte_size, mime_type,
                 state, created_at, updated_at, delete_after)
            VALUES
                (@id, @key, @owner, @sha, NULL, @size, @mime, 'uploading', @now, @now, NULL)
            """, connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@key", JournalObjectKeys.For(id));
        command.Parameters.AddWithValue("@owner", subject);
        command.Parameters.AddWithValue("@sha", sha256);
        command.Parameters.AddWithValue("@size", byteSize);
        command.Parameters.AddWithValue("@mime", mimeType);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(ct);

        return id;
    }

    /// <summary>
    /// The row an upload id names, and nothing else.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>No owner filter, and the caller supplies it.</b> A foreign-owned row must answer with
    /// the same <c>404</c> as an unknown id, so this method returns the row and the route compares
    /// <c>owner</c> itself — one answer for both cases, rather than two code paths that a future
    /// edit could make distinguishable.
    /// </remarks>
    public async Task<JournalObjectRow?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            """
            SELECT id, object_key, owner, sha256, byte_size, mime_type, state
              FROM journal_objects WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return Read(reader);
    }

    /// <summary>Holds the object row lock across a PUT, also excluding commit and sweep deletion.</summary>
    public async Task<LockedJournalObject> LockByIdAsync(string id, CancellationToken ct = default)
    {
        var connection = await OpenAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            try
            {
                await using var command = new MySqlCommand(
                    "SELECT id, object_key, owner, sha256, byte_size, mime_type, state "
                    + "FROM journal_objects WHERE id = @id FOR UPDATE", connection, transaction);
                command.Parameters.AddWithValue("@id", id);
                await using var reader = await command.ExecuteReaderAsync(ct);
                var row = await reader.ReadAsync(ct) ? Read(reader) : null;
                return new LockedJournalObject(connection, transaction, row, _time);
            }
            catch { await transaction.DisposeAsync(); throw; }
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    /// <summary>
    /// Bytes arrived and matched: <c>uploading</c> → <c>uploaded</c>.
    /// </summary>
    /// <remarks>
    /// Guarded on <c>owner</c> and on the state being <c>uploading</c>, so a retried PUT after a
    /// sweep or after a commit cannot resurrect a row. Zero rows affected is the caller's answer
    /// that this upload is no longer live.
    /// </remarks>
    public async Task<bool> MarkUploadedAsync(
        string id, string owner, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            """
            UPDATE journal_objects
               SET state = 'uploaded', updated_at = @now
             WHERE id = @id AND owner = @owner AND state = 'uploading'
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@owner", owner);
        command.Parameters.AddWithValue("@now", _time.GetUtcNow().UtcDateTime);

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// The bytes did not match the declaration: <c>aborted</c>, so the sweeper deletes them.
    /// </summary>
    /// <remarks>
    /// A mismatch is not a reason to delete synchronously — the caller has already answered the
    /// client and a bucket failure must not turn a <c>422</c> into a <c>500</c>. The row is parked
    /// and the sweep converges.
    /// </remarks>
    /// <remarks>
    /// Guarded on <c>uploading</c> and on owner: this is the PUT-time mismatch path, and the only
    /// state a mismatch can leave a row in. Widening it would let a stale caller pull a
    /// <c>committed</c> or <c>deleting</c> object out from under a message that points at it.
    /// </remarks>
    public async Task MarkAbortedAsync(string id, string owner, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            """
            UPDATE journal_objects
               SET state = 'aborted', updated_at = @now
             WHERE id = @id AND owner = @owner AND state = 'uploading'
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@owner", owner);
        command.Parameters.AddWithValue("@now", _time.GetUtcNow().UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Delete one object's bytes by key. The row is already gone — this is the half that cannot run
    /// inside the transaction that removed it.
    /// </summary>
    /// <remarks>
    /// A missing object is success, like every other delete here: the sweeper may have got to the
    /// key first, and that is the same end state.
    /// </remarks>
    public Task DeleteBytesAsync(string objectKey, CancellationToken ct = default)
    {
        return Bytes is null
            ? Task.FromException(new InvalidOperationException(
                "media_bytes_absent: this store has no object store to delete from."))
            : Bytes.DeleteAsync(objectKey, ct);
    }

    /// <summary>Rows the retention sweep found past their horizon; the sweeper deletes their bytes.</summary>
    public const string ObjectPrefix = JournalObjectKeys.Prefix;

    /// <summary>
    /// Object and byte counts for the status route.
    /// </summary>
    /// <remarks>
    /// <b>A <c>SUM</c> over the table, not a listing over the bucket.</b> The bucket is O(objects)
    /// and would put a full <c>ListObjectsV2</c> in front of every status poll; the table is what
    /// the deployment actually considers stored, and it is the number a backup is checked against.
    /// <c>committed</c> and <c>deleting</c> both count: a retired object's bytes are still there and
    /// a restore still needs them.
    /// </remarks>
    public async Task<JournalMediaStats> ReadStatsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            """
            SELECT COUNT(*), COALESCE(SUM(byte_size), 0)
              FROM journal_objects WHERE state IN ('committed','deleting')
            """, connection);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new JournalMediaStats(0, 0);

        return reader.IsDBNull(0) || reader.IsDBNull(1)
            ? new JournalMediaStats(0, 0)
            : new JournalMediaStats(reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>
    /// Every object the backup must have: <c>committed</c> and <c>deleting</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>deleting</c> is included on purpose. A retired object's bytes are still in the bucket for
    /// <c>delete_after</c> hours, and a restore taken an hour before the sweeper gets it must be
    /// able to reproduce the journal as it was at the moment the backup ran. Excluding them would
    /// make a restore silently lose attachments that were still readable.
    /// </para>
    /// <para>
    /// <c>uploading</c>, <c>uploaded</c> and <c>aborted</c> are excluded because nothing references
    /// them: they are either in flight or already declared dead, and a backup that carried them
    /// would report byte counts the journal does not consider stored.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<JournalObjectBackupRow>> ListBackupRowsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            """
            SELECT object_key, sha256, byte_size, mime_type, state
              FROM journal_objects
             WHERE state IN ('committed','deleting')
             ORDER BY object_key
            """, connection);

        var rows = new List<JournalObjectBackupRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new JournalObjectBackupRow
            {
                ObjectKey = reader.GetString(0),
                Sha256 = reader.GetString(1),
                ByteSize = reader.GetInt64(2),
                MimeType = reader.GetString(3),
                State = reader.GetString(4),
            });
        }

        return rows;
    }

    /// <summary>
    /// The objects a committed attachment actually points at, with the digest they must match.
    /// </summary>
    /// <remarks>
    /// This is the set <c>journal verify-media</c> walks. It joins through <c>journal_attachments</c>
    /// rather than reading the object table alone, because the question the operator is asking is
    /// "can every attachment a human might fetch still be fetched" — an orphaned <c>committed</c>
    /// row with no attachment is a sweeper problem, not a broken attachment.
    /// </remarks>
    public async Task<IReadOnlyList<JournalAttachmentObjectRow>> ListAttachmentObjectsAsync(
        int? sample, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            sample is > 0
                ? """
                  SELECT a.id, o.object_key, o.sha256, o.byte_size
                    FROM journal_attachments a
                    JOIN journal_objects o ON o.id = a.object_id
                   WHERE a.state = 'committed'
                   ORDER BY a.id
                   LIMIT @sample
                  """
                : """
                  SELECT a.id, o.object_key, o.sha256, o.byte_size
                    FROM journal_attachments a
                    JOIN journal_objects o ON o.id = a.object_id
                   WHERE a.state = 'committed'
                   ORDER BY a.id
                  """, connection);

        if (sample is > 0)
            command.Parameters.AddWithValue("@sample", sample.Value);

        var rows = new List<JournalAttachmentObjectRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new JournalAttachmentObjectRow
            {
                AttachmentId = reader.GetString(0),
                ObjectKey = reader.GetString(1),
                Sha256 = reader.GetString(2),
                ByteSize = reader.GetInt64(3),
            });
        }

        return rows;
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    internal static JournalObjectRow Read(MySqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ObjectKey = reader.GetString(1),
        Owner = reader.GetString(2),
        Sha256 = reader.GetString(3),
        ByteSize = reader.GetInt64(4),
        MimeType = reader.GetString(5),
        State = JournalMediaWire.TryParse(reader.GetString(6), out var state)
            ? state
            : throw new InvalidOperationException("journal_objects row with an unknown state"),
    };

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var builder = new MySqlConnectionStringBuilder(_connectionString);
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

/// <summary>
/// Where a journal object lives in the bucket.
/// </summary>
/// <remarks>
/// <para>
/// One flat prefix, <c>j1/</c>, and the key is the row id — <b>never the digest</b>. A digest-keyed
/// key would make the key itself a fact about content, so a listing leak would disclose what the
/// journal holds even without the bytes, and two subjects uploading the same bytes would collide on
/// a key before either had committed.
/// </para>
/// <para>The prefix is also the boundary the orphan sweep lists within: keys outside it are not
/// this table's business and are never deleted.</para>
/// </remarks>
public static class JournalObjectKeys
{
    public const string Prefix = "j1/";

    public static string For(string id) => Prefix + id;

    /// <summary>True when <paramref name="key"/> is one this deployment wrote.</summary>
    public static bool IsJournalKey(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>One row of the backup manifest: what the bucket must hold.</summary>
public sealed record JournalObjectBackupRow
{
    public required string ObjectKey { get; init; }
    public required string Sha256 { get; init; }
    public required long ByteSize { get; init; }
    public required string MimeType { get; init; }

    /// <summary><c>committed</c> or <c>deleting</c>; recorded so a restore can report what it restored.</summary>
    public required string State { get; init; }
}

/// <summary>One committed attachment and the object its bytes must come from.</summary>
public sealed record JournalAttachmentObjectRow
{
    public required string AttachmentId { get; init; }
    public required string ObjectKey { get; init; }
    public required string Sha256 { get; init; }
    public required long ByteSize { get; init; }
}
