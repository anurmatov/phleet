using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace Fleet.Agent.Tests;
public sealed class JournalSendToolsTests
{
    private const string Id = "01K00000000000000000000000";
    private sealed class Sender : ITelegramMediaSender
    {
        public long? BotId { get; set; } = 1;
        public int Sends, Uploads; public Exception? FailFile, FailUpload;
        public async Task<JournalMessage> SendUploadAsync(long chat, bool photo, HttpContent content, string fileName, CancellationToken ct)
        { Uploads++; if (FailUpload is not null) throw FailUpload; await content.CopyToAsync(Stream.Null, ct); return Sent(chat); }
        public Task<JournalMessage> SendFileIdAsync(long chat, string kind, string id, CancellationToken ct)
        { Sends++; if (FailFile is not null) throw FailFile; return Task.FromResult(Sent(chat)); }
        private static JournalMessage Sent(long chat) => new() { BotId = 1, ChatId = chat, ChatType = "private", MessageId = 99, SenderKind = JournalSenderKind.Agent, SenderId = "1", Media = [new(JournalAttachmentKind.Document, "text/plain", 3, "file.txt", "unique", FileId: "private-file")] };
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls, CrossHandles; public bool Cross, Drift, BadDigest, Archived = true, FileId = true;
        public Action? OnFirstHandle;
        public string Kind = "document";
        public HttpStatusCode? SecondStatus, ContentStatus;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            if (r.RequestUri!.AbsolutePath.EndsWith("/content"))
            {
                if (ContentStatus is { } status) return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"not_found\"}") });
                var bytes = Encoding.UTF8.GetBytes("abc");
                var content = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                content.Headers.Add("X-Journal-Message-Id", Id); content.Headers.Add("X-Journal-Ordinal", "0"); content.Headers.Add("X-Journal-Kind", Kind);
                content.Headers.Add("X-Journal-Sha256", BadDigest ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)));
                return Task.FromResult(content);
            }
            if (Cross && !r.RequestUri.AbsolutePath.Contains("cross-chat"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"source_denied\",\"reason\":\"cross_chat_disabled\"}") });
            if (Cross) CrossHandles++;
            if (CrossHandles > 1 && SecondStatus is { } second) return Task.FromResult(new HttpResponseMessage(second) { Content = new StringContent(second == HttpStatusCode.Unauthorized ? "{\"error\":\"unauthorized\"}" : "{\"error\":\"source_denied\",\"reason\":\"requester_not_member\"}") });
            OnFirstHandle?.Invoke(); OnFirstHandle = null;
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                kind = Drift && CrossHandles > 1 ? "photo" : Kind, mime_type = "text/plain", byte_size = 3,
                file_name = "file.txt", archived = Archived, file_id = FileId ? "private-file" : null, source = Cross ? "cross" : "same" })) };
            reply.Headers.Add("X-Journal-Message-Id", Id);
            return Task.FromResult(reply);
        }
    }
    private sealed class Rig : IDisposable
    {
        public readonly Handler Handler = new(); public readonly Sender Sender = new();
        public readonly RunningTask Owner = new() { Id = 1, Description = "synthetic", StartedAt = DateTimeOffset.UtcNow, Cts = new(), IsSessionTask = true, UserId = 10,
            Identity = new ConversationIdentity { PrincipalId = "synthetic", Role = PrincipalRole.Owner, ChannelId = ChannelIds.Telegram, ConversationId = "synthetic", SubmissionId = "synthetic", Attempt = 1 } };
        public readonly AllowlistHolder Allowlist = new(Microsoft.Extensions.Options.Options.Create(new TelegramOptions { AllowedUserIds = [10] }));
        public readonly JournalOptions Options = new() { IngestToken = "cj1.ingest.agent1.signature", ReadToken = "cj1.read.agent1.signature", SendEnabled = true, CrossChatEnabled = true, CrossChatToken = "cj1.read-cross-chat.agent1.signature" };
        public readonly JournalFilesGate Gate = new();
        private readonly HttpClient _http; public readonly TurnBindingPublisher Binding; public readonly JournalSendTools Tools;
        public Rig()
        {
            _http = new(Handler) { BaseAddress = new("http://journal.test") };
            var client = new JournalHttpClient(_http, "ingest", readToken: "read", crossChatToken: "cross");
            Binding = new(client, NullLogger<TurnBindingPublisher>.Instance);
            Binding.ObserveChat(10, 1, "private"); Binding.BeginTurn(10, TaskSource.UserMessage);
            Tools = new(client, Binding, Gate, Allowlist, Microsoft.Extensions.Options.Options.Create(Options), _ => Owner,
                () => Sender, null, NullLogger<JournalSendTools>.Instance);
        }
        public async Task<JsonElement> Run() => JsonDocument.Parse(await Tools.SendAsync(new(Id), default)).RootElement.Clone();
        public void Dispose() { Binding.Dispose(); Gate.Dispose(); Owner.Cts.Dispose(); _http.Dispose(); }
    }
    [Fact]
    public void RegisteredSchema_HasOnlyThreeArgumentsAndNoExtraProperties()
    {
        using var rig = new Rig(); var tool = rig.Tools.CreateTool().ProtocolTool;
        Assert.Equal("send_attachment", tool.Name);
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["message_id", "ordinal", "telegram_message_id"], tool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.False(tool.Annotations!.ReadOnlyHint); Assert.True(tool.Annotations.DestructiveHint);
        Assert.False(tool.Annotations.IdempotentHint); Assert.True(tool.Annotations.OpenWorldHint);
    }
    [Theory]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.Bridge)]
    [InlineData(TaskSource.CheckIn)]
    [InlineData(TaskSource.DebouncedGroupBatch)]
    public async Task NonHuman_DeniedWithoutHttp(TaskSource source)
    {
        using var rig = new Rig();
        // Source init-only: inject a separate turn through the production delegate boundary.
        var owner = new RunningTask { Id = 2, Description = "synthetic", StartedAt = DateTimeOffset.UtcNow, Cts = new(), IsSessionTask = true, UserId = 10, Source = source, Identity = rig.Owner.Identity };
        using var http = new HttpClient(rig.Handler) { BaseAddress = new("http://journal.test") };
        var tools = new JournalSendTools(new(http, "ingest", readToken: "read"), rig.Binding, rig.Gate, rig.Allowlist, Options.Create(rig.Options), _ => owner, () => rig.Sender, null, NullLogger<JournalSendTools>.Instance);
        Assert.Contains("not_a_human_request", await tools.SendAsync(new(Id), default)); Assert.Equal(0, rig.Handler.Calls); owner.Cts.Dispose();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameAndCross_FileIdSuccess_IsRedacted(bool cross)
    {
        using var rig = new Rig(); rig.Handler.Cross = cross;
        var result = await rig.Run();
        Assert.Equal("sent", result.GetProperty("outcome").GetString()); Assert.Equal("file_id", result.GetProperty("path").GetString());
        Assert.Equal(1, rig.Sender.Sends); Assert.Equal(0, rig.Sender.Uploads);
        Assert.Equal(cross ? 2 : 0, rig.Handler.CrossHandles);
        Assert.DoesNotContain("private-file", result.GetRawText()); Assert.False(result.TryGetProperty("chat_id", out _));
    }
    [Fact]
    public async Task StreamSuccess_VerifiesBytes()
    { using var rig = new Rig(); rig.Handler.FileId = false; Assert.Equal("sent", (await rig.Run()).GetProperty("outcome").GetString()); Assert.Equal(1, rig.Sender.Uploads); }
    [Fact]
    public async Task DigestMismatch_IsNotSent()
    { using var rig = new Rig(); rig.Handler.FileId = false; rig.Handler.BadDigest = true; Assert.Equal("integrity_failed", (await rig.Run()).GetProperty("outcome").GetString()); }
    [Fact]
    public async Task Drift_StopsBeforeTelegram()
    { using var rig = new Rig(); rig.Handler.Cross = true; rig.Handler.Drift = true; Assert.Equal("source_changed", (await rig.Run()).GetProperty("outcome").GetString()); Assert.Equal(0, rig.Sender.Sends + rig.Sender.Uploads); }
    [Theory]
    [InlineData(0, "ambiguous")]
    [InlineData(500, "ambiguous")]
    [InlineData(403, "destination_unreachable")]
    [InlineData(429, "rate_limited")]
    [InlineData(400, "telegram_rejected")]
    public async Task TelegramFailure_DoesNotRetry(int status, string expected)
    { using var rig = new Rig(); rig.Sender.FailFile = new JournalTelegramException(status); Assert.Equal(expected, (await rig.Run()).GetProperty("outcome").GetString()); Assert.Equal(1, rig.Sender.Sends); Assert.Equal(0, rig.Sender.Uploads); }
    [Fact]
    public async Task DefiniteInvalidFile_AllowsOnlyOneStreamFallback()
    { using var rig = new Rig(); rig.Sender.FailFile = new JournalTelegramException(400, true); Assert.Equal("sent", (await rig.Run()).GetProperty("outcome").GetString()); Assert.Equal(1, rig.Sender.Sends); Assert.Equal(1, rig.Sender.Uploads); }
    [Fact]
    public async Task AllowlistRevokedAfterHandle_DeniesSend()
    { using var rig = new Rig(); rig.Handler.OnFirstHandle = () => rig.Allowlist.Apply(new([], [10], [], [])); Assert.Equal("not_a_human_request", (await rig.Run()).GetProperty("outcome").GetString()); Assert.Equal(0, rig.Sender.Sends + rig.Sender.Uploads); }
    [Fact]
    public async Task SwitchOff_StopsAtSameRouteDenial()
    { using var rig = new Rig(); rig.Options.CrossChatEnabled = false; rig.Handler.Cross = true; Assert.Equal("source_denied", (await rig.Run()).GetProperty("error").GetString()); Assert.Equal(0, rig.Handler.CrossHandles); Assert.Equal(0, rig.Sender.Sends); }
    [Theory]
    [InlineData(400, "telegram_rejected")]
    [InlineData(0, "ambiguous")]
    [InlineData(-1, "cancelled")]
    [InlineData(-2, "source_unavailable")]
    public async Task UploadFailure_NeverRetries(int status, string expected)
    {
        using var rig = new Rig(); rig.Handler.FileId = false;
        rig.Sender.FailUpload = new JournalTelegramException(status);
        Assert.Equal(expected, (await rig.Run()).GetProperty("outcome").GetString());
        Assert.Equal(1, rig.Sender.Uploads); Assert.Equal(0, rig.Sender.Sends);
    }
    [Theory]
    [InlineData(true, 401)]
    [InlineData(false, 401)]
    [InlineData(true, 403)]
    [InlineData(false, 403)]
    public async Task RevocationOnSecondHandle_StopsBothPaths(bool fileId, int status)
    {
        using var rig = new Rig(); rig.Handler.Cross = true; rig.Handler.FileId = fileId; rig.Handler.SecondStatus = (HttpStatusCode)status;
        var result = await rig.Run(); Assert.Contains("source_denied", result.GetRawText());
        Assert.Equal(0, rig.Sender.Sends + rig.Sender.Uploads);
    }
    [Fact]
    public async Task DeletedBeforeContent_IsNotFoundWithoutSend()
    {
        using var rig = new Rig(); rig.Handler.FileId = false; rig.Handler.ContentStatus = HttpStatusCode.NotFound;
        Assert.Contains("not_found", (await rig.Run()).GetRawText()); Assert.Equal(0, rig.Sender.Sends + rig.Sender.Uploads);
    }
    [Fact]
    public async Task StreamPhoto400_DoesNotFallBackToDocument()
    {
        using var rig = new Rig(); rig.Handler.FileId = false; rig.Handler.Kind = "photo"; rig.Sender.FailUpload = new JournalTelegramException(400);
        Assert.Equal("telegram_rejected", (await rig.Run()).GetProperty("outcome").GetString()); Assert.Equal(1, rig.Sender.Uploads);
    }
    [Fact]
    public async Task SharedGate_BlocksBothToolsBeforeHttpAndCancellationReleasesWaiters()
    {
        using var rig = new Rig(); using var http = new HttpClient(rig.Handler) { BaseAddress = new("http://journal.test") };
        var root = Path.Combine(Path.GetTempPath(), "send-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fetch = new JournalFilesTools(new(http, "ingest", readToken: "read"), rig.Binding, new(root), new(), NullLogger<JournalFilesTools>.Instance, gate: rig.Gate);
            await rig.Gate.Serial.WaitAsync(); using var cancel = new CancellationTokenSource();
            var sending = rig.Tools.SendAsync(new(Id), cancel.Token); var fetching = fetch.FetchAsync(new(Id), cancel.Token);
            Assert.False(sending.IsCompleted); Assert.False(fetching.IsCompleted); Assert.Equal(0, rig.Handler.Calls);
            cancel.Cancel(); Assert.Contains("cancelled", await sending); await fetching; rig.Gate.Serial.Release();
            Assert.Equal("sent", (await rig.Run()).GetProperty("outcome").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task RateLimit_ReturnsRetryDelayWithoutRetryOrRawDescription()
    {
        using var rig = new Rig(); rig.Sender.FailFile = new JournalTelegramException(429, retryAfter: 31);
        var result = await rig.Run(); Assert.Equal("retry_after=31", result.GetProperty("reason").GetString());
        Assert.Equal(1, rig.Sender.Sends); Assert.Equal(0, rig.Sender.Uploads);
    }
    [Fact]
    public async Task BotMismatch_DoesNoHttp()
    { using var rig = new Rig(); rig.Sender.BotId = 2; Assert.Equal("bot_mismatch", (await rig.Run()).GetProperty("outcome").GetString()); Assert.Equal(0, rig.Handler.Calls); }
}
