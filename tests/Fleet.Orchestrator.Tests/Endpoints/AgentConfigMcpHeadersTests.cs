using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// #593 A1/A2 over the production adapters: <c>POST /api/agents</c> (<c>provision: false</c>),
/// <c>GET</c>/<c>PUT /api/agents/{name}/config</c>, <c>manage_agent_mcp_endpoints</c> and
/// <c>get_agent_config</c>. Mappings are names only; every refusal saves nothing. All names and
/// values are synthetic.
/// </summary>
public sealed class AgentConfigMcpHeadersTests : IAsyncLifetime
{
    private const string Url = "https://mcp.example.test/mcp";
    private const string KeyValue = "synthetic-key-value-71c2";
    private static readonly string HeldHash = SecretPlaceholders.Hash(SecretPlaceholders.Create());

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _envFile = "";

    private IServiceScopeFactory Scopes => _app.Services.GetRequiredService<IServiceScopeFactory>();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _envFile = Path.Combine(Path.GetTempPath(), $"mcp-headers-{Guid.NewGuid():N}.env");
        // A configured Telegram for create, and a value that must never appear in any answer.
        var token = "123456:" + new string('A', 35);
        await File.WriteAllTextAsync(_envFile,
            $"TELEGRAM_CTO_BOT_TOKEN={token}\nTELEGRAM_NOTIFIER_BOT_TOKEN={token}\nEXAMPLE_KEY={KeyValue}\n");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = _envFile,
            ["Provisioning:BaseDir"] = Path.Combine(Path.GetTempPath(), $"mcp-headers-{Guid.NewGuid():N}"),
        });
        IConfiguration config = builder.Configuration;
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton(new DockerService(
            NullLogger<DockerService>.Instance, new HttpClient(new RefusingDockerApi()) { BaseAddress = new Uri("http://localhost") }));
        builder.Services.AddSingleton(new JournalTokenService(config));
        builder.Services.AddSingleton<ContainerProvisioningService>();
        builder.Services.AddSingleton(new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance));
        builder.Services.AddSingleton(sp => new SetupService(
            config, null!, null!, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SetupService>.Instance, Substitute.For<ICredentialsReader>()));
        builder.Services.AddSingleton(sp => new ConfigService(
            config, Options.Create(new RabbitMqOptions()), sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConfigService>.Instance));
        builder.Services.AddSingleton(new TemporalClientRegistry(
            Options.Create(new TemporalOptions()), NullLogger<TemporalClientRegistry>.Instance));
        builder.Services.AddSingleton(new AgentConfigPublisherService(
            Options.Create(new RabbitMqOptions()), NullLogger<AgentConfigPublisherService>.Instance));
        builder.Services.AddSingleton<IAclChangeNotifier>(new NoopAclChangeNotifier());

        builder.Services.AddAgentWrites();
        _app = builder.Build();
        _app.MapAgentCreateEndpoints();
        _app.MapAgentConfigEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Agents.Add(Agent("agent0", "co-cto"));
            var agent1 = Agent("agent1", "developer");
            agent1.EnvRefs =
            [
                new AgentEnvRef { EnvKeyName = "EXAMPLE_KEY", PlaceholderSha256 = HeldHash },
                new AgentEnvRef { EnvKeyName = "EXAMPLE_SECRET" },
            ];
            agent1.McpEndpoints = [new AgentMcpEndpoint { McpName = "example", Url = Url, TransportType = "http" }];
            db.Agents.Add(agent1);
            var agent2 = Agent("agent2", "developer");
            agent2.EnvRefs = [new AgentEnvRef { EnvKeyName = "OTHER_KEY" }];
            db.Agents.Add(agent2);
            await db.SaveChangesAsync();
        }

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

    private static Agent Agent(string name, string role) => new()
    {
        Name = name, DisplayName = name, Role = role, Model = "claude-opus-5-5", Provider = "claude",
        MemoryLimitMb = 1024, ContainerName = "fleet-" + name,
    };

    private static object TwoMappings => new[]
    {
        new { headerName = "X-Example-Key", envRefName = "EXAMPLE_KEY" },
        new { headerName = "X-Example-Secret", envRefName = "EXAMPLE_SECRET" },
    };

    private Task<HttpResponseMessage> PutRawAsync(string agent, string json) =>
        _client.PutAsync($"/api/agents/{agent}/config", new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<string?> StoredJsonAsync(string agent = "agent1", string endpoint = "example")
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return (await db.AgentMcpEndpoints.AsNoTracking().Include(e => e.Agent)
            .SingleAsync(e => e.Agent.Name == agent && e.McpName == endpoint)).HeaderEnvRefsJson;
    }

    private async Task<List<(string Header, string Ref)>> ReadAsync(string agent = "agent1", string endpoint = "example")
    {
        var config = await _client.GetFromJsonAsync<JsonElement>($"/api/agents/{agent}/config");
        var ep = config.GetProperty("mcpEndpoints").EnumerateArray().Single(e => e.GetProperty("mcpName").GetString() == endpoint);
        return [.. ep.GetProperty("headerEnvRefs").EnumerateArray()
            .Select(m => (m.GetProperty("headerName").GetString()!, m.GetProperty("envRefName").GetString()!))];
    }

    private async Task SetStoredAsync(string? json)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var endpoint = await db.AgentMcpEndpoints.Include(e => e.Agent).SingleAsync(e => e.Agent.Name == "agent1" && e.McpName == "example");
        endpoint.HeaderEnvRefsJson = json;
        await db.SaveChangesAsync();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    // ── A1: create, read, update ──────────────────────────────────────────────

    [Fact]
    public async Task Create_stores_two_mappings_and_get_returns_names_only()
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "agent3", role = "developer", model = "claude-opus-5-5", provision = false,
            envRefs = new[] { "EXAMPLE_KEY", "EXAMPLE_SECRET" },
            mcpEndpoints = new[] { new { mcpName = "example", url = Url, transportType = "http", headerEnvRefs = TwoMappings } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([("X-Example-Key", "EXAMPLE_KEY"), ("X-Example-Secret", "EXAMPLE_SECRET")], await ReadAsync("agent3"));
        var stored = await StoredJsonAsync("agent3");
        Assert.Equal("""[{"headerName":"X-Example-Key","envRefName":"EXAMPLE_KEY"},{"headerName":"X-Example-Secret","envRefName":"EXAMPLE_SECRET"}]""", stored);
        var body = await _client.GetStringAsync("/api/agents/agent3/config");
        Assert.DoesNotContain(KeyValue, body);
    }

    [Fact]
    public async Task Create_maps_only_env_refs_it_supplies()
    {
        // The default bot token reference is added by the create, not supplied by the operator.
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "agent3", role = "developer", model = "claude-opus-5-5", provision = false,
            envRefs = new[] { "EXAMPLE_KEY" },
            mcpEndpoints = new[] { new { mcpName = "example", url = Url, transportType = "http", headerEnvRefs = TwoMappings } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(McpHeaderMappings.RefMissing, await ErrorAsync(response));
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.AnyAsync(a => a.Name == "agent3"));
    }

    [Fact]
    public async Task A_new_endpoint_defaults_to_none_and_get_always_returns_an_array()
    {
        Assert.Null(await StoredJsonAsync());
        Assert.Empty(await ReadAsync());
    }

    [Fact]
    public async Task Put_supplied_replaces_omitted_and_null_preserve_and_empty_clears()
    {
        var set = await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            mcpEndpoints = new[] { new { mcpName = "example", url = Url, transportType = "http", headerEnvRefs = TwoMappings } },
        });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(2, (await ReadAsync()).Count);

        // A legacy replace-all list without the field, matched by name in another letter case.
        Assert.Equal(HttpStatusCode.OK, (await PutRawAsync("agent1",
            $$"""{"mcpEndpoints":[{"mcpName":"EXAMPLE","url":"{{Url}}","transportType":"http"}]}""")).StatusCode);
        Assert.Equal(2, (await ReadAsync("agent1", "EXAMPLE")).Count);

        Assert.Equal(HttpStatusCode.OK, (await PutRawAsync("agent1",
            $$"""{"mcpEndpoints":[{"mcpName":"example","url":"{{Url}}","transportType":"http","headerEnvRefs":null}]}""")).StatusCode);
        Assert.Equal(2, (await ReadAsync()).Count);

        Assert.Equal(HttpStatusCode.OK, (await PutRawAsync("agent1",
            $$"""{"mcpEndpoints":[{"mcpName":"example","url":"{{Url}}","transportType":"http","headerEnvRefs":[{"headerName":"Authorization","envRefName":"EXAMPLE_KEY"}]}]}""")).StatusCode);
        Assert.Equal([("Authorization", "EXAMPLE_KEY")], await ReadAsync());

        Assert.Equal(HttpStatusCode.OK, (await PutRawAsync("agent1",
            $$"""{"mcpEndpoints":[{"mcpName":"example","url":"{{Url}}","transportType":"http","headerEnvRefs":[]}]}""")).StatusCode);
        Assert.Empty(await ReadAsync());
        Assert.Null(await StoredJsonAsync());
    }

    [Fact]
    public async Task Deleting_an_endpoint_deletes_its_mappings()
    {
        await SetStoredAsync("""[{"headerName":"X-Example-Key","envRefName":"EXAMPLE_KEY"}]""");

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/agents/agent1/config", new { mcpEndpoints = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            mcpEndpoints = new[] { new { mcpName = "example", url = Url, transportType = "http" } },
        })).StatusCode);

        Assert.Empty(await ReadAsync());
    }

    [Fact]
    public async Task A_save_with_mappings_leaves_reference_grants_unchanged()
    {
        await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            envRefs = new[] { "EXAMPLE_KEY", "EXAMPLE_SECRET" },
            mcpEndpoints = new[] { new { mcpName = "example", url = Url, transportType = "http", headerEnvRefs = TwoMappings } },
        });

        await using var scope = _app.Services.CreateAsyncScope();
        var refs = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().AgentEnvRefs.AsNoTracking()
            .Include(r => r.Agent).Where(r => r.Agent.Name == "agent1").ToDictionaryAsync(r => r.EnvKeyName, r => r.PlaceholderSha256);
        Assert.Equal(["EXAMPLE_KEY", "EXAMPLE_SECRET"], refs.Keys.Order());
        Assert.Equal(HeldHash, refs["EXAMPLE_KEY"]);
        Assert.Null(refs["EXAMPLE_SECRET"]);
    }

    // ── A2: what is refused, and that a refusal saves nothing ─────────────────

    public static TheoryData<string, string> Refused => new()
    {
        { """[{"headerName":"X-Example-Key","envRefName":"EXAMPLE_KEY","value":"inline"}]""", "Invalid request body" },
        { """[{"headerName":42,"envRefName":"EXAMPLE_KEY"}]""", "Invalid request body" },
        { """["X-Example-Key"]""", "Invalid request body" },
        { """[{"headerName":"X-Example-Key"}]""", McpHeaderMappings.Invalid },
        { """[null]""", McpHeaderMappings.Invalid },
        { """[{"headerName":"X Example","envRefName":"EXAMPLE_KEY"}]""", McpHeaderMappings.Invalid },
        { """[{"headerName":"Host","envRefName":"EXAMPLE_KEY"}]""", McpHeaderMappings.Invalid },
        { """[{"headerName":"Cookie","envRefName":"EXAMPLE_KEY"}]""", McpHeaderMappings.Invalid },
        { """[{"headerName":"X-Example-Key","envRefName":"1BAD"}]""", McpHeaderMappings.Invalid },
        { """[{"headerName":"X-Example-Key","envRefName":"TELEGRAM_NOTIFIER_BOT_TOKEN"}]""", McpHeaderMappings.Invalid },
        { """[{"headerName":"X-Example-Key","envRefName":"ZAI_CODING_PLAN_API_KEY"}]""", McpHeaderMappings.Invalid },
        { """[{"headerName":"X-Example-Key","envRefName":"EXAMPLE_KEY"},{"headerName":"x-example-key","envRefName":"EXAMPLE_SECRET"}]""", McpHeaderMappings.Invalid },
        { """[{"headerName":"X-Example-Key","envRefName":"MISSING_KEY"}]""", McpHeaderMappings.RefMissing },
        { """[{"headerName":"X-Example-Key","envRefName":"OTHER_KEY"}]""", McpHeaderMappings.RefMissing },
        { """[{"headerName":"X-Example-Key","envRefName":"example_key"}]""", McpHeaderMappings.RefMissing },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Put_refuses_and_saves_nothing(string mappings, string expected)
    {
        var response = await PutRawAsync("agent1",
            $$"""{"memoryLimitMb":2048,"mcpEndpoints":[{"mcpName":"example","url":"{{Url}}","transportType":"http","headerEnvRefs":{{mappings}}}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ErrorAsync(response);
        Assert.StartsWith(expected, error);
        Assert.DoesNotContain("inline", error);
        Assert.Null(await StoredJsonAsync());
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.Equal(1024, (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.AsNoTracking()
            .SingleAsync(a => a.Name == "agent1")).MemoryLimitMb);
    }

    [Theory]
    [InlineData("example", "sse", "claude", McpHeaderMappings.Unsupported)]
    [InlineData("example", "stdio", "codex", McpHeaderMappings.Unsupported)]
    [InlineData("example", "http", "gemini", McpHeaderMappings.Unsupported)]
    [InlineData("fleet-comms-journal", "http", "claude", McpHeaderMappings.Invalid)]
    [InlineData("fleet-journal-files", "http", "claude", McpHeaderMappings.Invalid)]
    public async Task Put_refuses_unsupported_endpoints_and_providers(string name, string transport, string provider, string expected)
    {
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            provider,
            mcpEndpoints = new[] { new { mcpName = name, url = Url, transportType = transport, headerEnvRefs = TwoMappings } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(expected, await ErrorAsync(response));
        Assert.Null(await StoredJsonAsync());
    }

    // M2: a mapped endpoint carries its credentials in headers, never in its URL, whatever its references.
    [Theory]
    [InlineData("https://user:synthetic-pw@mcp.example.test/mcp")]
    [InlineData("http://user:synthetic-pw@plain.example.test/mcp")]
    [InlineData("http://synthetic-pw@plain.example.test/mcp")]
    [InlineData("ftp://plain.example.test/mcp")]
    [InlineData("/mcp")]
    public async Task A_mapped_endpoint_url_with_user_info_or_not_http_is_refused_on_every_write_path(string url)
    {
        // EXAMPLE_SECRET is unbound here: the rule does not depend on a binding.
        var unbound = new[] { new { headerName = "X-Example-Secret", envRefName = "EXAMPLE_SECRET" } };

        var put = await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            mcpEndpoints = new[] { new { mcpName = "example", url, transportType = "http", headerEnvRefs = unbound } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var putError = await ErrorAsync(put);
        Assert.StartsWith(McpHeaderMappings.Invalid, putError);
        Assert.DoesNotContain("synthetic-pw", putError);
        Assert.Null(await StoredJsonAsync());

        var create = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "agent3", role = "developer", model = "claude-opus-5-5", provision = false,
            envRefs = new[] { "EXAMPLE_SECRET" },
            mcpEndpoints = new[] { new { mcpName = "example", url, transportType = "http", headerEnvRefs = unbound } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.DoesNotContain("synthetic-pw", await ErrorAsync(create));

        var tool = new ManageAgentMcpEndpointsTool(Scopes);
        var added = await tool.ManageAgentMcpEndpointsAsync("agent1", "add", "second", url, "http", [new("X-Example-Secret", "EXAMPLE_SECRET")]);
        Assert.StartsWith(McpHeaderMappings.Invalid, added);
        Assert.DoesNotContain("synthetic-pw", added);
        await SetStoredAsync("""[{"headerName":"X-Example-Secret","envRefName":"EXAMPLE_SECRET"}]""");
        var moved = await tool.ManageAgentMcpEndpointsAsync("agent1", "update", "example", url);
        Assert.StartsWith(McpHeaderMappings.Invalid, moved);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.False(await db.Agents.AnyAsync(a => a.Name == "agent3"));
        Assert.False(await db.AgentMcpEndpoints.AnyAsync(e => e.McpName == "second"));
        Assert.Equal(Url, (await db.AgentMcpEndpoints.AsNoTracking().SingleAsync(e => e.McpName == "example" && e.Agent.Name == "agent1")).Url);
    }

    [Fact]
    public async Task A_headerless_endpoint_url_is_unchanged_by_the_rule()
    {
        // Only a mapped endpoint is held to it: a headerless one saves what it always did.
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            mcpEndpoints = new[] { new { mcpName = "example", url = "sse-name-only", transportType = "sse" } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_provider_change_is_checked_against_the_mappings_the_agent_keeps()
    {
        await SetStoredAsync("""[{"headerName":"X-Example-Key","envRefName":"EXAMPLE_KEY"}]""");

        var gemini = await _client.PutAsJsonAsync("/api/agents/agent1/config", new { provider = "gemini" });
        Assert.Equal(HttpStatusCode.BadRequest, gemini.StatusCode);
        Assert.StartsWith(McpHeaderMappings.Unsupported, await ErrorAsync(gemini));

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/agents/agent1/config", new { provider = "codex" })).StatusCode);
    }

    [Fact]
    public async Task Removing_a_mapped_env_ref_stays_allowed_and_leaves_a_visible_dangling_mapping()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            mcpEndpoints = new[] { new { mcpName = "example", url = Url, transportType = "http", headerEnvRefs = TwoMappings } },
        })).StatusCode);

        // The dashboard sends every list on a save: the unchanged mapping is not authorized again.
        var save = await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            envRefs = new[] { "EXAMPLE_KEY" },
            mcpEndpoints = new[] { new { mcpName = "example", url = Url, transportType = "http", headerEnvRefs = TwoMappings } },
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var writer = _app.Services.GetRequiredService<AgentConfigWriter>();
        Assert.Equal(EnvRefChange.Removed, (await writer.RemoveEnvRefAsync("agent1", "EXAMPLE_KEY")).Value);

        Assert.Equal([("X-Example-Key", "EXAMPLE_KEY"), ("X-Example-Secret", "EXAMPLE_SECRET")], await ReadAsync());

        // A new pair still needs a held reference.
        var added = await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            mcpEndpoints = new[] { new { mcpName = "example", url = Url, transportType = "http",
                headerEnvRefs = new[] { new { headerName = "Authorization", envRefName = "EXAMPLE_SECRET" } } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
    }

    [Fact]
    public async Task A_malformed_stored_value_is_refused_never_read_as_none_and_can_be_cleared()
    {
        await SetStoredAsync("""{"headerName":"X-Example-Key"}""");

        var get = await _client.GetAsync("/api/agents/agent1/config");
        Assert.Equal(HttpStatusCode.InternalServerError, get.StatusCode);
        Assert.Contains(McpHeaderMappings.Invalid, await get.Content.ReadAsStringAsync());

        var keep = await PutRawAsync("agent1", $$"""{"mcpEndpoints":[{"mcpName":"example","url":"{{Url}}","transportType":"http"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, keep.StatusCode);
        Assert.StartsWith(McpHeaderMappings.Invalid, await ErrorAsync(keep));

        var clear = await PutRawAsync("agent1", $$"""{"mcpEndpoints":[{"mcpName":"example","url":"{{Url}}","transportType":"http","headerEnvRefs":[]}]}""");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Empty(await ReadAsync());
    }

    // ── A1: the MCP management tool and get_agent_config ──────────────────────

    [Fact]
    public async Task Mcp_add_update_read_and_remove_round_trip_names_only()
    {
        var tool = new ManageAgentMcpEndpointsTool(Scopes);
        McpHeaderEnvRef?[] two = [new("X-Example-Key", "EXAMPLE_KEY"), new("X-Example-Secret", "EXAMPLE_SECRET")];

        var added = await tool.ManageAgentMcpEndpointsAsync("agent1", "add", "second", Url, "http", two);
        Assert.Contains("headers [X-Example-Key ← EXAMPLE_KEY, X-Example-Secret ← EXAMPLE_SECRET]", added);
        Assert.Equal(2, (await ReadAsync("agent1", "second")).Count);

        var shown = await new GetAgentConfigTool(Scopes).GetAgentConfigAsync("agent1");
        Assert.Contains($"- second: {Url} (http) — headers: X-Example-Key ← EXAMPLE_KEY, X-Example-Secret ← EXAMPLE_SECRET", shown);
        Assert.DoesNotContain(KeyValue, shown);

        // Omitted keeps them; a URL change alone leaves them in place.
        await tool.ManageAgentMcpEndpointsAsync("agent1", "update", "second", "https://mcp.example.test/v2/mcp");
        Assert.Equal(2, (await ReadAsync("agent1", "second")).Count);

        var cleared = await tool.ManageAgentMcpEndpointsAsync("agent1", "update", "second", header_env_refs: []);
        Assert.Contains("header_env_refs:", cleared);
        Assert.Empty(await ReadAsync("agent1", "second"));

        Assert.Contains("Removed", await tool.ManageAgentMcpEndpointsAsync("agent1", "remove", "second"));
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().AgentMcpEndpoints.AnyAsync(e => e.McpName == "second"));
    }

    [Fact]
    public async Task Mcp_maps_only_references_the_agent_already_holds_and_refusals_save_nothing()
    {
        var tool = new ManageAgentMcpEndpointsTool(Scopes);

        var other = await tool.ManageAgentMcpEndpointsAsync("agent1", "add", "second", Url, "http", [new("X-Example-Key", "OTHER_KEY")]);
        Assert.StartsWith(McpHeaderMappings.RefMissing, other);
        Assert.Contains("Nothing was changed", other);

        var sse = await tool.ManageAgentMcpEndpointsAsync("agent1", "update", "example", transport_type: "sse",
            header_env_refs: [new("X-Example-Key", "EXAMPLE_KEY")]);
        Assert.StartsWith(McpHeaderMappings.Unsupported, sse);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.False(await db.AgentMcpEndpoints.AnyAsync(e => e.McpName == "second"));
        var example = await db.AgentMcpEndpoints.AsNoTracking().SingleAsync(e => e.McpName == "example" && e.Agent.Name == "agent1");
        Assert.Equal("http", example.TransportType);
        Assert.Null(example.HeaderEnvRefsJson);
    }

    private sealed class NoopAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Fails the test if anything reaches Docker.</summary>
    private sealed class RefusingDockerApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"unexpected Docker call {request.Method} {request.RequestUri}");
    }
}
