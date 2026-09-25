using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// The orchestrator surface after the #346 card removal, over HTTP: the card and route REST routes
/// and <c>/mcp/context</c> are gone, the four card and route tools are gone, the admin
/// <c>get_project_context</c> still answers, and callers still sending mode fields succeed.
/// </summary>
/// <remarks>
/// The host wires MCP exactly as <c>Program.cs</c> does — <c>AddMcpServer().WithHttpTransport()
/// .WithToolsFromAssembly()</c> over the orchestrator assembly and <c>MapMcp("/mcp")</c> — plus the
/// production <see cref="ProjectContextEndpoints"/>.
/// </remarks>
public sealed class CardRemovalSurfaceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _nextId = 1;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton(new PromptSizePolicy(10_000, 10_000, NullLogger.Instance));
        builder.Services.AddSingleton<IAclChangeNotifier, NoopAclNotifier>();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(ProjectContextTools).Assembly);

        _app = builder.Build();
        _app.MapMcp("/mcp");
        _app.MapProjectContextEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
            var ctx = new ProjectContext { Name = "project-a", CurrentVersion = 1 };
            ctx.Versions.Add(new ProjectContextVersion { VersionNumber = 1, Content = "Project A body." });
            db.ProjectContexts.Add(ctx);
            var agent = new Agent
            {
                Name = "agent-a", DisplayName = "agent-a", Role = "developer", Model = "model-x", ContainerName = "fleet-agent-a",
            };
            agent.Projects.Add(new AgentProject { ProjectName = "project-a" });
            db.Agents.Add(agent);
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
    }

    private sealed class NoopAclNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── REST ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("POST", "/api/project-contexts/project-a/card/versions")]
    [InlineData("POST", "/api/project-contexts/project-a/card/rollback/1")]
    [InlineData("POST", "/api/project-contexts/project-a/routes")]
    [InlineData("DELETE", "/api/project-contexts/project-a/routes/1")]
    [InlineData("POST", "/mcp/context?agent=agent-a")]
    public async Task Removed_routes_return_404(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(new { content = "x", kind = "repo", value = "org/app" });

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Context_detail_and_list_carry_no_card_fields()
    {
        var detail = await _client.GetFromJsonAsync<JsonElement>("/api/project-contexts/project-a");
        var list = await _client.GetFromJsonAsync<JsonElement>("/api/project-contexts");

        Assert.Equal(["name", "currentVersion", "versions"], detail.EnumerateObject().Select(p => p.Name).ToList());
        Assert.Equal(["name", "currentVersion", "isActive", "totalVersions", "agents", "currentBytes"],
            list[0].EnumerateObject().Select(p => p.Name).ToList());
    }

    [Fact]
    public void Config_put_body_with_projectModes_binds_and_ignores_them()
    {
        // What an older dashboard sends. ReadFromJsonAsync uses the web defaults, which skip unknown members.
        var body = JsonSerializer.Deserialize<AgentConfigUpdateRequest>(
            """{"projects":["project-a","project-b"],"projectModes":{"project-a":"card","project-b":"full"}}""",
            JsonSerializerOptions.Web);

        Assert.NotNull(body);
        Assert.Equal(["project-a", "project-b"], body!.Projects!);
    }

    // ── MCP (/mcp) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tool_discovery_lacks_the_card_and_route_tools()
    {
        var session = await InitializeMcpAsync();

        var tools = (await RpcAsync(session, "tools/list")).GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();

        Assert.Contains("get_project_context", tools);
        Assert.Contains("update_agent_config", tools);
        foreach (var removed in new[] { "get_project_card", "update_project_card", "rollback_project_card", "manage_project_routes" })
            Assert.DoesNotContain(removed, tools);
    }

    [Fact]
    public async Task Admin_get_project_context_returns_content()
    {
        var session = await InitializeMcpAsync();

        var text = CallText(await RpcAsync(session, "tools/call",
            new { name = "get_project_context", arguments = new { name = "project-a" } }));

        Assert.StartsWith("## Project Context: project-a\n", text.ReplaceLineEndings("\n"));
        Assert.Contains("Project A body.", text);
    }

    [Fact]
    public async Task Update_agent_config_with_an_extra_project_modes_argument_succeeds()
    {
        var session = await InitializeMcpAsync();

        var text = CallText(await RpcAsync(session, "tools/call", new
        {
            name = "update_agent_config",
            arguments = new { agent_name = "agent-a", projects = "project-a,project-b", project_modes = "project-a=card" },
        }));

        Assert.Contains("projects replaced (2 projects)", text);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(["project-a", "project-b"],
            await db.AgentProjects.Select(p => p.ProjectName).OrderBy(p => p).ToListAsync());
    }

    // ── MCP over raw HTTP ─────────────────────────────────────────────────────

    private async Task<string> InitializeMcpAsync()
    {
        var (session, _) = await SendAsync(null, new
        {
            jsonrpc = "2.0", id = _nextId++, method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "card-removal-test", version = "1.0" },
            },
        });
        Assert.False(string.IsNullOrEmpty(session));
        await SendAsync(session, new { jsonrpc = "2.0", method = "notifications/initialized" });
        return session!;
    }

    private async Task<JsonElement> RpcAsync(string session, string method, object? @params = null)
    {
        var (_, message) = await SendAsync(session, new { jsonrpc = "2.0", id = _nextId++, method, @params = @params ?? new { } });
        return message!.Value;
    }

    private async Task<(string? Session, JsonElement? Message)> SendAsync(string? session, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        request.Content = JsonContent.Create(body);

        using var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var text = await response.Content.ReadAsStringAsync();
        var returned = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;

        // A JSON body, or SSE whose last data: line is the message.
        var trimmed = text.TrimStart();
        var json = trimmed.StartsWith('{')
            ? trimmed
            : text.Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l["data:".Length..].Trim())
                .LastOrDefault(l => l.StartsWith('{'));
        return (returned, json is null ? null : JsonDocument.Parse(json).RootElement.Clone());
    }

    private static string CallText(JsonElement message)
    {
        var result = message.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), message.ToString());
        return string.Concat(result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));
    }
}
