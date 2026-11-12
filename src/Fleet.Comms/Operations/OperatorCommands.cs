using Fleet.Conversations;
using Fleet.Conversations.Journal;
using Fleet.Comms.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Comms.Operations;

/// <summary>
/// The operator path: enrollment, device listing, revocation and backup.
///
/// <para><b>Subcommands of this same assembly, never HTTP routes.</b> Two of these have no safe
/// public form. Issuing an enrollment code over HTTP would let anyone who reached the port mint a
/// registration; revoking a device without authenticating as it would be a one-request denial of
/// service against the owner's only way in (docs/first-party-api.md §5.5). As one-shot processes
/// the authorisation is possession of the store, which is what being the operator means.</para>
///
/// <para>Everything here goes through <see cref="AuthService"/> and <see cref="IAuthStore"/>. No
/// command builds its own hasher, its own credential format or its own SQL — a second Argon2id
/// configuration would fail silently, permanently, and only for real users.</para>
/// </summary>
public static class OperatorCommands
{
    private const string Usage = """
        Fleet.Comms operator commands

          conversations migrate                  apply the conversation schema (DDL account)
          conversations status                   applied schema version against this binary's

          journal token --purpose <ingest|status> --subject <s>
                                                 print a journal token minted with the first key
          journal status                         per-observer message counts and last ingest
          journal purge --message <id> | --conversation <id> | --telegram-chat <chatId>
                        [--before <ISO-8601>] [--confirm]
                                                 delete journal rows; without --confirm, print
                                                 the counts it would delete and change nothing
          journal verify-media [--sample N]      re-read and re-hash every committed attachment's
                                                 object; exits 1 on any mismatch or missing object

          media backup --out <dir>               copy every committed object this directory is
                                                 missing, re-hash each, write a manifest atomically
          media verify --in <dir>                check a backup directory against its manifest
          media restore --in <dir>               PutObject every object the bucket is missing,
                                                 then verify the bucket

        Run with no arguments to start the service.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter? stdout = null,
        TextWriter? stderr = null, CancellationToken ct = default)
    {
        var output = stdout ?? Console.Out;
        var error = stderr ?? Console.Error;

        try
        {
            return (args[0], args.Length > 1 ? args[1] : "") switch
            {
                ("conversations", "migrate") => await ConversationsMigrateAsync(output, error, ct),
                ("conversations", "status") => await ConversationsStatusAsync(output, ct),
                ("journal", "token") => JournalToken(args, output),
                ("journal", "status") => await JournalStatusAsync(output, error, ct),
                ("journal", "purge") => await JournalPurgeAsync(args, output, error, ct),
                ("journal", "verify-media") => await JournalMediaCommands.VerifyMediaAsync(args, output, error, ct),
                ("media", "backup") => await JournalMediaCommands.BackupAsync(args, output, error, ct),
                ("media", "verify") => await JournalMediaCommands.VerifyAsync(args, output, error, ct),
                ("media", "restore") => await JournalMediaCommands.RestoreAsync(args, output, error, ct),
                ("--help", _) or ("-h", _) or ("help", _) => Write(output, Usage, 0),
                _ => Write(error, $"Unknown command: {string.Join(' ', args)}\n\n{Usage}", 2),
            };
        }
        catch (OperatorCommandException e)
        {
            // A precondition the operator can fix, reported as a sentence rather than a stack trace.
            return Write(error, e.Message, 1);
        }
    }

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static string Required(string[] args, string name) =>
        Optional(args, name) ?? throw new OperatorCommandException($"{name} is required.\n\n{Usage}");

    /// <param name="allowNegativeNumber">
    /// Only <c>--telegram-chat</c> takes one: basic-group and supergroup chat ids are negative.
    /// Other flags still cannot become values.
    /// </param>
    private static string? Optional(string[] args, string name, bool allowNegativeNumber = false)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0)
            return null;
        if (index + 1 >= args.Length)
            throw new OperatorCommandException($"{name} needs a value.");
        var value = args[index + 1];
        if (value.StartsWith('-')
            && !(allowNegativeNumber && long.TryParse(value,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out _)))
            throw new OperatorCommandException($"{name} needs a value.");
        return value;
    }

    // ── conversations ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the forward-only conversation migrations, using the DDL connection string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ONLY path that applies a migration. Starting the process never does: a service
    /// that migrated on boot would turn a deployment mistake into an irreversible schema change and
    /// remove the operator's chance to take a backup first.
    /// </para>
    /// <para>
    /// It uses the separate DDL connection string, which the running service does not have. Run with
    /// the runtime credential it fails at the database, because that account holds no DDL grants —
    /// the intended outcome, not a misconfiguration to work around.
    /// </para>
    /// </remarks>
    private static async Task<int> ConversationsMigrateAsync(
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        var options = CommsConfiguration.Resolve();

        if (string.IsNullOrWhiteSpace(options.ConversationMigrationConnectionString))
            throw new OperatorCommandException(
                "Comms__ConversationMigrationConnectionString is not set.\n\n"
                + "  Migrations use a SEPARATE DDL account from the one the service runs as. The\n"
                + "  runtime account deliberately holds no DDL grants, so it cannot be used here.");

        try
        {
            var applied = await new MigrationRunner(options.ConversationMigrationConnectionString)
                .MigrateAsync(message => output.WriteLine(message), ct);

            output.WriteLine(applied.Count == 0
                ? "schema already up to date"
                : $"applied version(s): {string.Join(", ", applied)}");

            return 0;
        }
        catch (MigrationException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>
    /// Reports the applied schema version, the version this binary expects, and whether they agree.
    /// </summary>
    /// <remarks>
    /// An applied version AHEAD of the binary is as unhealthy as one behind it — that is the
    /// rollback-after-migration case — so the two are reported distinguishably rather than both as
    /// "mismatch".
    /// </remarks>
    private static async Task<int> ConversationsStatusAsync(TextWriter output, CancellationToken ct)
    {
        var options = CommsConfiguration.Resolve();

        var connection = !string.IsNullOrWhiteSpace(options.ConversationMigrationConnectionString)
            ? options.ConversationMigrationConnectionString
            : options.ConversationConnectionString;

        if (string.IsNullOrWhiteSpace(connection))
            throw new OperatorCommandException(
                "No conversation connection string is configured, so there is no schema to report on.");

        var status = await new MigrationRunner(connection).GetStatusAsync(ct);

        output.WriteLine($"applied:  {status.AppliedVersion?.ToString() ?? "none"}");
        output.WriteLine($"expected: {status.ExpectedVersion}");
        output.WriteLine($"matches:  {status.Matches}");
        output.WriteLine(status.Describe());

        return status.Matches ? 0 : 1;
    }

    // ── journal (#375) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Mints a journal token with the FIRST configured key and prints it to stdout — the operator's
    /// terminal, run through the <c>logging: none</c> one-shot, and nowhere else.
    /// </summary>
    private static int JournalToken(string[] args, TextWriter output)
    {
        var purpose = Required(args, "--purpose");
        var subject = Required(args, "--subject");

        if (purpose is not (JournalTokens.PurposeIngest or JournalTokens.PurposeStatus or JournalTokens.PurposeRead))
            throw new OperatorCommandException(
                $"--purpose must be {JournalTokens.PurposeIngest}, {JournalTokens.PurposeStatus} or {JournalTokens.PurposeRead}. "
                + "Other purposes are reserved for later slices and no route accepts them.");

        if (!JournalTokens.IsValidSubject(subject))
            throw new OperatorCommandException(
                "--subject is 1-128 characters of [A-Za-z0-9_-]. It names the publishing runtime.");

        IReadOnlyList<byte[]> keys;
        try
        {
            keys = CommsConfiguration.Resolve().Journal.Keys();
        }
        catch (FormatException e)
        {
            // Position only, never the value.
            throw new OperatorCommandException(
                $"journal_key_invalid: Comms__Journal__TokenKeys: {e.Message}.");
        }

        output.WriteLine(JournalTokens.Mint(keys[0], purpose, subject));
        return 0;
    }

    private static async Task<int> JournalStatusAsync(TextWriter output, TextWriter error, CancellationToken ct)
    {
        var store = new MySqlJournalStore(JournalConnectionString(), NullLogger.Instance);

        Fleet.Conversations.Contracts.JournalStoreStatus status;
        try
        {
            status = await store.GetStatusAsync(ct);
        }
        catch (Fleet.Conversations.Contracts.JournalStoreUnavailableException)
        {
            return Write(error, "journal status: the conversation database is not answering.", 1);
        }

        output.WriteLine($"schema version: {status.SchemaVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");

        if (status.SchemaVersion is null or < MySqlJournalStore.RequiredSchemaVersion)
        {
            output.WriteLine($"the journal tables arrive with version {MySqlJournalStore.RequiredSchemaVersion}; run conversations migrate");
            return 1;
        }

        if (status.Observers.Count == 0)
        {
            output.WriteLine("No observers.");
            return 0;
        }

        output.WriteLine($"{"OBSERVER",-32} {"MESSAGES",10} LAST_INGEST_AT");
        foreach (var observer in status.Observers)
        {
            output.WriteLine(
                $"{observer.Observer,-32} {observer.Messages,10} "
                + (observer.LastIngestAt is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : "-"));
        }

        return 0;
    }

    /// <summary>
    /// Deletes journal rows in ONE transaction and prints the counts. Without <c>--confirm</c> the
    /// same transaction runs and is rolled back, so the printed counts are exactly what a confirmed
    /// run would delete and nothing changes.
    /// </summary>
    private static async Task<int> JournalPurgeAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var message = Optional(args, "--message");
        var conversation = Optional(args, "--conversation");
        var chat = Optional(args, "--telegram-chat", allowNegativeNumber: true);
        var before = Optional(args, "--before");

        if ((message is null ? 0 : 1) + (conversation is null ? 0 : 1) + (chat is null ? 0 : 1) != 1)
            throw new OperatorCommandException(
                $"journal purge needs exactly one of --message, --conversation or --telegram-chat.\n\n{Usage}");

        long? chatId = null;
        if (chat is not null)
        {
            if (!long.TryParse(chat, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
                throw new OperatorCommandException("--telegram-chat is a non-zero integer chat id.");
            chatId = parsed;
        }

        DateTimeOffset? cutoff = null;
        if (before is not null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(before, "(Z|[+-][0-9]{2}:[0-9]{2})$")
                || !DateTimeOffset.TryParse(before, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
                throw new OperatorCommandException("--before is an ISO-8601 time with an offset, e.g. 2026-01-01T00:00:00Z.");
            cutoff = parsed;
        }

        var confirm = HasFlag(args, "--confirm");
        var selector = new JournalPurgeSelector
        {
            MessageId = message,
            ConversationId = conversation,
            TelegramChatId = chatId,
            Before = cutoff,
        };

        var connection = JournalConnectionString();

        JournalPurgeCounts counts;
        try
        {
            counts = await JournalRetention.PurgeAsync(connection, selector, confirm, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The class only: a driver message can carry the connection string. The transaction
            // rolled back, so nothing was deleted.
            return Write(error, $"journal purge failed ({e.GetType().Name}); nothing was deleted.", 1);
        }

        // The object count is printed because it is the only number here that is NOT a deletion:
        // the bytes are still in the bucket and leave after the delete grace. An operator reading
        // "purged" as "gone" would be wrong about the storage and right about the database.
        var summary = $"{counts.Messages} message(s), {counts.Observers} observer row(s), "
            + $"{counts.Attachments} attachment row(s), {counts.Conversations} conversation(s), "
            + $"{counts.Objects} object(s) scheduled for deletion";

        output.WriteLine(confirm
            ? $"deleted: {summary}"
            : $"would delete: {summary}\n(dry run; nothing changed. Pass --confirm to delete.)");

        return 0;
    }

    /// <summary>The runtime account when configured (it holds DELETE), else the DDL one.</summary>
    private static string JournalConnectionString() => JournalMediaCommandsSupport.ConnectionString();

    private static int Write(TextWriter writer, string message, int exitCode)
    {
        writer.WriteLine(message);
        return exitCode;
    }
}

/// <summary>An operator mistake with a fixable cause. Reported as a message, never a stack trace.</summary>
public sealed class OperatorCommandException(string message) : Exception(message);
