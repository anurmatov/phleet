using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;
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
using ModelContextProtocol.AspNetCore;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// <c>/mcp/context</c> against the REAL pinned SDK (#347 D6/D7, AC 8/9): every row of the spike
/// matrix and of the guard table, over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The host is built from the production pieces, called rather than copied:
/// <see cref="FleetMcpRegistration.AddFleetMcpServer"/> (transport options, the
/// <c>ConfigureSessionOptions</c> filter, every orchestrator tool, the registry) and
/// <see cref="FleetMcpRegistration.MapFleetMcp"/> (the guard, then both <c>MapMcp</c> routes), behind
/// the bearer gate built on <see cref="OrchestratorAuth.RequiresBearerToken"/> with a token set.
/// </para>
/// <para>
/// The SDK's behaviour is load-bearing here — implicit sessions, one session table across routes,
/// the header the session id comes back in. If an SDK bump moves any of it, these fail; the pin
/// must not move without them passing.
/// </para>
/// </remarks>
public class ContextMcpRouteTests : IAsyncLifetime
{
    private const string BearerToken = "test-bearer-token";
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly ProvisioningLogSink _logs = new();

    private ContextSessionRegistry Registry => _app.Services.GetRequiredService<ContextSessionRegistry>();

    public Task InitializeAsync() => StartAsync(withGuard: true);

    /// <summary>
    /// <paramref name="withGuard"/> = false reproduces the spike's unguarded host (both routes, the
    /// session filter, no guard). Used only by the test that pins the ACL to the request path.
    /// </summary>
    private async Task StartAsync(bool withGuard)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SinkLoggerProvider(_logs));
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddFleetMcpServer();
        // ProjectContextTools takes the #346 size policy; Program.cs registers it outside AddFleetMcpServer.
        builder.Services.AddSingleton(new PromptSizePolicy(10_000, 10_000, NullLogger.Instance));

        _app = builder.Build();

        // The production bearer gate, with a token configured.
        _app.Use(async (context, next) =>
        {
            if (!OrchestratorAuth.RequiresBearerToken(context.Request.Method, context.Request.Path.Value ?? ""))
            {
                await next(context);
                return;
            }

            var header = context.Request.Headers.Authorization.FirstOrDefault();
            var token = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? header["Bearer ".Length..].Trim()
                : null;
            if (token != BearerToken)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
        _app.MapPost("/api/probe", () => Results.Ok());

        if (withGuard)
        {
            _app.MapFleetMcp();
        }
        else
        {
            _app.MapMcp(ContextMcpRoute.ContextPath);
            _app.MapMcp(ContextMcpRoute.AdminPath);
        }

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
            Seed(db);
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

    /// <summary>agent-a: project-a (card), project-b (full). agent-b: nothing. project-c exists, unassigned.</summary>
    private static void Seed(OrchestratorDbContext db)
    {
        foreach (var (name, content) in new[] { ("project-a", "A body"), ("project-b", "B body"), ("project-c", "C body") })
        {
            var ctx = new ProjectContext { Name = name, CurrentVersion = 2 };
            ctx.Versions.Add(new ProjectContextVersion { VersionNumber = 1, Content = $"{content} v1" });
            ctx.Versions.Add(new ProjectContextVersion { VersionNumber = 2, Content = $"{content} v2" });
            db.ProjectContexts.Add(ctx);
        }

        var a = NewAgent(AgentA);
        a.Projects.Add(new AgentProject { ProjectName = "project-a", ContextMode = ProjectContextMode.Card });
        a.Projects.Add(new AgentProject { ProjectName = "project-b" });
        db.Agents.Add(a);
        db.Agents.Add(NewAgent(AgentB));

        // A memory-ACL grant for a project agent-a is NOT assigned. D7 must never read it.
        db.AgentProjectAccess.Add(new AgentProjectAccess { AgentName = AgentA, Project = "project-c" });
    }

    private static Agent NewAgent(string name) => new()
    {
        Name = name, DisplayName = name, Role = "developer", Model = "model-x", ContainerName = $"fleet-{name}",
    };

    // ── MCP over raw HTTP ─────────────────────────────────────────────────────

    private sealed record McpResponse(HttpStatusCode Status, string? SessionId, JsonElement? Message, string Body);

    private static string Ctx(string? agent) => agent is null ? ContextMcpRoute.ContextPath : $"{ContextMcpRoute.ContextPath}?agent={agent}";

    private int _nextId = 1;

    private async Task<McpResponse> SendAsync(HttpMethod method, string path, string? sessionId, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null) request.Headers.Add(ContextMcpRoute.SessionIdHeader, sessionId);
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var returnedSession = response.Headers.TryGetValues(ContextMcpRoute.SessionIdHeader, out var values)
            ? values.FirstOrDefault()
            : null;

        return new McpResponse(response.StatusCode, returnedSession, ParseMessage(text), text);
    }

    /// <summary>The JSON-RPC message in a JSON or SSE body (the last <c>data:</c> line).</summary>
    private static JsonElement? ParseMessage(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
            return JsonDocument.Parse(trimmed).RootElement.Clone();

        var data = text.Split('\n')
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => l["data:".Length..].Trim())
            .LastOrDefault(l => l.StartsWith('{'));
        return data is null ? null : JsonDocument.Parse(data).RootElement.Clone();
    }

    private Task<McpResponse> RpcAsync(string path, string? sessionId, string method, object? @params = null) =>
        SendAsync(HttpMethod.Post, path, sessionId, new { jsonrpc = "2.0", id = _nextId++, method, @params = @params ?? new { } });

    private async Task<string> InitializeAsync(string path)
    {
        var init = await RpcAsync(path, null, "initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "route-test", version = "1.0" },
        });
        Assert.Equal(HttpStatusCode.OK, init.Status);
        Assert.False(string.IsNullOrEmpty(init.SessionId), $"no session id: {init.Body}");

        var initialized = await SendAsync(HttpMethod.Post, path, init.SessionId,
            new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.True(initialized.Status is HttpStatusCode.Accepted or HttpStatusCode.OK, initialized.Body);
        return init.SessionId!;
    }

    private static List<string> ToolNames(McpResponse response) =>
        response.Message!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();

    private Task<McpResponse> CallAsync(string path, string? sessionId, string tool, object args) =>
        RpcAsync(path, sessionId, "tools/call", new { name = tool, arguments = args });

    /// <summary>The text of a successful tools/call result.</summary>
    private static string ResultText(McpResponse response)
    {
        Assert.Equal(HttpStatusCode.OK, response.Status);
        var result = response.Message!.Value.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), response.Body);
        return string.Concat(result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));
    }

    // ── initialize on each route ──────────────────────────────────────────────

    [Fact]
    public async Task Initialize_OnContextRoute_BindsTheSessionToTheAgent()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        Assert.True(Registry.TryGetAgent(session, out var bound));
        Assert.Equal(AgentA, bound);
        Assert.Contains(_logs.Entries, e => e.Message.StartsWith($"ContextMcpGuard bound agent={AgentA}", StringComparison.Ordinal));
        // The session id is never logged, only its 6-char hash.
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains(session, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Initialize_OnAdminRoute_IsNotBound()
    {
        var session = await InitializeAsync(ContextMcpRoute.AdminPath);

        Assert.False(Registry.Contains(session));
    }

    // ── the spike matrix, guarded ─────────────────────────────────────────────

    [Fact]
    public async Task ContextSession_ToolsList_IsExactlyGetProjectContext()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var list = await RpcAsync(Ctx(AgentA), session, "tools/list");

        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal([ContextMcpRoute.FallbackToolName], ToolNames(list));
    }

    [Fact]
    public async Task ContextSession_SameAgent_Passes()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var call = await CallAsync(Ctx(AgentA), session, "get_project_context", new { name = "project-b" });

        Assert.Contains("B body v2", ResultText(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextSession_OtherAgent_Is403()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var call = await CallAsync(Ctx(AgentB), session, "get_project_context", new { name = "project-a" });

        Assert.Equal(HttpStatusCode.Forbidden, call.Status);
        Assert.Contains(_logs.Entries, e => e.Message.StartsWith("ContextMcpGuard rejected status=403 reason=agent_mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContextSession_OnAdminRoute_Is403()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var list = await RpcAsync(ContextMcpRoute.AdminPath, session, "tools/list");
        var call = await CallAsync(ContextMcpRoute.AdminPath, session, "get_project_context", new { name = "project-c" });

        Assert.Equal(HttpStatusCode.Forbidden, list.Status);
        Assert.Equal(HttpStatusCode.Forbidden, call.Status);
        Assert.Contains(_logs.Entries, e => e.Message.StartsWith("ContextMcpGuard rejected status=403 reason=context_session_on_admin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContextSession_OnAdminLegacyMessagePath_Is403()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var post = await RpcAsync($"{ContextMcpRoute.AdminPath}/message?sessionId={session}", null, "tools/list");

        Assert.Equal(HttpStatusCode.Forbidden, post.Status);
    }

    [Fact]
    public async Task AdminSession_OnContextRoute_Is404()
    {
        var session = await InitializeAsync(ContextMcpRoute.AdminPath);

        var list = await RpcAsync(Ctx(AgentA), session, "tools/list");
        var call = await CallAsync(Ctx(AgentA), session, "list_project_contexts", new { });

        Assert.Equal(HttpStatusCode.NotFound, list.Status);
        Assert.Equal(HttpStatusCode.NotFound, call.Status);
        Assert.Contains(_logs.Entries, e => e.Message.StartsWith("ContextMcpGuard rejected status=404 reason=unbound_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdminSession_OnAdminRoute_IsUnchanged()
    {
        var session = await InitializeAsync(ContextMcpRoute.AdminPath);

        var list = await RpcAsync(ContextMcpRoute.AdminPath, session, "tools/list");
        var names = ToolNames(list);

        Assert.Contains("get_project_context", names);
        Assert.Contains("list_project_contexts", names);
        Assert.Contains("update_agent_config", names);
        Assert.True(names.Count > 10, $"admin route lost tools: {string.Join(", ", names)}");

        var listed = await CallAsync(ContextMcpRoute.AdminPath, session, "list_project_contexts", new { });
        Assert.Contains("project-c", ResultText(listed), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%20")]
    [InlineData("agent%20a")]
    [InlineData("agent/a")]
    [InlineData("agent-a&agent=agent-b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65
    public async Task MissingOrMalformedAgent_Is403(string? agent)
    {
        var path = agent is null ? ContextMcpRoute.ContextPath : $"{ContextMcpRoute.ContextPath}?agent={agent}";

        var init = await RpcAsync(path, null, "initialize", new
        {
            protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" },
        });

        Assert.Equal(HttpStatusCode.Forbidden, init.Status);
        Assert.Null(init.SessionId);
        Assert.Equal(0, Registry.Count);
        Assert.Contains(_logs.Entries, e => e.Message.StartsWith("ContextMcpGuard rejected status=403 reason=no_agent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaximumLengthAgent_Passes()
    {
        var agent = new string('a', 64);

        var session = await InitializeAsync(Ctx(agent));

        Assert.True(Registry.TryGetAgent(session, out var bound));
        Assert.Equal(agent, bound);
    }

    [Fact]
    public async Task SessionlessToolsList_Is200_AndTheImplicitSessionIsBoundToItsAgentOnly()
    {
        var list = await RpcAsync(Ctx(AgentA), null, "tools/list");

        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal([ContextMcpRoute.FallbackToolName], ToolNames(list));
        Assert.False(string.IsNullOrEmpty(list.SessionId), "the SDK no longer creates implicit sessions — revisit the guard");
        Assert.True(Registry.TryGetAgent(list.SessionId!, out var bound));
        Assert.Equal(AgentA, bound);

        // Reusable by its agent, by nobody else, and never on the admin route.
        Assert.Equal(HttpStatusCode.OK, (await RpcAsync(Ctx(AgentA), list.SessionId, "tools/list")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await RpcAsync(Ctx(AgentB), list.SessionId, "tools/list")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await RpcAsync(ContextMcpRoute.AdminPath, list.SessionId, "tools/list")).Status);
    }

    [Fact]
    public async Task SessionlessToolsCall_IsAOneRequestSessionBoundLikeAnyOther()
    {
        var call = await CallAsync(Ctx(AgentA), null, "get_project_context", new { name = "project-a" });

        Assert.Contains("A body v2", ResultText(call), StringComparison.Ordinal);
        Assert.True(Registry.TryGetAgent(call.SessionId!, out var bound));
        Assert.Equal(AgentA, bound);
    }

    [Fact]
    public async Task UnknownSessionId_OnContextRoute_Is404()
    {
        var list = await RpcAsync(Ctx(AgentA), "not-a-session", "tools/list");

        Assert.Equal(HttpStatusCode.NotFound, list.Status);
    }

    [Fact]
    public async Task Delete_RemovesTheBinding()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var delete = await SendAsync(HttpMethod.Delete, Ctx(AgentA), session, null);

        Assert.True(delete.Status is HttpStatusCode.OK or HttpStatusCode.NoContent, $"{delete.Status}: {delete.Body}");
        Assert.False(Registry.Contains(session));
        Assert.Equal(HttpStatusCode.NotFound, (await RpcAsync(Ctx(AgentA), session, "tools/list")).Status);
    }

    [Fact]
    public async Task Delete_ByAnotherAgent_Is403_AndKeepsTheBinding()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var delete = await SendAsync(HttpMethod.Delete, Ctx(AgentB), session, null);

        Assert.Equal(HttpStatusCode.Forbidden, delete.Status);
        Assert.True(Registry.Contains(session));
    }

    [Fact]
    public async Task AdminToolName_OnContextSession_IsAnError()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var call = await CallAsync(Ctx(AgentA), session, "list_project_contexts", new { });

        Assert.Equal(HttpStatusCode.OK, call.Status);
        var message = call.Message!.Value;
        var isError = message.TryGetProperty("error", out _)
            || (message.GetProperty("result").TryGetProperty("isError", out var flag) && flag.GetBoolean());
        Assert.True(isError, call.Body);
        Assert.DoesNotContain("project-c", call.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sse")]
    [InlineData("message")]
    public async Task LegacySseEndpoints_UnderTheContextRoute_Are404(string leaf)
    {
        var admin = await InitializeAsync(ContextMcpRoute.AdminPath);

        var response = leaf == "sse"
            ? await SendAsync(HttpMethod.Get, $"{ContextMcpRoute.ContextPath}/sse?agent={AgentA}", null, null)
            : await RpcAsync($"{ContextMcpRoute.ContextPath}/message?agent={AgentA}&sessionId={admin}", null, "tools/list");

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Contains(_logs.Entries, e => e.Message.StartsWith("ContextMcpGuard rejected status=404 reason=unsupported_transport", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PathCasing_DoesNotEscapeTheGuard()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        Assert.Equal(HttpStatusCode.Forbidden, (await RpcAsync($"/MCP/Context?agent={AgentB}", session, "tools/list")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await RpcAsync("/MCP", session, "tools/list")).Status);
    }

    // ── the ACL on /mcp/context (D7) ──────────────────────────────────────────

    [Theory]
    [InlineData("project-a")] // card assignment
    [InlineData("project-b")] // full assignment
    [InlineData("PROJECT-A")] // OrdinalIgnoreCase
    public async Task Acl_Assigned_ReturnsCurrentFullContentWithoutHistory(string project)
    {
        var session = await InitializeAsync(Ctx(AgentA));

        var text = ResultText(await CallAsync(Ctx(AgentA), session, "get_project_context", new { name = project }));

        var body = project.Equals("project-b", StringComparison.OrdinalIgnoreCase) ? "B body v2" : "A body v2";
        Assert.Contains(body, text, StringComparison.Ordinal);
        Assert.Contains("Current version: 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("v1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Version history", text, StringComparison.Ordinal);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Information &&
            e.Message.StartsWith($"ProjectContextFallback allowed agent={AgentA}", StringComparison.Ordinal) &&
            e.Message.EndsWith("version=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Acl_UnassignedAndNonexistent_GetIdenticalText()
    {
        var session = await InitializeAsync(Ctx(AgentA));

        // project-c exists and agent-a even holds a memory-ACL grant for it — not an assignment.
        var unassigned = ResultText(await CallAsync(Ctx(AgentA), session, "get_project_context", new { name = "project-c" }));
        var nonexistent = ResultText(await CallAsync(Ctx(AgentA), session, "get_project_context", new { name = "project-z" }));

        Assert.Equal("Project context 'project-c' is not available to this caller.", unassigned);
        Assert.Equal("Project context 'project-z' is not available to this caller.", nonexistent);
        Assert.Equal(unassigned.Replace("project-c", "X"), nonexistent.Replace("project-z", "X"));
        Assert.Contains(_logs.Entries, e => e.Message.Contains("denied agent=agent-a project=project-c reason=not_assigned", StringComparison.Ordinal));
        Assert.Contains(_logs.Entries, e => e.Message.Contains("denied agent=agent-a project=project-z reason=not_assigned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Acl_UnknownAgent_GetsTheSameText()
    {
        var session = await InitializeAsync(Ctx("agent-unknown"));

        var text = ResultText(await CallAsync(Ctx("agent-unknown"), session, "get_project_context", new { name = "project-a" }));

        Assert.Equal("Project context 'project-a' is not available to this caller.", text);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("reason=unknown_agent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Acl_AgentWithNoAssignments_IsDenied()
    {
        var session = await InitializeAsync(Ctx(AgentB));

        var text = ResultText(await CallAsync(Ctx(AgentB), session, "get_project_context", new { name = "project-a" }));

        Assert.Equal("Project context 'project-a' is not available to this caller.", text);
    }

    [Fact]
    public async Task Acl_DatabaseFailure_IsUnavailable_NotADenial()
    {
        var session = await InitializeAsync(Ctx(AgentA));
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE agent_projects";
            await cmd.ExecuteNonQueryAsync();
        }

        var text = ResultText(await CallAsync(Ctx(AgentA), session, "get_project_context", new { name = "project-a" }));

        Assert.Equal("Project context lookup is temporarily unavailable.", text);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("ProjectContextFallback", StringComparison.Ordinal));
    }

    // ── /mcp unchanged (AC 9) ─────────────────────────────────────────────────

    [Fact]
    public async Task AdminRoute_GetProjectContext_IsUnchanged_ForAnyProject()
    {
        var session = await InitializeAsync(ContextMcpRoute.AdminPath);

        var text = ResultText(await CallAsync(ContextMcpRoute.AdminPath, session, "get_project_context", new { name = "project-c" }));

        Assert.Contains("## Project Context: project-c", text, StringComparison.Ordinal);
        Assert.Contains("Current version: 2", text, StringComparison.Ordinal);
        Assert.Contains("Total versions: 2", text, StringComparison.Ordinal);
        Assert.Contains("### Version history:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.StartsWith("ProjectContextFallback", StringComparison.Ordinal));
    }

    // ── the reviewer pin: the ACL keys off the REQUEST path ───────────────────

    /// <summary>
    /// On the spike's UNGUARDED host an admin session (created on <c>/mcp</c>, every tool) is replayed
    /// on <c>/mcp/context</c>. The ACL must still apply — it is keyed on the path of the request the
    /// tool is serving, not on the route that created the session. The same session on <c>/mcp</c>
    /// reads as admin.
    /// </summary>
    [Fact]
    public async Task Acl_KeysOffTheRequestPath_NotTheSessionOrigin()
    {
        await DisposeAsync();
        await StartAsync(withGuard: false);

        var admin = await InitializeAsync(ContextMcpRoute.AdminPath);

        var viaContext = ResultText(await CallAsync(Ctx(AgentA), admin, "get_project_context", new { name = "project-c" }));
        var viaAdmin = ResultText(await CallAsync(ContextMcpRoute.AdminPath, admin, "get_project_context", new { name = "project-c" }));

        Assert.Equal("Project context 'project-c' is not available to this caller.", viaContext);
        Assert.Contains("C body v2", viaAdmin, StringComparison.Ordinal);

        // And a context session sees the CURRENT request's agent, which is why the guard binds it.
        var context = await InitializeAsync(Ctx(AgentA));
        var asB = ResultText(await CallAsync(Ctx(AgentB), context, "get_project_context", new { name = "project-a" }));
        Assert.Equal("Project context 'project-a' is not available to this caller.", asB);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("reason=binding_mismatch", StringComparison.Ordinal));
    }

    // ── transport options and the bearer exemption ────────────────────────────

    [Fact]
    public void TransportOptions_StayAtSdkDefaults_WithTheSessionFilterWired()
    {
        var options = _app.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;

        Assert.False(options.Stateless);
        Assert.False(options.PerSessionExecutionContext);
        Assert.Equal(TimeSpan.FromHours(2), options.IdleTimeout);
        Assert.Equal(10_000, options.MaxIdleSessionCount);
        Assert.NotNull(options.ConfigureSessionOptions);
    }

    [Theory]
    [InlineData("POST", "/mcp/context")]
    [InlineData("DELETE", "/mcp/context")]
    [InlineData("POST", "/mcp")]
    public void OrchestratorAuth_ExemptsBothMcpRoutes(string method, string path)
    {
        Assert.False(OrchestratorAuth.RequiresBearerToken(method, path));
    }

    [Fact]
    public async Task ContextRoute_NeedsNoBearer_WhileTheGateIsActive()
    {
        // The gate is live in this host...
        var probe = await _client.PostAsync("/api/probe", null);
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);

        // ...and the context route still initializes without a bearer.
        await InitializeAsync(Ctx(AgentA));
    }

    private sealed class SinkLoggerProvider(ProvisioningLogSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => sink.For<SinkLoggerProvider>();
        public void Dispose() { }
    }
}
