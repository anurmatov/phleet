using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.Push;
using Fleet.Orchestrator.Tests.EpicGrants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.Push;

/// <summary>
/// <c>/api/push</c> over real HTTP, behind the production gate, and delivery to registered devices.
/// </summary>
/// <remarks>
/// The host maps the production routes and calls the production predicate
/// (<see cref="OrchestratorAuth.RequiresBearerToken"/>) rather than restating it, so a route that
/// stopped being a write, or a gate that stopped covering it, turns a test here red.
/// </remarks>
public class PushEndpointsTests : IAsyncLifetime
{
    private const string Bearer = "synthetic-operator-token";
    private const string TokenA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string TokenB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private bool _enabled = true;
    private string _key = KeyBase64(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    private static string KeyBase64(ECDsa key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));

    public Task InitializeAsync() => StartAsync();

    private async Task StartAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.Configure<PushOptions>(o =>
        {
            o.Enabled = _enabled;
            o.ApnsKeyBase64 = _key;
            o.ApnsKeyId = "KEYID12345";
            o.ApnsTeamId = "TEAMID1234";
            o.ApnsTopic = "com.example.app";
        });
        builder.Services.AddSingleton<PushReadiness>();

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (OrchestratorAuth.RequiresBearerToken(context.Request.Method, context.Request.Path.Value ?? ""))
            {
                var header = context.Request.Headers.Authorization.FirstOrDefault();
                var token = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? header["Bearer ".Length..].Trim() : null;
                if (token != Bearer)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                    return;
                }
            }
            await next(context);
        });
        _app.MapPushEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreatedAsync();

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<HttpResponseMessage> PutAsync(string deviceId, object body, bool authorized = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/push/devices/{deviceId}") { Content = JsonContent.Create(body) };
        if (authorized) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> DeleteAsync(string deviceId, bool authorized = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/push/devices/{deviceId}");
        if (authorized) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        return _client.SendAsync(request);
    }

    private async Task<List<PushDevice>> DevicesAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().PushDevices.AsNoTracking().OrderBy(d => d.DeviceId).ToListAsync();
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;

    // ── Registering ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registering_NeedsTheOperatorToken()
    {
        using var put = await PutAsync("device-0001", new { token = TokenA }, authorized: false);
        using var delete = await DeleteAsync("device-0001", authorized: false);

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Empty(await DevicesAsync());
    }

    [Fact]
    public async Task ADevice_IsStored_AndATokenThatChangesReplacesTheOldOne()
    {
        using var first = await PutAsync("device-0001", new { token = TokenA });
        using var second = await PutAsync("device-0001", new { token = $"  {TokenB}  " });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var device = Assert.Single(await DevicesAsync());
        Assert.Equal(TokenB, device.Token);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has%20space-in-it")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task ADeviceId_ThatIsNotAPlainIdentifier_IsRefused(string deviceId)
    {
        using var response = await PutAsync(deviceId, new { token = TokenA });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("device_id_invalid", await ErrorAsync(response));
        Assert.Empty(await DevicesAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("0123456789abcdef0123456789abcdef/../x")]
    public async Task SomethingThatIsNotADeviceToken_IsRefused(string token)
    {
        using var response = await PutAsync("device-0001", new { token });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("token_invalid", await ErrorAsync(response));
        Assert.Empty(await DevicesAsync());
    }

    [Fact]
    public async Task TheEleventhDevice_IsRefused_ButAKnownOneCanStillUpdate()
    {
        for (var i = 0; i < PushEndpoints.MaxDevices; i++)
            (await PutAsync($"device-{i:0000}", new { token = TokenA })).Dispose();

        using var extra = await PutAsync("device-9999", new { token = TokenA });
        using var known = await PutAsync("device-0000", new { token = TokenB });

        Assert.Equal(HttpStatusCode.Conflict, extra.StatusCode);
        Assert.Equal("device_limit", await ErrorAsync(extra));
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(PushEndpoints.MaxDevices, (await DevicesAsync()).Count);
    }

    [Fact]
    public async Task WhenTheFeatureIsOff_RegisteringSaysSo_AndStoresNothing()
    {
        await DisposeAsync();
        _enabled = false;
        await StartAsync();

        using var response = await PutAsync("device-0001", new { token = TokenA });
        using var status = await _client.GetAsync("/api/push");

        // An app that registered against an orchestrator that will never send would otherwise tell
        // its owner that notifications are on.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("push_disabled", await ErrorAsync(response));
        Assert.Empty(await DevicesAsync());
        Assert.False(JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement.GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong-curve")]
    [InlineData("other-256-bit-curve")]
    [InlineData("public-only")]
    public async Task AKeyTheSenderCannotUse_KeepsTheNotifierOff_AndTheRouteSaysSo(string kind)
    {
        await DisposeAsync();
        _key = kind switch
        {
            "missing" => "",
            "malformed" => "synthetic",
            "wrong-curve" => KeyBase64(ECDsa.Create(ECCurve.NamedCurves.nistP384)),
            "other-256-bit-curve" => KeyBase64(ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1)),
            _ => Convert.ToBase64String(Encoding.UTF8.GetBytes(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfoPem())),
        };
        await StartAsync();

        // The notifier, given the same readiness as the routes and no client to build: it must
        // stop before asking for one.
        var log = new CapturingLogger<PushNotifierService>();
        using var notifier = new PushNotifierService(
            _app.Services.GetRequiredService<IOptions<PushOptions>>(),
            _app.Services.GetRequiredService<PushReadiness>(),
            new WorkflowStore(),
            new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance),
            new TemporalClientRegistry(Options.Create(new TemporalOptions()), NullLogger<TemporalClientRegistry>.Instance),
            new OperatorStops(),
            _app.Services.GetRequiredService<IServiceScopeFactory>(),
            _app.Services,
            TimeProvider.System,
            log);
        await notifier.StartAsync(CancellationToken.None);
        await notifier.ExecuteTask!;

        using var response = await PutAsync("device-0001", new { token = TokenA });
        using var status = await _client.GetAsync("/api/push");

        var line = Assert.Single(log.Lines);
        Assert.StartsWith("push_misconfigured: ", line);
        if (_key.Length > 0) Assert.DoesNotContain(_key, line);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("push_disabled", await ErrorAsync(response));
        Assert.Empty(await DevicesAsync());
        Assert.False(JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement.GetProperty("enabled").GetBoolean());
    }

    // ── Reading and removing ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheStatus_SaysWhetherItIsOnAndHowManyDevices_AndNeverShowsAToken()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();

        using var response = await _client.GetAsync("/api/push");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, root.GetProperty("devices").GetInt32());
        Assert.DoesNotContain(TokenA, body);
        Assert.DoesNotContain("device-0001", body);
    }

    [Fact]
    public async Task Removing_ForgetsThatDevice_AndRemovingOneThatIsNotThereIsNotAnError()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();
        (await PutAsync("device-0002", new { token = TokenB })).Dispose();

        using var removed = await DeleteAsync("device-0001");
        using var again = await DeleteAsync("device-0001");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("device-0002", Assert.Single(await DevicesAsync()).DeviceId);
    }

    // ── Delivery ─────────────────────────────────────────────────────────────────────────────

    private sealed class FakeApns(Func<string, ApnsResult> answer) : IApnsClient
    {
        public List<(string Token, string Kind)> Sent { get; } = [];

        public Task<ApnsResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct)
        {
            Sent.Add((deviceToken, message.Kind));
            return Task.FromResult(answer(deviceToken));
        }
    }

    private static readonly PushMessage Gate = new("gate", "Merge approval waiting", "example/project · PR #42", new Dictionary<string, string>(), "k1");
    private static readonly PushMessage Failure = new("run", "Workflow failed", "example/project · PR #7", new Dictionary<string, string>(), "k2");

    private PushDelivery NewDelivery(IApnsClient apns) =>
        new(_app.Services.GetRequiredService<IServiceScopeFactory>(), apns, NullLogger.Instance);

    private Task DeliverAsync(IApnsClient apns, params PushMessage[] messages) =>
        NewDelivery(apns).DeliverAsync(messages, _ => true, CancellationToken.None);

    [Fact]
    public async Task EveryMessage_GoesToEveryDevice()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();
        (await PutAsync("device-0002", new { token = TokenB })).Dispose();
        var apns = new FakeApns(_ => new ApnsResult(ApnsOutcome.Accepted));

        await DeliverAsync(apns, Gate, Failure);

        Assert.Equal(
            [(TokenA, "gate"), (TokenB, "gate"), (TokenA, "run"), (TokenB, "run")],
            apns.Sent);
    }

    [Fact]
    public async Task ADeviceAppleSaysIsGone_IsForgotten_AndNotAskedAgain()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();
        (await PutAsync("device-0002", new { token = TokenB })).Dispose();
        var apns = new FakeApns(token => new ApnsResult(token == TokenA ? ApnsOutcome.Unregistered : ApnsOutcome.Accepted, "Unregistered"));

        await DeliverAsync(apns, Gate, Failure);

        Assert.Equal("device-0002", Assert.Single(await DevicesAsync()).DeviceId);
        Assert.Single(apns.Sent, s => s.Token == TokenA);
    }

    [Fact]
    public async Task ADeliveryThatFails_KeepsTheDevice()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();
        var apns = new FakeApns(_ => new ApnsResult(ApnsOutcome.Failed, "TooManyRequests"));

        await DeliverAsync(apns, Gate);

        Assert.Single(await DevicesAsync());
    }

    [Fact]
    public async Task WithNoDevices_NothingIsSent()
    {
        var apns = new FakeApns(_ => new ApnsResult(ApnsOutcome.Accepted));

        await DeliverAsync(apns, Gate);

        Assert.Empty(apns.Sent);
    }

    // ── Across passes: the notifier's own pass, with the real detector ───────────────────────

    private static readonly ApnsResult Accepted = new(ApnsOutcome.Accepted);
    private static readonly ApnsResult Unavailable = new(ApnsOutcome.Failed, "ServiceUnavailable");

    private static WorkflowSummary GateRun(string phase = "merge-approval") =>
        new("pr-42", "run-1", "UwePrImplementationWorkflow", "fleet", "fleet", "Running", DateTimeOffset.UtcNow.AddHours(-1),
            PrNumber: 42, Repo: "example/project", Phase: phase, GateRef: "gate-a");

    private sealed class Passes(PushDelivery delivery)
    {
        private readonly PushEventDetector _detector = new(TimeSpan.FromMinutes(5), new OperatorStops());
        public PushDelivery Delivery { get; } = delivery;

        public Task RunAsync(params WorkflowSummary[] running) =>
            PushNotifierService.PassAsync(_detector, Delivery, running, [], [], DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task ATransientFailure_IsSentAgain_ToThatDeviceOnly_UntilItIsAccepted()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();
        (await PutAsync("device-0002", new { token = TokenB })).Dispose();
        var appleDown = true;
        var apns = new FakeApns(token => token == TokenA && appleDown ? Unavailable : Accepted);
        var passes = new Passes(NewDelivery(apns));

        await passes.RunAsync();
        Assert.Empty(apns.Sent);

        await passes.RunAsync(GateRun());
        Assert.Equal([(TokenA, "gate"), (TokenB, "gate")], apns.Sent);
        Assert.Equal(1, passes.Delivery.Owed);

        // The detector has nothing new; the device that missed it is sent it again, the other is not.
        appleDown = false;
        await passes.RunAsync(GateRun());
        Assert.Equal([(TokenA, "gate"), (TokenB, "gate"), (TokenA, "gate")], apns.Sent);
        Assert.Equal(0, passes.Delivery.Owed);

        await passes.RunAsync(GateRun());
        Assert.Equal(3, apns.Sent.Count);
    }

    [Fact]
    public async Task AFailedNotification_IsNotSentAgain_OnceItsEventHasResolved()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();
        var apns = new FakeApns(_ => Unavailable);
        var passes = new Passes(NewDelivery(apns));
        await passes.RunAsync();
        await passes.RunAsync(GateRun());
        Assert.Single(apns.Sent);

        // The gate was decided while Apple was down: the phone is not told about it now.
        await passes.RunAsync(GateRun(phase: "implementation"));

        Assert.Single(apns.Sent);
        Assert.Equal(0, passes.Delivery.Owed);
    }

    [Fact]
    public async Task ARefusalThatNeverClears_StopsAfterTheLastAttempt()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();
        var apns = new FakeApns(_ => new ApnsResult(ApnsOutcome.Failed, "TopicDisallowed"));
        var passes = new Passes(NewDelivery(apns));
        await passes.RunAsync();

        for (var i = 0; i < PushDelivery.MaxAttempts + 5; i++) await passes.RunAsync(GateRun());

        Assert.Equal(PushDelivery.MaxAttempts, apns.Sent.Count);
        Assert.Equal(0, passes.Delivery.Owed);
        Assert.Single(await DevicesAsync());
    }

    [Fact]
    public async Task ADeviceRemovedBeforeTheRetry_IsOwedNothing()
    {
        (await PutAsync("device-0001", new { token = TokenA })).Dispose();
        var apns = new FakeApns(_ => Unavailable);
        var passes = new Passes(NewDelivery(apns));
        await passes.RunAsync();
        await passes.RunAsync(GateRun());

        (await DeleteAsync("device-0001")).Dispose();
        await passes.RunAsync(GateRun());

        Assert.Single(apns.Sent);
        Assert.Equal(0, passes.Delivery.Owed);
    }
}
