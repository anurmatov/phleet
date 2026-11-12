using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

// Runs against CI's real MySQL service; missing database is a failure, never a skip.
public sealed class AddSignInMigrationTests : IAsyncLifetime
{
    private const string Before = "20261111162955_AddPushDevices";
    private const string Target = "20261112134638_AddSignIn";
    private static readonly string[] Tables = ["auth_users", "auth_devices", "auth_enrollments", "auth_tokens"];

    private string _admin = "";
    private string _database = "";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(RemovedProviderColumnMigrationTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{RemovedProviderColumnMigrationTests.ConnectionVariable} is not set, so the migration tests have no MySQL to run against.\n" +
                "  This FAILS rather than skipping on purpose: a skipped migration test reads as a pass.");

        _admin = new MySqlConnectionStringBuilder(configured) { Database = "" }.ConnectionString;
        _database = $"orch_mig_{Guid.NewGuid():N}"[..30];
        await ExecAsync(_admin, $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
        _connectionString = new MySqlConnectionStringBuilder(configured) { Database = _database }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_database.Length > 0)
            await ExecAsync(_admin, $"DROP DATABASE IF EXISTS `{_database}`");
    }

    [Fact]
    public async Task Up_CreatesTheFourTables_AndDownRemovesThem()
    {
        await MigrateAsync(Before);
        Assert.Equal(0L, await TableCountAsync());

        await MigrateAsync(Target);
        Assert.Equal(4L, await TableCountAsync());

        await MigrateAsync(Before);
        Assert.Equal(0L, await TableCountAsync());

        await MigrateAsync(Target);
        Assert.Equal(4L, await TableCountAsync());
    }

    [Fact]
    public async Task ADeviceCannotExistWithoutItsUser()
    {
        await MigrateAsync(Target);

        var orphan = await Assert.ThrowsAsync<MySqlException>(() => ExecAsync(_connectionString, """
            INSERT INTO auth_devices (DeviceId, UserId, Kind, Name, SecretHash, RegisteredAt)
            VALUES ('synthetic-device', 'nobody', 'app', '', '', UTC_TIMESTAMP())
            """));

        Assert.Equal(MySqlErrorCode.NoReferencedRow2, orphan.ErrorCode);
    }

    /// <summary>
    /// The production path end to end: the real store, on the real database, under the same
    /// retrying execution strategy the orchestrator configures — which refuses a transaction that
    /// is not opened through it.
    /// </summary>
    [Fact]
    public async Task TheStore_RunsSignInOnMySql_UnderTheRetryingStrategy()
    {
        await MigrateAsync(Target);
        await using var provider = NewProvider();
        var auth = new AuthService(
            new EfAuthStore(provider.GetRequiredService<IServiceScopeFactory>()),
            new Argon2idSecretHasher(memoryKib: 64, iterations: 1),
            new MonotonicClock(TimeProvider.System),
            NullLogger<AuthService>.Instance);

        await auth.EnsureOwnerAsync(4242);
        await auth.EnsureOwnerAsync(4242);
        Assert.Equal(4242, (await auth.FindUserAsync(UserRecord.OwnerId))!.TelegramUserId);

        var browser = (await auth.SignInBrowserAsync(UserRecord.OwnerId, "Laptop")).Value!;
        var code = (await auth.IssueEnrollmentCodeAsync(UserRecord.OwnerId, "Phone")).Value!.EnrollmentCode;
        var device = (await auth.RegisterDeviceAsync(code, null)).Value!;
        var token = (await auth.MintTokenAsync(device.DeviceId, device.DeviceSecret)).Value!.AccessToken;

        var asApp = (await auth.AuthenticateAsync(token, TokenKind.Access)).Value!;
        Assert.Equal((UserRecord.OwnerId, device.DeviceId, DeviceKind.App), (asApp.PrincipalId, asApp.DeviceId, asApp.Kind));
        Assert.True((await auth.AuthenticateAsync(browser.SessionToken, TokenKind.Session)).Succeeded);

        var devices = await auth.ListDevicesAsync(UserRecord.OwnerId);
        Assert.Equal(["Laptop", "Phone"], devices.Select(d => d.Name).Order().ToArray());
        // Timestamps come back as UTC, not as an unspecified local time.
        Assert.All(devices, d => Assert.Equal(TimeSpan.Zero, d.RegisteredAt.Offset));
        Assert.InRange(devices[0].RegisteredAt, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(await auth.RevokeDeviceAsync(UserRecord.OwnerId, device.DeviceId));
        Assert.False((await auth.AuthenticateAsync(token, TokenKind.Access)).Succeeded);
        Assert.False((await auth.MintTokenAsync(device.DeviceId, device.DeviceSecret)).Succeeded);
        Assert.True((await auth.AuthenticateAsync(browser.SessionToken, TokenKind.Session)).Succeeded);
    }

    /// <summary>
    /// The database compares these columns without regard to case, and a record id is
    /// case-sensitive base64url. A lookup must not believe a row whose id differs only in case.
    /// </summary>
    [Fact]
    public async Task ALookupDoesNotMatchAnIdThatDiffersOnlyInCase()
    {
        await MigrateAsync(Target);
        await using var provider = NewProvider();
        var store = new EfAuthStore(provider.GetRequiredService<IServiceScopeFactory>());
        var now = DateTimeOffset.UtcNow;

        await store.InTransactionAsync<object?>(async (tx, ct) =>
        {
            await tx.SaveUserAsync(new UserRecord { UserId = UserRecord.OwnerId, CreatedAt = now }, ct);
            await tx.SaveDeviceAsync(new DeviceRecord
            {
                DeviceId = "AbCdEf", PrincipalId = UserRecord.OwnerId, Kind = DeviceKind.App,
                SecretHash = "synthetic", RegisteredAt = now,
            }, ct);
            await tx.SaveTokenAsync(new TokenRecord
            {
                TokenId = "ToKeN", TokenHash = "synthetic", DeviceId = "AbCdEf",
                Kind = TokenKind.Access, ExpiresAt = now.AddMinutes(15),
            }, ct);
            return null;
        }, CancellationToken.None);

        var found = await store.InTransactionAsync(async (tx, ct) => (
            Exact: await tx.FindDeviceAsync("AbCdEf", ct),
            Folded: await tx.FindDeviceAsync("abcdef", ct),
            Token: await tx.FindTokenAsync("token", ct),
            User: await tx.FindUserAsync("OWNER", ct)), CancellationToken.None);

        Assert.NotNull(found.Exact);
        Assert.Null(found.Folded);
        Assert.Null(found.Token);
        Assert.Null(found.User);
    }

    [Fact]
    public async Task ABodyThatThrows_LeavesNothingBehind()
    {
        await MigrateAsync(Target);
        await using var provider = NewProvider();
        var store = new EfAuthStore(provider.GetRequiredService<IServiceScopeFactory>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InTransactionAsync<object?>(async (tx, ct) =>
        {
            await tx.SaveUserAsync(new UserRecord { UserId = "half-written", CreatedAt = DateTimeOffset.UtcNow }, ct);
            throw new InvalidOperationException("synthetic failure after a write");
        }, CancellationToken.None));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM auth_users"));
    }

    private ServiceProvider NewProvider() => new ServiceCollection()
        .AddDbContext<OrchestratorDbContext>(o => o.UseMySql(
            _connectionString, new MySqlServerVersion(new Version(8, 0, 0)),
            mysql => mysql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)))
        .BuildServiceProvider();

    private OrchestratorDbContext NewContext() => new(
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(_connectionString, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options);

    private async Task MigrateAsync(string target)
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private Task<long> TableCountAsync() => ScalarAsync(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ("
        + string.Join(", ", Tables.Select(t => $"'{t}'")) + ")");

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
