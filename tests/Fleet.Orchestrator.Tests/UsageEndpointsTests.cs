using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Fleet.Orchestrator.Tests.UsageRelayTests;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// <c>GET /api/usage</c> through the real endpoint: refusals before any upstream call, the receiver's
/// JSON passed through byte for byte, and every failure a 503 with a stable code.
/// </summary>
public sealed class UsageEndpointsTests
{
    private const string Origin = "http://usage-receiver.invalid:3900";
    private const string Token = "synthetic-receiver-token";
    private const string Query = "from=2026-01-05T10:00:00Z&to=2026-01-06T10:00:00Z&metric=output_tokens";

    private const string ReceiverBody =
        """{"from":"2026-01-05T10:00:00.000Z","to":"2026-01-06T10:00:00.000Z","metric":"cost_usd","bucket_seconds":3600,"matched_groups":1,"groups_shown":1,"truncated":false,"groups":[{"agent":"agent-a","provider":"claude","model":"model-x","reported_value":"0.30000001","eligible_turns":3,"measured_turns":2,"reset_excluded":1,"buckets":[{"start":"2026-01-05T10:00:00Z","reported_value":null,"eligible_turns":0,"measured_turns":0,"reset_excluded":1}]}]}""";

    [Theory]
    [InlineData(null, "usage_disabled")]
    [InlineData("false", "usage_disabled")]
    public async Task Disabled_Is503UsageDisabled_WithoutCallingTheReceiver(string? enabled, string code)
    {
        await using var host = await Host.StartAsync(Readiness(enabled, Origin, Token), _ => throw new InvalidOperationException("called"));

        var response = await host.Client.GetAsync($"/api/usage?{Query}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(code, await ErrorAsync(response));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task EnabledButMisconfigured_Is503UsageNotConfigured()
    {
        await using var host = await Host.StartAsync(Readiness("true", Origin + "/api/usage", Token), _ => throw new InvalidOperationException("called"));

        var response = await host.Client.GetAsync($"/api/usage?{Query}");

        Assert.Equal("usage_not_configured", await ErrorAsync(response));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task ReceiverJson_IsPassedThroughByteForByte_FromTheFixedOrigin()
    {
        await using var host = await Host.StartAsync(Readiness("true", Origin, Token), _ => Json(HttpStatusCode.OK, ReceiverBody));

        var response = await host.Client.GetAsync(
            "/api/usage?from=2026-01-05T15:00:00.1234567%2B05:00&to=2026-01-06T10:00:00Z&metric=cost_usd&agent=agent-a&model=model%20x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReceiverBody, await response.Content.ReadAsStringAsync());

        var call = Assert.Single(host.Calls);
        Assert.Equal("usage-receiver.invalid", call.RequestUri!.Host);
        Assert.Equal("/api/usage", call.RequestUri.AbsolutePath);
        // Converted to UTC and aligned to milliseconds; filters escaped, never re-interpreted.
        Assert.Equal("?from=2026-01-05T10%3A00%3A00.123Z&to=2026-01-06T10%3A00%3A00.000Z&metric=cost_usd&agent=agent-a&model=model%20x",
            call.RequestUri.Query);
        Assert.Equal(("Bearer", Token), (call.Headers.Authorization!.Scheme, call.Headers.Authorization.Parameter));
    }

    [Theory]
    [InlineData("to=2026-01-06T10:00:00Z&metric=output_tokens", "from")]
    [InlineData("from=2026-01-05T10:00:00&to=2026-01-06T10:00:00Z&metric=output_tokens", "from")]
    [InlineData("from=2026-01-06T10:00:00Z&to=2026-01-05T10:00:00Z&metric=output_tokens", "from")]
    [InlineData("from=2026-01-05T10:00:00Z&to=2026-01-12T10:00:00.001Z&metric=output_tokens", "to")]
    [InlineData("from=2026-01-05T10:00:00Z&to=2026-01-06T10:00:00Z&metric=cum_output_tokens", "metric")]
    [InlineData("from=2026-01-05T10:00:00Z&to=2026-01-06T10:00:00Z", "metric")]
    [InlineData(Query + "&url=http://elsewhere.invalid", "url")]
    [InlineData(Query + "&metric=input_tokens", "metric")]
    [InlineData(Query + "&agent=", "agent")]
    public async Task InvalidQuery_Is400_BeforeAnyUpstreamCall(string query, string field)
    {
        await using var host = await Host.StartAsync(Readiness("true", Origin, Token), _ => Json(HttpStatusCode.OK, ReceiverBody));

        var response = await host.Client.GetAsync($"/api/usage?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData("agent", 64)]
    [InlineData("provider", 32)]
    [InlineData("model", 128)]
    public async Task FilterLongerThanTheReceiverAccepts_Is400(string field, int max)
    {
        await using var host = await Host.StartAsync(Readiness("true", Origin, Token), _ => Json(HttpStatusCode.OK, ReceiverBody));

        var atLimit = await host.Client.GetAsync($"/api/usage?{Query}&{field}={new string('x', max)}");
        var overLimit = await host.Client.GetAsync($"/api/usage?{Query}&{field}={new string('x', max + 1)}");

        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
        Assert.Single(host.Calls);
    }

    public static TheoryData<Func<HttpRequestMessage, HttpResponseMessage>> UpstreamFailures => new()
    {
        _ => Json(HttpStatusCode.ServiceUnavailable, """{"error":"usage_unavailable"}"""),
        _ => Json(HttpStatusCode.Unauthorized, """{"error":"Unauthorized"}"""),
        _ => Json(HttpStatusCode.Found, ""),
        _ => Json(HttpStatusCode.OK, """{"groups":"not an array"}"""),
        _ => Json(HttpStatusCode.OK, "<html>"),
        _ => Json(HttpStatusCode.OK, "{\"groups\":[],\"pad\":\"" + new string('x', UsageReader.MaxResponseBytes) + "\"}"),
        _ => throw new HttpRequestException("name resolution failed"),
    };

    [Theory]
    [MemberData(nameof(UpstreamFailures))]
    public async Task UpstreamFailure_Is503UsageUnavailable_NeverAnEmpty200(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        await using var host = await Host.StartAsync(Readiness("true", Origin, Token), respond);

        var response = await host.Client.GetAsync($"/api/usage?{Query}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("usage_unavailable", await ErrorAsync(response));
        Assert.DoesNotContain(host.Log.Lines, l => l.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplyCutOffAfterTheHeaders_Is503_AndTheNextReadStillWorks()
    {
        var calls = 0;
        await using var host = await Host.StartAsync(Readiness("true", Origin, Token), _ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() }
            : Json(HttpStatusCode.OK, ReceiverBody));

        var broken = await host.Client.GetAsync($"/api/usage?{Query}");
        var next = await host.Client.GetAsync($"/api/usage?{Query}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, broken.StatusCode);
        Assert.Equal("usage_unavailable", await ErrorAsync(broken));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(ReceiverBody, await next.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SlowReceiver_Is503AtTheDeadline()
    {
        await using var host = await Host.StartAsync(Readiness("true", Origin, Token), handler: new SlowHandler());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var response = await host.Client.GetAsync($"/api/usage?{Query}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(watch.Elapsed < UsageReader.RequestDeadline + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void EdgeAuthentication_IsUnchanged_ForTheNewRead()
    {
        Assert.Equal(AccessDecision.Unauthorized,
            OrchestratorAuth.Decide(authRequired: true, "GET", "/api/usage", isEdge: true, CallerKind.None, hasRequestHeader: false));
        Assert.Equal(AccessDecision.Allow,
            OrchestratorAuth.Decide(authRequired: true, "GET", "/api/usage", isEdge: true, CallerKind.Service, hasRequestHeader: false));
        Assert.False(OrchestratorAuth.RequiresBearerToken("GET", "/api/usage"));
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, List<HttpRequestMessage> calls, ListLogger<UsageReader> log)
        {
            _app = app;
            Calls = calls;
            Log = log;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }
        public List<HttpRequestMessage> Calls { get; }
        public ListLogger<UsageReader> Log { get; }

        public static async Task<Host> StartAsync(
            UsageReadiness readiness, Func<HttpRequestMessage, HttpResponseMessage>? respond = null, HttpMessageHandler? handler = null)
        {
            var calls = new List<HttpRequestMessage>();
            var log = new ListLogger<UsageReader>();
            handler ??= new Handler(r => { calls.Add(r); return respond!(r); });

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(readiness);
            builder.Services.AddSingleton<IHttpClientFactory>(new Factory(handler));
            builder.Services.AddSingleton<ILogger<UsageReader>>(log);
            builder.Services.AddSingleton<UsageReader>();

            var app = builder.Build();
            app.MapUsageEndpoints();
            await app.StartAsync();
            return new Host(app, calls, log);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}
