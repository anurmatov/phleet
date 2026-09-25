using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
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

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// The #346 size report on the <c>/api/instructions</c> writes, over real HTTP against the lifted
/// handlers (<see cref="InstructionEndpoints"/>), plus <c>GET /api/prompt-size-policy</c>.
/// </summary>
/// <remarks>
/// Same harness as <see cref="OutputStyleEndpointsTests"/>: the production map calls and the
/// production bearer predicate on a SQLite store. Synthetic names and content only.
/// </remarks>
public sealed class InstructionEndpointsTests : IAsyncLifetime
{
    private PromptSizeTestHost _host = null!;
    private HttpClient Client => _host.Client;

    public async Task InitializeAsync() => _host = await PromptSizeTestHost.StartAsync(new PromptSizePolicy(10_000, 10_000, new SizeLogCapture()));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<JsonElement> CreateAsync(string name, string content)
    {
        var response = await Client.SendAsync(_host.Authed(HttpMethod.Post, "/api/instructions", new { name, content }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> NewVersionAsync(string name, string content)
    {
        var response = await Client.SendAsync(_host.Authed(HttpMethod.Post, $"/api/instructions/{name}/versions", new { content }));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task<int> CurrentVersionAsync(string name)
    {
        await using var scope = _host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return (await db.Instructions.SingleAsync(i => i.Name == name)).CurrentVersion;
    }

    [Fact]
    public async Task List_reports_the_current_version_bytes()
    {
        await CreateAsync("row-a", "abc");
        await NewVersionAsync("row-a", "дд中");

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/instructions");

        // #346 Size column: the current version (v2), measured in UTF-8 bytes — 2 + 2 + 3.
        Assert.Equal(7, list.EnumerateArray().Single(i => i.GetProperty("name").GetString() == "row-a")
            .GetProperty("currentBytes").GetInt32());
    }

    [Fact]
    public async Task Create_reports_size_with_no_previous_and_keeps_its_message()
    {
        var body = await CreateAsync("row-a", "abc");

        Assert.Equal("Instruction 'row-a' created at v1", body.GetProperty("message").GetString());
        var size = body.GetProperty("size");
        Assert.Equal("instruction", size.GetProperty("kind").GetString());
        Assert.Equal("utf8Bytes", size.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, size.GetProperty("previousBytes").ValueKind);
        Assert.Equal(3, size.GetProperty("bytes").GetInt32());
        Assert.Equal("under", size.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, size.GetProperty("warning").ValueKind);
    }

    [Fact]
    public async Task Over_the_limit_is_saved_crossed_then_still_over()
    {
        await CreateAsync("row-a", "abc");

        // AC 3: 10,001 ASCII bytes → 200, crossed, row persisted, message and version unchanged.
        var (status, first) = await NewVersionAsync("row-a", new string('a', 10_001));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Instruction 'row-a' updated to v2", first.GetProperty("message").GetString());
        Assert.Equal(2, first.GetProperty("version").GetInt32());
        var size = first.GetProperty("size");
        Assert.Equal("crossed", size.GetProperty("status").GetString());
        Assert.Equal(3, size.GetProperty("previousBytes").GetInt32());
        Assert.Equal(10_001, size.GetProperty("bytes").GetInt32());
        Assert.Equal(10_000, size.GetProperty("limitBytes").GetInt32());
        Assert.Equal("PromptSizeWarnings:InstructionBytes", size.GetProperty("limitKey").GetString());
        Assert.StartsWith("Instruction 'row-a' is 10,001 UTF-8 bytes, 1 over", size.GetProperty("warning").GetString());
        Assert.Equal(2, await CurrentVersionAsync("row-a"));

        // AC 5: a second over-limit update is stillOver, previous = the first write's bytes.
        var (_, second) = await NewVersionAsync("row-a", new string('a', 10_500));
        var again = second.GetProperty("size");
        Assert.Equal("stillOver", again.GetProperty("status").GetString());
        Assert.Equal(10_001, again.GetProperty("previousBytes").GetInt32());
        Assert.Equal(3, await CurrentVersionAsync("row-a"));
    }

    [Fact]
    public async Task Rollback_reports_the_restored_content_against_the_current_one()
    {
        await CreateAsync("row-a", new string('a', 10_200));
        await NewVersionAsync("row-a", "short");

        var response = await Client.SendAsync(_host.Authed(HttpMethod.Post, "/api/instructions/row-a/rollback/1"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Rolled back 'row-a' to v1 content — saved as v3", body.GetProperty("message").GetString());
        Assert.Equal(3, body.GetProperty("version").GetInt32());
        var size = body.GetProperty("size");
        Assert.Equal("crossed", size.GetProperty("status").GetString());
        Assert.Equal(5, size.GetProperty("previousBytes").GetInt32());
        Assert.Equal(10_200, size.GetProperty("bytes").GetInt32());
    }

    [Fact]
    public async Task Errors_are_unchanged_and_carry_no_size()
    {
        var (missingStatus, missing) = await NewVersionAsync("no-such-row", "abc");
        Assert.Equal(HttpStatusCode.NotFound, missingStatus);
        Assert.Equal("""{"error":"Instruction 'no-such-row' not found"}""", missing.GetRawText());

        var (emptyStatus, empty) = await NewVersionAsync("no-such-row", "  ");
        Assert.Equal(HttpStatusCode.BadRequest, emptyStatus);
        Assert.Equal("""{"error":"content is required"}""", empty.GetRawText());

        await CreateAsync("row-a", "abc");
        var conflict = await Client.SendAsync(_host.Authed(HttpMethod.Post, "/api/instructions", new { name = "row-a", content = "abc" }));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("""{"error":"Instruction 'row-a' already exists"}""", await conflict.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Toggle_active_is_unchanged()
    {
        await CreateAsync("row-a", new string('a', 20_000));

        var response = await Client.SendAsync(_host.Authed(HttpMethod.Post, "/api/instructions/row-a/toggle-active", new { isActive = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"name":"row-a","isActive":false}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Writes_still_need_the_bearer()
    {
        var response = await Client.PostAsJsonAsync("/api/instructions", new { name = "row-a", content = "abc" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Policy_get_needs_no_token()
    {
        var response = await Client.GetAsync("/api/prompt-size-policy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"unit":"utf8Bytes","instruction":{"limitBytes":10000,"limitKey":"PromptSizeWarnings:InstructionBytes"},"projectContext":{"limitBytes":10000,"limitKey":"PromptSizeWarnings:ProjectContextBytes"}}""",
            await response.Content.ReadAsStringAsync());
    }
}

/// <summary>
/// AC 6: with the instruction limit at 0, instruction writes report <c>disabled</c> while context
/// warnings still fire at 10,000 — the keys are independent.
/// </summary>
public sealed class InstructionEndpointsDisabledTests : IAsyncLifetime
{
    private PromptSizeTestHost _host = null!;

    public async Task InitializeAsync() => _host = await PromptSizeTestHost.StartAsync(new PromptSizePolicy(0, 10_000, new SizeLogCapture()));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Instruction_disabled_context_still_warns()
    {
        var instruction = await _host.Client.SendAsync(_host.Authed(HttpMethod.Post, "/api/instructions",
            new { name = "row-a", content = new string('a', 50_000) }));
        var instructionSize = (await instruction.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("size");

        Assert.Equal("disabled", instructionSize.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, instructionSize.GetProperty("limitBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, instructionSize.GetProperty("warning").ValueKind);

        var context = await _host.Client.SendAsync(_host.Authed(HttpMethod.Post, "/api/project-contexts",
            new { name = "row-b", content = new string('a', 10_001) }));
        var contextSize = (await context.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("size");

        Assert.Equal("crossed", contextSize.GetProperty("status").GetString());
        Assert.Equal(10_000, contextSize.GetProperty("limitBytes").GetInt32());

        var policy = await _host.Client.GetFromJsonAsync<JsonElement>("/api/prompt-size-policy");
        Assert.Equal(JsonValueKind.Null, policy.GetProperty("instruction").GetProperty("limitBytes").ValueKind);
        Assert.Equal(10_000, policy.GetProperty("projectContext").GetProperty("limitBytes").GetInt32());
    }
}

/// <summary>
/// The instruction, project-context and policy handlers on a SQLite store behind the production
/// bearer predicate. The policy is registered here because <c>Program.cs</c> registers it.
/// </summary>
internal sealed class PromptSizeTestHost : IAsyncDisposable
{
    public const string Token = "test-bearer-token";

    private readonly SqliteConnection _connection;

    public WebApplication App { get; }
    public HttpClient Client { get; }

    private PromptSizeTestHost(SqliteConnection connection, WebApplication app)
    {
        _connection = connection;
        App = app;
        Client = app.GetTestClient();
    }

    public static async Task<PromptSizeTestHost> StartAsync(PromptSizePolicy policy)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(connection));
        builder.Services.AddSingleton(policy);

        var app = builder.Build();

        app.Use(async (context, next) =>
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

            if (token != Token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }

            await next(context);
        });

        app.MapInstructionEndpoints();
        app.MapPromptSizePolicyEndpoints();
        app.MapProjectContextEndpoints();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await app.StartAsync();
        return new PromptSizeTestHost(connection, app);
    }

    public HttpRequestMessage Authed(HttpMethod method, string url, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
