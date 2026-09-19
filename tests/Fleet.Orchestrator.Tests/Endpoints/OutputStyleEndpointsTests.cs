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

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// The <c>/api/output-styles</c> acceptance criteria, driven over real HTTP (#317).
/// </summary>
/// <remarks>
/// <para>
/// These exist because everything else about this surface could be true while the surface itself
/// was unreachable. A handler tested by calling it directly proves the handler; it cannot prove
/// the route is mapped, that the 409 guard is wired to the delete, or — the one that matters most
/// — that a write without a bearer is refused while a read without one is not.
/// </para>
/// <para>
/// So the host is built from the SAME two pieces a live orchestrator uses:
/// <see cref="OutputStyleEndpoints.MapOutputStyleEndpoints"/> and
/// <see cref="OrchestratorAuth.RequiresBearerToken"/>. Nothing here re-declares a route or
/// re-states the auth rule, which is what makes a revert in either one show up as a red test
/// rather than a green one.
/// </para>
/// <para>
/// What this host is NOT: the real <c>Program.cs</c>, which needs MySQL, RabbitMQ and Docker to
/// start. The store is SQLite. Middleware ordering outside these two pieces is not covered.
/// </para>
/// </remarks>
public class OutputStyleEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-bearer-token";

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static string StyleFile(string name, string description, string body = "Write it plainly.") =>
        $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n";

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));

        _app = builder.Build();

        // The production gate, called rather than copied.
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

        _app.MapOutputStyleEndpoints();

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

    private HttpRequestMessage Authed(HttpMethod method, string url, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return request;
    }

    private async Task SeedStyleAsync(string name, string body)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.OutputStyles.Add(new OutputStyle
        {
            Name = name,
            Body = body,
            Description = OutputStyleRenderer.ReadDescription(body),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentAsync(string name, string? style)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(new Agent
        {
            Name          = name,
            DisplayName   = name,
            Role          = "developer",
            Provider      = "claude",
            Model         = "opus",
            ContainerName = $"fleet-{name}",
            OutputStyle   = style,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ── AC1: list carries the body; get returns one row ───────────────────────

    [Fact]
    public async Task List_CarriesTheFullBodyOfEveryRow()
    {
        var first  = StyleFile("alpha", "first style");
        var second = StyleFile("beta", "second style");
        await SeedStyleAsync("alpha", first);
        await SeedStyleAsync("beta", second);

        var response = await _client.GetAsync("/api/output-styles");
        response.EnsureSuccessStatusCode();

        var rows = await ReadJsonAsync(response);
        Assert.Equal(2, rows.GetArrayLength());

        var alpha = rows.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "alpha");
        Assert.Equal(first, alpha.GetProperty("body").GetString());
        Assert.Equal("first style", alpha.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Get_ReturnsOneRowWithItsBody_AndTheAgentsOnIt()
    {
        var body = StyleFile("alpha", "first style");
        await SeedStyleAsync("alpha", body);
        await SeedAgentAsync("agent-one", "alpha");
        await SeedAgentAsync("agent-two", null);

        var response = await _client.GetAsync("/api/output-styles/alpha");
        response.EnsureSuccessStatusCode();

        var row = await ReadJsonAsync(response);
        Assert.Equal(body, row.GetProperty("body").GetString());
        Assert.Equal(["agent-one"], row.GetProperty("agents").EnumerateArray().Select(a => a.GetString()!).ToArray());
    }

    [Fact]
    public async Task Get_ForAnUnknownName_Is404()
    {
        var response = await _client.GetAsync("/api/output-styles/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── AC2: POST then GET back byte-identical ────────────────────────────────

    [Fact]
    public async Task Post_ThenGet_ReturnsTheBodyByteIdentical()
    {
        // Non-ASCII and a trailing newline on purpose: a save path that normalises line endings or
        // re-encodes would pass a "contains" assertion and fail this one, and the operator would
        // discover it as a style that reads subtly differently from the one they wrote.
        var body = "---\nname: alpha\ndescription: keep it terse — no em-dash rewriting\n---\n\nWrite «plainly».\r\nSecond line.\n";

        var create = await _client.SendAsync(Authed(HttpMethod.Post, "/api/output-styles",
            new { name = "alpha", body }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var read = await _client.GetAsync("/api/output-styles/alpha");
        read.EnsureSuccessStatusCode();

        var row = await ReadJsonAsync(read);
        Assert.Equal(body, row.GetProperty("body").GetString());

        // ...and the description came from the frontmatter rather than the request.
        Assert.Equal("keep it terse — no em-dash rewriting", row.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Post_WithAnExistingName_Is409_AndLeavesTheOriginalBody()
    {
        var original = StyleFile("alpha", "first style", "Original body.");
        await SeedStyleAsync("alpha", original);

        var response = await _client.SendAsync(Authed(HttpMethod.Post, "/api/output-styles",
            new { name = "alpha", body = StyleFile("alpha", "second style", "Replacement body.") }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var read = await ReadJsonAsync(await _client.GetAsync("/api/output-styles/alpha"));
        Assert.Equal(original, read.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Post_WithAFrontmatterNameThatDisagrees_Is400()
    {
        // Claude Code matches on the frontmatter name, so this is a style that would load as
        // nothing while every artifact reported it as present.
        var response = await _client.SendAsync(Authed(HttpMethod.Post, "/api/output-styles",
            new { name = "alpha", body = StyleFile("not-alpha", "mismatched") }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── AC4 and AC5: delete refuses while referenced, removes when not ────────

    [Fact]
    public async Task Delete_AReferencedStyle_Is409_NamesTheAgents_AndKeepsTheRow()
    {
        await SeedStyleAsync("alpha", StyleFile("alpha", "first style"));
        await SeedAgentAsync("agent-one", "alpha");
        await SeedAgentAsync("agent-two", "alpha");

        var response = await _client.SendAsync(Authed(HttpMethod.Delete, "/api/output-styles/alpha"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var payload = await ReadJsonAsync(response);
        var error = payload.GetProperty("error").GetString()!;
        Assert.Contains("agent-one", error);
        Assert.Contains("agent-two", error);

        // The row is still there — a refusal that deleted anyway would be worse than a cascade.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/output-styles/alpha")).StatusCode);
    }

    [Fact]
    public async Task Delete_AnUnreferencedStyle_RemovesItFromTheList()
    {
        await SeedStyleAsync("alpha", StyleFile("alpha", "first style"));
        await SeedStyleAsync("beta", StyleFile("beta", "second style"));
        await SeedAgentAsync("agent-one", "beta");

        var response = await _client.SendAsync(Authed(HttpMethod.Delete, "/api/output-styles/alpha"));
        response.EnsureSuccessStatusCode();

        var rows = await ReadJsonAsync(await _client.GetAsync("/api/output-styles"));
        Assert.Equal(["beta"], rows.EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task Delete_ByACaseVariantOfAnAssignedName_IsStillRefused()
    {
        // The store decides case sensitivity, so a guard written as a WHERE predicate would refuse
        // on MySQL and allow here. OutputStyleUsage groups case-insensitively for exactly this.
        await SeedStyleAsync("alpha", StyleFile("alpha", "first style"));
        await SeedAgentAsync("agent-one", "ALPHA");

        var response = await _client.SendAsync(Authed(HttpMethod.Delete, "/api/output-styles/alpha"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── AC3 (the half a test can reach): an edit is not reverted by a reread ──

    [Fact]
    public async Task Put_ReplacesTheBody_AndTheEditIsWhatIsReadBack()
    {
        // AC3's redeploy half needs a live orchestrator; what is checkable here is that the write
        // lands in the store and the read reflects it, with the description re-derived.
        await SeedStyleAsync("alpha", StyleFile("alpha", "first style", "Original body."));

        var edited = StyleFile("alpha", "edited description", "Edited body.");
        var update = await _client.SendAsync(Authed(HttpMethod.Put, "/api/output-styles/alpha", new { body = edited }));
        update.EnsureSuccessStatusCode();

        var row = await ReadJsonAsync(await _client.GetAsync("/api/output-styles/alpha"));
        Assert.Equal(edited, row.GetProperty("body").GetString());
        Assert.Equal("edited description", row.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Put_NamesTheAgentsToReprovision_BecauseTheEditIsNotLive()
    {
        await SeedStyleAsync("alpha", StyleFile("alpha", "first style"));
        await SeedAgentAsync("agent-one", "alpha");

        var response = await _client.SendAsync(Authed(HttpMethod.Put, "/api/output-styles/alpha",
            new { body = StyleFile("alpha", "edited") }));
        response.EnsureSuccessStatusCode();

        var message = (await ReadJsonAsync(response)).GetProperty("message").GetString()!;
        Assert.Contains("Reprovision", message);
        Assert.Contains("agent-one", message);
    }

    // ── AC6: reads are open, writes are not ──────────────────────────────────

    [Fact]
    public async Task UnauthenticatedGets_Succeed()
    {
        await SeedStyleAsync("alpha", StyleFile("alpha", "first style"));

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/output-styles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/output-styles/alpha")).StatusCode);
    }

    [Theory]
    [InlineData("POST",   "/api/output-styles")]
    [InlineData("PUT",    "/api/output-styles/alpha")]
    [InlineData("DELETE", "/api/output-styles/alpha")]
    public async Task UnauthenticatedWrites_Are401(string method, string url)
    {
        await SeedStyleAsync("alpha", StyleFile("alpha", "first style"));

        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method != "DELETE")
            request.Content = JsonContent.Create(new { name = "alpha", body = StyleFile("alpha", "x") });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARejectedWriteChangesNothing()
    {
        // The status code alone would be satisfied by a handler that 401s after saving.
        var original = StyleFile("alpha", "first style", "Original body.");
        await SeedStyleAsync("alpha", original);

        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/output-styles/alpha"));
        await _client.PutAsJsonAsync("/api/output-styles/alpha", new { body = StyleFile("alpha", "edited") });

        var row = await ReadJsonAsync(await _client.GetAsync("/api/output-styles/alpha"));
        Assert.Equal(original, row.GetProperty("body").GetString());
    }

    [Fact]
    public async Task AWriteWithTheWrongToken_Is401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/output-styles")
        {
            Content = JsonContent.Create(new { name = "alpha", body = StyleFile("alpha", "x") }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }
}
