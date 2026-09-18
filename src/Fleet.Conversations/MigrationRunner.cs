using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace Fleet.Conversations;

/// <summary>One forward-only migration script, embedded in the assembly.</summary>
public sealed record MigrationScript
{
    public required int Version { get; init; }
    public required string Name { get; init; }
    public required string Sql { get; init; }
    public required string Checksum { get; init; }
}

/// <summary>What the database says about its schema, against what this binary expects.</summary>
public sealed record SchemaStatus
{
    /// <summary>Highest applied version, or null when the store has never been migrated.</summary>
    public int? AppliedVersion { get; init; }

    public required int ExpectedVersion { get; init; }

    public bool Matches => AppliedVersion == ExpectedVersion;

    /// <summary>
    /// True when the database is AHEAD of this binary — the rollback-after-migration case.
    /// </summary>
    /// <remarks>
    /// As unhealthy as being behind, and deliberately distinguishable from it: a binary rolled back
    /// after a migration must refuse rather than write rows a newer schema wrote differently.
    /// </remarks>
    public bool IsAhead => AppliedVersion is { } applied && applied > ExpectedVersion;

    public bool IsBehind => AppliedVersion is not { } applied || applied < ExpectedVersion;

    public string Describe() => AppliedVersion switch
    {
        null => $"no schema applied; this binary expects version {ExpectedVersion}",
        var a when a == ExpectedVersion => $"schema version {a} matches this binary",
        var a when a > ExpectedVersion =>
            $"schema version {a} is AHEAD of this binary, which expects {ExpectedVersion} "
            + "— this is the rollback-after-migration case",
        var a => $"schema version {a} is BEHIND this binary, which expects {ExpectedVersion}",
    };
}

/// <summary>Raised when the schema cannot be advanced safely. Never a retryable condition.</summary>
public sealed class MigrationException(string message) : Exception(message);

/// <summary>
/// Applies forward-only numbered migration scripts, and reports what has been applied.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ NEVER invoked as a startup side effect. A process that migrates on boot turns a deployment
/// mistake into an irreversible schema change, and removes the operator's chance to take a backup
/// first. It is reached only through the <c>conversations migrate</c> operator subcommand, which is
/// dispatched before any web application is built.
/// </para>
/// <para>
/// It takes the DDL connection string, which the running service does not have. The runtime account
/// holds SELECT/INSERT/UPDATE/DELETE on this schema and no DDL grants at all — so running a
/// migration with it fails at the database rather than succeeding quietly, and that is an asserted
/// property rather than an assumed one.
/// </para>
/// </remarks>
public sealed class MigrationRunner
{
    private readonly string _connectionString;

    public MigrationRunner(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A migration connection string is required.", nameof(connectionString));

        _connectionString = connectionString;
    }

    /// <summary>The scripts this binary carries, in version order.</summary>
    public static IReadOnlyList<MigrationScript> Scripts { get; } = LoadScripts();

    /// <summary>The version this binary expects the database to be at.</summary>
    public static int ExpectedVersion { get; } = Scripts.Count == 0 ? 0 : Scripts[^1].Version;

    private static IReadOnlyList<MigrationScript> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => ReadScript(assembly, n))
            .OrderBy(s => s.Version)
            .ToList();
    }

    private static MigrationScript ReadScript(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new MigrationException($"embedded migration '{resourceName}' could not be opened");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sql = reader.ReadToEnd();

        // The file name carries the version as a zero-padded integer prefix. Resource names are
        // dotted, so the script file name is the last two segments: "0001_initial_schema" + ".sql".
        var parts = resourceName.Split('.');
        var fileName = parts.Length >= 2 ? parts[^2] : resourceName;
        var prefix = new string(fileName.TakeWhile(char.IsDigit).ToArray());

        if (prefix.Length == 0 || !int.TryParse(prefix, out var version))
            throw new MigrationException(
                $"migration '{fileName}' has no leading integer version. A version is the "
                + "zero-padded integer prefix of its script filename.");

        return new MigrationScript
        {
            Version = version,
            Name = fileName,
            Sql = sql,
            Checksum = Sha256(sql),
        };
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>
    /// Creates the bookkeeping table if it is absent. It is created by the runner rather than by a
    /// migration script, because a script cannot record its own application before the table that
    /// records it exists.
    /// </summary>
    private static async Task EnsureBookkeepingAsync(MySqlConnection connection, CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version     INT UNSIGNED NOT NULL,
              script_name VARCHAR(191) NOT NULL,
              checksum    CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
              applied_at  DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
              PRIMARY KEY (version)
            ) ENGINE=InnoDB
            """;

        await using var command = new MySqlCommand(ddl, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Reads the applied version without creating anything.</summary>
    public async Task<SchemaStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadStatusAsync(connection, ct);
    }

    private static async Task<SchemaStatus> ReadStatusAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            """
            SELECT MAX(version) FROM schema_migrations
            WHERE EXISTS (SELECT 1 FROM information_schema.tables
                          WHERE table_schema = DATABASE() AND table_name = 'schema_migrations')
            """,
            connection);

        object? applied;
        try
        {
            applied = await command.ExecuteScalarAsync(ct);
        }
        catch (MySqlException)
        {
            // The table does not exist yet, which is simply "never migrated".
            applied = null;
        }

        return new SchemaStatus
        {
            AppliedVersion = applied is null or DBNull ? null : Convert.ToInt32(applied),
            ExpectedVersion = ExpectedVersion,
        };
    }

    /// <summary>
    /// Applies every script above the applied version, in order, and records each.
    /// </summary>
    /// <returns>The versions applied by this invocation; empty when already up to date.</returns>
    public async Task<IReadOnlyList<int>> MigrateAsync(
        Action<string>? report = null, CancellationToken ct = default)
    {
        report ??= _ => { };

        await using var connection = await OpenAsync(ct);
        await EnsureBookkeepingAsync(connection, ct);

        var recorded = await ReadRecordedAsync(connection, ct);

        // An already-applied script whose bytes have changed is a DIFFERENT migration. Applying the
        // difference silently is how two deployments end up carrying the same version number and
        // different schemas — so this refuses by name rather than re-applying or ignoring it.
        foreach (var script in Scripts)
        {
            if (!recorded.TryGetValue(script.Version, out var applied))
                continue;

            if (!string.Equals(applied.Checksum, script.Checksum, StringComparison.Ordinal))
                throw new MigrationException(
                    $"migration {script.Version} ('{script.Name}') has already been applied, but its "
                    + "contents have changed since.\n\n"
                    + $"  recorded checksum: {applied.Checksum}\n"
                    + $"  script checksum:   {script.Checksum}\n\n"
                    + "  An edited migration is a different migration. This refuses rather than\n"
                    + "  re-applying or ignoring it, because applying the difference silently is how\n"
                    + "  two deployments end up at the same version number with different schemas.\n"
                    + "  Add a new forward-only script instead of editing this one.");
        }

        var pending = Scripts.Where(s => !recorded.ContainsKey(s.Version)).ToList();

        if (pending.Count == 0)
        {
            report($"already at version {ExpectedVersion}; nothing to apply");
            return [];
        }

        var appliedNow = new List<int>();

        foreach (var script in pending)
        {
            report($"applying {script.Version} ({script.Name})");

            // One transaction per script. A script that fails part-way leaves the database at the
            // LAST fully applied version rather than somewhere between two, and the service then
            // refuses conversation routes until an operator resolves it.
            await using var transaction = await connection.BeginTransactionAsync(ct);

            try
            {
                foreach (var statement in SplitStatements(script.Sql))
                {
                    await using var command = new MySqlCommand(statement, connection, transaction);
                    await command.ExecuteNonQueryAsync(ct);
                }

                await using (var record = new MySqlCommand(
                    """
                    INSERT INTO schema_migrations (version, script_name, checksum)
                    VALUES (@version, @name, @checksum)
                    """,
                    connection, transaction))
                {
                    record.Parameters.AddWithValue("@version", script.Version);
                    record.Parameters.AddWithValue("@name", script.Name);
                    record.Parameters.AddWithValue("@checksum", script.Checksum);
                    await record.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                appliedNow.Add(script.Version);
            }
            catch (Exception e)
            {
                await transaction.RollbackAsync(CancellationToken.None);

                throw new MigrationException(
                    $"migration {script.Version} ('{script.Name}') failed and was rolled back. "
                    + $"The database remains at version {(appliedNow.Count > 0 ? appliedNow[^1] : recorded.Keys.DefaultIfEmpty(0).Max())}. "
                    + $"Underlying error: {e.Message}");
            }
        }

        report($"applied {appliedNow.Count} migration(s); now at version {appliedNow[^1]}");
        return appliedNow;
    }

    private static async Task<Dictionary<int, (string Checksum, DateTime AppliedAt)>> ReadRecordedAsync(
        MySqlConnection connection, CancellationToken ct)
    {
        var recorded = new Dictionary<int, (string, DateTime)>();

        await using var command = new MySqlCommand(
            "SELECT version, checksum, applied_at FROM schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            recorded[reader.GetInt32(0)] = (reader.GetString(1), reader.GetDateTime(2));

        return recorded;
    }

    /// <summary>
    /// Splits a script into statements on semicolons, ignoring semicolons inside comments and
    /// string literals.
    /// </summary>
    /// <remarks>
    /// The scripts are ours and deliberately plain — no stored routines, no <c>DELIMITER</c> — so
    /// this does not need to be a MySQL parser. It does need to survive a semicolon inside a comment
    /// or a quoted default, which is why it tracks both rather than splitting naively.
    /// </remarks>
    internal static IReadOnlyList<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder();

        var inLineComment = false;
        var inBlockComment = false;
        char? quote = null;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (c is '\n') { inLineComment = false; current.Append(c); }
                continue;
            }

            if (inBlockComment)
            {
                if (c is '*' && next is '/') { inBlockComment = false; i++; }
                continue;
            }

            if (quote is null)
            {
                if (c is '-' && next is '-') { inLineComment = true; i++; continue; }
                if (c is '#') { inLineComment = true; continue; }
                if (c is '/' && next is '*') { inBlockComment = true; i++; continue; }

                if (c is '\'' or '"' or '`') { quote = c; current.Append(c); continue; }

                if (c is ';')
                {
                    Flush();
                    continue;
                }
            }
            else
            {
                if (c is '\\' && next != '\0') { current.Append(c).Append(next); i++; continue; }
                if (c == quote) quote = null;
            }

            current.Append(c);
        }

        Flush();
        return statements;

        void Flush()
        {
            var statement = current.ToString().Trim();
            if (statement.Length > 0) statements.Add(statement);
            current.Clear();
        }
    }
}
