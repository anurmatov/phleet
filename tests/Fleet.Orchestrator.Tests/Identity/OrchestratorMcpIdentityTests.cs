using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Fleet.Shared.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tests.Identity;

/// <summary>
/// The orchestrator's MCP endpoint as it is wired in production: the sign-in pipeline, then the
/// agent identity, then the grant check against the agents' stored tools. Until this, the endpoint
/// took no credential and held nobody to anything.
/// </summary>
/// <remarks>The tools are two stand-ins; every real tool sits behind the same filter.</remarks>
public sealed class OrchestratorMcpIdentityTests : IAsyncLifetime
{
    private const string ServiceBearer = "synthetic-service-token";
    private const string ConfigToken = "synthetic-config-token";
    private static readonly byte[] Key = AgentIdentityTokensTests.Key;

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private string _envFile = null!;
    private bool _enforce = true;

    [McpServerToolType]
    public sealed class StandInTools
    {
        [McpServerTool(Name = "list_agents"), Description("A tool a worker may be granted.")]
        public static string ListAgents(IHttpContextAccessor accessor) => "listed for " + AgentCallerHttp.Of(accessor).Name;

        [McpServerTool(Name = "update_agent_config"), Description("A tool only a coordinator is granted.")]
        public static string UpdateAgentConfig() => "updated";
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync(bool enforce = true, bool withKey = true, bool withServiceBearer = true)
    {
        _enforce = enforce;
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _envFile = Path.Combine(Path.GetTempPath(), $"identity-{Guid.NewGuid():N}.env");
        await File.WriteAllTextAsync(_envFile, "FLEET_CTO_AGENT=cto-agent\n");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:AuthToken"] = withServiceBearer ? ServiceBearer : null,
            ["Provisioning:EnvFilePath"] = _envFile,
            ["AgentIdentity:Key"] = withKey ? AgentIdentityTokensTests.Encode(Key) : "",
            ["AgentIdentity:Enforce"] = enforce ? "true" : "false",
            ["Orchestrator:ConfigToken"] = ConfigToken,
        });
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton<ISecretHasher>(new Argon2idSecretHasher(memoryKib: 64, iterations: 1));
        builder.Services.AddFleetSignIn(durable: true);
        builder.Services.AddAgentIdentity(builder.Configuration);
        builder.Services.AddSingleton<DbAgentGrantSource>();
        builder.Services.AddSingleton<IAgentGrantSource>(sp => sp.GetRequiredService<DbAgentGrantSource>());
        builder.Services.Configure<Fleet.Orchestrator.Configuration.RabbitMqOptions>(o => { o.Host = ""; o.Exchange = ""; });
        builder.Services.AddSingleton<ConfigService>();
        builder.Services.AddSingleton<IConfigWriter>(sp => sp.GetRequiredService<ConfigService>());
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<StandInTools>()
            .WithTools<Fleet.Orchestrator.Tools.SetConfigValuesTool>()
            .AddAgentToolAuthorization(OrchestratorMcp.ServerName);

        _app = builder.Build();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Agents.Add(NewAgent("worker", "mcp__fleet-orchestrator__list_agents"));
            db.Agents.Add(NewAgent("cto-agent", "mcp__fleet-orchestrator__list_agents", "mcp__fleet-orchestrator__update_agent_config",
                "mcp__fleet-orchestrator__set_config_values"));
            await db.SaveChangesAsync();
        }

        // The production order: sign-in first, then who the MCP caller is.
        _app.UseFleetAccess();
        _app.UseMcpAgentIdentity();
        _app.MapMcp(OrchestratorMcp.Path);
        _app.MapAgentIdentityEndpoints();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        if (_envFile is not null) File.Delete(_envFile);
    }

    private static Agent NewAgent(string name, params string[] tools) => new()
    {
        Name = name, DisplayName = name, Role = "developer", Model = "sonnet", Provider = "claude",
        ContainerName = "fleet-" + name, MemoryLimitMb = 512,
        Tools = [.. tools.Select(t => new AgentTool { ToolName = t, IsEnabled = true })],
    };

    private static string AgentToken(string name) => AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeAgent, name);

    private Task<McpClient> ConnectAsync(string? bearer, Dictionary<string, string>? headers = null)
    {
        headers ??= [];
        if (bearer is not null) headers["Authorization"] = $"Bearer {bearer}";
        return McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers.Count == 0 ? null : headers,
            },
            new HttpClient(_app.GetTestServer().CreateHandler()), loggerFactory: null, ownsHttpClient: true));
    }

    private static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true ? "ERROR " : "") + text;
    }

    private async Task<HttpStatusCode> ListStatusAsync(string? bearer)
    {
        using var http = _app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    private static Dictionary<string, object?> ConfigWrite(string value) =>
        new() { ["values_json"] = JsonSerializer.Serialize(new Dictionary<string, string> { ["EXAMPLE_SETTING"] = value }) };

    /// <summary>
    /// <c>set_config_values</c> needs the config token, and an agent's <c>Authorization</c> is its
    /// identity: the config token travels in its own header, which provisioning keeps beside the
    /// identity. Both checks still apply, with enforcement off and on.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetConfigValues_TakesTheConfigTokenBesideTheIdentity_AndStillChecksBoth(bool enforce)
    {
        await StartAsync(enforce);
        var withConfig = new Dictionary<string, string> { [Fleet.Orchestrator.Tools.SetConfigValuesTool.ConfigTokenHeader] = ConfigToken };
        var wrongConfig = new Dictionary<string, string> { [Fleet.Orchestrator.Tools.SetConfigValuesTool.ConfigTokenHeader] = "not-the-config-token" };

        await using (var coordinator = await ConnectAsync(AgentToken("cto-agent"), withConfig))
        {
            var said = await CallAsync(coordinator, "set_config_values", ConfigWrite("one"));
            Assert.True(said.Contains("EXAMPLE_SETTING"), said);
        }
        Assert.Contains("EXAMPLE_SETTING=one", await File.ReadAllTextAsync(_envFile));

        await using (var wrong = await ConnectAsync(AgentToken("cto-agent"), wrongConfig))
            Assert.Contains("unauthorized", await CallAsync(wrong, "set_config_values", ConfigWrite("two")));
        await using (var none = await ConnectAsync(AgentToken("cto-agent")))
            Assert.Contains("unauthorized", await CallAsync(none, "set_config_values", ConfigWrite("two")));
        // The config token is not a grant: an agent without the tool is refused before it runs.
        await using (var worker = await ConnectAsync(AgentToken("worker"), withConfig))
            Assert.StartsWith("ERROR " + AgentToolAuthorization.NotGrantedCode, await CallAsync(worker, "set_config_values", ConfigWrite("two")));
        Assert.DoesNotContain("EXAMPLE_SETTING=two", await File.ReadAllTextAsync(_envFile));
    }

    /// <summary>A configuration from before the header still works while tokens are not required, and is refused once they are.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SetConfigValues_ByTheConfigTokenAsBearer_WorksOnlyWhileTokensAreNotRequired(bool enforce, bool served)
    {
        await StartAsync(enforce);

        if (served)
        {
            await using var legacy = await ConnectAsync(ConfigToken);
            Assert.Contains("EXAMPLE_SETTING", await CallAsync(legacy, "set_config_values", ConfigWrite("legacy")));
            Assert.Contains("EXAMPLE_SETTING=legacy", await File.ReadAllTextAsync(_envFile));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await ListStatusAsync(ConfigToken));
        }
    }

    [Fact]
    public async Task ACallerWithNoCredential_IsRefused_WhereItWasOnceServedEveryTool()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await ListStatusAsync(bearer: null));
    }

    [Fact]
    public async Task AWorker_CallsWhatItIsGranted_AndNothingElse()
    {
        await StartAsync();
        await using var client = await ConnectAsync(AgentToken("worker"));

        Assert.Equal(["list_agents"], (await client.ListToolsAsync()).Select(t => t.Name));
        Assert.Equal("listed for worker", await CallAsync(client, "list_agents"));
        Assert.StartsWith("ERROR " + AgentToolAuthorization.NotGrantedCode, await CallAsync(client, "update_agent_config"));
    }

    [Fact]
    public async Task TheCoordinator_CallsTheToolsItIsGranted()
    {
        await StartAsync();
        await using var client = await ConnectAsync(AgentToken("cto-agent"));

        Assert.Equal("updated", await CallAsync(client, "update_agent_config"));
    }

    [Fact]
    public async Task ATokenForAnAgentThatDoesNotExist_IsGrantedNothing()
    {
        await StartAsync();
        await using var client = await ConnectAsync(AgentToken("deleted-agent"));

        Assert.Empty(await client.ListToolsAsync());
        Assert.StartsWith("ERROR " + AgentToolAuthorization.NotGrantedCode, await CallAsync(client, "list_agents"));
    }

    [Fact]
    public async Task AToolTakenAwayInTheStore_IsRefusedAtOnce_BeforeAnyReprovision()
    {
        await StartAsync();
        await using var client = await ConnectAsync(AgentToken("cto-agent"));
        Assert.Equal("updated", await CallAsync(client, "update_agent_config"));

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var tool = await db.AgentTools.SingleAsync(t => t.ToolName == "mcp__fleet-orchestrator__update_agent_config");
            tool.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        Assert.StartsWith("ERROR " + AgentToolAuthorization.NotGrantedCode, await CallAsync(client, "update_agent_config"));
    }

    [Fact]
    public async Task TheOperator_IsServedEveryTool_ByTheServiceBearer_OrAServiceToken()
    {
        await StartAsync();

        await using (var byBearer = await ConnectAsync(ServiceBearer))
        {
            Assert.Equal(3, (await byBearer.ListToolsAsync()).Count);
            Assert.Equal("updated", await CallAsync(byBearer, "update_agent_config"));
        }

        var serviceToken = AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, "operator");
        await using var byToken = await ConnectAsync(serviceToken);
        Assert.Equal("updated", await CallAsync(byToken, "update_agent_config"));
    }

    [Fact]
    public async Task AnAgentsToken_IsNotASignInCredential_ForAnythingButTheMcpEndpoint()
    {
        await StartAsync();
        _app.MapPost("/api/agents/example/restart", () => Results.Ok());
        using var http = _app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agents/example/restart");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AgentToken("cto-agent")}");
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WhileTokensAreNotRequired_ACallerWithoutOneIsServedAsBefore_AndAnAgentWithOneIsHeldToItsGrants()
    {
        await StartAsync(enforce: false);

        await using (var legacy = await ConnectAsync(bearer: null))
            Assert.Equal("updated", await CallAsync(legacy, "update_agent_config"));

        await using var worker = await ConnectAsync(AgentToken("worker"));
        Assert.StartsWith("ERROR " + AgentToolAuthorization.NotGrantedCode, await CallAsync(worker, "update_agent_config"));
    }

    [Fact]
    public async Task TheGrantsRoute_ListsEachAgentsGrantsOnTheFleetsServers()
    {
        await StartAsync();
        using var http = _app.GetTestClient();

        var body = await http.GetFromJsonAsync<JsonElement>("/internal/agent-mcp-grants");

        var worker = body.GetProperty("agents").GetProperty("worker").EnumerateArray().Select(g => g.GetString()).ToList();
        // What it was granted, and the two every agent has: memory_get, and notify_cto for all but the CTO.
        Assert.Equal(
            ["mcp__fleet-memory__memory_get", "mcp__fleet-orchestrator__list_agents", "mcp__fleet-temporal__notify_cto"],
            worker);
        var cto = body.GetProperty("agents").GetProperty("cto-agent").EnumerateArray().Select(g => g.GetString()).ToList();
        Assert.DoesNotContain("mcp__fleet-temporal__notify_cto", cto);
        Assert.Contains("mcp__fleet-orchestrator__update_agent_config", cto);
    }
}
