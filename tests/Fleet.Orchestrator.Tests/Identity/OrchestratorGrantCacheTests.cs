using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Tests.Auth;
using Fleet.Shared;
using Fleet.Shared.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Identity;

/// <summary>
/// What a server that is not the orchestrator believes each agent is granted: read from the
/// orchestrator, refreshed, and refused as "unknown" once nobody can vouch for it. Also the check
/// on routes that are a service's and never an agent's.
/// </summary>
public sealed class OrchestratorGrantCacheTests
{
    private static readonly byte[] Key = AgentIdentityTokensTests.Key;

    /// <summary>Stands for the orchestrator's <c>/internal/agent-mcp-grants</c>.</summary>
    private sealed class Orchestrator : HttpMessageHandler, IHttpClientFactory
    {
        public Dictionary<string, string[]> Agents { get; } = new(StringComparer.Ordinal);
        public bool Down { get; set; }
        public string? RawBody { get; set; }
        public int Reads { get; private set; }
        public List<Uri> Asked { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Reads++;
            Asked.Add(request.RequestUri!);
            if (Down) throw new HttpRequestException("connection refused");
            var body = RawBody ?? JsonSerializer.Serialize(new { agents = Agents });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (OrchestratorGrantCache Cache, Orchestrator Orchestrator, TestTimeProvider Time) NewCache(
        bool withKey = true, string? orchestratorUrl = null)
    {
        var orchestrator = new Orchestrator();
        var time = new TestTimeProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentIdentity:OrchestratorUrl"] = orchestratorUrl,
        }).Build();
        var settings = new AgentIdentitySettings(withKey ? [Key] : [], enforce: false);
        return (new OrchestratorGrantCache(orchestrator, settings, configuration, NullLogger<OrchestratorGrantCache>.Instance, time), orchestrator, time);
    }

    [Fact]
    public async Task AnAgentsGrants_AreWhatTheOrchestratorSays()
    {
        var (cache, orchestrator, _) = NewCache();
        orchestrator.Agents["agent-1"] = ["mcp__fleet-memory__memory_get", "mcp__fleet-telegram__send_message"];

        var answer = await cache.GrantsOfAsync("agent-1", CancellationToken.None);

        Assert.True(answer.Known);
        Assert.Equal(["mcp__fleet-memory__memory_get", "mcp__fleet-telegram__send_message"], answer.Grants);
        Assert.Equal(new Uri("http://fleet-orchestrator:3600/internal/agent-mcp-grants"), Assert.Single(orchestrator.Asked));
    }

    [Fact]
    public async Task TheGrants_AreReadFromTheConfiguredOrchestrator()
    {
        var (cache, orchestrator, _) = NewCache(orchestratorUrl: "http://orchestrator.example.test:3600/");

        await cache.GrantsOfAsync("agent-1", CancellationToken.None);

        Assert.Equal(new Uri("http://orchestrator.example.test:3600/internal/agent-mcp-grants"), Assert.Single(orchestrator.Asked));
    }

    [Fact]
    public async Task AnAgentTheOrchestratorDoesNotList_IsGrantedNothing_WhichIsAnAnswer()
    {
        var (cache, orchestrator, _) = NewCache();
        orchestrator.Agents["agent-1"] = ["mcp__fleet-memory__memory_get"];

        var answer = await cache.GrantsOfAsync("stranger", CancellationToken.None);

        Assert.True(answer.Known);
        Assert.Empty(answer.Grants);
    }

    [Fact]
    public async Task AKnownAgent_IsAnsweredFromMemory_WithoutAskingAgain()
    {
        var (cache, orchestrator, _) = NewCache();
        orchestrator.Agents["agent-1"] = ["mcp__fleet-memory__memory_get"];

        for (var i = 0; i < 5; i++) await cache.GrantsOfAsync("agent-1", CancellationToken.None);

        Assert.Equal(1, orchestrator.Reads);
    }

    [Fact]
    public async Task AnAgentCreatedSinceTheLastRead_IsFoundByAskingAgain_ButNotOnEveryCall()
    {
        var (cache, orchestrator, time) = NewCache();
        orchestrator.Agents["agent-1"] = [];
        await cache.GrantsOfAsync("agent-1", CancellationToken.None);

        orchestrator.Agents["agent-2"] = ["mcp__fleet-memory__memory_search"];
        // Straight away: the last read was a moment ago, so this one waits its turn.
        Assert.Empty((await cache.GrantsOfAsync("agent-2", CancellationToken.None)).Grants);
        Assert.Equal(1, orchestrator.Reads);

        time.Advance(OrchestratorGrantCache.MissRefreshEvery + TimeSpan.FromSeconds(1));
        Assert.Equal(["mcp__fleet-memory__memory_search"], (await cache.GrantsOfAsync("agent-2", CancellationToken.None)).Grants);
        Assert.Equal(2, orchestrator.Reads);
    }

    [Fact]
    public async Task WhenTheOrchestratorCannotBeReached_BeforeAnyRead_TheAnswerIsUnknown_NotNothing()
    {
        var (cache, orchestrator, _) = NewCache();
        orchestrator.Down = true;

        var answer = await cache.GrantsOfAsync("agent-1", CancellationToken.None);

        Assert.False(answer.Known);
    }

    [Fact]
    public async Task WhatWasRead_StandsThroughAShortOutage_AndIsNotBelievedThroughALongOne()
    {
        var (cache, orchestrator, time) = NewCache();
        orchestrator.Agents["agent-1"] = ["mcp__fleet-memory__memory_get"];
        await cache.GrantsOfAsync("agent-1", CancellationToken.None);
        orchestrator.Down = true;

        time.Advance(TimeSpan.FromMinutes(5));
        await cache.RefreshAsync(force: true, CancellationToken.None);
        var during = await cache.GrantsOfAsync("agent-1", CancellationToken.None);
        Assert.True(during.Known);
        Assert.Single(during.Grants);

        time.Advance(OrchestratorGrantCache.StaleAfter);
        Assert.False((await cache.GrantsOfAsync("agent-1", CancellationToken.None)).Known);

        // The orchestrator is back: the next read restores the answer.
        orchestrator.Down = false;
        time.Advance(OrchestratorGrantCache.MissRefreshEvery + TimeSpan.FromSeconds(1));
        Assert.True((await cache.GrantsOfAsync("agent-1", CancellationToken.None)).Known);
    }

    [Theory]
    [InlineData("""{"error":"Database is not configured"}""")]
    [InlineData("""{"agents":["agent-1"]}""")]
    [InlineData("""{"agents":{"agent-1":"mcp__fleet-memory"}}""")]
    [InlineData("""[]""")]
    public async Task AnAnswerThatIsNotAGrantsMap_IsNotBelieved(string body)
    {
        var (cache, orchestrator, _) = NewCache();
        orchestrator.RawBody = body;

        Assert.False((await cache.GrantsOfAsync("agent-1", CancellationToken.None)).Known);
    }

    [Fact]
    public async Task WithoutAKey_NothingIsEverRead_BecauseNoCallerIsAVerifiedAgent()
    {
        var (cache, orchestrator, _) = NewCache(withKey: false);

        await cache.RefreshAsync(force: true, CancellationToken.None);
        var answer = await cache.GrantsOfAsync("agent-1", CancellationToken.None);

        Assert.False(answer.Known);
        Assert.Equal(0, orchestrator.Reads);
    }

    /// <summary>
    /// The grants answer is what this server believes every agent may do, so it is read from the
    /// configured address only: a redirect is not followed somewhere else.
    /// </summary>
    [Fact]
    public void TheGrantsClient_FollowsNoRedirect()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAgentIdentityFromOrchestrator(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(OrchestratorGrantCache.HttpClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler;

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    // ── names a model used to type ───────────────────────────────────────────

    [Fact]
    public void WhatIsAttributedToAnAgent_IsTheCaller_AndTheTypedNameOnlyWhenTheCallerIsNotAnAgent()
    {
        static IHttpContextAccessor As(AgentCaller? caller)
        {
            var context = new DefaultHttpContext();
            if (caller is not null) AgentCallerHttp.Set(context, caller);
            return new HttpContextAccessor { HttpContext = context };
        }

        // An agent cannot put another agent's name on what it does, verified or (before tokens) claimed.
        Assert.Equal("agent-1", AgentCallerHttp.AgentNameOr(As(new AgentCaller("agent-1", AgentCallerKind.Agent)), "cto-agent"));
        Assert.Equal("agent-1", AgentCallerHttp.AgentNameOr(As(new AgentCaller("agent-1", AgentCallerKind.Unverified)), "cto-agent"));
        // A service is not an agent, so what it was told to attribute stands.
        Assert.Equal("cto-agent", AgentCallerHttp.AgentNameOr(As(new AgentCaller("fleet-orchestrator", AgentCallerKind.Service)), "cto-agent"));
        Assert.Equal("cto-agent", AgentCallerHttp.AgentNameOr(As(null), "cto-agent"));
        Assert.Equal("", AgentCallerHttp.AgentNameOr(As(null), null));
        Assert.Null(AgentCallerHttp.AgentNameOf(As(new AgentCaller("fleet-orchestrator", AgentCallerKind.Service))));
    }

    [Fact]
    public void NoFleetMcpServer_ReadsWhoIsCallingFromTheUrl_OrSkipsTheSharedChecks()
    {
        // The three servers that are not the orchestrator. Their tools ask AgentCallerHttp; a read of
        // ?agent= creeping back in would be believed again without anything failing.
        var src = Path.Combine(RepositoryRoot(), "src");
        foreach (var project in new[] { "Fleet.Memory", "Fleet.Telegram", "Fleet.Temporal" })
        {
            var sources = Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.DoesNotContain(sources, f => File.ReadAllText(f).Contains("Query[\"agent\"]"));

            var program = File.ReadAllText(Path.Combine(src, project, "Program.cs"));
            Assert.Contains("AddAgentIdentityFromOrchestrator", program);
            Assert.Contains("UseAgentIdentity", program);
            Assert.Contains("AddAgentToolAuthorization", program);
        }
    }

    /// <summary>Records what the orchestrator sends fleet-memory.</summary>
    private sealed class MemoryServer : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Path, string? Authorization)> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }

    [Fact]
    public async Task TheOrchestrator_ReadsFleetMemorysRestApi_AsAService()
    {
        var memory = new MemoryServer();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentIdentity:Key"] = AgentIdentityTokensTests.Encode(Key),
        }).Build();
        var proxy = new Fleet.Orchestrator.Services.MemoryProxyService(memory, configuration);

        using var list = await proxy.GetListAsync();
        using var one = await proxy.GetAsync("abc");
        using var deleted = await proxy.DeleteAsync("abc");

        var expected = "Bearer " + AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, "fleet-orchestrator");
        Assert.Equal(3, memory.Requests.Count);
        Assert.All(memory.Requests, request =>
        {
            Assert.StartsWith("/internal/", request.Path);
            Assert.Equal(expected, request.Authorization);
        });
    }

    [Fact]
    public async Task WithoutAKey_TheOrchestratorSendsFleetMemoryNoCredential_AsBefore()
    {
        var memory = new MemoryServer();
        var proxy = new Fleet.Orchestrator.Services.MemoryProxyService(memory, new ConfigurationBuilder().Build());

        using var list = await proxy.GetListAsync();

        Assert.Null(Assert.Single(memory.Requests).Authorization);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fleet.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    // ── routes that are a service's ──────────────────────────────────────────

    private static async Task<WebApplication> ServiceRoutesAsync(bool withKey, bool enforce)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentIdentity:Key"] = withKey ? AgentIdentityTokensTests.Encode(Key) : "",
            ["AgentIdentity:Enforce"] = enforce ? "true" : "false",
        });
        builder.Services.AddAgentIdentity(builder.Configuration);
        var app = builder.Build();
        app.UseServiceIdentity(context => context.Request.Path.StartsWithSegments("/internal"));
        app.MapGet("/internal/memory", () => "every memory");
        app.MapGet("/health", () => "ok");
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(WebApplication app, string path, string? bearer = null)
    {
        using var http = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnderEnforcement_AServiceRouteIsServedToAServiceOnly_NotToAnAgentWithItsOwnValidToken()
    {
        await using var app = await ServiceRoutesAsync(withKey: true, enforce: true);
        var service = AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, "fleet-orchestrator");
        var agent = AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeAgent, "agent-1");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "/internal/memory", service)).Status);

        var asAgent = await GetAsync(app, "/internal/memory", agent);
        Assert.Equal(HttpStatusCode.Forbidden, asAgent.Status);
        Assert.Contains(AgentIdentityApp.ServiceRequiredCode, asAgent.Body);
        Assert.DoesNotContain("every memory", asAgent.Body);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(app, "/internal/memory")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "/health")).Status);
    }

    [Fact]
    public async Task UntilEnforcement_AServiceRouteIsServedAsItAlwaysWas_ButAWrongTokenIsStillRefused()
    {
        await using var app = await ServiceRoutesAsync(withKey: true, enforce: false);
        var wrong = AgentIdentityTokens.Mint(AgentIdentityTokensTests.OtherKey, AgentIdentityTokens.PurposeService, "fleet-orchestrator");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "/internal/memory")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(app, "/internal/memory", wrong)).Status);
    }

    [Fact]
    public async Task WithoutAKey_AServiceRouteIsServedAsItAlwaysWas()
    {
        await using var app = await ServiceRoutesAsync(withKey: false, enforce: false);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "/internal/memory")).Status);
    }
}
