using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.Push;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Push;

/// <summary>
/// A chat reply nobody was watching becomes a phone notification (#530 step 9): the chat service
/// asks, the orchestrator queues, the notifier sends. Identifiers only.
/// </summary>
public sealed class ChatReplyPushTests : IAsyncLifetime
{
    private const string ConversationId = "01JCHATREPXY0000000000000A";
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private bool _pushEnabled = true;

    public Task InitializeAsync() => StartAsync();

    private async Task StartAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.Configure<PushOptions>(o =>
        {
            o.Enabled = _pushEnabled;
            o.ApnsKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem()));
            o.ApnsKeyId = "KEYID12345";
            o.ApnsTeamId = "TEAMID1234";
            o.ApnsTopic = "com.example.app";
        });
        builder.Services.AddSingleton<PushReadiness>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ChatReplyPushes>();
        builder.Services.AddSingleton(new JournalTokenService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Journal:TokenKey"] = Key }).Build()));

        _app = builder.Build();
        _app.MapPushEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Agents.Add(new Agent
            {
                Name = "builder", DisplayName = "Builder", Role = "developer", Model = "example-model",
                ContainerName = "fleet-builder",
            });
            await db.SaveChangesAsync();
        }

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private string Token(string purpose = SignIn.ChatReplyPushTokenPurpose, string subject = SignIn.CheckTokenSubject) =>
        _app.Services.GetRequiredService<JournalTokenService>().Mint(purpose, subject);

    private Task<HttpResponseMessage> PostAsync(object body, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SignIn.ChatReplyPushPath) { Content = JsonContent.Create(body) };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _client.SendAsync(request);
    }

    private ChatReplyPushes Replies => _app.Services.GetRequiredService<ChatReplyPushes>();

    // ── the route ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_is_queued_with_who_replied_and_where_and_nothing_said()
    {
        var response = await PostAsync(new { agent = "builder", conversationId = ConversationId, seq = 41 }, Token());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = Assert.Single(Replies.Take());
        Assert.Equal("chat", message.Kind);
        Assert.Equal("Builder", message.Title);
        Assert.Equal("New reply", message.Body);
        Assert.Equal("builder", message.Target["agentName"]);
        Assert.Equal(ConversationId, message.Target["conversationId"]);
        Assert.True(Replies.IsCurrent(message.Key));
    }

    [Fact]
    public async Task Only_the_chat_services_own_token_is_accepted()
    {
        var body = new { agent = "builder", conversationId = ConversationId, seq = 1 };

        foreach (var bearer in new[] { null, "not-a-token", Token(purpose: SignIn.CheckTokenPurpose), Token(subject: "someone-else"), Token(purpose: "ingest") })
        {
            var response = await PostAsync(body, bearer);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Empty(Replies.Take());
    }

    [Theory]
    [InlineData("build.er", ConversationId, 1L)]
    [InlineData("builder", "not-a-ulid", 1L)]
    [InlineData("builder", "01JCHATREPXY0000000000000a", 1L)]
    [InlineData("builder", ConversationId, null)]
    public async Task A_body_that_does_not_name_an_agent_and_a_conversation_is_refused(string agent, string conversationId, long? seq)
    {
        var response = await PostAsync(new { agent, conversationId, seq }, Token());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Replies.Take());
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("BUILDER")]
    public async Task An_agent_this_orchestrator_does_not_know_by_its_exact_name_is_ignored(string agent)
    {
        var response = await PostAsync(new { agent, conversationId = ConversationId, seq = 1 }, Token());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Replies.Take());
    }

    [Fact]
    public async Task With_push_off_nothing_is_queued()
    {
        await DisposeAsync();
        _pushEnabled = false;
        await StartAsync();

        var response = await PostAsync(new { agent = "builder", conversationId = ConversationId, seq = 1 }, Token());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Replies.Take());
    }

    [Fact]
    public void The_route_is_exempt_from_the_service_token_rule_by_its_exact_name_only()
    {
        Assert.False(OrchestratorAuth.RequiresBearerToken("POST", SignIn.ChatReplyPushPath));
        Assert.True(OrchestratorAuth.RequiresBearerToken("POST", SignIn.ChatReplyPushPath + "/more"));
    }

    // ── the notification ─────────────────────────────────────────────────────

    [Fact]
    public void The_payload_carries_identifiers_and_no_reply_text()
    {
        var payload = Encoding.UTF8.GetString(PushMessages.ChatReply("builder", "Builder", ConversationId, 7).ToPayload());
        using var json = JsonDocument.Parse(payload);

        Assert.Equal("Builder", json.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("New reply", json.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("body").GetString());
        var phleet = json.RootElement.GetProperty("phleet");
        Assert.Equal("chat", phleet.GetProperty("kind").GetString());
        Assert.Equal("builder", phleet.GetProperty("agentName").GetString());
        Assert.Equal(ConversationId, phleet.GetProperty("conversationId").GetString());
        Assert.Equal(3, phleet.EnumerateObject().Count());
    }

    [Fact]
    public void Each_reply_is_its_own_notification()
    {
        Assert.NotEqual(
            PushMessages.ChatReply("builder", "Builder", ConversationId, 7).Key,
            PushMessages.ChatReply("builder", "Builder", ConversationId, 9).Key);
    }

    [Fact]
    public async Task A_queued_reply_goes_to_every_device_on_the_next_pass_and_only_once()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.PushDevices.Add(new PushDevice { DeviceId = "device-0001", Token = new string('a', 64) });
            db.PushDevices.Add(new PushDevice { DeviceId = "device-0002", Token = new string('b', 64) });
            await db.SaveChangesAsync();
        }

        var apns = new RecordingApns();
        var delivery = new PushDelivery(_app.Services.GetRequiredService<IServiceScopeFactory>(), apns, NullLogger.Instance);
        var detector = new PushEventDetector(TimeSpan.FromMinutes(5), new OperatorStops());

        Assert.True(Replies.Enqueue(PushMessages.ChatReply("builder", "Builder", ConversationId, 3)));

        await PushNotifierService.PassAsync(detector, delivery, [], [], [], DateTimeOffset.UtcNow, CancellationToken.None, Replies);
        Assert.Equal(["chat", "chat"], apns.Kinds);

        await PushNotifierService.PassAsync(detector, delivery, [], [], [], DateTimeOffset.UtcNow, CancellationToken.None, Replies);
        Assert.Equal(2, apns.Kinds.Count);
    }

    // ── the queue ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_queued_reply_wakes_the_notifier_before_its_interval()
    {
        var replies = new ChatReplyPushes(TimeProvider.System);
        var wait = replies.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        replies.Enqueue(PushMessages.ChatReply("builder", "Builder", ConversationId, 1));

        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void A_reply_stops_being_retried_after_its_lifetime()
    {
        var time = new ManualTime();
        var replies = new ChatReplyPushes(time);
        var message = PushMessages.ChatReply("builder", "Builder", ConversationId, 1);

        replies.Enqueue(message);
        replies.Take();
        Assert.True(replies.IsCurrent(message.Key));

        time.Now += ChatReplyPushes.Lifetime;
        Assert.False(replies.IsCurrent(message.Key));
    }

    [Fact]
    public void A_flood_is_bounded()
    {
        var replies = new ChatReplyPushes(TimeProvider.System);

        for (var i = 0; i < ChatReplyPushes.MaxQueued; i++)
            Assert.True(replies.Enqueue(PushMessages.ChatReply("builder", "Builder", ConversationId, (ulong)i)));

        Assert.False(replies.Enqueue(PushMessages.ChatReply("builder", "Builder", ConversationId, 999)));
        Assert.Equal(ChatReplyPushes.MaxQueued, replies.Take().Count);
    }

    private sealed class RecordingApns : IApnsClient
    {
        public List<string> Kinds { get; } = [];

        public Task<ApnsResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct)
        {
            Kinds.Add(message.Kind);
            return Task.FromResult(new ApnsResult(ApnsOutcome.Accepted));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
