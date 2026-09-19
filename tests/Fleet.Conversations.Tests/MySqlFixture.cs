using Fleet.Protocol;
using Fleet.Conversations;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Fleet.Conversations.Tests;

/// <summary>
/// A migrated, disposable MySQL schema for one test class.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This fixture FAILS when the database is absent. It never skips.</b>
/// </para>
/// <para>
/// A suite that skipped for want of a connection string is indistinguishable from a passing one in
/// a run summary — "12 passed" reads the same whether it exercised MySQL or quietly exercised
/// nothing. That is precisely how a missing gate gets shipped, so the absence of a database is a
/// hard failure with a message naming the variable, and CI provides a real
/// <c>mysql:8.0</c> service container rather than letting the job go green without one.
/// </para>
/// </remarks>
public sealed class MySqlFixture : IAsyncLifetime
{
    /// <summary>Connection string with DDL rights, used to create and migrate the schema.</summary>
    public const string MigrationVariable = "FLEET_CONVERSATIONS_MIGRATION_CONNECTION";

    /// <summary>
    /// Optional. When set, the store is exercised through a DDL-less account, which is what the
    /// deployment actually runs as.
    /// </summary>
    public const string RuntimeVariable = "FLEET_CONVERSATIONS_CONNECTION";

    private string _adminConnectionString = string.Empty;
    private string _database = string.Empty;

    /// <summary>Connection string the store under test uses.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Connection string the migration runner uses.</summary>
    public string MigrationConnectionString { get; private set; } = string.Empty;

    public ConversationStoreOptions Options { get; } = new();

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(MigrationVariable);

        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{MigrationVariable} is not set, so these tests have no database to run against.\n\n"
                + "  This FAILS rather than skipping on purpose. A skipped integration suite looks\n"
                + "  identical to a passing one in a run summary, which is how a gate that never\n"
                + "  executed gets shipped as evidence that it did.\n\n"
                + "  CI supplies a mysql:8.0 service container. Locally, point it at any MySQL 8.0:\n"
                + $"    export {MigrationVariable}='Server=127.0.0.1;Port=3306;User ID=root;Password=…;'\n"
                + $"  and optionally {RuntimeVariable} for a DDL-less runtime account.");

        _adminConnectionString = configured;

        // One disposable schema per fixture, so classes cannot see each other's rows and a failed
        // run leaves nothing behind for the next one to trip over.
        _database = $"comms_test_{Ulid.NewUlid().ToLowerInvariant()}";

        var builder = new MySqlConnectionStringBuilder(_adminConnectionString) { Database = string.Empty };

        await using (var connection = new MySqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new MySqlCommand(
                $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4", connection);
            await create.ExecuteNonQueryAsync();
        }

        MigrationConnectionString = new MySqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _database,
        }.ConnectionString;

        var runtime = Environment.GetEnvironmentVariable(RuntimeVariable);

        ConnectionString = string.IsNullOrWhiteSpace(runtime)
            ? MigrationConnectionString
            : new MySqlConnectionStringBuilder(runtime) { Database = _database }.ConnectionString;

        await new MigrationRunner(MigrationConnectionString).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database.Length == 0) return;

        var builder = new MySqlConnectionStringBuilder(_adminConnectionString) { Database = string.Empty };

        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new MySqlCommand($"DROP DATABASE IF EXISTS `{_database}`", connection);
        await drop.ExecuteNonQueryAsync();
    }

    public MySqlConversationStore CreateStore(string? owner = null) =>
        new(ConnectionString, Options, NullLogger<MySqlConversationStore>.Instance,
            owner ?? "fleet-comms:test");

    /// <summary>Reads one row as pipe-joined values, for asserting on stored state directly.</summary>
    public async Task<string> ScalarRowAsync(string sql)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return string.Empty;

        return string.Join("|", Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString()));
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // ── scratch databases and accounts, for the migration and operator suites ────────

    /// <summary>
    /// An additional empty, UNMIGRATED database. The class fixture's own schema is migrated during
    /// setup, which proves apply-from-empty and nothing else; every question about what the runner
    /// does on a fresh store, or refuses to do, needs a database it has not touched.
    /// </summary>
    public async Task<ScratchDatabase> CreateScratchDatabaseAsync()
    {
        var name = $"comms_scratch_{Ulid.NewUlid().ToLowerInvariant()}";

        await using (var connection = new MySqlConnection(ServerConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new MySqlCommand(
                $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new ScratchDatabase(name, WithDatabase(_adminConnectionString, name), this);
    }

    /// <summary>
    /// A DML-only account on <paramref name="database"/> — SELECT/INSERT/UPDATE/DELETE and no DDL
    /// grant at all, which is what the deployment's runtime account is.
    /// </summary>
    /// <remarks>
    /// Created here rather than taken from <c>FLEET_CONVERSATIONS_CONNECTION</c> so the property is
    /// asserted in every environment. CI provisions exactly this split and runs the whole store
    /// suite through it; a local run without that variable would otherwise fall back to the DDL
    /// account and the assertion would pass by testing nothing.
    /// </remarks>
    public async Task<RestrictedAccount> CreateDmlOnlyAccountAsync(string database)
    {
        var user = $"c_dml_{Ulid.NewUlid()[..16].ToLowerInvariant()}";
        const string password = "dml-only-test";

        await using (var connection = new MySqlConnection(ServerConnectionString()))
        {
            await connection.OpenAsync();

            foreach (var sql in new[]
            {
                $"CREATE USER '{user}'@'%' IDENTIFIED BY '{password}'",
                $"GRANT SELECT, INSERT, UPDATE, DELETE ON `{database}`.* TO '{user}'@'%'",
                "FLUSH PRIVILEGES",
            })
            {
                await using var command = new MySqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        var builder = new MySqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = database,
            UserID = user,
            Password = password,
        };

        return new RestrictedAccount(user, builder.ConnectionString, this);
    }

    internal async Task DropDatabaseAsync(string name)
    {
        await using var connection = new MySqlConnection(ServerConnectionString());
        await connection.OpenAsync();
        await using var drop = new MySqlCommand($"DROP DATABASE IF EXISTS `{name}`", connection);
        await drop.ExecuteNonQueryAsync();
    }

    internal async Task DropAccountAsync(string user)
    {
        await using var connection = new MySqlConnection(ServerConnectionString());
        await connection.OpenAsync();
        await using var drop = new MySqlCommand($"DROP USER IF EXISTS '{user}'@'%'", connection);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Reads one row from an arbitrary database, for asserting on a scratch schema.</summary>
    public static async Task<string> ScalarRowOnAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return string.Empty;

        return string.Join("|", Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString()));
    }

    public static async Task ExecuteOnAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opens a connection and holds a SHARED lock on the conversation row until disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared, not exclusive, and that is the whole point. An exclusive lock blocks the accept
    /// whether or not TX1a asks for one, because inserting a submission takes a shared lock on its
    /// parent row for the foreign key — so a test built on <c>FOR UPDATE</c> passes with the
    /// store's own lock removed, which is exactly what the first version of it did.
    /// </para>
    /// <para>
    /// A shared lock is compatible with that foreign-key check and incompatible with
    /// <c>SELECT … FOR UPDATE</c>, so it blocks the accept if and only if TX1a really asks for the
    /// row exclusively.
    /// </para>
    /// </remarks>
    public async Task<HeldRowLock> HoldSharedConversationLockAsync(string conversationId)
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();

        var transaction = await connection.BeginTransactionAsync();

        await using var command = new MySqlCommand(
            "SELECT id FROM conversations WHERE id = @id LOCK IN SHARE MODE", connection, transaction);
        command.Parameters.AddWithValue("@id", conversationId);
        await command.ExecuteScalarAsync();

        return new HeldRowLock(connection, transaction);
    }

    private string ServerConnectionString() =>
        new MySqlConnectionStringBuilder(_adminConnectionString) { Database = string.Empty }
            .ConnectionString;

    private static string WithDatabase(string connectionString, string database) =>
        new MySqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;
}

/// <summary>An empty database that drops itself.</summary>
public sealed class ScratchDatabase(string name, string connectionString, MySqlFixture fixture)
    : IAsyncDisposable
{
    public string Name { get; } = name;

    /// <summary>DDL-capable connection string, scoped to this database.</summary>
    public string ConnectionString { get; } = connectionString;

    public ValueTask DisposeAsync() => new(fixture.DropDatabaseAsync(Name));
}

/// <summary>A DML-only account that drops itself.</summary>
public sealed class RestrictedAccount(string user, string connectionString, MySqlFixture fixture)
    : IAsyncDisposable
{
    public string User { get; } = user;

    public string ConnectionString { get; } = connectionString;

    public ValueTask DisposeAsync() => new(fixture.DropAccountAsync(User));
}

/// <summary>A conversation row lock held by a second connection, released on disposal.</summary>
public sealed class HeldRowLock(MySqlConnection connection, MySqlTransaction transaction)
    : IAsyncDisposable
{
    private bool _released;

    /// <summary>Idempotent: a test releases the lock explicitly, then <c>await using</c> runs too.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;

        await transaction.RollbackAsync();
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}

[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>;
