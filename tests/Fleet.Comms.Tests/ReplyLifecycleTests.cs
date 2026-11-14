using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Comms.Auth;
using Fleet.Conversations.Contracts;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.Push;
using Fleet.Protocol;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Comms.Tests;

public sealed class ReplyLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_stop_or_restart_refuses_Reply_until_the_current_runtime_reports(bool restart)
    {
        var key = new byte[32];
        var transport = new RuntimeApi();
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(transport) { BaseAddress = new Uri("http://localhost") });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["Journal:TokenKey"] = Convert.ToBase64String(key);
        var database = Guid.NewGuid().ToString();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseInMemoryDatabase(database));
        builder.Services.AddSingleton<ISecretHasher>(new Argon2idSecretHasher(memoryKib: 64, iterations: 1));
        builder.Services.AddFleetSignIn(durable: false);
        builder.Services.AddSingleton<JournalTokenService>();
        builder.Services.AddSingleton<TaskHistoryStore>();
        builder.Services.AddSingleton<AgentRegistry>();
        builder.Services.AddSingleton(docker);
        builder.Services.AddRateLimiter(o => o.AddSignInPolicy());
        await using var issuer = builder.Build();
        issuer.UseFleetAccess();
        issuer.MapAuthEndpoints();
        await issuer.StartAsync();
        using (var scope = issuer.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Agents.Add(new Fleet.Orchestrator.Data.Agent { Name = "agent1", DisplayName = "Agent One", Role = "test", Model = "test",
                ContainerName = "configured-agent1", ChatEnabled = true });
            await db.SaveChangesAsync();
        }
        var auth = issuer.Services.GetRequiredService<AuthService>();
        await auth.EnsureOwnerAsync(null);
        var session = (await auth.SignInBrowserAsync(UserRecord.OwnerId, "Synthetic")).Value!;
        var credential = new PresentedCredential(null, session.SessionToken);
        using var browser = issuer.GetTestClient();
        browser.DefaultRequestHeaders.Add("Cookie", $"{SignIn.SessionCookie}={session.SessionToken}");
        async Task<bool> ListedReply() => (await browser.GetFromJsonAsync<JsonElement>("/api/chat/agents"))
            .GetProperty("agents")[0].GetProperty("replySupported").GetBoolean();
        using var check = new OrchestratorSignInCheck(issuer.GetTestClient(), "http://localhost", key,
            TimeProvider.System, NullLogger<OrchestratorSignInCheck>.Instance);
        var registry = issuer.Services.GetRequiredService<AgentRegistry>();
        AgentHeartbeat Report(string? id = null, DateTimeOffset? at = null, bool capable = true) =>
            new("agent1", "idle", at ?? DateTimeOffset.UtcNow, ContainerName: (id ?? transport.Id)[..12],
                Capabilities: capable ? [SignIn.ChatReplyCapability] : null);
        var oldReport = Report();
        registry.UpdateAgent(oldReport);
        Assert.True((await check.CheckAsync(credential, "agent1", false, default)).AgentReplySupported);

        Assert.True(await ListedReply());
        var lifecycle = new AgentLifecycle(issuer.Services.GetRequiredService<IServiceScopeFactory>(), registry,
            docker, null!, new OperatorStops(), null!);
        var stopped = restart ? await lifecycle.RestartAsync("agent1") : await lifecycle.StopAsync("agent1");
        Assert.True(stopped.Saved);
        var invalidated = registry.Get("agent1")!.Capabilities is null;
        registry.UpdateAgent(oldReport); // A queued report cannot revive the stopped/prior start.

        var store = new FakeConversationStore { OwnerPrincipalId = UserRecord.OwnerId, AgentName = "agent1" };
        store.ReplyTargets["01JREPLYTARGET000000000001"] = new ReplyTarget {
            Kind = ConversationEventKind.TurnFinal, Text = "An earlier answer" };
        await using var comms = await NorthTestHost.StartAsync(conversations: store, issuer: check);
        comms.Client.DefaultRequestHeaders.Add("Cookie", $"{SignIn.SessionCookie}={session.SessionToken}");
        comms.Client.DefaultRequestHeaders.Add(SignIn.RequestHeader, "1");
        var refused = await comms.Client.PostAsJsonAsync($"/v1/conversations/{store.ConversationId}/submissions", new {
            protocol = ProtocolVersion.Current, type = "create", submissionId = "reply-1", text = "my unsent words",
            replyToEventId = "01JREPLYTARGET000000000001" });
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict && store.Accepted.Count == 0,
            $"status={refused.StatusCode}, durable accepts={store.Accepted.Count}");
        Assert.True(invalidated);
        Assert.False(await ListedReply());
        Assert.Contains("/containers/configured-agent1/json", transport.Reads);
        Assert.Equal("reply_unavailable", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(store.Accepted);
        var ordinary = await comms.Client.PostAsJsonAsync($"/v1/conversations/{store.ConversationId}/submissions", new {
            protocol = ProtocolVersion.Current, type = "create", submissionId = "plain-1", text = "a new message" });
        Assert.Equal(HttpStatusCode.Created, ordinary.StatusCode);
        Assert.Null(Assert.Single(store.Accepted).ReplyToEventId);

        if (!restart) Assert.True((await lifecycle.StartAsync("agent1")).Saved);
        Assert.False((await check.CheckAsync(credential, "agent1", true, default)).AgentReplySupported);
        registry.UpdateAgent(Report());
        Assert.True((await check.CheckAsync(credential, "agent1", true, default)).AgentReplySupported);
        var oldId = transport.Id;
        transport.Id = new string('b', 64); // Replacement: an old runtime's late report has a different ID.
        transport.Started = DateTimeOffset.UtcNow;
        registry.UpdateAgent(Report(oldId));
        Assert.False((await check.CheckAsync(credential, "agent1", true, default)).AgentReplySupported);
        registry.UpdateAgent(Report(at: oldReport.Timestamp));
        Assert.False((await check.CheckAsync(credential, "agent1", true, default)).AgentReplySupported);
        registry.UpdateAgent(Report());
        Assert.True((await check.CheckAsync(credential, "agent1", true, default)).AgentReplySupported);
        transport.Exists = false;
        Assert.False((await check.CheckAsync(credential, "agent1", true, default)).AgentReplySupported);
        transport.Exists = true;
        registry.UpdateAgent(Report(capable: false));
        Assert.False((await check.CheckAsync(credential, "agent1", true, default)).AgentReplySupported);
        registry.UpdateAgent(Report());
        transport.Unresponsive = true;
        var withoutDocker = await check.CheckAsync(credential, "agent1", true, default);
        Assert.True(withoutDocker.AgentChatEnabled);
        Assert.False(withoutDocker.AgentReplySupported);
    }

    private sealed class RuntimeApi : HttpMessageHandler
    {
        public bool Exists = true;
        public bool Unresponsive;
        public string Id = new('a', 64);
        public DateTimeOffset Started = DateTimeOffset.UtcNow.AddMinutes(-1);
        private bool _running = true;
        public List<string> Reads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                if (Unresponsive) await Task.Delay(Timeout.Infinite, ct);
                if (!Exists) return new HttpResponseMessage(HttpStatusCode.NotFound);
                Reads.Add(request.RequestUri!.AbsolutePath.Replace("/v1.41", ""));
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(JsonSerializer.Serialize(new { Id, State = new { Running = _running, StartedAt = Started } })) };
            }
            _running = !request.RequestUri!.AbsolutePath.EndsWith("/stop", StringComparison.Ordinal);
            if (_running) Started = DateTimeOffset.UtcNow;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
