using System.Net;
using System.Text;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services;
using Fleet.WireFixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// The broker-to-receiver usage relay: default off, shape and identity checks on the broker callback,
/// one frozen row per POST with the bytes unchanged, and every failure a counted drop.
/// </summary>
public sealed class UsageRelayTests
{
    private const string Origin = "http://usage-receiver.invalid:3900";
    private const string Token = "synthetic-receiver-token";
    private static readonly byte[] Row = WireGolden.SampleBytes("orchestrator.usage.json");

    // ── configuration ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, null, false, null)]
    [InlineData("false", Origin, Token, false, null)]
    [InlineData("true", Origin, Token, true, null)]
    [InlineData("true", Origin + "/", Token, true, null)]
    [InlineData("true", "", Token, false, "usage_receiver_url_invalid")]
    [InlineData("true", "ftp://usage-receiver.invalid", Token, false, "usage_receiver_url_invalid")]
    [InlineData("true", Origin + "/api/usage", Token, false, "usage_receiver_url_invalid")]
    [InlineData("true", Origin + "/?x=1", Token, false, "usage_receiver_url_invalid")]
    [InlineData("true", "http://user:pass@usage-receiver.invalid", Token, false, "usage_receiver_url_invalid")]
    [InlineData("true", Origin, "", false, "usage_auth_token_missing")]
    [InlineData("true", Origin, "two words", false, "usage_auth_token_missing")]
    public void Readiness_RequiresAFixedOriginAndAToken(string? enabled, string? url, string? token, bool ready, string? fault)
    {
        var readiness = Readiness(enabled, url, token);

        Assert.Equal(ready, readiness.IsReady);
        Assert.Equal(fault, readiness.Fault);
        Assert.Equal(ready, UsageOptions.From(Config(enabled, url, token)).IsReady);
    }

    // ── broker side ──────────────────────────────────────────────────────────

    [Fact]
    public void Off_QueuesNothing()
    {
        var relay = Relay(new Handler(_ => throw new InvalidOperationException("must not be called")), enabled: null);

        Assert.False(relay.TryEnqueue("usage.agent1", Row));
    }

    [Theory]
    [InlineData("usage.agent2", "agent_mismatch")]
    [InlineData("usage.", "routing_key")]
    [InlineData("heartbeat.agent1", "routing_key")]
    public void RowNamingAnotherAgent_OrUnderAnotherKey_IsRefused(string routingKey, string reason)
    {
        Assert.Equal(reason, UsageRelay.Validate(routingKey, Row));
    }

    [Theory]
    [InlineData("""not json""", "row_json")]
    [InlineData("""["a"]""", "row_shape")]
    [InlineData("""{"agent":"agent1","provider":"claude","model":"m","turn_started_at":"x","turn_ended_at":"y"}""", "row_shape")]
    [InlineData("""{"turn_id":7,"agent":"agent1","provider":"claude","model":"m","turn_started_at":"x","turn_ended_at":"y"}""", "row_shape")]
    public void MalformedRow_IsRefused(string body, string reason)
    {
        Assert.Equal(reason, UsageRelay.Validate("usage.agent1", Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void OversizedRow_IsRefused()
    {
        var body = Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('x', Fleet.Contracts.AgentTurnUsage.MaxBytes) + "\"}");

        Assert.Equal("row_size", UsageRelay.Validate("usage.agent1", body));
    }

    [Fact]
    public void QueueIsBounded_AndAFullQueueDrops()
    {
        var relay = Relay(new Handler(_ => throw new InvalidOperationException("not started")));

        for (var i = 0; i < UsageRelay.QueueCapacity; i++)
            Assert.True(relay.TryEnqueue("usage.agent1", Row));

        Assert.False(relay.TryEnqueue("usage.agent1", Row));
        Assert.Equal(1, relay.Dropped);
    }

    [Fact]
    public async Task UsageKey_NeverReachesTheHeartbeatPath()
    {
        Assert.True(HeartbeatConsumerService.IsUsageKey("usage.agent1"));
        Assert.False(HeartbeatConsumerService.IsUsageKey("heartbeat.agent1"));
        Assert.False(HeartbeatConsumerService.IsUsageKey("usage-other.agent1"));
        await Task.CompletedTask;
    }

    // ── receiver side ────────────────────────────────────────────────────────

    [Fact]
    public async Task Forward_PostsTheFrozenRowUnchanged_WithTheBearer()
    {
        HttpRequestMessage? sent = null;
        byte[]? payload = null;
        var relay = Relay(new Handler(request =>
        {
            sent = request;
            payload = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{"accepted":1}""");
        }));

        await relay.ForwardAsync(Row, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal(new Uri(Origin + "/api/usage"), sent.RequestUri);
        Assert.Equal(("Bearer", Token), (sent.Headers.Authorization!.Scheme, sent.Headers.Authorization.Parameter));
        Assert.Equal("{\"rows\":[" + Encoding.UTF8.GetString(Row) + "]}", Encoding.UTF8.GetString(payload!));
        Assert.Equal((1L, 0L), (relay.Forwarded, relay.Dropped));
    }

    public static TheoryData<Func<HttpRequestMessage, HttpResponseMessage>, string> Failures => new()
    {
        { _ => Json(HttpStatusCode.Unauthorized, """{"error":"Unauthorized"}"""), "receiver_http_401" },
        { _ => Json(HttpStatusCode.BadRequest, """{"error":"x","row_index":0,"field":"model"}"""), "receiver_http_400" },
        { _ => Json(HttpStatusCode.ServiceUnavailable, """{"error":"insert failed"}"""), "receiver_http_503" },
        { _ => Json(HttpStatusCode.Found, ""), "receiver_http_302" },
        { _ => Json(HttpStatusCode.OK, """{"accepted":0}"""), "receiver_reply" },
        { _ => Json(HttpStatusCode.OK, "not json"), "receiver_reply" },
        { _ => throw new HttpRequestException("no route " + Token), "HttpRequestException" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Forward_AnyFailure_IsOneDrop_NoRetry_AndNeverLogsTheTokenOrRow(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string reason)
    {
        var calls = 0;
        var log = new ListLogger<UsageRelay>();
        var relay = Relay(new Handler(r => { calls++; return respond(r); }), logger: log);

        await relay.ForwardAsync(Row, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal((0L, 1L), (relay.Forwarded, relay.Dropped));
        var line = Assert.Single(log.Lines);
        Assert.Contains(reason, line);
        Assert.DoesNotContain(Token, line);
        Assert.DoesNotContain("session-1", line);
    }

    [Fact]
    public async Task Forward_SlowReceiver_IsDroppedAtTheDeadline()
    {
        var relay = Relay(new SlowHandler());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await relay.ForwardAsync(Row, CancellationToken.None);

        Assert.True(watch.Elapsed < UsageRelay.RequestDeadline + TimeSpan.FromSeconds(3));
        Assert.Equal(1L, relay.Dropped);
    }

    [Fact]
    public async Task Worker_DiscardsARowOlderThanAMinute()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var relay = new UsageRelay(Readiness("true", Origin, Token), new Factory(new Handler(_ =>
        {
            calls++;
            return Json(HttpStatusCode.OK, """{"accepted":1}""");
        })), NullLogger<UsageRelay>.Instance)
        {
            Clock = () => now,
        };

        relay.TryEnqueue("usage.agent1", Row);   // queued "now"
        now += UsageRelay.MaxAge + TimeSpan.FromSeconds(1);
        relay.TryEnqueue("usage.agent1", Row);   // queued after the clock moved: fresh

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await relay.StartAsync(stop.Token);
        while (relay.Forwarded + relay.Dropped < 2 && !stop.IsCancellationRequested)
            await Task.Delay(10);
        await relay.StopAsync(CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal((1L, 1L), (relay.Forwarded, relay.Dropped));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal static IConfiguration Config(string? enabled, string? url, string? token) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Usage:Enabled"] = enabled,
            ["Usage:ReceiverUrl"] = url,
            ["Usage:AuthToken"] = token,
        }).Build();

    internal static UsageReadiness Readiness(string? enabled, string? url, string? token) => new(Config(enabled, url, token));

    private static UsageRelay Relay(HttpMessageHandler handler, string? enabled = "true", ILogger<UsageRelay>? logger = null) =>
        new(Readiness(enabled, Origin, Token), new Factory(handler), logger ?? NullLogger<UsageRelay>.Instance);

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    internal sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    internal sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
