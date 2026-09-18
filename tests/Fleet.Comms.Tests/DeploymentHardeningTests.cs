using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Comms.Tests;

/// <summary>
/// The review round that found these described them as reproduced failures, not as style. Each test
/// here pins the behaviour that was wrong, so reverting any one of the fixes goes red.
/// </summary>
[Collection("auth-store-path")]
public sealed class DeploymentHardeningTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("fleet-comms-harden-").FullName;

    private string DatabasePath => Path.Combine(_directory, "auth.db");

    public DeploymentHardeningTests() =>
        Environment.SetEnvironmentVariable("Comms__AuthStorePath", DatabasePath);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Comms__AuthStorePath", null);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── the ops listener may not be published ────────────────────────────────

    [Theory]
    [InlineData("http://127.0.0.1:8081")]
    [InlineData("http://localhost:8081")]
    [InlineData("http://[::1]:8081")]
    [InlineData("http://127.0.0.1:8081;http://localhost:9091")]
    public void LoopbackOpsAddressesAreAccepted(string url) =>
        OpsListenerAddress.EnsureLoopbackOnly(url);

    /// <summary>
    /// The invariant was previously true of the default value and of nothing else: the configured
    /// address went straight to <c>UseUrls</c>, so any of these published the readiness oracle —
    /// which reports whether the owner's auth store is answering.
    /// </summary>
    [Theory]
    [InlineData("http://0.0.0.0:8081")]
    [InlineData("http://*:8081")]
    [InlineData("http://+:8081")]
    [InlineData("http://192.168.1.10:8081")]
    [InlineData("http://comms.example.com:8081")]
    [InlineData("http://[::]:8081")]
    // One good address does not excuse a wildcard beside it.
    [InlineData("http://127.0.0.1:8081;http://0.0.0.0:9091")]
    [InlineData("")]
    public void NonLoopbackOpsAddressesAreRefusedBeforeAnythingStarts(string url) =>
        Assert.Throws<InvalidOperationException>(() => OpsListenerAddress.EnsureLoopbackOnly(url));

    // ── an empty store is not silently created ───────────────────────────────

    /// <summary>
    /// A deleted-and-recreated volume used to yield a brand new empty database: `/ready` green, the
    /// container healthy, and every device forgotten. The deployment document promises the
    /// opposite, so the store opens read-write and refuses to create.
    /// </summary>
    [Fact]
    public async Task AMissingStoreIsUnavailableRatherThanSilentlyCreated()
    {
        using var store = new SqliteAuthStore(DatabasePath);

        await Assert.ThrowsAsync<AuthStoreUnavailableException>(() =>
            store.InTransactionAsync((tx, ct) => tx.CountActiveDevicesAsync("p", ct),
                CancellationToken.None));

        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task StoreInitCreatesItOnceAndIsSafeToRepeat()
    {
        var (first, _, _) = await Run("store", "init");
        Assert.Equal(0, first);
        Assert.True(File.Exists(DatabasePath));

        var (second, stdout, _) = await Run("store", "init");
        Assert.Equal(0, second);
        Assert.Contains("Already initialised", stdout, StringComparison.Ordinal);

        // And it is a working store afterwards.
        var (list, listOut, _) = await Run("devices", "list");
        Assert.Equal(0, list);
        Assert.Contains("No devices.", listOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreInitRefusesWhenTheDirectoryIsMissing()
    {
        var unmounted = Path.Combine(_directory, "not-mounted", "auth.db");
        Environment.SetEnvironmentVariable("Comms__AuthStorePath", unmounted);
        try
        {
            var (exit, _, stderr) = await Run("store", "init");

            // Creating the directory here would hide exactly the condition it must surface.
            Assert.NotEqual(0, exit);
            Assert.Contains("Mount the volume first", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(unmounted));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Comms__AuthStorePath", DatabasePath);
        }
    }

    // ── backup publication ───────────────────────────────────────────────────

    /// <summary>
    /// The reproduced data loss: two backups racing on one destination left <b>no</b> file at all,
    /// because the loser's cleanup deleted the winner's completed output.
    /// </summary>
    [Fact]
    public async Task ConcurrentBackupsToOneDestinationNeverDestroyTheWinnersFile()
    {
        await Run("store", "init");
        var destination = Path.Combine(_directory, "contested.db");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Run("store", "backup", "--out", destination)));

        // Exactly one wins; the others fail. What must never happen is all of them failing to leave
        // a backup behind.
        Assert.Contains(results, r => r.Exit == 0);
        Assert.True(File.Exists(destination), "the winning backup was deleted by a loser's cleanup");

        // And no temporary files survive.
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public async Task BackupRefusesToWriteOverTheLiveDatabase()
    {
        await Run("store", "init");

        using var store = new SqliteAuthStore(DatabasePath);
        await Assert.ThrowsAsync<BackupRefusedException>(() =>
            store.BackupToAsync(DatabasePath, CancellationToken.None));

        // Still a working store, not a truncated one.
        var (exit, _, _) = await Run("devices", "list");
        Assert.Equal(0, exit);
    }

    // ── enrollment precondition ──────────────────────────────────────────────

    /// <summary>
    /// Acceptance 25's precondition: with an active device, issuing another code exits non-zero and
    /// writes nothing. Checked and inserted in one transaction, so a concurrent registration cannot
    /// slip past it.
    /// </summary>
    [Fact]
    public async Task IssuingACodeWhileADeviceIsActiveExitsNonZeroAndWritesNothing()
    {
        await Run("store", "init");
        var (_, code, _) = await Run("enroll", "issue", "--principal", "p_owner");

        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var service = new AuthService(store, new Argon2idSecretHasher(),
                new MonotonicClock(TimeProvider.System), NullLogger<AuthService>.Instance);
            Assert.True((await service.RegisterDeviceAsync(code.Trim())).Succeeded);
        }

        var before = EnrollmentCount();
        Assert.Equal(1, before);   // the one that produced the active device

        var (exit, _, stderr) = await Run("enroll", "issue", "--principal", "p_owner");

        Assert.NotEqual(0, exit);
        Assert.Contains("already has an active device", stderr, StringComparison.Ordinal);

        // No new ENROLLMENT row. Counting devices here would have passed whether or not the insert
        // was rolled back, because a refused issue writes no device either way.
        Assert.Equal(before, EnrollmentCount());
    }

    [Fact]
    public async Task IssuingACodeForADifferentPrincipalIsUnaffected()
    {
        await Run("store", "init");
        var (_, code, _) = await Run("enroll", "issue", "--principal", "p_owner");

        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var service = new AuthService(store, new Argon2idSecretHasher(),
                new MonotonicClock(TimeProvider.System), NullLogger<AuthService>.Instance);
            await service.RegisterDeviceAsync(code.Trim());
        }

        var (exit, stdout, _) = await Run("enroll", "issue", "--principal", "p_other");

        Assert.Equal(0, exit);
        Assert.NotEmpty(stdout.Trim());
    }

    [Fact]
    public async Task ARevokedDeviceNoLongerBlocksANewCode()
    {
        await Run("store", "init");
        var (_, code, _) = await Run("enroll", "issue", "--principal", "p_owner");

        string deviceId;
        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var service = new AuthService(store, new Argon2idSecretHasher(),
                new MonotonicClock(TimeProvider.System), NullLogger<AuthService>.Instance);
            deviceId = (await service.RegisterDeviceAsync(code.Trim())).Value!.DeviceId;
        }

        Assert.NotEqual(0, (await Run("enroll", "issue", "--principal", "p_owner")).Exit);
        Assert.Equal(0, (await Run("devices", "revoke", "--device-id", deviceId)).Exit);
        Assert.Equal(0, (await Run("enroll", "issue", "--principal", "p_owner")).Exit);
    }

    /// <summary>
    /// Counts ENROLLMENT rows, by reading the table directly.
    ///
    /// <para>The first version of this helper counted devices, which is not the assertion the test
    /// claims to make: a refused `enroll issue` writes no device either way, so it passed whether
    /// or not the enrollment insert had been rolled back. Enrollment rows are what "no partial
    /// write" is about, and nothing on <c>IAuthStoreTransaction</c> exposes them — adding a port
    /// member only tests could use would be worse than a read-only query here.</para>
    /// </summary>
    private int EnrollmentCount()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM enrollments";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // ── disaster-restore invalidation, and loss versus first use ─────────────

    /// <summary>
    /// A restore reinstates whatever the snapshot held, and a device secret is long-lived — so the
    /// restored device can mint fresh tokens no matter how the old ones expired. `devices revoke
    /// --all` is the step that closes that before ingress reopens.
    /// </summary>
    [Fact]
    public async Task RevokeAllInvalidatesEveryRestoredCredential()
    {
        await Run("store", "init");

        var secrets = new List<(string DeviceId, string Secret)>();
        foreach (var principal in new[] { "p_owner", "p_second" })
        {
            var (_, code, _) = await Run("enroll", "issue", "--principal", principal);
            using var store = new SqliteAuthStore(DatabasePath);
            var service = Service(store);
            var device = (await service.RegisterDeviceAsync(code.Trim())).Value!;
            secrets.Add((device.DeviceId, device.DeviceSecret));
        }

        var (exit, stdout, _) = await Run("devices", "revoke", "--all");
        Assert.Equal(0, exit);
        Assert.Contains("Revoked 2 device", stdout, StringComparison.Ordinal);

        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var service = Service(store);
            foreach (var (deviceId, secret) in secrets)
            {
                // The long-lived credential, not just the tokens it had already minted.
                Assert.False((await service.MintTokenAsync(deviceId, secret)).Succeeded);
            }
        }

        var (_, list, _) = await Run("devices", "list");
        Assert.DoesNotContain("active", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeAllOnAnEmptyStoreSaysSoAndSucceeds()
    {
        await Run("store", "init");

        var (exit, stdout, _) = await Run("devices", "revoke", "--all");

        Assert.Equal(0, exit);
        Assert.Contains("Nothing to revoke", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The distinction the deployment document depends on: a volume that has held a store before
    /// and now has no database is storage LOSS, and initialising an empty one there is the silent
    /// re-enrollment the document promises cannot happen.
    /// </summary>
    [Fact]
    public async Task AMissingDatabaseInAUsedVolumeIsRefusedAsStorageLoss()
    {
        Assert.Equal(0, (await Run("store", "init")).Exit);
        File.Delete(DatabasePath);

        var (exit, _, stderr) = await Run("store", "init");

        Assert.NotEqual(0, exit);
        Assert.Contains("storage loss", stderr, StringComparison.Ordinal);
        Assert.Contains("restore from a backup", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task StartingOverAfterLossRequiresAnExplicitFlag()
    {
        Assert.Equal(0, (await Run("store", "init")).Exit);
        File.Delete(DatabasePath);

        var (exit, stdout, _) = await Run("store", "init", "--recover");

        Assert.Equal(0, exit);
        Assert.True(File.Exists(DatabasePath));
        Assert.Contains("EMPTY", stdout, StringComparison.Ordinal);
    }

    // ── a normal open verifies; only `store init` may create ─────────────────

    /// <summary>
    /// The hole this closes: `CREATE TABLE IF NOT EXISTS` ran on EVERY open, so opening
    /// <c>ReadWrite</c> rather than <c>ReadWriteCreate</c> bought nothing once a file existed. A
    /// zero-byte leftover, a truncated restore or an unrelated database at the store path was
    /// handed a schema on first use and became a working store with no devices — the silent
    /// re-enrollment the deployment document promises cannot happen, through the one door left
    /// open.
    /// </summary>
    [Theory]
    [InlineData("zero-byte", "")]
    [InlineData("not-a-database", "this is not a SQLite file at all")]
    public async Task AFileThatIsNotAnAuthStoreIsUnavailable_NotQuietlyGivenASchema(
        string name, string contents)
    {
        var path = Path.Combine(_directory, name + ".db");
        await File.WriteAllTextAsync(path, contents);
        Environment.SetEnvironmentVariable("Comms__AuthStorePath", path);

        using var store = new SqliteAuthStore(path);
        await Assert.ThrowsAsync<AuthStoreUnavailableException>(() =>
            store.InTransactionAsync((tx, ct) => tx.CountActiveDevicesAsync("p", ct),
                CancellationToken.None));

        // And no auth schema was created behind our back. Asserted on the TABLES rather than on the
        // bytes: `PRAGMA journal_mode=WAL` writes a database header to a zero-byte file, which is
        // harmless — an empty database with no tables is still refused — but it does mean byte
        // equality is the wrong question to ask here.
        using var recheck = new SqliteAuthStore(path);
        Assert.False(await recheck.IsUsableAsync());
    }

    [Fact]
    public async Task AnUnrelatedDatabaseAtTheStorePathIsUnavailable()
    {
        var path = Path.Combine(_directory, "someone-elses.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)";
            await command.ExecuteNonQueryAsync();
        }

        using var store = new SqliteAuthStore(path);
        var thrown = await Assert.ThrowsAsync<AuthStoreUnavailableException>(() =>
            store.InTransactionAsync((tx, ct) => tx.CountActiveDevicesAsync("p", ct),
                CancellationToken.None));

        Assert.Contains("schema is incomplete", thrown.Message, StringComparison.Ordinal);

        // The other database is intact — no auth tables were added to it.
        using var check = new SqliteAuthStore(path);
        Assert.False(await check.IsUsableAsync());
    }

    [Fact]
    public async Task AHealthyStoreOpensNormally()
    {
        await Run("store", "init");

        using var store = new SqliteAuthStore(DatabasePath);
        Assert.True(await store.IsUsableAsync());
    }

    /// <summary>
    /// A second `store init` must VALIDATE what is there. Reporting "Already initialised" for any
    /// bytes at that path meant setup.sh would go on to start the service against a zero-byte file.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("truncated restore")]
    public async Task RepeatedInitRefusesAFileThatIsNotAUsableStore(string contents)
    {
        await File.WriteAllTextAsync(DatabasePath, contents);

        var (exit, _, stderr) = await Run("store", "init");

        Assert.NotEqual(0, exit);
        Assert.Contains("not a usable auth store", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The marker lives in the volume, so a deleted VOLUME takes it — and a wiped volume would look
    /// exactly like a first install. The deployment records provisioning outside the volume for
    /// precisely that case.
    /// </summary>
    [Fact]
    public async Task AWipedVolumeStillRequiresExplicitRecovery()
    {
        Assert.Equal(0, (await Run("store", "init")).Exit);

        // The whole volume goes: database and marker together.
        Directory.Delete(_directory, recursive: true);
        Directory.CreateDirectory(_directory);

        Environment.SetEnvironmentVariable("Comms__StoreProvisioned", "true");
        try
        {
            var (exit, _, stderr) = await Run("store", "init");

            Assert.NotEqual(0, exit);
            Assert.Contains("storage loss", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(DatabasePath));

            Assert.Equal(0, (await Run("store", "init", "--recover")).Exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Comms__StoreProvisioned", null);
        }
    }

    [Fact]
    public async Task RevokeAllBurnsUnconsumedEnrollmentCodesToo()
    {
        await Run("store", "init");

        // An unconsumed code is a live registration credential for the rest of its TTL, and a
        // snapshot restores it alongside the devices.
        var (_, unusedCode, _) = await Run("enroll", "issue", "--principal", "p_owner");

        var (exit, stdout, _) = await Run("devices", "revoke", "--all");
        Assert.Equal(0, exit);
        Assert.Contains("burned 1 unconsumed", stdout, StringComparison.Ordinal);

        using var store = new SqliteAuthStore(DatabasePath);
        Assert.False((await Service(store).RegisterDeviceAsync(unusedCode.Trim())).Succeeded);
    }

    // ── the CLI must never abort ─────────────────────────────────────────────

    /// <summary>
    /// Two backups through the real entry point, at one destination. Both must <b>return</b>.
    ///
    /// <para>The destination claim threw <see cref="InvalidOperationException"/>, which nothing in
    /// <see cref="OperatorCommands.RunAsync"/> caught — so the invocation that lost the race did not
    /// report a condition the operator could act on, it aborted the process with a stack trace.
    /// This drives both through <c>RunAsync</c> rather than the store, because the store is not
    /// where that failure was.</para>
    /// </summary>
    [Fact]
    public async Task ConcurrentBackupsThroughTheCliBothReturn_ExactlyOneWins()
    {
        await Run("store", "init");
        var destination = Path.Combine(_directory, "raced.db");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Run("store", "backup", "--out", destination)));

        // Every call returned an exit code — none aborted.
        Assert.Equal(6, results.Length);
        Assert.Single(results.Where(r => r.Exit == 0));
        Assert.All(results.Where(r => r.Exit != 0), r => Assert.NotEmpty(r.Stderr.Trim()));

        Assert.True(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, "*.claim"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));

        // And the winner's file is a real store, not a claim placeholder.
        Assert.Equal(0, (await Run("store", "verify", "--in", destination)).Exit);
    }

    [Fact]
    public async Task BackingUpOverTheLiveDatabaseThroughTheCliExitsOneWithAMessage()
    {
        await Run("store", "init");

        var (exit, _, stderr) = await Run("store", "backup", "--out", DatabasePath);

        Assert.Equal(1, exit);
        Assert.Contains("over itself", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A genuine interrupted publication: the backup is fully written and validated, and the
    /// process dies in the instant before the rename.
    ///
    /// <para>The previous version of this test pointed at a missing directory, so it failed
    /// <b>before</b> `VACUUM INTO` ever ran — a pre-start failure, which proves nothing about the
    /// window that matters. This throws from the seam between validation and publication, which is
    /// the only point at which an incomplete file could reach the destination.</para>
    /// </summary>
    [Fact]
    public async Task AnInterruptionBetweenValidationAndPublicationLeavesNoBackup()
    {
        await Run("store", "init");
        var destination = Path.Combine(_directory, "interrupted.db");

        using (var store = new SqliteAuthStore(DatabasePath))
        {
            store.OnBeforePublish = () => throw new OperationCanceledException("killed mid-publish");

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                store.BackupToAsync(destination, CancellationToken.None));
        }

        // The destination never existed. This is the property the sibling claim file buys: claiming
        // the destination itself would have left a zero-byte file here, under the name a restore
        // would later trust.
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));

        // And the path is usable again afterwards.
        Assert.Equal(0, (await Run("store", "backup", "--out", destination)).Exit);
        Assert.Equal(0, (await Run("store", "verify", "--in", destination)).Exit);
    }

    /// <summary>
    /// The state a real SIGKILL leaves: a claim file with no process behind it. The next attempt at
    /// that exact path must say so and say what to do, not fail obscurely.
    /// </summary>
    [Fact]
    public async Task AStaleClaimRefusesWithAnActionableMessage()
    {
        await Run("store", "init");
        var destination = Path.Combine(_directory, "claimed.db");
        await File.WriteAllTextAsync(destination + ".claim", "");

        var (exit, _, stderr) = await Run("store", "backup", "--out", destination);

        Assert.Equal(1, exit);
        Assert.Contains("interrupted", stderr, StringComparison.Ordinal);
        Assert.Contains(".claim", stderr, StringComparison.Ordinal);

        File.Delete(destination + ".claim");
        Assert.Equal(0, (await Run("store", "backup", "--out", destination)).Exit);
    }

    // ── engine integrity, not only schema ────────────────────────────────────

    /// <summary>
    /// Corrupted enrollment pages with an intact schema. `sqlite_master` and `PRAGMA table_info`
    /// live on their own pages, so a pure schema check passes this file completely — it opens as a
    /// healthy store and then fails on whichever request happens to touch the damaged page.
    /// </summary>
    [Fact]
    public async Task ADamagedStoreIsUnavailable_NotHealthyUntilSomethingTouchesTheBadPage()
    {
        await Run("store", "init");
        var (_, code, _) = await Run("enroll", "issue", "--principal", "p_owner");
        Assert.NotEmpty(code.Trim());

        // Overwrite the pages holding rows, leaving the header and schema page intact.
        var bytes = await File.ReadAllBytesAsync(DatabasePath);
        Assert.True(bytes.Length > 8192, "the store is smaller than expected; adjust the offset");
        for (var i = 4096; i < bytes.Length; i++)
            bytes[i] = 0x5A;
        await File.WriteAllBytesAsync(DatabasePath, bytes);

        using var store = new SqliteAuthStore(DatabasePath);
        var thrown = await Assert.ThrowsAsync<AuthStoreUnavailableException>(() =>
            store.InTransactionAsync((tx, ct) => tx.CountActiveDevicesAsync("p", ct),
                CancellationToken.None));

        Assert.Contains("integrity", thrown.Message, StringComparison.OrdinalIgnoreCase);

        // And `store verify` refuses it, so a damaged file is never restored on purpose either.
        var (exit, _, stderr) = await Run("store", "verify", "--in", DatabasePath);
        Assert.NotEqual(0, exit);
        Assert.Contains("do not restore it", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreVerifyRejectsSomethingThatIsNotAnAuthStore()
    {
        var bogus = Path.Combine(_directory, "bogus.db");
        await File.WriteAllTextAsync(bogus, "not a database");

        var (exit, _, stderr) = await Run("store", "verify", "--in", bogus);

        Assert.NotEqual(0, exit);
        Assert.Contains("do not restore it", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreVerifyAcceptsARealBackup()
    {
        await Run("store", "init");
        var destination = Path.Combine(_directory, "good.db");
        Assert.Equal(0, (await Run("store", "backup", "--out", destination)).Exit);

        var (exit, stdout, _) = await Run("store", "verify", "--in", destination);

        Assert.Equal(0, exit);
        Assert.Contains("is a usable auth store", stdout, StringComparison.Ordinal);
    }

    // ── columns, not only table names ────────────────────────────────────────

    /// <summary>
    /// All three table NAMES present and the columns missing — a partial restore, a hand-edited
    /// database, a rolled-back schema. Checking names alone let this open cleanly and then fail one
    /// request at a time with an unhandled engine error instead of a clean `503`.
    /// </summary>
    [Fact]
    public async Task ATableWithTheRightNameAndTheWrongColumnsIsUnavailable()
    {
        var path = Path.Combine(_directory, "wrong-columns.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE enrollments (enrollment_id TEXT PRIMARY KEY);
                CREATE TABLE devices (device_id TEXT PRIMARY KEY);
                CREATE TABLE tokens (token_id TEXT PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var store = new SqliteAuthStore(path);
        var thrown = await Assert.ThrowsAsync<AuthStoreUnavailableException>(() =>
            store.InTransactionAsync((tx, ct) => tx.CountActiveDevicesAsync("p", ct),
                CancellationToken.None));

        Assert.Contains("schema is incomplete", thrown.Message, StringComparison.Ordinal);
    }

    // ── cleanup must not interpret a filename as a pattern ───────────────────

    /// <summary>
    /// `*` and `?` are ordinary characters in a Linux filename, and the destination name is the
    /// operator's. Passing it to <c>Directory.EnumerateFiles</c> as a glob meant a backup written
    /// to <c>audit-*.db</c> deleted a completed, verified backup of <c>audit-one.db</c> beside it —
    /// a file nobody named, that a restore may depend on, destroyed while the store tidied up after
    /// itself.
    /// </summary>
    [Theory]
    [InlineData("audit-*.db")]
    [InlineData("audit-?.db")]
    public async Task AWildcardInADestinationNameDoesNotReachOtherBackups(string wildcardName)
    {
        await Run("store", "init");

        // A completed, verified backup whose name the wildcard would match.
        var neighbour = Path.Combine(_directory, "audit-one.db");
        Assert.Equal(0, (await Run("store", "backup", "--out", neighbour)).Exit);
        Assert.Equal(0, (await Run("store", "verify", "--in", neighbour)).Exit);

        // An active temporary belonging to that other destination, which must also survive.
        var neighbourTemporary = neighbour + ".tmp-0123456789ab";
        await File.WriteAllTextAsync(neighbourTemporary, "another backup, mid-flight");

        // Now back up to a destination whose literal name contains the wildcard.
        var wildcard = Path.Combine(_directory, wildcardName);
        Assert.Equal(0, (await Run("store", "backup", "--out", wildcard)).Exit);

        Assert.True(File.Exists(neighbour), "a completed backup of another destination was deleted");
        Assert.True(File.Exists(neighbourTemporary), "another destination's live temporary was deleted");
        Assert.Equal(0, (await Run("store", "verify", "--in", neighbour)).Exit);
        Assert.True(File.Exists(wildcard));
    }

    /// <summary>
    /// The sweep still has to work: a temporary genuinely owned by this destination, left by an
    /// interrupted run, is removed by the next successful backup to the same path.
    /// </summary>
    [Fact]
    public async Task AStaleTemporaryOwnedByThisDestinationIsStillSwept()
    {
        await Run("store", "init");
        var destination = Path.Combine(_directory, "swept.db");

        var stale = destination + ".tmp-abcdef012345";
        await File.WriteAllTextAsync(stale, "left by an interrupted run");
        await File.WriteAllTextAsync(stale + "-wal", "");

        Assert.Equal(0, (await Run("store", "backup", "--out", destination)).Exit);

        Assert.False(File.Exists(stale), "an owned stale temporary was not swept");
        Assert.False(File.Exists(stale + "-wal"));
        Assert.True(File.Exists(destination));
    }

    /// <summary>
    /// A neighbouring file whose name merely starts the same way is not ours. `swept.db.tmp-…`
    /// belongs to `swept.db`; `swept.db.backup.tmp-…` belongs to `swept.db.backup`.
    /// </summary>
    [Fact]
    public async Task ALongerNeighbourNameIsNotMistakenForAnOwnedTemporary()
    {
        await Run("store", "init");
        var destination = Path.Combine(_directory, "swept.db");

        var neighbour = destination + ".backup.tmp-abcdef012345";
        await File.WriteAllTextAsync(neighbour, "belongs to swept.db.backup");

        // Also a file with our prefix but the wrong token shape — not ours either.
        var wrongShape = destination + ".tmp-not-hex";
        await File.WriteAllTextAsync(wrongShape, "not one of ours");

        Assert.Equal(0, (await Run("store", "backup", "--out", destination)).Exit);

        Assert.True(File.Exists(neighbour));
        Assert.True(File.Exists(wrongShape));
    }

    private static AuthService Service(IAuthStore store) =>
        new(store, new Argon2idSecretHasher(), new MonotonicClock(TimeProvider.System),
            NullLogger<AuthService>.Instance);

    private static async Task<(int Exit, string Stdout, string Stderr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
