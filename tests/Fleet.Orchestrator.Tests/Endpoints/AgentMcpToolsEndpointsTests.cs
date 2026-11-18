using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.McpTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// <c>POST /api/agents/{name}/mcp-tools</c> over real HTTP: which saved endpoints are asked, what
/// they are sent, and that the answer carries tool names and descriptions and never a header value.
/// </summary>
public sealed class AgentMcpToolsEndpointsTests : IAsyncLifetime
{
    private const string Post = "/api/agents/agent1/mcp-tools";
    private const string Secret = "value-from-env-file";
    private const string BoundSecret = "bound-value-from-env-file";

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _envFile = null!;
    private readonly RecordingLister _lister = new();

    private sealed class RecordingLister : IMcpToolLister
    {
        public List<McpListTarget> Asked { get; } = [];

        public Task<McpServerTools> ListAsync(McpListTarget target, CancellationToken ct)
        {
            lock (Asked) Asked.Add(target);
            return Task.FromResult(new McpServerTools(
                target.Name, McpServerTools.Ok, null,
                [new McpToolInfo("lookup", $"Looks things up on {target.Name}."), new McpToolInfo("plain", null)]));
        }
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _envFile = Path.Combine(Path.GetTempPath(), $"mcp-tools-{Guid.NewGuid():N}.env");
        await File.WriteAllTextAsync(_envFile, $"EXAMPLE_API_KEY={Secret}\nBOUND_API_KEY={BoundSecret}\n");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
                ["AgentIdentity:Key"] = new string('A', 43),
            ["Provisioning:EnvFilePath"] = _envFile,
            ["FleetMemory:McpUrl"] = "http://fleet-memory:3100/mcp",
        });
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton<JournalTokenService>();
        builder.Services.AddSingleton<IMcpToolLister>(_lister);
        builder.Services.AddSingleton<McpToolDiscovery>();

        _app = builder.Build();
        _app.MapAgentMcpToolsEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreatedAsync();

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        File.Delete(_envFile);
    }

    private async Task SeedAsync(Action<Agent, OrchestratorDbContext> configure)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "developer", Model = "sonnet", Provider = "claude",
            ContainerName = "fleet-agent1", MemoryLimitMb = 1024,
        };
        configure(agent, db);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
    }

    private static string Mapping(string header, string envRef) =>
        $$"""[{"headerName":"{{header}}","envRefName":"{{envRef}}"}]""";

    private async Task<(JsonElement Body, string Raw)> ListAsync()
    {
        var response = await _client.PostAsync(Post, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(raw).RootElement, raw);
    }

    private static JsonElement Server(JsonElement body, string name) =>
        Assert.Single(body.GetProperty("servers").EnumerateArray(), s => s.GetProperty("name").GetString() == name);

    [Fact]
    public async Task AnUnknownAgent_Is404_AndNothingIsAsked()
    {
        var response = await _client.PostAsync("/api/agents/nobody/mcp-tools", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_lister.Asked);
    }

    [Fact]
    public async Task TheRoute_IsNotAGet()
    {
        await SeedAsync((_, _) => { });

        var response = await _client.GetAsync(Post);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(_lister.Asked);
    }

    [Fact]
    public async Task EachSavedEndpoint_IsAsked_AndItsToolsComeBackWithDescriptions()
    {
        await SeedAsync((agent, _) =>
        {
            agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "example", Url = "https://mcp.example.test/mcp", TransportType = "http" });
            agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-telegram", Url = "http://fleet-telegram:3800/mcp", TransportType = "http" });
        });

        var (body, _) = await ListAsync();

        Assert.Equal("agent1", body.GetProperty("agent").GetString());
        Assert.Equal(
            ["Read", "Write", "Edit", "Bash", "Glob", "Grep"],
            body.GetProperty("alwaysOn").EnumerateArray().Select(t => t.GetString()));
        // The agent's own servers, and fleet-memory, which every agent is given.
        Assert.Equal(
            ["example", "fleet-memory", "fleet-telegram"],
            body.GetProperty("servers").EnumerateArray().Select(s => s.GetProperty("name").GetString()));

        var example = Server(body, "example");
        Assert.Equal("ok", example.GetProperty("status").GetString());
        var tools = example.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal("lookup", tools[0].GetProperty("name").GetString());
        Assert.Equal("Looks things up on example.", tools[0].GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, tools[1].GetProperty("description").ValueKind);

        var telegram = Assert.Single(_lister.Asked, t => t.Name == "fleet-telegram");
        Assert.Equal("?formatting_mode=0", telegram.Url.Query);
    }

    [Fact]
    public async Task AnUnboundHeader_IsSentToTheServer_AndItsValueIsNotInTheAnswer()
    {
        await SeedAsync((agent, _) =>
        {
            agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "EXAMPLE_API_KEY" });
            agent.McpEndpoints.Add(new AgentMcpEndpoint
            {
                McpName = "example", Url = "https://mcp.example.test/mcp", TransportType = "http",
                HeaderEnvRefsJson = Mapping("X-Example-Key", "EXAMPLE_API_KEY"),
            });
        });

        var (body, raw) = await ListAsync();

        Assert.Equal("ok", Server(body, "example").GetProperty("status").GetString());
        var asked = Assert.Single(_lister.Asked, t => t.Name == "example");
        Assert.Equal(Secret, asked.Headers["X-Example-Key"]);
        Assert.DoesNotContain(Secret, raw);
    }

    private Task SeedBoundAsync(string url, string hosts) => SeedAsync((agent, db) =>
    {
        db.SecretBindings.Add(new SecretBinding { EnvKeyName = "BOUND_API_KEY", Hosts = hosts, HeaderName = "X-Example-Key" });
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "BOUND_API_KEY" });
        agent.McpEndpoints.Add(new AgentMcpEndpoint
        {
            McpName = "example", Url = url, TransportType = "http",
            HeaderEnvRefsJson = Mapping("X-Example-Key", "BOUND_API_KEY"),
        });
    });

    [Fact]
    public async Task ABoundSecret_IsSentToItsBindingsHost_AndItsValueIsNotInTheAnswer()
    {
        await SeedBoundAsync("https://mcp.example.test/mcp", hosts: "mcp.example.test");

        var (body, raw) = await ListAsync();

        Assert.Equal("ok", Server(body, "example").GetProperty("status").GetString());
        var asked = Assert.Single(_lister.Asked, t => t.Name == "example");
        Assert.Equal("mcp.example.test", asked.Url.Host);
        Assert.Equal(BoundSecret, asked.Headers["X-Example-Key"]);
        Assert.DoesNotContain(BoundSecret, raw);
    }

    [Fact]
    public async Task ABoundSecret_IsNotSentToAHostItsBindingDoesNotName()
    {
        await SeedBoundAsync("https://other.example.test/mcp", hosts: "mcp.example.test");

        var (body, raw) = await ListAsync();

        var example = Server(body, "example");
        Assert.Equal("misconfigured", example.GetProperty("status").GetString());
        Assert.Empty(example.GetProperty("tools").EnumerateArray());
        Assert.Contains("BOUND_API_KEY", example.GetProperty("reason").GetString());
        Assert.DoesNotContain(_lister.Asked, t => t.Name == "example");
        Assert.DoesNotContain(BoundSecret, raw);
        // The other servers are still asked.
        Assert.Equal("ok", Server(body, "fleet-memory").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnAgentWithoutHeaders_NeverReadsTheEnvFile()
    {
        File.Delete(_envFile);
        await SeedAsync((agent, _) =>
            agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "example", Url = "https://mcp.example.test/mcp", TransportType = "http" }));

        var (body, _) = await ListAsync();

        Assert.Equal("ok", Server(body, "example").GetProperty("status").GetString());
    }
}
