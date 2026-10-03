using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
namespace Fleet.Comms.Tests;
public sealed class JournalAttachmentSendEndpointTests
{
    private const string Id = "01K00000000000000000000000";
    private static readonly byte[] Key = Enumerable.Repeat((byte)42, 32).ToArray();
    private sealed class Source : IJournalSendSource
    {
        public int Calls; public JournalSendSource? Row = new(new(Id, 0, "document", "application/pdf", null, "not_archived", "media_disabled", null, null, null, null, null), "tg:group:-202", "group", -202, true, "synthetic-file", 101);
        public Task<JournalSendSource?> FindSendSourceAsync(string subject, string boundKey, long requester, string? messageId, long? telegramMessageId, int ordinal, CancellationToken ct = default)
        { Calls++; Assert.Equal(303, requester); return Task.FromResult(Row); }
    }
    private sealed class Auth : IJournalCrossChatAuthorization
    {
        public bool Effective = true, Available = true; public string? Member = "member"; public int Calls;
        public Task<(bool Available, bool Effective, string? Member)> CheckAsync(string subject, long botId, long? chatId, long? userId, CancellationToken ct)
        { Calls++; return Task.FromResult((Available, Effective, Member)); }
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly Source Source = new(); public readonly Auth Auth = new(); private WebApplication _app = null!;
        public HttpClient Client = null!;
        public static async Task<Host> Start(bool bound = true, string kind = "private")
        {
            var host = new Host(); var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
            var bindings = new JournalTurnBindings(TimeProvider.System);
            if (bound) bindings.Put("agent1", new("epoch", 1, "bound", kind, 101, kind == "private" ? 303 : -303));
            host._app = builder.Build(); JournalAuth.Use(host._app, [Key], new());
            var scope = new JournalBindingScope(bindings, new HashSet<long>());
            var resolver = new JournalSendSourceResolver(host.Source, scope, host.Auth);
            JournalAttachmentSendEndpoints.Map(host._app, resolver);
            var content = new JournalAttachmentContentEndpoint(null, null, new(new HashSet<string>()), scope, new(), send: resolver);
            host._app.MapPost(JournalAttachmentRequest.ContentPath, content.HandleAsync);
            host._app.MapPost(JournalAttachmentSendEndpoints.CrossContentPath, content.HandleAsync);
            await host._app.StartAsync(); host.Client = host._app.GetTestClient(); return host;
        }
        public Task<HttpResponseMessage> Send(string path = JournalAttachmentSendEndpoints.CrossHandlePath, string? body = null, string purpose = "read-cross-chat")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body ?? "{\"message_id\":\"" + Id + "\"}", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new("Bearer", JournalTokens.Mint(Key, purpose, "agent1"));
            return Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await _app.DisposeAsync(); }
    }
    [Theory]
    [InlineData(JournalAttachmentSendEndpoints.CrossHandlePath)]
    [InlineData(JournalAttachmentSendEndpoints.CrossContentPath)]
    public async Task WrongPurpose_PrecedesBodyAndLookup(string path)
    {
        await using var host = await Host.Start(); using var reply = await host.Send(path, "invalid", "read");
        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode); Assert.Equal("{\"error\":\"unauthorized\"}", await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Source.Calls + host.Auth.Calls);
    }
    [Theory]
    [InlineData("{\"telegram_message_id\":5}")]
    [InlineData("{\"requester\":303}")]
    [InlineData("{\"telegramChatId\":303}")]
    [InlineData("{\"message_id\":\"bad\"}")]
    public async Task CrossBodies_AreStrictBeforeLookup(string body)
    {
        await using var host = await Host.Start(); using var reply = await host.Send(body: body);
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode); Assert.Equal(0, host.Source.Calls + host.Auth.Calls);
    }
    [Fact]
    public async Task MediaOff_HandleWorksAndForeignBotIdIsOmitted()
    {
        await using var host = await Host.Start(); host.Source.Row = host.Source.Row! with { FileIdBotId = 999 };
        using var reply = await host.Send(); Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.TryGetProperty("file_id", out _)); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("byte_size").ValueKind);
        Assert.Equal(1, host.Auth.Calls); Assert.Equal(Id, reply.Headers.GetValues("X-Journal-Message-Id").Single());
    }
    [Fact]
    public async Task DirectCalls_RecheckRevocationAndMembershipEveryTime()
    {
        await using var host = await Host.Start();
        using var first = await host.Send(); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        host.Auth.Effective = false; using var off = await host.Send(); Assert.Equal(HttpStatusCode.Unauthorized, off.StatusCode);
        host.Auth.Effective = true; host.Auth.Member = "not_member";
        using var left = await host.Send(); Assert.Equal(HttpStatusCode.Forbidden, left.StatusCode); Assert.Contains("requester_not_member", await left.Content.ReadAsStringAsync());
        Assert.Equal(3, host.Auth.Calls);
    }
    [Theory]
    [InlineData(JournalAttachmentSendEndpoints.CrossHandlePath)]
    [InlineData(JournalAttachmentSendEndpoints.CrossContentPath)]
    public async Task OffOrFailure_MasksExistingAndMissingBeforeMedia(string path)
    {
        await using var host = await Host.Start();
        foreach (var missing in new[] { false, true })
        {
            if (missing) host.Source.Row = null;
            host.Auth.Available = false; using var failed = await host.Send(path); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal("{\"error\":\"authorization_unavailable\"}", await failed.Content.ReadAsStringAsync());
            host.Auth.Available = true; host.Auth.Effective = false;
            using var off = await host.Send(path); Assert.Equal(HttpStatusCode.Unauthorized, off.StatusCode);
        }
    }
    [Fact]
    public async Task GroupBinding_IsRefusedBeforeLookup()
    {
        await using var host = await Host.Start(kind: "group"); using var reply = await host.Send();
        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode); Assert.Contains("not_private_binding", await reply.Content.ReadAsStringAsync()); Assert.Equal(0, host.Source.Calls + host.Auth.Calls);
    }
    [Fact]
    public async Task SameRoute_RefusesCrossWithoutAuthorization()
    {
        await using var host = await Host.Start(); using var reply = await host.Send(JournalAttachmentSendEndpoints.HandlePath, purpose: "read");
        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode); Assert.Contains("cross_chat_disabled", await reply.Content.ReadAsStringAsync()); Assert.Equal(0, host.Auth.Calls);
    }
}
