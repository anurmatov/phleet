using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// The #346 size report on the <c>/api/project-contexts</c> full-context writes, over real HTTP
/// against <see cref="Fleet.Orchestrator.Endpoints.ProjectContextEndpoints"/>. Synthetic text only.
/// </summary>
public sealed class ProjectContextEndpointsTests : IAsyncLifetime
{
    private readonly SizeLogCapture _logs = new();
    private PromptSizeTestHost _host = null!;
    private HttpClient Client => _host.Client;

    public async Task InitializeAsync() => _host = await PromptSizeTestHost.StartAsync(new PromptSizePolicy(10_000, 10_000, _logs));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<JsonElement> PostAsync(string url, object? payload = null)
    {
        var response = await Client.SendAsync(_host.Authed(HttpMethod.Post, url, payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task List_reports_the_canonical_context_bytes()
    {
        await PostAsync("/api/project-contexts", new { name = "row-a", content = "abc" });
        await PostAsync("/api/project-contexts/row-a/versions", new { content = "дд中" });
        await PostAsync("/api/project-contexts/row-a/rollback/1");

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/project-contexts");

        // #346 Size column: the row CurrentVersion points at (v3, a copy of v1).
        Assert.Equal(3, list.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "row-a")
            .GetProperty("currentBytes").GetInt32());
    }

    [Fact]
    public async Task Cyrillic_is_measured_in_bytes()
    {
        var created = await PostAsync("/api/project-contexts", new { name = "row-a", content = "abc" });
        Assert.Equal("Project context 'row-a' created at v1", created.GetProperty("message").GetString());
        Assert.Equal("projectContext", created.GetProperty("size").GetProperty("kind").GetString());

        // AC 4: 5,001 "д" is 10,002 bytes → crossed.
        var body = await PostAsync("/api/project-contexts/row-a/versions", new { content = new string('д', 5_001) });

        Assert.Equal("Project context 'row-a' updated to v2", body.GetProperty("message").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        var size = body.GetProperty("size");
        Assert.Equal("crossed", size.GetProperty("status").GetString());
        Assert.Equal(10_002, size.GetProperty("bytes").GetInt32());
        Assert.Equal(3, size.GetProperty("previousBytes").GetInt32());
        Assert.Equal("PromptSizeWarnings:ProjectContextBytes", size.GetProperty("limitKey").GetString());
        Assert.StartsWith("Project context 'row-a' is 10,002 UTF-8 bytes, 2 over the 10,000-byte soft limit", size.GetProperty("warning").GetString());

        await using var scope = _host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(2, (await db.ProjectContexts.SingleAsync(p => p.Name == "row-a")).CurrentVersion);
    }

    [Fact]
    public async Task Second_over_limit_write_and_rollback_are_still_over()
    {
        await PostAsync("/api/project-contexts", new { name = "row-a", content = new string('a', 12_000) });
        var second = await PostAsync("/api/project-contexts/row-a/versions", new { content = new string('a', 11_000) });

        Assert.Equal("stillOver", second.GetProperty("size").GetProperty("status").GetString());
        Assert.Equal(12_000, second.GetProperty("size").GetProperty("previousBytes").GetInt32());

        var rollback = await PostAsync("/api/project-contexts/row-a/rollback/1");
        Assert.Equal("Rolled back 'row-a' to v1 content — saved as v3", rollback.GetProperty("message").GetString());
        var size = rollback.GetProperty("size");
        Assert.Equal("stillOver", size.GetProperty("status").GetString());
        Assert.Equal(11_000, size.GetProperty("previousBytes").GetInt32());
        Assert.Equal(12_000, size.GetProperty("bytes").GetInt32());
    }

    [Fact]
    public async Task Back_under_the_limit_is_under()
    {
        await PostAsync("/api/project-contexts", new { name = "row-a", content = new string('a', 12_000) });
        var body = await PostAsync("/api/project-contexts/row-a/versions", new { content = "short" });

        Assert.Equal("under", body.GetProperty("size").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("size").GetProperty("warning").ValueKind);
    }

    [Fact]
    public async Task Rest_writes_log_names_and_sizes_never_content()
    {
        await PostAsync("/api/project-contexts", new { name = "row-a", content = "SECRET-SENTINEL" + new string('a', 10_000) });

        var line = Assert.Single(_logs.Entries, e => e.Message.StartsWith("PromptSize "));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("PromptSize crossed: surface=rest kind=projectContext name=row-a bytes=10,015 previousBytes=none limitBytes=10,000", line.Message);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains("SECRET-SENTINEL"));
    }

    [Fact]
    public async Task Errors_are_unchanged_and_carry_no_size()
    {
        var missing = await Client.SendAsync(_host.Authed(HttpMethod.Post, "/api/project-contexts/no-such-row/versions", new { content = "abc" }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("""{"error":"Project context 'no-such-row' not found"}""", await missing.Content.ReadAsStringAsync());

        await PostAsync("/api/project-contexts", new { name = "row-a", content = "abc" });
        var badVersion = await Client.SendAsync(_host.Authed(HttpMethod.Post, "/api/project-contexts/row-a/rollback/9"));
        Assert.Equal(HttpStatusCode.NotFound, badVersion.StatusCode);
        Assert.Equal("""{"error":"Version 9 not found"}""", await badVersion.Content.ReadAsStringAsync());
    }
}
