using Fleet.Conversations;
using Fleet.Comms.Auth;
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

          enroll issue --principal <id>          issue a single-use enrollment code
          devices list [--principal <id>]        list devices (never secrets)
          devices revoke --device-id <id>        revoke a device the owner cannot revoke itself
          devices revoke --all                   revoke every device; run after a disaster restore
          store init [--recover]                 create the auth database, once, on purpose
          store verify --in <path>               check a backup before trusting it
          store backup --out <path>              consistent copy of the auth store

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
                ("enroll", "issue") => await IssueAsync(args, output, ct),
                ("devices", "list") => await ListAsync(args, output, ct),
                ("devices", "revoke") => Optional(args, "--device-id") is null && HasFlag(args, "--all")
                    ? await RevokeAllAsync(output, ct)
                    : await RevokeAsync(args, output, error, ct),
                ("store", "init") => await InitAsync(args, output, ct),
                ("store", "verify") => await VerifyAsync(args, output, error, ct),
                ("store", "backup") => await BackupAsync(args, output, ct),
                ("conversations", "migrate") => await ConversationsMigrateAsync(output, error, ct),
                ("conversations", "status") => await ConversationsStatusAsync(output, ct),
                ("--help", _) or ("-h", _) or ("help", _) => Write(output, Usage, 0),
                _ => Write(error, $"Unknown command: {string.Join(' ', args)}\n\n{Usage}", 2),
            };
        }
        catch (OperatorCommandException e)
        {
            // A precondition the operator can fix, reported as a sentence rather than a stack trace.
            return Write(error, e.Message, 1);
        }
        catch (BackupRefusedException e)
        {
            // A refusal, not a fault: the destination exists, another invocation holds the claim,
            // the destination is the live database, or what was written did not validate. Each is
            // something the operator can act on, and none of them should end the process with a
            // stack trace — which is what happened while these were InvalidOperationException and
            // nothing caught them.
            return Write(error, e.Message, 1);
        }
        catch (AuthStoreUnavailableException e)
        {
            // Store locked beyond the busy timeout, unwritable path, missing directory. The store's
            // own message carries no path (§13), and the transaction rolled back, so there is no
            // partial write for a later command to trip over.
            return Write(error, e.Message, 1);
        }
    }

    /// <summary>
    /// Create the database. Deliberately explicit, and deliberately the only thing that can.
    ///
    /// <para>The store otherwise opens <c>ReadWrite</c>, so a deleted-and-recreated volume is an
    /// error rather than a brand new empty database that reports ready and has forgotten every
    /// device. Making first use a decision is what lets the deployment document's "restore
    /// required, never silent re-enrollment" actually be true.</para>
    /// </summary>
    private static async Task<int> InitAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var path = ResolveStorePath();
        var directory = Path.GetDirectoryName(path)!;
        var marker = Path.Combine(directory, StoreInitialisedMarker);

        if (File.Exists(path))
        {
            // VALIDATED, not merely counted in bytes. "Already initialised" for any file at that
            // path reported success for a zero-byte leftover, a truncated restore or an unrelated
            // database — and setup.sh would then start the service against it.
            using var existing = new SqliteAuthStore(path);
            if (!await existing.IsUsableAsync(ct))
            {
                throw new OperatorCommandException(
                    $"There is a file at {path}, but it is not a usable auth store — it may be " +
                    "empty, truncated or an unrelated database. Restore from a backup, or move it " +
                    "aside and run `store init` again.");
            }

            output.WriteLine($"Already initialised: {path}");
            await EnsureMarkerAsync(marker, ct);
            return 0;
        }

        if (!Directory.Exists(directory))
        {
            throw new OperatorCommandException(
                $"The directory does not exist: {directory}. Mount the volume first — creating it " +
                "here would hide an unmounted volume.");
        }

        // FIRST INITIALISATION versus LOSS, decided from TWO places, because each covers a case the
        // other cannot.
        //
        // The marker lives inside the volume and catches a deleted database. It cannot catch a
        // deleted VOLUME — the marker goes with it, and a wiped volume then looks exactly like a
        // fresh install, which is the loss most likely to happen and the one where creating empty
        // state silently is worst.
        //
        // `Comms:StoreProvisioned` lives in the deployment's .env, on the host, outside the volume.
        // setup.sh and upgrade.sh set it after the first successful init, so it survives the volume
        // and says "this deployment has been initialised before" even when nothing inside the
        // volume does.
        var provisioned = CommsConfiguration.Resolve().StoreProvisioned;

        if ((File.Exists(marker) || provisioned) && !HasFlag(args, "--recover"))
        {
            throw new OperatorCommandException(
                "This deployment has been initialised before and the auth database is gone. That " +
                "is storage loss, not a first install: restore from a backup " +
                "(docs/comms-deployment.md), or pass --recover to deliberately start over with no " +
                "devices enrolled.");
        }

        using (var store = new SqliteAuthStore(path, allowCreate: true))
            await store.InTransactionAsync((tx, token) => tx.CountActiveDevicesAsync("", token), ct);

        await EnsureMarkerAsync(marker, ct);

        output.WriteLine(HasFlag(args, "--recover")
            ? $"Re-initialised {path} — EMPTY. Every previously enrolled device is gone; re-enroll."
            : $"Initialised {path}");
        return 0;
    }

    /// <summary>
    /// Records that this volume has held a store, so a later missing database is recognisable as
    /// loss rather than as a first run. Written beside the database, inside the same volume — that
    /// is the whole point, since a fresh volume has neither.
    /// </summary>
    private static async Task EnsureMarkerAsync(string marker, CancellationToken ct)
    {
        if (!File.Exists(marker))
        {
            await File.WriteAllTextAsync(marker,
                "This volume holds a Fleet.Comms auth store.\n" +
                "If the database beside this file is missing, that is storage loss:\n" +
                "restore from a backup rather than initialising an empty one.\n", ct);
        }
    }

    /// <summary>
    /// Revoke every device and every token, in one transaction.
    ///
    /// <para>The disaster-restore step. A snapshot predating a revocation brings that device back
    /// active, and its secret is the long-lived credential — expiring access tokens does not help,
    /// because the restored device can mint more. Doing this by hand, device by device, after the
    /// service is already reachable leaves a window in which a restored credential works; this runs
    /// before ingress reopens and takes all of them at once.</para>
    /// </summary>
    private static async Task<int> RevokeAllAsync(TextWriter output, CancellationToken ct)
    {
        var (service, store) = Build();
        using (store)
        {
            var (devices, enrollments) = await service.RevokeAllDevicesAsync(ct);
            output.WriteLine(devices == 0 && enrollments == 0
                ? "Nothing to revoke."
                : $"Revoked {devices} device(s) and every token, and burned {enrollments} unconsumed " +
                  "enrollment code(s). Re-enroll before reopening ingress.");
        }

        return 0;
    }

    private static async Task<int> IssueAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var principal = Required(args, "--principal");
        var (service, store) = Build();
        using (store)
        {
            var issued = await service.IssueEnrollmentCodeForOperatorAsync(principal, ct);
            if (issued is null)
            {
                throw new OperatorCommandException(
                    $"{principal} already has an active device. Revoke it first: " +
                    "devices list, then devices revoke --device-id <id>.");
            }

            var code = issued;

            // stdout, and nowhere else. Never a log line, never a file, never an environment
            // variable — the code is a bearer credential for the next fifteen minutes, and the only
            // place it belongs is the operator's terminal.
            output.WriteLine(code);
        }

        return 0;
    }

    private static async Task<int> ListAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var principal = Optional(args, "--principal");
        var (service, store) = Build();
        using (store)
        {
            var devices = await service.ListDevicesAsync(principal, ct);
            if (devices.Count == 0)
            {
                output.WriteLine("No devices.");
                return 0;
            }

            output.WriteLine($"{"DEVICE ID",-20} {"PRINCIPAL",-20} {"STATUS",-8} REGISTERED");
            foreach (var device in devices)
            {
                // Identifiers, status and timestamps. No secret hash, no salt, no token material —
                // not abbreviated, not "just the prefix for debugging". A device id is a non-secret
                // handle; everything else on the record is not, and none of it is needed to decide
                // which device to revoke.
                output.WriteLine(
                    $"{device.DeviceId,-20} {device.PrincipalId,-20} " +
                    $"{(device.IsActive ? "active" : "revoked"),-8} " +
                    $"{device.RegisteredAt:yyyy-MM-dd HH:mm:ss}Z");
            }
        }

        return 0;
    }

    private static async Task<int> RevokeAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var deviceId = Required(args, "--device-id");
        var (service, store) = Build();
        using (store)
        {
            if (!await service.RevokeDeviceAsync(deviceId, ct))
                return Write(error, $"No such device: {deviceId}", 1);

            output.WriteLine($"Revoked {deviceId} and all of its tokens.");
        }

        return 0;
    }

    /// <summary>
    /// Check a backup before anything depends on it.
    ///
    /// <para>The restore procedure used to be a `sqlite3 PRAGMA integrity_check` incantation, which
    /// assumes sqlite3 is installed and answers a narrower question than "is this an auth store" —
    /// a perfectly intact database of the wrong schema passes it. This asks the question the
    /// restore actually depends on, using the same verification the service applies on every
    /// open.</para>
    /// </summary>
    private static async Task<int> VerifyAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var path = Path.GetFullPath(Required(args, "--in"));
        if (!File.Exists(path))
            return Write(error, $"No such file: {path}", 1);

        using var candidate = new SqliteAuthStore(path);
        if (!await candidate.IsUsableAsync(ct, thorough: true))
        {
            return Write(error,
                $"{path} is NOT a usable auth store — do not restore it. It may be empty, " +
                "truncated, or a database of a different shape.", 1);
        }

        var devices = await candidate.InTransactionAsync(
            (tx, token) => tx.ListDevicesAsync(null, token), ct);

        output.WriteLine($"{path} is a usable auth store ({devices.Count} device record(s)).");
        return 0;
    }

    private static async Task<int> BackupAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var destination = Required(args, "--out");
        var (_, store) = Build();
        using (store)
        {
            await store.BackupToAsync(destination, ct);
            output.WriteLine($"Wrote {destination}");
        }

        return 0;
    }

    /// <summary>
    /// Build the same service graph the north app uses, minus anything that listens.
    ///
    /// <para>Configuration comes from the identical sources — environment variables and
    /// <c>appsettings.json</c> — so a subcommand and the service always agree about which store
    /// they are talking to. A command that read the path from somewhere else could helpfully back
    /// up a database nobody is serving.</para>
    /// </summary>
    private static string ResolveStorePath()
    {
        var path = CommsConfiguration.Resolve().AuthStorePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new OperatorCommandException(
                $"{CommsOptions.SectionName}:{nameof(CommsOptions.AuthStorePath)} is required " +
                "and has no default. Point it at the same store the service uses.");
        }

        // Logged as an absolute path so "which database did that touch?" is answerable from the
        // output rather than from knowing the working directory.
        return Path.GetFullPath(path);
    }

    private static (AuthService Service, SqliteAuthStore Store) Build()
    {
        var store = new SqliteAuthStore(ResolveStorePath());
        var service = new AuthService(store, new Argon2idSecretHasher(),
            new MonotonicClock(TimeProvider.System), NullLogger<AuthService>.Instance);
        return (service, store);
    }

    private const string StoreInitialisedMarker = ".fleet-comms-store";

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static string Required(string[] args, string name) =>
        Optional(args, name) ?? throw new OperatorCommandException($"{name} is required.\n\n{Usage}");

    private static string? Optional(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0)
            return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            throw new OperatorCommandException($"{name} needs a value.");
        return args[index + 1];
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

    private static int Write(TextWriter writer, string message, int exitCode)
    {
        writer.WriteLine(message);
        return exitCode;
    }
}

/// <summary>An operator mistake with a fixable cause. Reported as a message, never a stack trace.</summary>
public sealed class OperatorCommandException(string message) : Exception(message);
