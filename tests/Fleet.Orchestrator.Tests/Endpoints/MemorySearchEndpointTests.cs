using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Fleet.Orchestrator.Tests.UsageRelayTests;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// <c>GET /api/memory/search</c> through the real route: what reaches fleet-memory is <c>q</c> and
/// the filters it knows, escaped, and nothing else; what comes back is fleet-memory's own status
/// and body.
/// </summary>
public sealed class MemorySearchEndpointTests
{
    private const string Token = "synthetic-service-token";
    private const string Hits = """[{"id":"5e1f9a3c-0000-4000-8000-000000000001","title":"Заметка","match":"id","highlights":[],"score":null}]""";

    [Fact]
    public async Task OnlyQ_IsForwardedAlone()
    {
        await using var host = await Host.StartAsync(_ => Json(HttpStatusCode.OK, Hits));

        var response = await host.Client.GetAsync("/api/memory/search?q=5e1f9a3c");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Hits, await response.Content.ReadAsStringAsync());
        var call = Assert.Single(host.Calls);
        Assert.Equal("fleet-memory.invalid", call.Host);
        Assert.Equal("/internal/memory/search", call.AbsolutePath);
        Assert.Equal("?q=5e1f9a3c", call.Query);
    }

    [Fact]
    public async Task EveryAllowedFilter_IsForwardedEscaped_InAFixedOrder()
    {
        await using var host = await Host.StartAsync(_ => Json(HttpStatusCode.OK, "[]"));

        // Sent in another order, with characters that mean something in a query string.
        await host.Client.GetAsync(
            "/api/memory/search?limit=5&since=2026-01-05T10:00:00%2B05:00&tag=c%23%20%26%20more&type=how-to&project=a%2Fb%3Fx%3D1&q=%D0%BF%D0%BE%D0%B8%D1%81%D0%BA%20a%26b%3Dc");

        var call = Assert.Single(host.Calls);
        Assert.Equal(
            "?q=%D0%BF%D0%BE%D0%B8%D1%81%D0%BA%20a%26b%3Dc&project=a%2Fb%3Fx%3D1&type=how-to&tag=c%23%20%26%20more&since=2026-01-05T10%3A00%3A00%2B05%3A00&limit=5",
            call.Query);
    }

    [Theory]
    [InlineData("q=notes&agent=agent1", "?q=notes")]
    [InlineData("q=notes&url=http://elsewhere.invalid&tag=ops", "?q=notes&tag=ops")]
    [InlineData("q=notes&Q2=x&min_score=0.1&offset=40", "?q=notes")]
    // A filter left empty is not a filter.
    [InlineData("q=notes&project=&type=&tag=&since=&limit=", "?q=notes")]
    // A filter given twice is sent once, with its first value.
    [InlineData("q=notes&tag=one&tag=two", "?q=notes&tag=one")]
    public async Task AnythingElseOnTheQueryString_IsNotForwarded(string query, string forwarded)
    {
        await using var host = await Host.StartAsync(_ => Json(HttpStatusCode.OK, "[]"));

        await host.Client.GetAsync($"/api/memory/search?{query}");

        Assert.Equal(forwarded, Assert.Single(host.Calls).Query);
    }

    [Theory]
    [InlineData("q=notes&since=yesterday", """{"error":"Invalid 'since': expected an ISO date"}""")]
    [InlineData("q=notes&limit=many", """{"error":"Invalid 'limit': expected a number from 1 to 50"}""")]
    public async Task ARefusalFromFleetMemory_ComesBackAs400WithItsOwnWords(string query, string refusal)
    {
        await using var host = await Host.StartAsync(_ => Json(HttpStatusCode.BadRequest, refusal));

        var response = await host.Client.GetAsync($"/api/memory/search?{query}");

        // The value went to fleet-memory as typed: it is the one that says what is wrong with it.
        Assert.Single(host.Calls);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(refusal, await response.Content.ReadAsStringAsync());
        Assert.StartsWith("Invalid", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithoutACredential_NothingIsAsked()
    {
        await using var host = await Host.StartAsync(_ => Json(HttpStatusCode.OK, Hits), authorize: false);

        var response = await host.Client.GetAsync("/api/memory/search?q=notes&tag=ops");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void BuildSearchQuery_AsksForNoNameOutsideTheList()
    {
        var asked = new List<string>();

        var query = MemoryProxyService.BuildSearchQuery("a b", name => { asked.Add(name); return name == "tag" ? "x y" : null; });

        Assert.Equal("q=a%20b&tag=x%20y", query);
        Assert.Equal(["project", "type", "tag", "since", "limit"], asked);
        Assert.Equal(asked, MemoryProxyService.SearchFilterNames);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, List<Uri> calls, HttpClient client)
        {
            _app = app;
            Calls = calls;
            Client = client;
        }

        public HttpClient Client { get; }

        /// <summary>The address of every request that reached the stand-in for fleet-memory.</summary>
        public List<Uri> Calls { get; }

        public static async Task<Host> StartAsync(Func<HttpRequestMessage, HttpResponseMessage> respond, bool authorize = true)
        {
            var calls = new List<Uri>();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentIdentity:Key"] = new string('A', 43),
                ["Orchestrator:AuthToken"] = Token,
                ["FleetMemory:Url"] = "http://fleet-memory.invalid:3100/",
            });
            builder.Services.AddFleetSignIn(durable: false);
            builder.Services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(r => { calls.Add(r.RequestUri!); return respond(r); })));
            builder.Services.AddSingleton<MemoryProxyService>();

            var app = builder.Build();
            app.UseFleetAccess();
            app.MapMemoryEndpoints();
            await app.StartAsync();

            var client = app.GetTestClient();
            if (authorize) client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
            return new Host(app, calls, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}
