using System.Net;
using System.Text;
using Fleet.Orchestrator.Data;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

public sealed class JournalCrossChatRevocationTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private readonly Handler _telegram = new(_ => "{\"ok\":true,\"result\":{\"id\":101,\"status\":\"member\"}}");
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
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync(); File.Delete(_env);
    }

    private async Task SeedAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = new Fleet.Orchestrator.Data.Agent { Name = "agent1", DisplayName = "Synthetic", Role = "test", Model = "test", ContainerName = "synthetic" };
        agent.JournalEnabled = true;
        agent.JournalCrossChatEnabled = true;
        agent.EnvRefs = [new() { EnvKeyName = "TELEGRAM_SYNTHETIC_BOT_TOKEN" }];
        agent.Tools = [new() { ToolName = JournalGrants.SendGrant }];
        db.Agents.Add(agent); await db.SaveChangesAsync();
    }
    private sealed class SendSource : IJournalSendSource
    {
        public Task<JournalSendSource?> FindSendSourceAsync(string subject, string boundKey, long requester, string? messageId, long? telegramMessageId, int ordinal, CancellationToken ct = default) =>
            Task.FromResult<JournalSendSource?>(new(new(messageId!, 0, "document", "application/pdf", 9, "not_archived", "media_disabled", null, null, null, null, null), "tg:group:-202", "group", -202, true, "synthetic-file", 101));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommsDirectCalls_RevokeAgainstSavedDbRowWithoutReprovision(bool removeGrant)
    {
        await SeedAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        await using var comms = builder.Build(); var key = Enumerable.Repeat((byte)42, 32).ToArray();
        var bindings = new JournalTurnBindings(TimeProvider.System); bindings.Put("agent1", new("epoch", 1, "bound", "private", 101, 303));
        using var auth = new JournalCrossChatAuthorizationClient(_app.GetTestClient(), "http://localhost", key);
        var scope = new JournalBindingScope(bindings, new HashSet<long>());
        var resolver = new JournalSendSourceResolver(new SendSource(), scope, auth);
        JournalAuth.Use(comms, [key], new()); JournalAttachmentSendEndpoints.Map(comms, resolver);
        var content = new JournalAttachmentContentEndpoint(null, null, new(new HashSet<string>()), scope, new(), send: resolver);
        comms.MapPost(JournalAttachmentSendEndpoints.CrossContentPath, content.HandleAsync);
        await comms.StartAsync(); using var client = comms.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", JournalTokens.Mint(key, "read-cross-chat", "agent1"));
        const string body = "{\"message_id\":\"01K00000000000000000000000\"}";
        using var first = await client.PostAsync(JournalAttachmentSendEndpoints.CrossHandlePath, new StringContent(body)); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var dbScope = _app.Services.CreateScope(); var db = dbScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents.Include(a => a.Tools).SingleAsync();
        if (removeGrant) agent.Tools[0].IsEnabled = false; else agent.JournalCrossChatEnabled = false;
        await db.SaveChangesAsync();
        foreach (var path in new[] { JournalAttachmentSendEndpoints.CrossHandlePath, JournalAttachmentSendEndpoints.CrossContentPath })
        {
            using var revoked = await client.PostAsync(path, new StringContent(body)); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.Equal("{\"error\":\"unauthorized\"}", await revoked.Content.ReadAsStringAsync());
        }
        if (removeGrant) agent.Tools[0].IsEnabled = true; else agent.JournalCrossChatEnabled = true;
        await db.SaveChangesAsync();
        using var restored = await client.PostAsync(JournalAttachmentSendEndpoints.CrossHandlePath, new StringContent(body)); Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(2, _telegram.MemberCalls);
    }
    private sealed class Handler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        public int MemberCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("getMe")) MemberCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
        }
    }
}
