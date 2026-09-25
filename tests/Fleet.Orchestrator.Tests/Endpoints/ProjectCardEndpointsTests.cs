using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Fleet.Orchestrator.Tests.ProjectCardTestSupport;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// The #347 card, route and full-write REST surface, driven over real HTTP.
/// </summary>
/// <remarks>
/// Built like <see cref="OutputStyleEndpointsTests"/>: the host maps the SAME
/// <see cref="ProjectContextEndpoints.MapProjectContextEndpoints"/> and
/// <see cref="ProjectCardEndpoints.MapProjectCardEndpoints"/> a live orchestrator maps, behind the
/// production <see cref="OrchestratorAuth.RequiresBearerToken"/> gate. The store is SQLite with
/// foreign keys on, so the cascade the migration declares is the one exercised.
/// </remarks>
public class ProjectCardEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-bearer-token";

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly CapturingLoggerProvider _logs = new();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logs);
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        // The #346 size report on the full-context writes; Program.cs registers the policy.
        builder.Services.AddSingleton(new PromptSizePolicy(10_000, 10_000, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));

        _app = builder.Build();

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

            if (token != Token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }

            await next(context);
        });

        _app.MapProjectContextEndpoints();
        _app.MapProjectCardEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<T> WithDbAsync<T>(Func<OrchestratorDbContext, T> action)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return action(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
    }

    private Task<int> SeedContextAsync(string name, params string[] fullVersions) =>
        WithDbAsync(db => SeedContext(db, name, fullVersions).Id);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostAsync(string url, object? payload = null) => SendAsync(HttpMethod.Post, url, payload);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<JsonElement> GetDetailAsync(string name)
    {
        var response = await _client.GetAsync($"/api/project-contexts/{name}");
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response);
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    // ── Card write gate (AC 4) ────────────────────────────────────────────────

    [Fact]
    public async Task CardWrite_MissingAKeepOfTheCurrentFull_Is400NamingIt_AndSavesNothing()
    {
        await SeedContextAsync("project-a", "Full v1.\n<!-- keep:x -->\n<!-- keep:y -->\n");
        (await PostAsync("/api/project-contexts/project-a/card/versions",
            new { content = "Card.\n<!-- keep:x -->\n<!-- keep:y -->", basedOnFullVersion = 1 })).EnsureSuccessStatusCode();

        var response = await PostAsync("/api/project-contexts/project-a/card/versions",
            new { content = "Shorter card.\n<!-- keep:y -->", basedOnFullVersion = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(["x"], Strings(body.GetProperty("missingKeeps")));
        Assert.Contains("x", body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("invalidKeeps", out _));

        var card = (await GetDetailAsync("project-a")).GetProperty("card");
        Assert.Equal(1, card.GetProperty("currentVersion").GetInt32());
        Assert.Equal(1, card.GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task CardWrite_KeepsPresentWithDifferentSurroundingText_AndExtraSlugs_Saves()
    {
        await SeedContextAsync("project-a", "Rule one, long form.\n<!-- keep:x -->\n");

        var response = await PostAsync("/api/project-contexts/project-a/card/versions",
            new { content = "R1 <!-- keep:x --> and <!-- keep:card-only -->", basedOnFullVersion = 1, reason = "first", createdBy = "agent-a" });

        response.EnsureSuccessStatusCode();
        var body = await ReadJsonAsync(response);
        Assert.Equal(1, body.GetProperty("version").GetInt32());

        var card = (await GetDetailAsync("project-a")).GetProperty("card");
        Assert.Equal(1, card.GetProperty("basedOnFullVersion").GetInt32());
        Assert.False(card.GetProperty("stale").GetBoolean());
        Assert.Empty(Strings(card.GetProperty("missingKeeps")));
        var v1 = card.GetProperty("versions")[0];
        Assert.Equal("agent-a", v1.GetProperty("createdBy").GetString());
        Assert.Equal("first", v1.GetProperty("reason").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", v1.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task CardWrite_WithAnInvalidKeepCandidate_Is400NamingItAndTheGrammar()
    {
        await SeedContextAsync("project-a", "Full.");

        var response = await PostAsync("/api/project-contexts/project-a/card/versions",
            new { content = "Card <!-- keep:Bad -->", basedOnFullVersion = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(["<!-- keep:Bad -->"], Strings(body.GetProperty("invalidKeeps")));
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains("<!-- keep:Bad -->", error);
        Assert.Contains("[a-z0-9][a-z0-9-]{0,63}", error);
        Assert.Equal(JsonValueKind.Null, (await GetDetailAsync("project-a")).GetProperty("card").ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3)]
    public async Task CardWrite_BasedOnFullVersionMissingOrOutOfRange_Is400(int? basedOn)
    {
        await SeedContextAsync("project-a", "Full v1.", "Full v2.");

        var response = await PostAsync("/api/project-contexts/project-a/card/versions",
            new { content = "Card.", basedOnFullVersion = basedOn });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("basedOnFullVersion", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetDetailAsync("project-a")).GetProperty("card").ValueKind);
    }

    [Fact]
    public async Task CardWrite_BlankContent_Is400_AndUnknownProject_Is404()
    {
        await SeedContextAsync("project-a", "Full.");

        var blank = await PostAsync("/api/project-contexts/project-a/card/versions", new { content = "  \n", basedOnFullVersion = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var missing = await PostAsync("/api/project-contexts/project-b/card/versions", new { content = "Card.", basedOnFullVersion = 1 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CardWrite_ResolvesTheNameCaseInsensitively()
    {
        await SeedContextAsync("project-a", "Full.");

        var response = await PostAsync("/api/project-contexts/PROJECT-A/card/versions", new { content = "Card.", basedOnFullVersion = 1 });

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, (await GetDetailAsync("project-a")).GetProperty("card").GetProperty("currentVersion").GetInt32());
    }

    [Fact]
    public async Task CardWrites_ArePrunedToTwentyVersions_OldestFirst()
    {
        await SeedContextAsync("project-a", "Full.");

        for (var i = 1; i <= ProjectCardService.MaxCardVersions + 2; i++)
            (await PostAsync("/api/project-contexts/project-a/card/versions", new { content = $"Card {i}.", basedOnFullVersion = 1 })).EnsureSuccessStatusCode();

        var card = (await GetDetailAsync("project-a")).GetProperty("card");
        var numbers = card.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("versionNumber").GetInt32()).ToList();
        Assert.Equal(22, card.GetProperty("currentVersion").GetInt32());
        Assert.Equal(ProjectCardService.MaxCardVersions, numbers.Count);
        Assert.Equal(Enumerable.Range(3, 20).Reverse(), numbers);
    }

    // ── Full writes: keep validation and the card block (AC 4, AC 5) ──────────

    [Fact]
    public async Task FullVersion_WithAnInvalidKeepCandidate_Is400NamingIt_AndSavesNothing()
    {
        await SeedContextAsync("project-a", "Full v1.");

        var response = await PostAsync("/api/project-contexts/project-a/versions", new { content = "Full v2 <!-- keep:Bad -->" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(["<!-- keep:Bad -->"], Strings(body.GetProperty("invalidKeeps")));
        Assert.Contains("<!-- keep:Bad -->", body.GetProperty("error").GetString());
        Assert.Contains("<!-- keep:slug -->", body.GetProperty("error").GetString());
        Assert.Equal(1, (await GetDetailAsync("project-a")).GetProperty("currentVersion").GetInt32());
    }

    [Fact]
    public async Task FullCreate_WithAnInvalidKeepCandidate_Is400_AndCreatesNothing()
    {
        var response = await PostAsync("/api/project-contexts", new { name = "project-a", content = "Full <!-- keep: spaced -->" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(Strings((await ReadJsonAsync(response)).GetProperty("invalidKeeps")));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/project-contexts/project-a")).StatusCode);
    }

    [Fact]
    public async Task FullVersion_WithoutACard_HasNoCardBlock()
    {
        await SeedContextAsync("project-a", "Full v1.");

        var response = await PostAsync("/api/project-contexts/project-a/versions", new { content = "Full v2 <!-- keep:x -->" });

        response.EnsureSuccessStatusCode();
        var body = await ReadJsonAsync(response);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.False(body.TryGetProperty("card", out _));
    }

    [Fact]
    public async Task FullVersion_AfterACardForV1_ReportsTheCardStale_AndDetailAgrees()
    {
        await SeedContextAsync("project-a", "Full v1.");
        (await PostAsync("/api/project-contexts/project-a/card/versions", new { content = "Card.", basedOnFullVersion = 1 })).EnsureSuccessStatusCode();

        var response = await PostAsync("/api/project-contexts/project-a/versions", new { content = "Full v2.\n<!-- keep:y -->" });

        response.EnsureSuccessStatusCode();
        var card = (await ReadJsonAsync(response)).GetProperty("card");
        Assert.True(card.GetProperty("stale").GetBoolean());
        Assert.Equal(1, card.GetProperty("basedOnFullVersion").GetInt32());
        Assert.Equal(["y"], Strings(card.GetProperty("missingKeeps")));

        var detail = (await GetDetailAsync("project-a")).GetProperty("card");
        Assert.True(detail.GetProperty("stale").GetBoolean());
        Assert.Equal(1, detail.GetProperty("basedOnFullVersion").GetInt32());
        Assert.Equal(["y"], Strings(detail.GetProperty("missingKeeps")));

        var row = (await ReadJsonAsync(await _client.GetAsync("/api/project-contexts"))).EnumerateArray().Single();
        Assert.Equal(1, row.GetProperty("cardVersion").GetInt32());
        Assert.True(row.GetProperty("cardStale").GetBoolean());
    }

    [Fact]
    public async Task FullRollback_ToContentWithAnInvalidMarker_IsAllowed_AndWarns()
    {
        var id = await SeedContextAsync("project-a", "Old <!-- keep:Bad -->", "Current.");

        var response = await PostAsync("/api/project-contexts/project-a/rollback/1");

        response.EnsureSuccessStatusCode();
        Assert.Equal(3, (await ReadJsonAsync(response)).GetProperty("version").GetInt32());
        Assert.Contains(_logs.Warnings, w => w.Contains("<!-- keep:Bad -->") && w.Contains("project-a"));
        Assert.Equal(3, await WithDbAsync(db => db.ProjectContexts.Single(p => p.Id == id).CurrentVersion));
    }

    // ── Card rollback ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CardRollback_CopiesContentAndBasedOn_IsExemptFromTheGate_AndWarnsOnInvalid()
    {
        var id = await SeedContextAsync("project-a", "Full v1.", "Full v2 <!-- keep:x -->");
        await WithDbAsync(db =>
        {
            SeedCard(db, id, 1, "Old card <!-- keep:Bad -->", basedOnFullVersion: 1, current: false);
            SeedCard(db, id, 2, "Card <!-- keep:x -->", basedOnFullVersion: 2);
            return 0;
        });

        var response = await PostAsync("/api/project-contexts/project-a/card/rollback/1");

        response.EnsureSuccessStatusCode();
        Assert.Equal(3, (await ReadJsonAsync(response)).GetProperty("version").GetInt32());

        var card = (await GetDetailAsync("project-a")).GetProperty("card");
        Assert.Equal(3, card.GetProperty("currentVersion").GetInt32());
        Assert.Equal(1, card.GetProperty("basedOnFullVersion").GetInt32());
        Assert.True(card.GetProperty("stale").GetBoolean());
        Assert.Equal(["x"], Strings(card.GetProperty("missingKeeps")));
        Assert.Equal(["<!-- keep:Bad -->"], Strings(card.GetProperty("invalidKeeps")));
        Assert.Equal("Old card <!-- keep:Bad -->", card.GetProperty("versions")[0].GetProperty("content").GetString());
        Assert.Contains(_logs.Warnings, w => w.Contains("<!-- keep:Bad -->"));
    }

    [Fact]
    public async Task CardRollback_UnknownVersionOrProject_Is404()
    {
        await SeedContextAsync("project-a", "Full.");
        (await PostAsync("/api/project-contexts/project-a/card/versions", new { content = "Card.", basedOnFullVersion = 1 })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/api/project-contexts/project-a/card/rollback/9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/api/project-contexts/project-b/card/rollback/1")).StatusCode);
    }

    // ── Routes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Route_Add_NormalisesTheValue_Returns201_AndDetailListsIt()
    {
        await SeedContextAsync("project-a", "Full.");

        var repo = await PostAsync("/api/project-contexts/project-a/routes", new { kind = "repo", value = "Org/App" });
        var chat = await PostAsync("/api/project-contexts/project-a/routes", new { kind = "chat", value = " -100000000001 " });
        var wf = await PostAsync("/api/project-contexts/project-a/routes", new { kind = "workflow", value = "ExampleWorkflow" });

        Assert.Equal(HttpStatusCode.Created, repo.StatusCode);
        var created = await ReadJsonAsync(repo);
        Assert.Equal("repo", created.GetProperty("kind").GetString());
        Assert.Equal("org/app", created.GetProperty("value").GetString());
        Assert.True(created.GetProperty("id").GetInt32() > 0);
        Assert.Equal(HttpStatusCode.Created, chat.StatusCode);
        Assert.Equal("-100000000001", (await ReadJsonAsync(chat)).GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.Created, wf.StatusCode);

        var routes = (await GetDetailAsync("project-a")).GetProperty("routes").EnumerateArray()
            .Select(r => $"{r.GetProperty("kind").GetString()}={r.GetProperty("value").GetString()}")
            .ToArray();
        Assert.Equal(["chat=-100000000001", "repo=org/app", "workflow=ExampleWorkflow"], routes);
    }

    [Fact]
    public async Task Route_Duplicate_Is409_IncludingAfterNormalisation()
    {
        await SeedContextAsync("project-a", "Full.");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync("/api/project-contexts/project-a/routes", new { kind = "repo", value = "org/app" })).StatusCode);

        var response = await PostAsync("/api/project-contexts/project-a/routes", new { kind = "repo", value = "ORG/app" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("org/app", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Route_SameSignalOnTwoContexts_IsAllowed()
    {
        await SeedContextAsync("project-a", "Full.");
        await SeedContextAsync("project-b", "Full.");

        Assert.Equal(HttpStatusCode.Created, (await PostAsync("/api/project-contexts/project-a/routes", new { kind = "workflow", value = "ExampleWorkflow" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync("/api/project-contexts/project-b/routes", new { kind = "workflow", value = "ExampleWorkflow" })).StatusCode);
    }

    [Theory]
    [InlineData("branch", "x")]
    [InlineData("repo", "not-a-repo")]
    [InlineData("chat", "abc")]
    [InlineData("workflow", "  ")]
    public async Task Route_InvalidKindOrValue_Is400(string kind, string value)
    {
        await SeedContextAsync("project-a", "Full.");

        var response = await PostAsync("/api/project-contexts/project-a/routes", new { kind, value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await GetDetailAsync("project-a")).GetProperty("routes").GetArrayLength());
    }

    [Fact]
    public async Task Route_AddToUnknownProject_Is404()
    {
        var response = await PostAsync("/api/project-contexts/project-b/routes", new { kind = "repo", value = "org/app" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Route_Delete_OfAnotherContextsRoute_Is404_AndLeavesIt()
    {
        await SeedContextAsync("project-a", "Full.");
        await SeedContextAsync("project-b", "Full.");
        var created = await ReadJsonAsync(await PostAsync("/api/project-contexts/project-b/routes", new { kind = "repo", value = "org/app" }));
        var id = created.GetProperty("id").GetInt32();

        var wrong = await SendAsync(HttpMethod.Delete, $"/api/project-contexts/project-a/routes/{id}");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal(1, (await GetDetailAsync("project-b")).GetProperty("routes").GetArrayLength());

        var right = await SendAsync(HttpMethod.Delete, $"/api/project-contexts/project-b/routes/{id}");
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Equal(0, (await GetDetailAsync("project-b")).GetProperty("routes").GetArrayLength());
    }

    // ── Auth, list and cascade ────────────────────────────────────────────────

    [Theory]
    [InlineData("POST", "/api/project-contexts/project-a/card/versions")]
    [InlineData("POST", "/api/project-contexts/project-a/card/rollback/1")]
    [InlineData("POST", "/api/project-contexts/project-a/routes")]
    [InlineData("DELETE", "/api/project-contexts/project-a/routes/1")]
    public async Task CardAndRouteWrites_WithoutABearer_Are401(string method, string url)
    {
        await SeedContextAsync("project-a", "Full.");
        Assert.True(OrchestratorAuth.RequiresBearerToken(method, url));

        var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) };
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_CarriesCardVersion_Stale_AndCardAssignments()
    {
        var id = await SeedContextAsync("project-a", "Full.");
        await SeedContextAsync("project-b", "Full.");
        await WithDbAsync(db =>
        {
            SeedCard(db, id, 1, "Card.", basedOnFullVersion: 1);
            SeedAgent(db, "agent-a", ("project-a", ProjectContextMode.Card));
            SeedAgent(db, "agent-b", ("Project-A", ProjectContextMode.Full), ("project-b", ProjectContextMode.Full));
            return 0;
        });

        var rows = (await ReadJsonAsync(await _client.GetAsync("/api/project-contexts"))).EnumerateArray().ToList();

        var a = rows.Single(r => r.GetProperty("name").GetString() == "project-a");
        Assert.Equal(1, a.GetProperty("cardVersion").GetInt32());
        Assert.False(a.GetProperty("cardStale").GetBoolean());
        Assert.Equal(["agent-a"], Strings(a.GetProperty("cardAssignments")));
        Assert.Equal(["agent-a", "agent-b"], Strings(a.GetProperty("agents")));

        var b = rows.Single(r => r.GetProperty("name").GetString() == "project-b");
        Assert.Equal(JsonValueKind.Null, b.GetProperty("cardVersion").ValueKind);
        Assert.False(b.GetProperty("cardStale").GetBoolean());
        Assert.Empty(Strings(b.GetProperty("cardAssignments")));
    }

    /// <summary>
    /// D10: card versions and routes reference <c>project_contexts.Id</c> with ON DELETE CASCADE.
    /// Deleted with raw SQL so EF's client-side cascade cannot stand in for the database's.
    /// </summary>
    [Fact]
    public async Task DeletingAContext_CascadesToItsCardVersionsAndRoutes()
    {
        var id = await SeedContextAsync("project-a", "Full.");
        var other = await SeedContextAsync("project-b", "Full.");
        (await PostAsync("/api/project-contexts/project-a/card/versions", new { content = "Card.", basedOnFullVersion = 1 })).EnsureSuccessStatusCode();
        (await PostAsync("/api/project-contexts/project-a/routes", new { kind = "repo", value = "org/app" })).EnsureSuccessStatusCode();
        (await PostAsync("/api/project-contexts/project-b/routes", new { kind = "repo", value = "org/app" })).EnsureSuccessStatusCode();

        await using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys";
            Assert.Equal(1L, (long)(await pragma.ExecuteScalarAsync())!);
        }

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM project_contexts WHERE Id = {0}", id);
        }

        await WithDbAsync(db =>
        {
            Assert.False(db.ProjectContextCardVersions.Any(v => v.ProjectContextId == id));
            Assert.False(db.ProjectContextRoutes.Any(r => r.ProjectContextId == id));
            Assert.Single(db.ProjectContextRoutes.Where(r => r.ProjectContextId == other));
            return 0;
        });
    }
}
