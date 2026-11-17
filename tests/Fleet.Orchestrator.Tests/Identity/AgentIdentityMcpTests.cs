using System.ComponentModel;
using System.Net;
using Fleet.Shared;
using Fleet.Shared.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tests.Identity;

/// <summary>
/// The request middleware and the grant filters every fleet MCP server shares, against a real MCP
/// server hosted in this process and called by the real MCP client: who the server takes a caller
/// to be, who it refuses, and which tools it lets an agent see and call.
/// </summary>
public sealed class AgentIdentityMcpTests
{
    private const string Server = "fleet-example";
    private static readonly byte[] Key = AgentIdentityTokensTests.Key;

    [McpServerToolType]
    public sealed class ExampleTools
    {
        [McpServerTool(Name = "whoami"), Description("Says who the server takes the caller to be.")]
        public static string WhoAmI(IHttpContextAccessor accessor)
        {
            var caller = AgentCallerHttp.Of(accessor);
            return $"{caller.Kind}:{caller.Name}";
        }

        [McpServerTool(Name = "restricted"), Description("A tool few are granted.")]
        public static string Restricted() => "done";
    }

    private sealed class Grants : IAgentGrantSource
    {
        public Dictionary<string, string[]> ByAgent { get; } = new(StringComparer.Ordinal);
        public bool Available { get; set; } = true;

        public ValueTask<AgentGrantAnswer> GrantsOfAsync(string agent, CancellationToken ct) =>
            ValueTask.FromResult(Available
                ? new AgentGrantAnswer(true, ByAgent.GetValueOrDefault(agent, []))
                : AgentGrantAnswer.Unknown);
    }

    private sealed class Host(WebApplication app, Grants grants) : IAsyncDisposable
    {
        public Grants Grants { get; } = grants;

        public HttpClient Http() => app.GetTestClient();

        /// <summary>An MCP client as an agent's CLI is: the URL it was given, and the header, if any.</summary>
        public Task<McpClient> ConnectAsync(string? token = null, string? claimedAgent = null) =>
            McpClient.CreateAsync(new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri("http://localhost/mcp" + (claimedAgent is null ? "" : $"?agent={claimedAgent}")),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = token is null ? null : new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
                },
                new HttpClient(app.GetTestServer().CreateHandler()), loggerFactory: null, ownsHttpClient: true));

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<Host> StartAsync(bool withKey, bool enforce, Func<HttpContext, bool>? isOperator = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentIdentity:Key"] = withKey ? AgentIdentityTokensTests.Encode(Key) : "",
            ["AgentIdentity:Enforce"] = enforce ? "true" : "false",
        });
        var grants = new Grants();
        builder.Services.AddAgentIdentity(builder.Configuration);
        builder.Services.AddSingleton<IAgentGrantSource>(grants);
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<ExampleTools>().AddAgentToolAuthorization(Server);

        var app = builder.Build();
        app.UseAgentIdentity(context => context.Request.Path.StartsWithSegments("/mcp"), isOperator);
        app.MapMcp("/mcp");
        app.MapGet("/health", () => "ok");
        await app.StartAsync();
        return new Host(app, grants);
    }

    private static string AgentToken(string name, byte[]? key = null) =>
        AgentIdentityTokens.Mint(key ?? Key, AgentIdentityTokens.PurposeAgent, name);

    private static string ServiceToken(string name = "fleet-orchestrator") =>
        AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, name);

    private static async Task<string> CallAsync(McpClient client, string tool)
    {
        var result = await client.CallToolAsync(tool);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true ? "ERROR " : "") + text;
    }

    private static async Task<HttpStatusCode> PostStatusAsync(Host host, string? token, string query = "")
    {
        using var http = host.Http();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp" + query)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    // ── who the caller is ────────────────────────────────────────────────────

    [Fact]
    public async Task AnAgentsToken_MakesItThatAgent_WhateverItsUrlClaims()
    {
        await using var host = await StartAsync(withKey: true, enforce: true);
        host.Grants.ByAgent["agent-1"] = [$"mcp__{Server}__whoami"];
        await using var client = await host.ConnectAsync(AgentToken("agent-1"), claimedAgent: "agent-2");

        Assert.Equal("Agent:agent-1", await CallAsync(client, "whoami"));
    }

    [Fact]
    public async Task WithoutAToken_ACallerIsServedByItsClaim_WhileTokensAreNotRequired()
    {
        await using var host = await StartAsync(withKey: true, enforce: false);
        await using var client = await host.ConnectAsync(claimedAgent: "agent-1");

        Assert.Equal("Unverified:agent-1", await CallAsync(client, "whoami"));
    }

    [Fact]
    public async Task WithoutAKey_TheServerBehavesAsItDidBeforeTokens()
    {
        await using var host = await StartAsync(withKey: false, enforce: false);
        // A token is not refused for the server lacking the key: it simply proves nothing here.
        await using var client = await host.ConnectAsync(AgentToken("agent-9"), claimedAgent: "agent-1");

        Assert.Equal("Unverified:agent-1", await CallAsync(client, "whoami"));
        Assert.Equal("done", await CallAsync(client, "restricted"));
    }

    [Fact]
    public async Task UnderEnforcement_ACallerWithoutAValidToken_IsRefusedBeforeAnyTool()
    {
        await using var host = await StartAsync(withKey: true, enforce: true);

        Assert.Equal(HttpStatusCode.Unauthorized, await PostStatusAsync(host, token: null, query: "?agent=agent-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostStatusAsync(host, "some-other-bearer"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostStatusAsync(host, AgentToken("agent-1", AgentIdentityTokensTests.OtherKey)));
        Assert.NotEqual(HttpStatusCode.Unauthorized, await PostStatusAsync(host, AgentToken("agent-1")));
    }

    [Fact]
    public async Task AWrongToken_IsRefusedEvenWhileTokensAreNotRequired()
    {
        await using var host = await StartAsync(withKey: true, enforce: false);

        Assert.Equal(HttpStatusCode.Unauthorized, await PostStatusAsync(host, AgentToken("agent-1", AgentIdentityTokensTests.OtherKey), "?agent=agent-1"));
        Assert.NotEqual(HttpStatusCode.Unauthorized, await PostStatusAsync(host, token: null, query: "?agent=agent-1"));
    }

    [Fact]
    public async Task ARefusal_SaysWhy_ByAFixedCode_AndEchoesNothing()
    {
        await using var host = await StartAsync(withKey: true, enforce: true);
        using var http = host.Http();
        var wrong = AgentToken("agent-1", AgentIdentityTokensTests.OtherKey);

        using var missing = await http.PostAsync("/mcp?agent=agent-1", new StringContent("{}"));
        Assert.Contains(AgentIdentityApp.RequiredCode, await missing.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}") };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {wrong}");
        using var invalid = await http.SendAsync(request);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains(AgentIdentityApp.InvalidCode, body);
        Assert.DoesNotContain(wrong, body);
        Assert.DoesNotContain("agent-1", body);
    }

    [Fact]
    public async Task WhatIsNotTheMcpEndpoint_IsNotAskedForAToken()
    {
        await using var host = await StartAsync(withKey: true, enforce: true);
        using var http = host.Http();

        using var health = await http.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // ── what the caller may do ───────────────────────────────────────────────

    [Fact]
    public async Task AnAgent_SeesAndCallsOnlyTheToolsItIsGranted()
    {
        await using var host = await StartAsync(withKey: true, enforce: true);
        host.Grants.ByAgent["agent-1"] = [$"mcp__{Server}__whoami"];
        await using var client = await host.ConnectAsync(AgentToken("agent-1"));

        Assert.Equal(["whoami"], (await client.ListToolsAsync()).Select(t => t.Name));

        var refused = await CallAsync(client, "restricted");
        Assert.StartsWith("ERROR " + AgentToolAuthorization.NotGrantedCode, refused);
        Assert.Contains($"mcp__{Server}__restricted", refused);
        Assert.Equal("Agent:agent-1", await CallAsync(client, "whoami"));
    }

    [Fact]
    public async Task AGrantIsCheckedOnTheServer_EvenWhileTokensAreNotRequired()
    {
        // An agent that has been reprovisioned sends its token, and is held to its grants at once.
        await using var host = await StartAsync(withKey: true, enforce: false);
        host.Grants.ByAgent["agent-1"] = [$"mcp__{Server}__whoami"];
        await using var client = await host.ConnectAsync(AgentToken("agent-1"));

        Assert.StartsWith("ERROR " + AgentToolAuthorization.NotGrantedCode, await CallAsync(client, "restricted"));
    }

    [Fact]
    public async Task AnAgentTheServerDoesNotKnow_IsGrantedNothing()
    {
        await using var host = await StartAsync(withKey: true, enforce: true);
        await using var client = await host.ConnectAsync(AgentToken("stranger"));

        Assert.Empty(await client.ListToolsAsync());
        Assert.StartsWith("ERROR " + AgentToolAuthorization.NotGrantedCode, await CallAsync(client, "whoami"));
    }

    [Fact]
    public async Task AGrantOfTheWholeServer_GrantsEveryTool()
    {
        await using var host = await StartAsync(withKey: true, enforce: true);
        host.Grants.ByAgent["agent-1"] = [$"mcp__{Server}"];
        await using var client = await host.ConnectAsync(AgentToken("agent-1"));

        Assert.Equal(2, (await client.ListToolsAsync()).Count);
        Assert.Equal("done", await CallAsync(client, "restricted"));
    }

    [Fact]
    public async Task AService_IsNotAnAgent_AndIsNotHeldToGrants()
    {
        await using var host = await StartAsync(withKey: true, enforce: true);
        await using var client = await host.ConnectAsync(ServiceToken());

        Assert.Equal(2, (await client.ListToolsAsync()).Count);
        Assert.Equal("Service:fleet-orchestrator", await CallAsync(client, "whoami"));
        Assert.Equal("done", await CallAsync(client, "restricted"));
    }

    [Fact]
    public async Task TheServersOwnOperatorCheck_ServesItsOperator_WithoutAToken()
    {
        await using var host = await StartAsync(withKey: true, enforce: true,
            isOperator: context => context.Request.Headers["X-Example-Operator"] == "yes");
        using var http = host.Http();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("X-Example-Operator", "yes");
        using var response = await http.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnUnverifiedCaller_IsNotHeldToGrants_BecauseItsNameProvesNothing()
    {
        await using var host = await StartAsync(withKey: true, enforce: false);
        host.Grants.ByAgent["agent-1"] = [];
        await using var client = await host.ConnectAsync(claimedAgent: "agent-1");

        // Served as before tokens. Enforcement is what closes this, not a check on a typed name.
        Assert.Equal(2, (await client.ListToolsAsync()).Count);
        Assert.Equal("done", await CallAsync(client, "restricted"));
    }

    [Fact]
    public async Task WhenGrantsCannotBeRead_AnAgentIsRefusedUnderEnforcement_AndServedOtherwise()
    {
        await using (var enforcing = await StartAsync(withKey: true, enforce: true))
        {
            enforcing.Grants.Available = false;
            await using var client = await enforcing.ConnectAsync(AgentToken("agent-1"));

            Assert.StartsWith("ERROR " + AgentToolAuthorization.GrantsUnavailableCode, await CallAsync(client, "whoami"));
            Assert.Empty(await client.ListToolsAsync());
        }

        await using var observing = await StartAsync(withKey: true, enforce: false);
        observing.Grants.Available = false;
        await using var served = await observing.ConnectAsync(AgentToken("agent-1"));

        Assert.Equal("Agent:agent-1", await CallAsync(served, "whoami"));
        Assert.Equal(2, (await served.ListToolsAsync()).Count);
    }

    // ── settings ─────────────────────────────────────────────────────────────

    [Fact]
    public void EnforcementWithoutAKey_StopsTheService_RatherThanRefusingOrBelievingEveryone()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentIdentity:Enforce"] = "true",
        }).Build();

        var fault = Assert.Throws<InvalidOperationException>(() => AgentIdentitySettings.From(configuration));
        Assert.StartsWith("agent_identity_misconfigured", fault.Message);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("enabled")]
    public void ASwitchThatIsNeitherTrueNorFalse_StopsTheService(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentIdentity:Key"] = AgentIdentityTokensTests.Encode(Key),
            ["AgentIdentity:Enforce"] = value,
        }).Build();

        Assert.Throws<InvalidOperationException>(() => AgentIdentitySettings.From(configuration));
    }

    [Fact]
    public void AKeyThatCannotBeRead_StopsTheService_WithoutSayingTheKey()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentIdentity:Key"] = "too*short",
        }).Build();

        var fault = Assert.Throws<InvalidOperationException>(() => AgentIdentitySettings.From(configuration));
        Assert.DoesNotContain("too*short", fault.Message);
    }

    [Fact]
    public void TheSettings_DefaultToNoKeyAndNoEnforcement()
    {
        var settings = AgentIdentitySettings.From(new ConfigurationBuilder().Build());

        Assert.False(settings.HasKey);
        Assert.False(settings.Enforce);
        Assert.Null(settings.ServiceToken("fleet-orchestrator"));
    }
}
