using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services.Push;
using System.Security.Cryptography;
using System.Text;
using System.Net.WebSockets;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Auth;

/// <summary>
/// A clock the test moves by hand, with a wall reading and a monotonic counter that move
/// independently — because that is the only way to write the test that matters.
///
/// <para><see cref="Advance"/> moves both, as real time passing does. <see cref="SetBackwards"/>
/// moves <b>only the wall reading</b>, which is exactly what an NTP step or a restored snapshot
/// does to a host: the monotonic counter is, by definition, unaffected. A fake that moved both
/// together could not distinguish "time passed" from "someone changed the clock", and would
/// therefore pass whether or not the code under test handles a rollback at all.</para>
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "Use SetBackwards for a clock jump.");
        _now += delta;
        _timestamp += delta.Ticks;
    }

    /// <summary>A backward wall-clock jump. The monotonic counter deliberately does not move.</summary>
    public void SetBackwards(TimeSpan delta) => _now -= delta;

    /// <summary>A forward wall-clock correction: only the wall reading moves, unlike <see cref="Advance"/>.</summary>
    public void SetForward(TimeSpan delta) => _now += delta;
}

/// <summary>
/// Wraps a real hasher and counts verifications, so "every rejection costs the same" is asserted
/// as an exact call count rather than as a wall-clock measurement.
/// </summary>
internal sealed class CountingSecretHasher(ISecretHasher inner) : ISecretHasher
{
    private int _verifications;

    public int Verifications => Volatile.Read(ref _verifications);

    public string Hash(string secret) => inner.Hash(secret);

    public bool Verify(string secret, string encodedHash)
    {
        Interlocked.Increment(ref _verifications);
        return inner.Verify(secret, encodedHash);
    }

    public bool VerifyOrDummy(string secret, string? encodedHash)
    {
        Interlocked.Increment(ref _verifications);
        return inner.VerifyOrDummy(secret, encodedHash);
    }
}

/// <summary>
/// The orchestrator's sign-in over real HTTP: the production services, the production pipeline
/// (<see cref="FleetAccess.UseFleetAccess"/>) and the production routes, plus two plain routes to
/// stand for "a read" and "a write" of the operator API.
/// </summary>
internal sealed class SignInHost : IAsyncDisposable
{
    public const string ServiceToken = "synthetic-service-token";
    public const string ConfigToken = "synthetic-config-token";

    /// <summary>A 32-byte key, base64url, as <c>Journal:TokenKey</c> expects.</summary>
    public static readonly string TokenKey = Convert.ToBase64String(new byte[32]).TrimEnd('=');

    private readonly SqliteConnection? _connection;
    private readonly WebApplication _app;

    public TestTimeProvider Time { get; }
    public CountingSecretHasher Hasher { get; }
    public InMemoryAuthStore? MemoryStore { get; }
    public AuthService Auth => _app.Services.GetRequiredService<AuthService>();
    public IServiceProvider Services => _app.Services;

    public JournalTokenService Tokens => _app.Services.GetRequiredService<JournalTokenService>();

    private SignInHost(WebApplication app, SqliteConnection? connection, TestTimeProvider time,
        CountingSecretHasher hasher, InMemoryAuthStore? memoryStore)
    {
        _app = app;
        _connection = connection;
        Time = time;
        Hasher = hasher;
        MemoryStore = memoryStore;
    }

    /// <param name="serviceToken">Null starts an orchestrator that gates nothing.</param>
    /// <param name="durable">True runs on the database store (SQLite in memory); false on the in-memory store.</param>
    /// <param name="tokenKey">The key shared with the chat service. Null starts a deployment that has none.</param>
    public static async Task<SignInHost> StartAsync(
        string? serviceToken = ServiceToken, string? configToken = ConfigToken, bool durable = false,
        string? tokenKey = "default", IWorkflowSignalClient? signalClient = null, bool push = false, bool sockets = false)
    {
        if (tokenKey == "default") tokenKey = TokenKey;

        var time = new TestTimeProvider();
        // Real Argon2id at a fraction of the production cost: the property under test is how many
        // times it runs, never how long it takes.
        var hasher = new CountingSecretHasher(new Argon2idSecretHasher(memoryKib: 64, iterations: 1));
        InMemoryAuthStore? memoryStore = durable ? null : new InMemoryAuthStore();
        SqliteConnection? connection = null;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:AuthToken"] = serviceToken,
            ["Orchestrator:ConfigToken"] = configToken,
            ["Journal:TokenKey"] = tokenKey,
        });

        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<ISecretHasher>(hasher);
        if (durable)
        {
            connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(connection));
        }
        else
        {
            builder.Services.AddSingleton<IAuthStore>(memoryStore!);
        }

        builder.Services.AddFleetSignIn(durable);
        builder.Services.AddSingleton<JournalTokenService>();
        builder.Services.AddRateLimiter(rl =>
        {
            rl.AddSignInPolicy();
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        if (signalClient is not null) builder.Services.AddSingleton(signalClient);
        if (push)
        {
            builder.Services.Configure<PushOptions>(o => {
                o.Enabled = true;
                o.ApnsKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem()));
                o.ApnsKeyId = "SYNTHETIC"; o.ApnsTeamId = "SYNTHETIC"; o.ApnsTopic = "example.invalid";
            });
            builder.Services.AddSingleton<PushReadiness>();
            builder.Services.AddSingleton<ChatReplyPushes>();
        }
        var app = builder.Build();
        if (durable)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreatedAsync();
        }

        await app.Services.GetRequiredService<AuthService>().EnsureOwnerAsync(null);

        app.UseWebSockets();
        app.UseRateLimiter();
        app.UseFleetAccess();
        app.MapAuthEndpoints();
        if (signalClient is not null) app.MapWorkflowSignalEndpoints();
        if (push) app.MapPushEndpoints();
        app.MapGet("/api/agents", () => Results.Ok(new { read = true }));
        app.MapPost("/api/agents/example/restart", () => Results.Ok(new { wrote = true }));
        app.MapGet("/api/config/values", () => Results.Ok(new { config = true }));
        app.MapPut("/api/config/values", () => Results.Ok(new { config = true }));
        if (!sockets) app.MapGet("/ws", () => Results.Ok(new { socket = true }));
        else app.Map("/ws", async (HttpContext http) => {
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            var watch = ControlSocketAccess.WatchAsync(http, socket, lifetime);
            try {
                while (!lifetime.IsCancellationRequested) {
                    await socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, lifetime.Token);
                    await Task.Delay(25, lifetime.Token);
                }
            } catch (Exception) when (lifetime.IsCancellationRequested || socket.State != WebSocketState.Open) { }
            finally { await lifetime.CancelAsync(); await watch; }
        });
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        await app.StartAsync();
        return new SignInHost(app, connection, time, hasher, memoryStore);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    /// <summary>Add an agent row. Needs the database store (<c>durable: true</c>).</summary>
    public async Task SeedAgentAsync(string name, bool chatEnabled, string? displayName = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(new Agent
        {
            Name = name,
            DisplayName = displayName ?? name,
            Role = "test",
            Model = "example-model",
            Provider = "claude",
            MemoryLimitMb = 1024,
            ContainerName = $"fleet-{name}",
            ChatEnabled = chatEnabled,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A client that keeps no cookies of its own: every credential a request carries is put there by the test.</summary>
    public HttpClient Client() => _app.GetTestClient();
    public WebSocketClient SocketClient() => _app.GetTestServer().CreateWebSocketClient();

    // ── requests ─────────────────────────────────────────────────────────────

    public static HttpRequestMessage Request(
        HttpMethod method, string path, string? bearer = null, string? cookie = null,
        bool edge = true, bool requestHeader = false, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (cookie is not null) request.Headers.Add("Cookie", $"{CallerAuthenticator.SessionCookie}={cookie}");
        if (edge) request.Headers.Add(OrchestratorAuth.EdgeHeader, "1");
        if (requestHeader) request.Headers.Add(OrchestratorAuth.RequestHeader, "1");
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? bearer = null, string? cookie = null,
        bool edge = true, bool requestHeader = false, object? body = null) =>
        Client().SendAsync(Request(method, path, bearer, cookie, edge, requestHeader, body));

    // ── flows ────────────────────────────────────────────────────────────────

    /// <summary>Sign a browser in and return the value of its session cookie.</summary>
    public async Task<string> SignInBrowserAsync(string? token = ServiceToken, string? name = "Test browser")
    {
        var response = await SendAsync(HttpMethod.Post, "/api/auth/browser-session",
            body: new { token, deviceName = name });
        response.EnsureSuccessStatusCode();
        return SessionCookieOf(response)
               ?? throw new InvalidOperationException("sign-in set no session cookie");
    }

    public async Task<string> IssueEnrollmentCodeAsync(string browserCookie, string? name = "Test phone")
    {
        var response = await SendAsync(HttpMethod.Post, "/api/auth/enrollments",
            cookie: browserCookie, requestHeader: true, body: new { deviceName = name });
        response.EnsureSuccessStatusCode();
        return (await JsonAsync(response)).GetProperty("enrollmentCode").GetString()!;
    }

    public Task<HttpResponseMessage> RegisterAsync(string? enrollmentCode, string? name = null) =>
        SendAsync(HttpMethod.Post, "/api/auth/devices", body: new { enrollmentCode, deviceName = name });

    public Task<HttpResponseMessage> TokenAsync(string? deviceId, string? deviceSecret) =>
        SendAsync(HttpMethod.Post, "/api/auth/token", body: new { deviceId, deviceSecret });

    /// <summary>A browser, then a phone enrolled from it: (cookie, deviceId, deviceSecret, accessToken).</summary>
    public async Task<(string Cookie, string DeviceId, string DeviceSecret, string AccessToken)> EnrolledAppAsync()
    {
        var cookie = await SignInBrowserAsync();
        var registered = await JsonAsync(await RegisterAsync(await IssueEnrollmentCodeAsync(cookie)));
        var deviceId = registered.GetProperty("deviceId").GetString()!;
        var deviceSecret = registered.GetProperty("deviceSecret").GetString()!;
        var minted = await JsonAsync(await TokenAsync(deviceId, deviceSecret));
        return (cookie, deviceId, deviceSecret, minted.GetProperty("accessToken").GetString()!);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>The session cookie a response set, or null. An emptied cookie reads as null.</summary>
    public static string? SessionCookieOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        foreach (var value in values)
        {
            var pair = value.Split(';')[0];
            if (!pair.StartsWith(CallerAuthenticator.SessionCookie + "=", StringComparison.Ordinal)) continue;
            var cookie = pair[(CallerAuthenticator.SessionCookie.Length + 1)..];
            return cookie.Length == 0 ? null : cookie;
        }

        return null;
    }

    public static string? SetCookieHeaderOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(CallerAuthenticator.SessionCookie + "=", StringComparison.Ordinal))
            : null;
}
