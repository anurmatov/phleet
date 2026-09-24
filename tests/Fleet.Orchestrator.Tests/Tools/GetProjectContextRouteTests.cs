using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// <c>get_project_context</c> decides per call, from the CURRENT request, whether it is the admin
/// read or the card agents' fallback read (#347 D7, AC 9). These drive the tool method directly with
/// a chosen <see cref="HttpContext"/>, which reaches the rows the guard makes unreachable over HTTP.
/// </summary>
public sealed class GetProjectContextRouteTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private readonly HttpContextAccessor _accessor = new();
    private readonly ProvisioningLogSink _logs = new();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<IHttpContextAccessor>(_accessor);
        services.AddSingleton(TimeProvider.System);
        services.Configure<HttpServerTransportOptions>(_ => { });
        services.AddSingleton<ContextSessionRegistry>();
        services.AddSingleton(_logs.For<ProjectContextAccess>());
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.EnsureCreatedAsync();

        var ctx = new ProjectContext { Name = "project-a", CurrentVersion = 1 };
        ctx.Versions.Add(new ProjectContextVersion { VersionNumber = 1, Content = "A body" });
        db.ProjectContexts.Add(ctx);
        var agent = new Agent
        {
            Name = "agent-a", DisplayName = "agent-a", Role = "developer", Model = "m", ContainerName = "fleet-agent-a",
        };
        agent.Projects.Add(new AgentProject { ProjectName = "project-a", ContextMode = ProjectContextMode.Card });
        db.Agents.Add(agent);
        db.Agents.Add(new Agent
        {
            Name = "agent-b", DisplayName = "agent-b", Role = "developer", Model = "m", ContainerName = "fleet-agent-b",
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private ProjectContextTools Tools => new(_services.GetRequiredService<IServiceScopeFactory>());
    private ContextSessionRegistry Registry => _services.GetRequiredService<ContextSessionRegistry>();

    private void Request(string path, string? query = null, string? sessionId = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        if (query is not null) http.Request.QueryString = new QueryString(query);
        if (sessionId is not null) http.Request.Headers[ContextMcpRoute.SessionIdHeader] = sessionId;
        _accessor.HttpContext = http;
    }

    [Fact]
    public async Task NoHttpContext_IsTheAdminRead()
    {
        _accessor.HttpContext = null;

        var text = await Tools.GetProjectContextAsync("project-a");

        Assert.Contains("### Version history:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminPath_IsTheAdminRead_EvenWithAnAgentQuery()
    {
        Request("/mcp", "?agent=agent-b");

        var text = await Tools.GetProjectContextAsync("project-a");

        Assert.Contains("### Version history:", text, StringComparison.Ordinal);
        Assert.Empty(_logs.Entries);
    }

    [Fact]
    public async Task ContextPath_AssignedAgent_IsAllowed()
    {
        Registry.Bind("s1", "agent-a");
        Request("/mcp/context", "?agent=agent-a", "s1");

        var text = await Tools.GetProjectContextAsync("project-a");

        Assert.Equal("## Project Context: project-a\nCurrent version: 1\n\nA body\n", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task ContextPath_SessionBoundToAnotherAgent_IsABindingMismatch()
    {
        Registry.Bind("s1", "agent-b");
        Request("/mcp/context", "?agent=agent-a", "s1");

        var text = await Tools.GetProjectContextAsync("project-a");

        Assert.Equal("Project context 'project-a' is not available to this caller.", text);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning &&
            e.Message == "ProjectContextFallback denied agent=agent-a project=project-a reason=binding_mismatch version=-");
    }

    [Fact]
    public async Task ContextPath_UnboundSession_IsABindingMismatch()
    {
        Request("/mcp/context", "?agent=agent-a", "never-bound");

        var text = await Tools.GetProjectContextAsync("project-a");

        Assert.Equal("Project context 'project-a' is not available to this caller.", text);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("reason=binding_mismatch", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("?agent=")]
    [InlineData("?agent=a%20b")]
    public async Task ContextPath_WithoutAWellFormedAgent_IsDenied(string? query)
    {
        Request("/mcp/context", query);

        var text = await Tools.GetProjectContextAsync("project-a");

        Assert.Equal("Project context 'project-a' is not available to this caller.", text);
    }

    [Fact]
    public async Task ContextPath_Sessionless_UsesTheAclOnly()
    {
        // The SDK's implicit session: no id yet, the guard binds it as the response starts.
        Request("/mcp/context", "?agent=agent-b");

        var text = await Tools.GetProjectContextAsync("project-a");

        Assert.Equal("Project context 'project-a' is not available to this caller.", text);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("agent=agent-b project=project-a reason=not_assigned", StringComparison.Ordinal));
    }

    [Fact]
    public void IsAssigned_ComparesOrdinalIgnoreCase()
    {
        Assert.True(ProjectContextAccess.IsAssigned(["Project-A"], "project-a"));
        Assert.False(ProjectContextAccess.IsAssigned(["project-a"], "project-a2"));
        Assert.False(ProjectContextAccess.IsAssigned([], "project-a"));
    }
}
