using System.Data;
using Microsoft.Data.Sqlite;

namespace Fleet.Comms.Auth;

/// <summary>
/// The durable <see cref="IAuthStore"/>, and the one the deployable uses.
///
/// <para><b>Why a real engine and not a dictionary.</b> A store that loses device registrations on
/// restart does not implement "authentication"; it implements "authentication until the next
/// deploy", and the owner discovers the difference by being locked out of a client that has no
/// enrollment code to re-present. Registration, consumption and revocation are exactly the facts
/// that have to outlive the process holding them.</para>
///
/// <para><b>Why SQLite.</b> This boundary stores one owner, one device and a handful of short-lived
/// tokens — an upper bound of a few rows, with a write every fifteen minutes. What it needs from a
/// store is transactions, durability and a real concurrency model, and SQLite provides all three in
/// a file, with no service to run alongside it. That also means the persistence layer is exercised
/// by the ordinary test run on every machine and in CI, rather than behind a conditional skip —
/// which is the difference between a tested implementation and one that merely compiles. A
/// deployment that wants a networked engine implements <see cref="IAuthStore"/> against it; the
/// port is deliberately narrow enough that doing so is a small piece of work.</para>
///
/// <para><b>Transactions are <c>BEGIN IMMEDIATE</c>, not deferred.</b> The register path reads
/// (how many active devices?) and then writes, and a deferred transaction takes its write lock at
/// the first write — leaving a window in which two concurrent registrations both read zero and both
/// proceed. Taking the lock up front is what makes "exactly one active device" hold under
/// concurrency, and it is enforced by the engine rather than by an in-process latch that a second
/// instance of this service would not share.</para>
/// </summary>
public sealed class SqliteAuthStore : IAuthStore, IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS enrollments (
            enrollment_id TEXT PRIMARY KEY,
            code_hash     TEXT    NOT NULL,
            principal_id  TEXT    NOT NULL,
            expires_at    INTEGER NOT NULL,
            consumed_at   INTEGER NULL,
            device_id     TEXT    NULL,
            revoked_at    INTEGER NULL
        );
        CREATE TABLE IF NOT EXISTS devices (
            device_id             TEXT PRIMARY KEY,
            principal_id          TEXT    NOT NULL,
            secret_hash           TEXT    NOT NULL,
            enrollment_id         TEXT    NOT NULL,
            registered_at         INTEGER NOT NULL,
            first_token_minted_at INTEGER NULL,
            revoked_at            INTEGER NULL
        );
        CREATE TABLE IF NOT EXISTS tokens (
            token_id   TEXT PRIMARY KEY,
            token_hash TEXT    NOT NULL,
            device_id  TEXT    NOT NULL,
            expires_at INTEGER NOT NULL,
            revoked_at INTEGER NULL
        );
        CREATE INDEX IF NOT EXISTS ix_devices_principal ON devices (principal_id);
        CREATE INDEX IF NOT EXISTS ix_tokens_device ON tokens (device_id);
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    /// <summary>The one shape a backup temporary may have; see EnumerateOwnedTemporaries.</summary>
    private const string TemporarySuffix = ".tmp-";

    private const int TemporaryTokenLength = 12;

    /// <summary>The resolved absolute path, logged once at startup and used to reject self-backup.</summary>
    public string DatabasePath { get; } = "";
    private readonly string? _sessionPragmas;
    private readonly bool _allowCreate;
    private bool _initialized;

    /// <summary>
    /// Invoked between validating the written backup and publishing it, so a test can interrupt at
    /// the one point where an incomplete file could reach the destination. <c>internal</c>: this is
    /// a seam, not behaviour, and the alternative — killing a process mid-rename — is a flake.
    /// </summary>
    internal Action? OnBeforePublish { get; set; }

    public SqliteAuthStore(string databasePath, bool allowCreate = false)
        : this(databasePath, sessionPragmas: null, allowCreate)
    {
    }

    /// <summary>
    /// Fault-injection seam for tests, <c>internal</c> so it is not part of the public surface.
    ///
    /// <para><paramref name="sessionPragmas"/> runs on every connection this store opens. It exists
    /// because the failure that matters here — the engine refusing a write because it is out of
    /// space — cannot be provoked from outside a connection, and <c>PRAGMA max_page_count</c> is
    /// per-connection. Injecting a fake exception instead would test the catch block rather than
    /// the mapping, and would not have caught the real <c>SQLITE_FULL</c> escaping as a 500.</para>
    /// </summary>
    internal SqliteAuthStore(string databasePath, string? sessionPragmas, bool allowCreate = false)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("An auth store path is required.", nameof(databasePath));

        _sessionPragmas = sessionPragmas;
        _allowCreate = allowCreate;
        DatabasePath = Path.GetFullPath(databasePath);

        // ReadWrite, NOT ReadWriteCreate, unless an operator explicitly asked to create it.
        //
        // Opening with Create meant a deleted-and-recreated volume silently became a brand new
        // empty database: /ready went green, the service reported healthy, and it had forgotten
        // every device. That is the one failure the deployment document promises cannot happen
        // ("restore required, never silent re-enrollment"), and lazy schema creation was quietly
        // providing it. Creation is now `store init`, run once and on purpose.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = allowCreate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = true,
            // Microsoft.Data.Sqlite retries a busy database for this long before surfacing an
            // error, which is what lets a second concurrent registration wait for the first to
            // commit rather than failing the request.
            DefaultTimeout = 30,
        }.ToString();
    }

    public async Task<T> InTransactionAsync<T>(
        Func<IAuthStoreTransaction, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await ApplySessionPragmasAsync(connection, ct);
        }
        catch (Exception e) when (IsStoreFailure(e))
        {
            connection?.Dispose();
            throw Unavailable(e);
        }

        await using (connection)
        {
            SqliteTransaction transaction;
            try
            {
                transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            }
            catch (Exception e) when (IsStoreFailure(e))
            {
                throw Unavailable(e);
            }

            // Not inside the catch above: an exception thrown by `body` is the caller's, and must
            // reach the caller unchanged after the transaction rolls back. Only the store's own
            // failures become AuthStoreUnavailableException, because only those mean 503.
            await using (transaction)
            {
                var result = await body(new Transaction(connection, transaction), ct);

                try
                {
                    await transaction.CommitAsync(ct);
                }
                catch (Exception e) when (IsStoreFailure(e))
                {
                    throw Unavailable(e);
                }

                return result;
            }
        }
    }

    /// <summary>
    /// `VACUUM INTO`, which is SQLite's own online-backup path: it takes a read transaction, writes
    /// a fresh defragmented database, and is safe while the service is serving.
    ///
    /// <para>The destination must not already exist — SQLite refuses rather than overwriting, and
    /// that refusal is worth keeping: silently replacing yesterday's good backup with today's is
    /// how a corrupt store propagates into the only copy that could have restored it.</para>
    /// </summary>
    public async Task BackupToAsync(string destinationPath, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var destination = Path.GetFullPath(destinationPath);

        if (string.Equals(destination, DatabasePath, StringComparison.Ordinal))
            throw new BackupRefusedException("Refusing to back the auth store up over itself.");

        if (File.Exists(destination))
            throw new BackupRefusedException($"Refusing to overwrite an existing backup: {destinationPath}");

        // A CLAIM FILE, beside the destination — not the destination itself.
        //
        // Two properties are needed at once and they pull in opposite directions. Exactly one
        // concurrent winner needs an atomic exclusive create, and `File.Move(src, dst, overwrite:
        // false)` does not provide one: it tests and then renames, and a 64-way race produces two
        // winners (measured, not assumed). But claiming the DESTINATION with `FileMode.CreateNew`
        // publishes a zero-byte file the moment the claim is taken, so an interrupted backup leaves
        // an empty file under the name a restore would later trust.
        //
        // Claiming a sibling gives both: `CreateNew` is O_EXCL so exactly one caller proceeds, and
        // the destination does not exist until a complete, validated database is renamed onto it.
        // An interruption leaves a claim and a temporary, never a backup.
        var claimPath = destination + ".claim";
        var temporary = destination + TemporarySuffix + Guid.NewGuid().ToString("n")[..TemporaryTokenLength];

        FileStream claim;
        try
        {
            claim = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException e)
        {
            throw new BackupRefusedException(
                $"Another backup is already writing to {destinationPath}. If no backup is running, " +
                $"a previous one was interrupted — remove {Path.GetFileName(claimPath)} and any " +
                $"{Path.GetFileName(destination)}.tmp-* files beside it, then retry.", e);
        }

        try
        {
            await using (var connection = new SqliteConnection(_connectionString))
            {
                await connection.OpenAsync(ct);
                await ApplySessionPragmasAsync(connection, ct);

                await using var command = connection.CreateCommand();
                // Parameterised: a destination path is operator input, and concatenating it into
                // SQL would make a path containing a quote a syntax error at best.
                command.CommandText = "VACUUM INTO $destination";
                command.Parameters.AddWithValue("$destination", temporary);
                await command.ExecuteNonQueryAsync(ct);
            }

            // VALIDATED before it is published. `VACUUM INTO` either writes a complete database or
            // fails, but "the engine returned success" is not the same statement as "this file is
            // an auth store", and the whole value of a backup is what happens months later when
            // someone restores it under pressure.
            using (var written = new SqliteAuthStore(temporary))
            {
                if (!await written.IsUsableAsync(ct))
                {
                    throw new BackupRefusedException(
                        "The backup that was written is not a usable auth store; nothing was published.");
                }
            }

            OnBeforePublish?.Invoke();

            File.Move(temporary, destination, overwrite: false);

            // Sweep temporaries an earlier interrupted run left for THIS destination. They are the
            // full size of the database, so on a real store they are not a tidiness problem — and
            // the operator who was told to remove a claim file has no reason to expect a second
            // artifact beside it.
            //
            // MATCHED LITERALLY, never as a search pattern. `*` and `?` are ordinary characters in
            // a Linux filename, so passing the operator's destination name to
            // `Directory.EnumerateFiles` as a glob made a backup written to `audit-*.db` delete a
            // completed, verified backup of `audit-one.db` sitting beside it. Deleting a file the
            // operator never named — and that a restore may depend on — is the worst thing this
            // method could do, and it was doing it while cleaning up after itself.
            foreach (var stale in EnumerateOwnedTemporaries(destination))
            {
                TryDelete(stale);
                TryDelete(stale + "-wal");
                TryDelete(stale + "-shm");
            }
        }
        catch (Exception e) when (IsStoreFailure(e))
        {
            TryDelete(temporary);
            TryDelete(temporary + "-wal");
            TryDelete(temporary + "-shm");
            throw Unavailable(e);
        }
        catch
        {
            // The destination is never touched on failure: it does not exist yet.
            TryDelete(temporary);
            TryDelete(temporary + "-wal");
            TryDelete(temporary + "-shm");
            throw;
        }
        finally
        {
            claim.Dispose();
            TryDelete(claimPath);
        }
    }

    /// <summary>
    /// The temporaries this destination owns, identified by an exact prefix rather than a glob.
    ///
    /// <para>The namespace is <c>&lt;destination&gt;.tmp-&lt;12 hex&gt;</c>, and membership is
    /// decided by <see cref="string.StartsWith(string, StringComparison)"/> on the file name — so a
    /// destination whose own name contains <c>*</c> or <c>?</c> cannot reach outside itself, and a
    /// neighbouring backup with a longer name cannot be mistaken for one of ours.</para>
    /// </summary>
    private static IEnumerable<string> EnumerateOwnedTemporaries(string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            yield break;

        var prefix = Path.GetFileName(destination) + TemporarySuffix;

        // "*" is the only pattern passed to the filesystem, so nothing in the operator's filename
        // is ever interpreted. Every decision below is ordinal string comparison.
        foreach (var candidate in Directory.EnumerateFiles(directory, "*"))
        {
            var name = Path.GetFileName(candidate);
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            // Exactly our shape: the suffix, then the token, and nothing after it. This keeps the
            // WAL sidecars out of the enumeration — they are deleted explicitly by the caller,
            // which is where the intent is visible.
            var token = name[prefix.Length..];
            if (token.Length == TemporaryTokenLength && token.All(char.IsAsciiHexDigitLower))
                yield return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort. The caller is already failing; a leftover file is reported by the
            // command's own non-zero exit rather than masked by a second exception here.
        }
    }

    public void Dispose()
    {
        // Returns pooled connections to the OS so a later instance over the same file — a restart,
        // in a test — genuinely reopens it.
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
        _initGate.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initGate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            // VERIFY BEFORE MUTATING, on the non-create path. `PRAGMA journal_mode=WAL` is a
            // persistent change to the file, and running it first meant pointing the service at
            // someone else's database rewrote its journal mode before deciding it was not ours.
            if (!_allowCreate)
                await VerifySchemaAsync(connection, ct);

            await using (var pragmas = connection.CreateCommand())
            {
                // WAL so a reader is never blocked by the writer, and so a crash between statements
                // leaves a recoverable log rather than a torn page.
                pragmas.CommandText = "PRAGMA journal_mode=WAL;\nPRAGMA foreign_keys=ON;";
                await pragmas.ExecuteNonQueryAsync(ct);
            }

            if (_allowCreate)
            {
                // The ONLY path that may create schema, reached only from `store init`. A normal
                // open verifies above and never creates: `CREATE TABLE IF NOT EXISTS` running on
                // every open quietly undid the point of opening ReadWrite rather than
                // ReadWriteCreate, handing a zero-byte leftover or an unrelated file a full schema
                // and turning it into a working store with no devices.
                await using var create = connection.CreateCommand();
                create.CommandText = Schema;
                await create.ExecuteNonQueryAsync(ct);
            }
            _initialized = true;
        }
        catch (Exception e) when (IsStoreFailure(e))
        {
            // Deliberately not fatal at construction: an unreachable store is a 503 by contract
            // (§16), and a request that cannot be answered must say so rather than the process
            // refusing to start and taking every other route down with it.
            throw Unavailable(e);
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task ApplySessionPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sessionPragmas))
            return;

        await using var command = connection.CreateCommand();
        command.CommandText = _sessionPragmas;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The tables a usable auth store has, and the columns each read and write depends on.
    ///
    /// <para>Table names alone were not enough: a file can carry all three names and none of the
    /// columns — a partial restore, a hand-edited database, or a schema from a future version
    /// rolled back — and it would have opened cleanly and then failed one request at a time with
    /// an unhandled engine error instead of a clean `503`.</para>
    /// </summary>
    private static readonly (string Table, string[] Columns)[] RequiredSchema =
    [
        ("enrollments",
            ["enrollment_id", "code_hash", "principal_id", "expires_at", "consumed_at", "device_id",
             "revoked_at"]),
        ("devices",
            ["device_id", "principal_id", "secret_hash", "enrollment_id", "registered_at",
             "first_token_minted_at", "revoked_at"]),
        ("tokens", ["token_id", "token_hash", "device_id", "expires_at", "revoked_at"]),
    ];

    /// <summary>
    /// Confirm this file is an auth store, not merely a file SQLite could open.
    ///
    /// <para>Anything missing raises <see cref="AuthStoreUnavailableException"/>, so <c>/ready</c>
    /// answers `503` and the container never reports healthy — an operator sees a service that
    /// refuses to serve rather than one that serves an empty universe.</para>
    /// </summary>
    private static async Task VerifySchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        // ENGINE INTEGRITY FIRST. A schema check reads sqlite_master and PRAGMA table_info, both of
        // which live on their own pages — so a file whose enrollments pages are corrupted passes a
        // pure schema check completely, opens as a healthy store, and then fails on whichever
        // request happens to touch the damaged page. `quick_check` walks the b-trees and finds
        // that; it is a page-level check rather than the full row-by-row one, which is the right
        // trade for something on every open of a store holding a handful of rows.
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check(1)";
            var result = await integrity.ExecuteScalarAsync(ct) as string;

            if (!string.Equals(result, "ok", StringComparison.Ordinal))
            {
                // The engine's own message can name pages and tables; §13 keeps it out of the
                // response. What the operator needs is that this file is damaged.
                throw new AuthStoreUnavailableException(
                    "The auth store failed an integrity check — the file is damaged. " +
                    "Restore from a backup; do not keep serving from it.");
            }
        }

        foreach (var (table, columns) in RequiredSchema)
        {
            await using var command = connection.CreateCommand();
            // `PRAGMA table_info` returns nothing at all for a table that does not exist, so one
            // query answers both "is the table there?" and "does it have the columns?".
            command.CommandText = $"PRAGMA table_info({table})";

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    found.Add(reader.GetString(1));
            }

            if (!columns.All(found.Contains))
            {
                // No path and no column names in the message: §13, and the operator already knows
                // which store they pointed this at. What they need is that it is not one.
                throw new AuthStoreUnavailableException(
                    "The auth store schema is incomplete. Run `store init` on a new volume, " +
                    "or restore from a backup.");
            }
        }
    }

    /// <summary>
    /// Whether the file at this path is a usable auth store. Used by <c>store init</c> so a second
    /// run validates what is there rather than trusting that bytes exist.
    /// </summary>
    /// <summary>
    /// Whether the file at this path is a usable auth store.
    ///
    /// <para><paramref name="thorough"/> runs the full row-by-row <c>integrity_check</c> instead of
    /// the page-level <c>quick_check</c> an ordinary open uses. `store verify` asks for it, because
    /// the whole value of a backup is what happens months later when someone restores it under
    /// pressure, and that is worth more than the milliseconds.</para>
    /// </summary>
    public async Task<bool> IsUsableAsync(CancellationToken ct = default, bool thorough = false)
    {
        try
        {
            await EnsureInitializedAsync(ct);

            if (thorough)
            {
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check";
                if (await command.ExecuteScalarAsync(ct) as string != "ok")
                    return false;
            }

            await InTransactionAsync((tx, token) => tx.CountActiveDevicesAsync("", token), ct);
            return true;
        }
        catch (AuthStoreUnavailableException)
        {
            return false;
        }
    }

    private static bool IsStoreFailure(Exception e) => e is SqliteException or IOException;

    private static AuthStoreUnavailableException Unavailable(Exception e) =>
        // The inner exception is not attached: its message can carry a filesystem path, and §13
        // forbids error text sourced from a runtime exception reaching a client. The edge
        // middleware answers with the fixed body regardless; this keeps the path out of the log too.
        new($"The auth store is unavailable ({e.GetType().Name}).");

    private sealed class Transaction(SqliteConnection connection, SqliteTransaction transaction)
        : IAuthStoreTransaction
    {
        public Task<EnrollmentRecord?> FindEnrollmentAsync(string enrollmentId, CancellationToken ct) =>
            ReadOneAsync(
                "SELECT code_hash, principal_id, expires_at, consumed_at, device_id, revoked_at " +
                "FROM enrollments WHERE enrollment_id = $id",
                [("$id", enrollmentId)],
                reader => new EnrollmentRecord
                {
                    EnrollmentId = enrollmentId,
                    CodeHash = reader.GetString(0),
                    PrincipalId = reader.GetString(1),
                    ExpiresAt = ReadInstant(reader, 2)!.Value,
                    ConsumedAt = ReadInstant(reader, 3),
                    DeviceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    RevokedAt = ReadInstant(reader, 5),
                }, ct);

        public Task SaveEnrollmentAsync(EnrollmentRecord record, CancellationToken ct) =>
            ExecuteAsync(
                """
                INSERT INTO enrollments
                    (enrollment_id, code_hash, principal_id, expires_at, consumed_at, device_id, revoked_at)
                VALUES ($id, $hash, $principal, $expires, $consumed, $device, $revoked)
                ON CONFLICT (enrollment_id) DO UPDATE SET
                    code_hash = excluded.code_hash,
                    principal_id = excluded.principal_id,
                    expires_at = excluded.expires_at,
                    consumed_at = excluded.consumed_at,
                    device_id = excluded.device_id,
                    revoked_at = excluded.revoked_at
                """,
                [
                    ("$id", record.EnrollmentId),
                    ("$hash", record.CodeHash),
                    ("$principal", record.PrincipalId),
                    ("$expires", Instant(record.ExpiresAt)),
                    ("$consumed", Instant(record.ConsumedAt)),
                    ("$device", (object?)record.DeviceId),
                    ("$revoked", Instant(record.RevokedAt)),
                ], ct);

        public Task<DeviceRecord?> FindDeviceAsync(string deviceId, CancellationToken ct) =>
            ReadOneAsync(
                "SELECT principal_id, secret_hash, enrollment_id, registered_at, " +
                "first_token_minted_at, revoked_at FROM devices WHERE device_id = $id",
                [("$id", deviceId)],
                reader => new DeviceRecord
                {
                    DeviceId = deviceId,
                    PrincipalId = reader.GetString(0),
                    SecretHash = reader.GetString(1),
                    EnrollmentId = reader.GetString(2),
                    RegisteredAt = ReadInstant(reader, 3)!.Value,
                    FirstTokenMintedAt = ReadInstant(reader, 4),
                    RevokedAt = ReadInstant(reader, 5),
                }, ct);

        public Task SaveDeviceAsync(DeviceRecord record, CancellationToken ct) =>
            ExecuteAsync(
                """
                INSERT INTO devices
                    (device_id, principal_id, secret_hash, enrollment_id, registered_at,
                     first_token_minted_at, revoked_at)
                VALUES ($id, $principal, $hash, $enrollment, $registered, $firstMint, $revoked)
                ON CONFLICT (device_id) DO UPDATE SET
                    principal_id = excluded.principal_id,
                    secret_hash = excluded.secret_hash,
                    enrollment_id = excluded.enrollment_id,
                    registered_at = excluded.registered_at,
                    first_token_minted_at = excluded.first_token_minted_at,
                    revoked_at = excluded.revoked_at
                """,
                [
                    ("$id", record.DeviceId),
                    ("$principal", record.PrincipalId),
                    ("$hash", record.SecretHash),
                    ("$enrollment", record.EnrollmentId),
                    ("$registered", Instant(record.RegisteredAt)),
                    ("$firstMint", Instant(record.FirstTokenMintedAt)),
                    ("$revoked", Instant(record.RevokedAt)),
                ], ct);

        public async Task<int> CountActiveDevicesAsync(string principalId, CancellationToken ct)
        {
            await using var command = Command(
                "SELECT COUNT(*) FROM devices WHERE principal_id = $p AND revoked_at IS NULL",
                [("$p", principalId)]);
            return Convert.ToInt32(await Guarded(() => command.ExecuteScalarAsync(ct)));
        }

        public async Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
            string? principalId, CancellationToken ct)
        {
            var filter = principalId is null ? "" : " WHERE principal_id = $p";
            await using var command = Command(
                "SELECT device_id, principal_id, secret_hash, enrollment_id, registered_at, " +
                "first_token_minted_at, revoked_at FROM devices" + filter +
                " ORDER BY registered_at, device_id",
                principalId is null ? [] : [("$p", (object?)principalId)]);

            var devices = new List<DeviceRecord>();
            await using var reader = await Guarded(() => command.ExecuteReaderAsync(ct));
            while (await Guarded(() => reader.ReadAsync(ct)))
            {
                devices.Add(new DeviceRecord
                {
                    DeviceId = reader.GetString(0),
                    PrincipalId = reader.GetString(1),
                    SecretHash = reader.GetString(2),
                    EnrollmentId = reader.GetString(3),
                    RegisteredAt = ReadInstant(reader, 4)!.Value,
                    FirstTokenMintedAt = ReadInstant(reader, 5),
                    RevokedAt = ReadInstant(reader, 6),
                });
            }

            return devices;
        }

        public Task<TokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct) =>
            ReadOneAsync(
                "SELECT token_hash, device_id, expires_at, revoked_at FROM tokens WHERE token_id = $id",
                [("$id", tokenId)],
                reader => new TokenRecord
                {
                    TokenId = tokenId,
                    TokenHash = reader.GetString(0),
                    DeviceId = reader.GetString(1),
                    ExpiresAt = ReadInstant(reader, 2)!.Value,
                    RevokedAt = ReadInstant(reader, 3),
                }, ct);

        public Task SaveTokenAsync(TokenRecord record, CancellationToken ct) =>
            ExecuteAsync(
                """
                INSERT INTO tokens (token_id, token_hash, device_id, expires_at, revoked_at)
                VALUES ($id, $hash, $device, $expires, $revoked)
                ON CONFLICT (token_id) DO UPDATE SET
                    token_hash = excluded.token_hash,
                    device_id = excluded.device_id,
                    expires_at = excluded.expires_at,
                    revoked_at = excluded.revoked_at
                """,
                [
                    ("$id", record.TokenId),
                    ("$hash", record.TokenHash),
                    ("$device", record.DeviceId),
                    ("$expires", Instant(record.ExpiresAt)),
                    ("$revoked", Instant(record.RevokedAt)),
                ], ct);

        public async Task<int> RevokeAllEnrollmentsAsync(DateTimeOffset at, CancellationToken ct)
        {
            await using var command = Command(
                "UPDATE enrollments SET revoked_at = $at WHERE revoked_at IS NULL",
                [("$at", Instant(at))]);
            return await Guarded(() => command.ExecuteNonQueryAsync(ct));
        }

        public Task RevokeTokensForDeviceAsync(string deviceId, DateTimeOffset at, CancellationToken ct) =>
            ExecuteAsync(
                "UPDATE tokens SET revoked_at = $at WHERE device_id = $device AND revoked_at IS NULL",
                [("$at", Instant(at)), ("$device", deviceId)], ct);

        private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }

        private async Task ExecuteAsync(
            string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
        {
            await using var command = Command(sql, parameters);
            await Guarded(() => command.ExecuteNonQueryAsync(ct));
        }

        private async Task<T?> ReadOneAsync<T>(
            string sql, (string Name, object? Value)[] parameters,
            Func<SqliteDataReader, T> map, CancellationToken ct) where T : class
        {
            await using var command = Command(sql, parameters);
            await using var reader = await Guarded(() => command.ExecuteReaderAsync(ct));
            return await Guarded(() => reader.ReadAsync(ct)) ? map(reader) : null;
        }

        /// <summary>
        /// Run one statement and turn an <b>engine</b> failure into
        /// <see cref="AuthStoreUnavailableException"/>, so it reaches the client as `503` rather
        /// than `500`.
        ///
        /// <para>Opening, beginning and committing were already mapped; these were not, and the
        /// difference is not cosmetic. §5.2 tells a client that `500` means "the server is broken,
        /// report this" and `503` means "something it needs is unavailable, retry with backoff" —
        /// so a database that is out of space was telling the owner's client to stop retrying and
        /// file a bug. Demonstrated with a real `SQLITE_FULL`, not a manufactured exception.</para>
        ///
        /// <para>Deliberately narrow: it wraps statements this class issues, so an exception raised
        /// by the caller's transaction body still propagates untouched.</para>
        /// </summary>
        private static async Task<TResult> Guarded<TResult>(Func<Task<TResult>> statement)
        {
            try
            {
                return await statement();
            }
            catch (Exception e) when (IsStoreFailure(e))
            {
                throw Unavailable(e);
            }
        }

        /// <summary>
        /// Instants are stored as UTC ticks. An integer compares and round-trips exactly; a
        /// formatted string invites a parse whose offset handling is one refactor away from being
        /// wrong, on the values that decide whether a credential has expired.
        /// </summary>
        private static object? Instant(DateTimeOffset? value) => value?.UtcTicks;

        private static DateTimeOffset? ReadInstant(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal)
                ? null
                : new DateTimeOffset(reader.GetInt64(ordinal), TimeSpan.Zero);
    }
}
