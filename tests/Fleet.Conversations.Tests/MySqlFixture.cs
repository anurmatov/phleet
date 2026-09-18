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
}

[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>;
