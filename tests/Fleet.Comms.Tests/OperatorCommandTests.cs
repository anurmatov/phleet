using Fleet.Comms.Auth;
using Fleet.Comms.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Comms.Tests;

/// <summary>
/// The operator path: enrollment, listing, revocation and backup.
///
/// <para>These drive <see cref="OperatorCommands.RunAsync"/> — the same entry point <c>Program</c>
/// dispatches to — with the store path supplied through the environment exactly as the container
/// supplies it, and with stdout and stderr captured. Calling <see cref="AuthService"/> directly
/// would prove the service works and say nothing about the command.</para>
/// </summary>
[Collection("auth-store-path")]
public sealed class OperatorCommandTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("fleet-comms-ops-").FullName;

    private string DatabasePath => Path.Combine(_directory, "auth.db");

    public OperatorCommandTests()
    {
        Environment.SetEnvironmentVariable("Comms__AuthStorePath", DatabasePath);

        // Nothing creates the database implicitly any more — a deleted volume must be an error,
        // not a silently empty store. `store init` is the one thing that may create it, and
        // setup.sh runs it once for exactly this reason.
        OperatorCommands.RunAsync(["store", "init"], TextWriter.Null, TextWriter.Null)
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Comms__AuthStorePath", null);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    // ── enroll issue ─────────────────────────────────────────────────────────

    /// <summary>
    /// F1: nothing in a running deployment could issue the first enrollment code, because
    /// `IssueEnrollmentCodeAsync` is deliberately unreachable from every route. This is that path,
    /// and the code it prints must actually register a device through the north route.
    /// </summary>
    [Fact]
    public async Task IssuedCodeRegistersADeviceThroughTheNorthRoute()
    {
        var (exit, stdout, _) = await Run("enroll", "issue", "--principal", "p_owner");

        Assert.Equal(0, exit);
        var code = stdout.Trim();
        Assert.NotEmpty(code);

        // Through the real route table, against the same file the command wrote to.
        await using var host = await NorthTestHost.StartAsync(store: new SqliteAuthStore(DatabasePath, allowCreate: true));
        var registration = await host.RegisterAsync(code);

        Assert.Equal(System.Net.HttpStatusCode.OK, registration.StatusCode);
    }

    [Fact]
    public async Task TheCodeGoesToStdoutAndNowhereElse()
    {
        var (_, stdout, stderr) = await Run("enroll", "issue", "--principal", "p_owner");
        var code = stdout.Trim();

        Assert.DoesNotContain(code, stderr, StringComparison.Ordinal);

        // The credential is `<enrollmentId>.<secret>`. The id half is a NON-secret handle and is of
        // course stored — it is the primary key the server looks the record up by, and without it
        // the server would have to Argon2 the presented value against every row. The entropy is
        // entirely in the secret half, and that is what must appear nowhere on disk: the store
        // holds only its Argon2id hash.
        var secret = code.Split('.')[1];

        foreach (var file in Directory.GetFiles(_directory))
        {
            var text = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.DoesNotContain(code, text, StringComparison.Ordinal);

            // Not a prefix either — eight characters of a base64url secret is exactly the
            // "just for debugging" shape §13 forbids.
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret[..8], text, StringComparison.Ordinal);
        }
    }

    // ── devices list ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DevicesListShowsIdentifiersAndStatus_AndNoSecretMaterial()
    {
        var device = await EnrollAsync();
        var (exit, stdout, _) = await Run("devices", "list");

        Assert.Equal(0, exit);
        Assert.Contains(device.DeviceId, stdout, StringComparison.Ordinal);
        Assert.Contains("p_owner", stdout, StringComparison.Ordinal);
        Assert.Contains("active", stdout, StringComparison.Ordinal);

        // Field by field against the actual output: no secret, no hash, no salt, and not a prefix
        // of any of them either.
        Assert.DoesNotContain(device.DeviceSecret, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(device.DeviceSecret[..8], stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("$argon2id$", stdout, StringComparison.Ordinal);

        var stored = await StoredSecretHashAsync(device.DeviceId);
        Assert.DoesNotContain(stored, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Split('$')[4], stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevicesListFiltersByPrincipalWhenAsked()
    {
        await EnrollAsync();

        var (_, mine, _) = await Run("devices", "list", "--principal", "p_owner");
        var (_, theirs, _) = await Run("devices", "list", "--principal", "p_someone_else");

        Assert.Contains("p_owner", mine, StringComparison.Ordinal);
        Assert.Contains("No devices.", theirs, StringComparison.Ordinal);
    }

    // ── devices revoke: the lost phone ───────────────────────────────────────

    /// <summary>
    /// F2 end to end. One active device per principal, so a lost phone blocks its own replacement
    /// and cannot self-revoke — it is gone. This is the only path out, and without it the product
    /// has a permanent lockout.
    /// </summary>
    [Fact]
    public async Task RevokingALostDeviceInvalidatesItsTokenAndUnblocksAReplacement()
    {
        var device = await EnrollAsync();

        await using (var host = await NorthTestHost.StartAsync(store: new SqliteAuthStore(DatabasePath, allowCreate: true)))
        {
            var minted = await AuthLifecycleTests.Body<NorthTestHost.TokenBody>(
                await host.TokenAsync(device.DeviceId, device.DeviceSecret));
            Assert.Equal(System.Net.HttpStatusCode.OK, (await host.SessionAsync(minted.AccessToken)).StatusCode);

            // The lost phone blocks its own replacement, and now it blocks it one step earlier:
            // `enroll issue` refuses outright while an active device exists, rather than handing
            // out a code that could only ever be rejected at registration.
            var (blockedExit, _, blockedErr) = await Run("enroll", "issue", "--principal", "p_owner");
            Assert.NotEqual(0, blockedExit);
            Assert.Contains("already has an active device", blockedErr, StringComparison.Ordinal);

            var (exit, stdout, _) = await Run("devices", "revoke", "--device-id", device.DeviceId);
            Assert.Equal(0, exit);
            Assert.Contains(device.DeviceId, stdout, StringComparison.Ordinal);

            // The token the lost phone holds stops working immediately.
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
                (await host.SessionAsync(minted.AccessToken)).StatusCode);

            // And the replacement can now enroll.
            Assert.Equal(System.Net.HttpStatusCode.OK,
                (await host.RegisterAsync(await IssueCodeAsync())).StatusCode);
        }

        var (_, list, _) = await Run("devices", "list");
        Assert.Contains("revoked", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokingAnUnknownDeviceExitsNonZeroWithNoPartialWrite()
    {
        var device = await EnrollAsync();

        var (exit, _, stderr) = await Run("devices", "revoke", "--device-id", "no-such-device");

        Assert.NotEqual(0, exit);
        Assert.Contains("No such device", stderr, StringComparison.Ordinal);

        // The real device is untouched — a failed command must not half-revoke something else.
        var (_, list, _) = await Run("devices", "list");
        Assert.Contains($"{device.DeviceId}", list, StringComparison.Ordinal);
        Assert.Contains("active", list, StringComparison.Ordinal);
    }

    // ── store backup ─────────────────────────────────────────────────────────

    /// <summary>
    /// The criterion that matters: a backup nobody has restored is not evidence. This one is taken
    /// while another store handle is open, restored into an isolated copy, and a token that was
    /// valid in the source is checked against the restored database.
    /// </summary>
    [Fact]
    public async Task ABackupTakenWhileServingRestoresIntoAWorkingIsolatedStore()
    {
        // Everything on the real clock, both sides. An earlier version of this test minted through
        // the north test host, whose clock starts in January — so the token was fifteen minutes old
        // in test time and eight months expired against the restored store's system clock. The
        // restore was fine; the test was comparing two different clocks.
        var code = await IssueCodeAsync();

        string token;
        using (var source = new SqliteAuthStore(DatabasePath, allowCreate: true))
        {
            var sourceService = Service(source);
            var device = (await sourceService.RegisterDeviceAsync(code)).Value!;
            token = (await sourceService.MintTokenAsync(device.DeviceId, device.DeviceSecret))
                .Value!.AccessToken;

            // Taken with that store handle still open and serving.
            var destination = Path.Combine(_directory, "backup.db");
            var (exit, stdout, _) = await Run("store", "backup", "--out", destination);

            Assert.Equal(0, exit);
            Assert.Contains(destination, stdout, StringComparison.Ordinal);
            Assert.True(File.Exists(destination));

            // VACUUM INTO writes a standalone database: no WAL sidecars to carry along, which is
            // what makes the documented restore a plain file copy rather than a three-file dance.
            Assert.False(File.Exists(destination + "-wal"));
            Assert.False(File.Exists(destination + "-shm"));
        }

        // Isolated: its own path, its own store instance, no contact with the source.
        var restoredPath = Path.Combine(_directory, "restored.db");
        File.Copy(Path.Combine(_directory, "backup.db"), restoredPath);

        using var restored = new SqliteAuthStore(restoredPath, allowCreate: true);
        var principal = await Service(restored).AuthenticateAsync(token);

        Assert.True(principal.Succeeded);
        Assert.Equal("p_owner", principal.Value!.PrincipalId);
    }

    [Fact]
    public async Task BackupRefusesToOverwriteAndLeavesTheExistingFileIntact()
    {
        await EnrollAsync();
        var destination = Path.Combine(_directory, "existing.db");
        await File.WriteAllTextAsync(destination, "yesterday's good backup");

        var (exit, _, stderr) = await Run("store", "backup", "--out", destination);

        Assert.NotEqual(0, exit);
        Assert.Contains("Refusing to overwrite", stderr, StringComparison.Ordinal);
        Assert.Equal("yesterday's good backup", await File.ReadAllTextAsync(destination));
    }

    // ── argument and precondition handling ───────────────────────────────────

    [Theory]
    [InlineData("enroll", "issue")]
    [InlineData("devices", "revoke")]
    [InlineData("store", "backup")]
    public async Task AMissingRequiredArgumentExitsNonZeroWithAMessage(string verb, string noun)
    {
        var (exit, _, stderr) = await Run(verb, noun);

        Assert.NotEqual(0, exit);
        Assert.NotEmpty(stderr.Trim());
    }

    [Fact]
    public async Task AnUnknownCommandExitsNonZeroAndPrintsUsage()
    {
        var (exit, _, stderr) = await Run("wat", "nope");

        Assert.NotEqual(0, exit);
        Assert.Contains("enroll issue", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingStorePathExitsNonZeroAndNamesTheKey()
    {
        Environment.SetEnvironmentVariable("Comms__AuthStorePath", "   ");
        try
        {
            var (exit, _, stderr) = await Run("devices", "list");

            Assert.NotEqual(0, exit);
            Assert.Contains("AuthStorePath", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Comms__AuthStorePath", DatabasePath);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<(int Exit, string Stdout, string Stderr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static AuthService Service(IAuthStore store) =>
        new(store, new Argon2idSecretHasher(), new MonotonicClock(TimeProvider.System),
            NullLogger<AuthService>.Instance);

    private async Task<string> IssueCodeAsync()
    {
        var (_, stdout, _) = await Run("enroll", "issue", "--principal", "p_owner");
        return stdout.Trim();
    }

    private async Task<NorthTestHost.RegisterDeviceBody> EnrollAsync()
    {
        var code = await IssueCodeAsync();
        await using var host = await NorthTestHost.StartAsync(store: new SqliteAuthStore(DatabasePath, allowCreate: true));
        return await AuthLifecycleTests.Body<NorthTestHost.RegisterDeviceBody>(
            await host.RegisterAsync(code));
    }

    private async Task<string> StoredSecretHashAsync(string deviceId)
    {
        using var store = new SqliteAuthStore(DatabasePath, allowCreate: true);
        return await store.InTransactionAsync(async (tx, ct) =>
            (await tx.FindDeviceAsync(deviceId, ct))!.SecretHash, CancellationToken.None);
    }
}
