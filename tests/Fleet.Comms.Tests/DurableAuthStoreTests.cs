using System.Net;
using System.Net.Http.Json;
using Fleet.Comms;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Protocol;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Comms.Tests;

/// <summary>
/// The store the deployable actually runs on, against the real engine.
///
/// <para>Issue #292 scope 4 asks for auth/enrollment/device/token persistence. A store that loses
/// registrations on restart does not provide it: the owner's device is enrolled once, holds a
/// secret nobody can re-issue, and a deploy silently turns that secret into an unknown device. The
/// exclusion in the issue is the <i>conversation</i> store, not this one.</para>
///
/// <para>These run on every machine and in CI with no service to stand up, which is the point —
/// a persistence layer behind a conditional skip is a persistence layer nobody has run. Where a
/// test needs a "restart", it disposes the store and opens a new one over the same file, which is
/// what a container restart does to it.</para>
/// </summary>
public sealed class DurableAuthStoreTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("fleet-comms-auth-").FullName;

    private string DatabasePath => Path.Combine(_directory, "auth.db");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    // ── the headline property ────────────────────────────────────────────────

    [Fact]
    public async Task ARegisteredDeviceStillAuthenticatesAfterARestart()
    {
        string deviceId, deviceSecret;

        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var auth = Service(store);
            var code = await auth.IssueEnrollmentCodeAsync("p_owner");
            var device = (await auth.RegisterDeviceAsync(code)).Value!;
            (deviceId, deviceSecret) = (device.DeviceId, device.DeviceSecret);
        }

        // A new process, over the same file, with no memory of anything.
        using (var restarted = new SqliteAuthStore(DatabasePath))
        {
            var auth = Service(restarted);

            var minted = await auth.MintTokenAsync(deviceId, deviceSecret);
            Assert.True(minted.Succeeded);

            var principal = await auth.AuthenticateAsync(minted.Value!.AccessToken);
            Assert.True(principal.Succeeded);
            Assert.Equal("p_owner", principal.Value!.PrincipalId);
        }
    }

    [Fact]
    public async Task RevocationSurvivesARestart()
    {
        string deviceId, deviceSecret, token;

        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var auth = Service(store);
            var device = (await auth.RegisterDeviceAsync(
                await auth.IssueEnrollmentCodeAsync("p_owner"))).Value!;
            (deviceId, deviceSecret) = (device.DeviceId, device.DeviceSecret);

            token = (await auth.MintTokenAsync(deviceId, deviceSecret)).Value!.AccessToken;
            var principal = (await auth.AuthenticateAsync(token)).Value!;
            Assert.True((await auth.RevokeSelfAsync(principal, deviceId)).Succeeded);
        }

        using (var restarted = new SqliteAuthStore(DatabasePath))
        {
            var auth = Service(restarted);

            // A revocation that a restart could undo would be worse than no revocation at all:
            // the operator has been told the stolen device is out, and it would be back.
            Assert.False((await auth.AuthenticateAsync(token)).Succeeded);
            Assert.False((await auth.MintTokenAsync(deviceId, deviceSecret)).Succeeded);
        }
    }

    [Fact]
    public async Task AConsumedEnrollmentCodeStaysConsumedAcrossARestart()
    {
        string code;

        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var auth = Service(store);
            code = await auth.IssueEnrollmentCodeAsync("p_owner");
            var device = (await auth.RegisterDeviceAsync(code)).Value!;
            await auth.MintTokenAsync(device.DeviceId, device.DeviceSecret);
        }

        using (var restarted = new SqliteAuthStore(DatabasePath))
        {
            // The recovery window closed on the first mint, and the mint is in the file.
            Assert.False((await Service(restarted).RegisterDeviceAsync(code)).Succeeded);
        }
    }

    [Fact]
    public async Task AnExpiredTokenBurnedBeforeARestart_IsStillRefusedAfterOne()
    {
        var time = new TestTimeProvider();
        string token;

        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var auth = Service(store, time);
            var device = (await auth.RegisterDeviceAsync(
                await auth.IssueEnrollmentCodeAsync("p_owner"))).Value!;
            token = (await auth.MintTokenAsync(device.DeviceId, device.DeviceSecret)).Value!.AccessToken;

            time.Advance(AuthService.AccessTokenTtl);
            Assert.False((await auth.AuthenticateAsync(token)).Succeeded);
        }

        // The restart is the case the monotonic clock cannot cover — it has to re-anchor to
        // whatever the host says, and here the host says the token has not expired yet. The burn
        // written during the refusal is what still refuses it.
        using (var restarted = new SqliteAuthStore(DatabasePath))
        {
            var rewound = new TestTimeProvider();
            Assert.False((await Service(restarted, rewound).AuthenticateAsync(token)).Succeeded);
        }
    }

    // ── concurrency, atomicity, rollback ─────────────────────────────────────

    /// <summary>
    /// Two registrations for the same owner arriving together. "Exactly one active device" is a
    /// read-then-write decision, so it holds only if the transaction takes its write lock before
    /// the read — which is why the store uses `BEGIN IMMEDIATE` rather than a deferred
    /// transaction. Enforced by the engine, not by an in-process latch that a second instance of
    /// this service would not share.
    /// </summary>
    [Fact]
    public async Task ConcurrentRegistrationsProduceExactlyOneActiveDevice()
    {
        using var store = new SqliteAuthStore(DatabasePath);
        var auth = Service(store);

        var codes = new List<string>();
        for (var i = 0; i < 8; i++)
            codes.Add(await auth.IssueEnrollmentCodeAsync("p_owner"));

        var results = await Task.WhenAll(codes.Select(c => auth.RegisterDeviceAsync(c)));

        var registered = results.Where(r => r.Succeeded).ToList();
        Assert.Single(registered);
        Assert.All(results.Where(r => !r.Succeeded),
            r => Assert.Equal(ProtocolErrorCode.DeviceLimit, r.Error));

        // And the store agrees with the answers it gave.
        await store.InTransactionAsync(async (tx, ct) =>
        {
            Assert.Equal(1, await tx.CountActiveDevicesAsync("p_owner", ct));
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AFailedTransactionRollsBackEveryWriteInIt()
    {
        using var store = new SqliteAuthStore(DatabasePath);
        var auth = Service(store);
        var code = await auth.IssueEnrollmentCodeAsync("p_owner");
        var enrollmentId = code.Split('.')[0];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.InTransactionAsync<int>(async (tx, ct) =>
            {
                var enrollment = await tx.FindEnrollmentAsync(enrollmentId, ct);
                await tx.SaveEnrollmentAsync(
                    enrollment! with { ConsumedAt = DateTimeOffset.UtcNow, DeviceId = "d_phantom" }, ct);
                await tx.SaveDeviceAsync(new DeviceRecord
                {
                    DeviceId = "d_phantom",
                    PrincipalId = "p_owner",
                    SecretHash = "irrelevant",
                    EnrollmentId = enrollmentId,
                    RegisteredAt = DateTimeOffset.UtcNow,
                }, ct);

                throw new InvalidOperationException("simulated failure after writing");
            }, CancellationToken.None));

        // Nothing survived — and, critically, the owner is not locked out: a code that was
        // half-consumed would be both spent and unusable, with no path back.
        await store.InTransactionAsync(async (tx, ct) =>
        {
            Assert.Null(await tx.FindDeviceAsync("d_phantom", ct));
            Assert.Null((await tx.FindEnrollmentAsync(enrollmentId, ct))!.ConsumedAt);
            return 0;
        }, CancellationToken.None);

        Assert.True((await auth.RegisterDeviceAsync(code)).Succeeded);
    }

    [Fact]
    public async Task AnUnreachableStoreFailsClosedAsUnavailable()
    {
        // The path is a directory, so the engine cannot open it as a database. Chosen over a
        // permission bit because the container runs as root, where a permission bit means nothing.
        var blocked = Path.Combine(_directory, "not-a-file");
        Directory.CreateDirectory(blocked);

        using var store = new SqliteAuthStore(blocked);

        await Assert.ThrowsAsync<AuthStoreUnavailableException>(() =>
            store.InTransactionAsync<int>((_, _) => Task.FromResult(0), CancellationToken.None));
    }

    [Fact]
    public async Task AStoreFailureCarriesNoFilesystemPathIntoItsMessage()
    {
        var blocked = Path.Combine(_directory, "secret-looking-name");
        Directory.CreateDirectory(blocked);

        using var store = new SqliteAuthStore(blocked);

        var thrown = await Assert.ThrowsAsync<AuthStoreUnavailableException>(() =>
            store.InTransactionAsync<int>((_, _) => Task.FromResult(0), CancellationToken.None));

        // §13: error text never comes from a runtime exception, and a store path is exactly the
        // kind of detail that reaches a log line by accident.
        Assert.DoesNotContain("secret-looking-name", thrown.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, thrown.ToString(), StringComparison.Ordinal);
    }

    // ── the deployable, not just the class ───────────────────────────────────

    /// <summary>
    /// The composition test. Everything above proves <see cref="SqliteAuthStore"/> works; this
    /// proves it is what a deployment gets — over the real route table, surviving a restart of the
    /// whole application, with nothing substituted.
    /// </summary>
    [Fact]
    public async Task TheDeployableDefaultStoreIsDurableAcrossAnApplicationRestart()
    {
        string deviceId, deviceSecret;

        await using (var app = BuildDeployableApp())
        {
            await app.StartAsync();
            Assert.IsType<SqliteAuthStore>(app.Services.GetRequiredService<IAuthStore>());

            var auth = app.Services.GetRequiredService<AuthService>();
            var code = await auth.IssueEnrollmentCodeAsync("p_owner");

            var client = app.GetTestClient();
            var registration = await client.PostAsJsonAsync("/v1/auth/devices",
                new { protocol = ProtocolVersion.Current, enrollmentCode = code });
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

            var device = (await registration.Content
                .ReadFromJsonAsync<NorthTestHost.RegisterDeviceBody>(FleetProtocolJson.Options))!;
            (deviceId, deviceSecret) = (device.DeviceId, device.DeviceSecret);

            await app.StopAsync();
        }

        await using (var restarted = BuildDeployableApp())
        {
            await restarted.StartAsync();
            var client = restarted.GetTestClient();

            var minted = await client.PostAsJsonAsync("/v1/auth/token",
                new { protocol = ProtocolVersion.Current, deviceId, deviceSecret });
            Assert.Equal(HttpStatusCode.OK, minted.StatusCode);

            var token = (await minted.Content
                .ReadFromJsonAsync<NorthTestHost.TokenBody>(FleetProtocolJson.Options))!;

            var session = new HttpRequestMessage(HttpMethod.Get, "/v1/session");
            session.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.AccessToken}");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(session)).StatusCode);

            await restarted.StopAsync();
        }
    }

    /// <summary>
    /// A missing store path fails when the application is <b>built</b>, not on the first request
    /// that needs it.
    ///
    /// <para>The two failures are not interchangeable. An unreachable store is temporary and is a
    /// `503` a client retries; an unset path is an operator mistake that will never resolve itself,
    /// and deferring it to the first request means the deployment comes up, reports healthy, and
    /// only announces the problem to whoever first tries to enroll a device.</para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingStorePathFailsAtStartup_NotOnTheFirstRequest(string path)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => BuildDeployableApp(storePath: path));

        Assert.Contains(nameof(CommsOptions.AuthStorePath), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoBakedInStorePathToFallBackOn()
    {
        // A default such as "auth.db" is relative, resolves against the working directory, and lets
        // a deployment that never set the key come up healthy and lose every registration on the
        // next container recreation. The absence of a default is the guard.
        Assert.Equal("", new CommsOptions().AuthStorePath);

        Assert.Throws<ArgumentException>(() => new SqliteAuthStore("   "));
    }

    // ── engine failures inside the transaction body ──────────────────────────

    /// <summary>
    /// A real <c>SQLITE_FULL</c>, produced by capping the isolated test database with
    /// <c>PRAGMA max_page_count</c> rather than by manufacturing an exception — so this exercises
    /// the mapping, not the catch block.
    ///
    /// <para>Opening, beginning and committing a transaction already translated engine errors. The
    /// statements the store issues <i>inside</i> the body did not, so a database out of space
    /// surfaced as `500 internal` — which per §5.2 tells the client the server is broken and to
    /// stop retrying, when the correct answer is `503` and retry with backoff.</para>
    /// </summary>
    [Fact]
    public async Task AFullDatabaseIsStoreUnavailable_NotAnUnhandledFault()
    {
        string deviceId, deviceSecret;
        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var auth = Service(store);
            var device = (await auth.RegisterDeviceAsync(
                await auth.IssueEnrollmentCodeAsync("p_owner"))).Value!;
            (deviceId, deviceSecret) = (device.DeviceId, device.DeviceSecret);
        }

        // Scoped, and disposed before the store below is opened. `max_page_count` lives on the
        // connection, connections are pooled, and the pool is keyed on the connection string — which
        // the session pragma is deliberately not part of. Without disposing first, the "released"
        // store below draws a still-capped connection out of the pool and the cap appears permanent.
        using (var capped = new SqliteAuthStore(DatabasePath, SessionPragmaCappingGrowth()))
        {
            var cappedAuth = Service(capped);

            // Mint until the engine runs out of pages. The cap is the file's current size, so any
            // growth fails; how many rows fit in existing free space is an implementation detail.
            var thrown = await Assert.ThrowsAsync<AuthStoreUnavailableException>(async () =>
            {
                for (var attempt = 0; attempt < 500; attempt++)
                    await cappedAuth.MintTokenAsync(deviceId, deviceSecret);
            });

            // The message must not carry the engine's own text — §13, same rule as the path check.
            Assert.DoesNotContain(_directory, thrown.ToString(), StringComparison.Ordinal);
        }

        // Rollback: the failed transaction left nothing behind, and the store works again once the
        // constraint is lifted — so the failure was capacity, not a corrupted file or lost device.
        using var released = new SqliteAuthStore(DatabasePath);
        Assert.True((await Service(released).MintTokenAsync(deviceId, deviceSecret)).Succeeded);
    }

    /// <summary>The same failure over HTTP, where the status code is what a client actually sees.</summary>
    [Fact]
    public async Task AFullDatabaseReturns503OverHttp_Not500()
    {
        string deviceId, deviceSecret;
        using (var store = new SqliteAuthStore(DatabasePath))
        {
            var auth = Service(store);
            var device = (await auth.RegisterDeviceAsync(
                await auth.IssueEnrollmentCodeAsync("p_owner"))).Value!;
            (deviceId, deviceSecret) = (device.DeviceId, device.DeviceSecret);
        }

        using var capped = new SqliteAuthStore(DatabasePath, SessionPragmaCappingGrowth());
        await using var app = BuildDeployableApp(store: capped);
        await app.StartAsync();
        var client = app.GetTestClient();

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 500; attempt++)
        {
            response = await client.PostAsJsonAsync("/v1/auth/token",
                new { protocol = ProtocolVersion.Current, deviceId, deviceSecret });
            if (response.StatusCode != HttpStatusCode.OK)
                break;
        }

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await AuthLifecycleTests.Body<AuthLifecycleTests.ErrorBody>(response);
        Assert.Equal("internal", body.Code);
        Assert.Equal(ProtocolErrors.Internal, body.Message);

        await app.StopAsync();
    }

    /// <summary>
    /// Pin the database at its current size so any growth is `SQLITE_FULL`. Read on a throwaway
    /// connection because `max_page_count` is per-connection, which is exactly why the store needs
    /// a session-pragma seam to be testable this way at all.
    /// </summary>
    private string SessionPragmaCappingGrowth()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_count;";
        var pages = Convert.ToInt64(command.ExecuteScalar());

        return $"PRAGMA max_page_count={pages};";
    }

    private WebApplication BuildDeployableApp(string? storePath = null, IAuthStore? store = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Comms:AuthStorePath"] = storePath ?? DatabasePath,
        });

        // `store` is used only by the fault-injection tests, which need a store constructed with a
        // session pragma. Left null, nothing is substituted and this is the graph Program builds.
        if (store is not null)
            builder.Services.AddSingleton(store);

        return CommsApp.BuildNorthApp(builder);
    }

    private static AuthService Service(IAuthStore store, TimeProvider? time = null) =>
        new(store, new Argon2idSecretHasher(), new MonotonicClock(time ?? TimeProvider.System),
            NullLogger<AuthService>.Instance);
}
