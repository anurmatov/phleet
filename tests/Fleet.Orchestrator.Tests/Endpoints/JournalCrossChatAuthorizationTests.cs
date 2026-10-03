using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Endpoints;

public sealed class JournalCrossChatAuthorizationTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private JournalTokenService _tokens = null!;
    private readonly TelegramMembershipClientTests.Handler _telegram = new(_ => "{\"ok\":true,\"result\":{\"id\":101,\"status\":\"member\"}}");
    private readonly string _env = Path.Combine(Path.GetTempPath(), $"synthetic-{Guid.NewGuid():N}.env");

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(_env, "TELEGRAM_SYNTHETIC_BOT_TOKEN=synthetic\n");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["Journal:TokenKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray());
        builder.Configuration["Provisioning:EnvFilePath"] = _env;
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseInMemoryDatabase($"authz-{_env}"));
        builder.Services.AddSingleton<JournalTokenService>();
        builder.Services.AddSingleton(new TelegramMembershipClient(_telegram, new Uri("http://localhost/")));
        _app = builder.Build();
        // Same global predicate as Program; this route has its own distinct bearer.
        _app.Use(async (context, next) =>
        {
            if (OrchestratorAuth.RequiresBearerToken(context.Request.Method, context.Request.Path.Value ?? "")
                && context.Request.Headers.Authorization != "Bearer synthetic-admin")
            { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        _app.MapJournalCrossChatAuthorization();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _tokens = _app.Services.GetRequiredService<JournalTokenService>();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.Mint(JournalTokenService.PurposeCrossChatAuthz, "fleet-comms"));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose(); await _app.DisposeAsync(); File.Delete(_env);
    }

    private async Task SeedAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = JournalGrantsTests.NewAgent();
        agent.JournalEnabled = true;
        agent.JournalCrossChatEnabled = true;
        agent.EnvRefs = [new() { EnvKeyName = "TELEGRAM_SYNTHETIC_BOT_TOKEN" }];
        agent.Tools = [new() { ToolName = JournalGrants.SendGrant }];
        db.Agents.Add(agent); await db.SaveChangesAsync();
    }
    private async Task<JsonElement> AnswerAsync(object body)
    {
        var response = await _client.PostAsJsonAsync(JournalCrossChatAuthorizationEndpoints.Path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData("/internal/journal/cross-chat-authorization", false)]
    [InlineData("/internal/journal/cross-chat-authorization/extra", true)]
    [InlineData("/internal/journal/cross-chat-authorization-forged", true)]
    [InlineData("/api/agents/agent1/config", true)]
    public void GlobalAdminAuth_ExemptsOnlyExactSelfAuthenticatedRoute(string path, bool gated) =>
        Assert.Equal(gated, OrchestratorAuth.RequiresBearerToken("POST", path));

    [Fact]
    public async Task AdminCredential_IsNotACommsCredential()
    {
        _client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-admin");
        var response = await _client.PostAsJsonAsync(JournalCrossChatAuthorizationEndpoints.Path, new { subject = "agent1", botId = 101 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("read", "agent1")]
    [InlineData("read-cross-chat", "agent1")]
    [InlineData("cross-chat-authz", "agent1")]
    [InlineData("cross-chat-authz", "fleet-comms", true)]
    public async Task UnauthorizedCaller_DeniedBeforeBodyRead(string purpose, string subject, bool corrupt = false)
    {
        var token = _tokens.Mint(purpose, subject);
        if (corrupt) token = token[..^2] + "xx";
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var response = await _client.PostAsync(JournalCrossChatAuthorizationEndpoints.Path, new StringContent("not json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _telegram.MeCalls + _telegram.MemberCalls);
    }

    [Theory]
    [InlineData("{\"subject\":\"agent1\",\"botId\":101,\"chatId\":-202}")]
    [InlineData("{\"subject\":\"agent1\",\"botId\":101,\"userId\":303}")]
    [InlineData("{\"subject\":\"agent1\",\"botId\":101,\"requester\":303}")]
    [InlineData("{\"subject\":\"agent1\",\"botId\":0}")]
    [InlineData("{\"subject\":\"\",\"botId\":101}")]
    public async Task InvalidBody_IsBadRequest(string json)
    {
        var response = await _client.PostAsync(JournalCrossChatAuthorizationEndpoints.Path, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _telegram.MeCalls + _telegram.MemberCalls);
    }

    [Fact]
    public async Task OversizeBody_IsRejected()
    {
        var response = await _client.PostAsync(JournalCrossChatAuthorizationEndpoints.Path, new StringContent(new string(' ', 1025)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SavedRevocation_IsUncached_AndSourceOptional()
    {
        var body = new { subject = "agent1", botId = 101 };
        Assert.Equal("off", (await AnswerAsync(body)).GetProperty("switch").GetString());
        await SeedAsync();
        var enabled = await AnswerAsync(body);
        Assert.Equal("effective", enabled.GetProperty("switch").GetString());
        Assert.Equal(JsonValueKind.Null, enabled.GetProperty("member").ValueKind);
        Assert.Equal(0, _telegram.MeCalls + _telegram.MemberCalls);
        var member = await AnswerAsync(new { subject = "agent1", botId = 101, chatId = -202, userId = 303 });
        Assert.Equal("member", member.GetProperty("member").GetString());
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents.SingleAsync();
        agent.JournalCrossChatEnabled = false; await db.SaveChangesAsync();
        Assert.Equal("off", (await AnswerAsync(body)).GetProperty("switch").GetString());
        agent.JournalCrossChatEnabled = true; await db.SaveChangesAsync();
        Assert.Equal("effective", (await AnswerAsync(body)).GetProperty("switch").GetString());
        agent.JournalEnabled = false; await db.SaveChangesAsync();
        Assert.Equal("off", (await AnswerAsync(body)).GetProperty("switch").GetString());
        agent.JournalEnabled = true;
        db.AgentTools.RemoveRange(await db.AgentTools.ToListAsync()); await db.SaveChangesAsync();
        Assert.Equal("off", (await AnswerAsync(body)).GetProperty("switch").GetString());
    }
}
