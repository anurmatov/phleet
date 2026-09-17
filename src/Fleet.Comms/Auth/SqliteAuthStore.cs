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
    private bool _initialized;

    public SqliteAuthStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("An auth store path is required.", nameof(databasePath));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
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

            await using var command = connection.CreateCommand();
            // WAL so a reader is never blocked by the writer, and so a crash between statements
            // leaves a recoverable log rather than a torn page.
            command.CommandText = "PRAGMA journal_mode=WAL;\nPRAGMA foreign_keys=ON;\n" + Schema;
            await command.ExecuteNonQueryAsync(ct);

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
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
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
            await command.ExecuteNonQueryAsync(ct);
        }

        private async Task<T?> ReadOneAsync<T>(
            string sql, (string Name, object? Value)[] parameters,
            Func<SqliteDataReader, T> map, CancellationToken ct) where T : class
        {
            await using var command = Command(sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? map(reader) : null;
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
