using System.Net;
using System.Text.Json;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;

namespace Fleet.Journal.Client.Tests;

public sealed class TurnBindingHttpClientTests
{
    [Fact]
    public async Task Put_UsesIngestHeaderAndCamelCaseState()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        var client = new JournalHttpClient(http, "synthetic-token");
        var result = await client.PutTurnBindingAsync(new("epoch1", 2, "bound", "private", 7001, 101), default);
        Assert.Equal(204, result.Status);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("/journal/v1/turn-binding", handler.Path);
        Assert.Equal("Bearer synthetic-token", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("epoch1", body.RootElement.GetProperty("epoch").GetString());
        Assert.Equal(101, body.RootElement.GetProperty("chatId").GetInt64());
        Assert.DoesNotContain("synthetic-token", handler.Body);
    }

    [Fact]
    public async Task Put_UnboundOmitsChatFields_AndPropagatesStale()
    {
        var handler = new Handler { Status = HttpStatusCode.Conflict, ResponseBody = "{\"error\":\"stale\"}" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        var client = new JournalHttpClient(http, "synthetic-token");
        var result = await client.PutTurnBindingAsync(new("epoch1", 3, "unbound"), default);
        Assert.Equal(409, result.Status);
        Assert.Equal("stale", result.Error);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());
        Assert.Equal(TimeSpan.FromSeconds(2), JournalHttpClient.BindingTimeout);
    }

    [Fact]
    public async Task Put_HangingHandler_IsStoppedByTheTwoSecondBudget()
    {
        using var http = new HttpClient(new HangingHandler()) { BaseAddress = new Uri("http://journal.test") };
        var client = new JournalHttpClient(http, "synthetic-token");
        var result = await client.PutTurnBindingAsync(new("epoch1", 1, "unbound"), default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, result.Status);
        Assert.Equal("timeout", result.Error);
    }

    [Theory]
    [InlineData(false, false, "/journal/v1/attachments/send-handle", "read-token")]
    [InlineData(false, true, "/journal/v1/attachments/send-content", "read-token")]
    [InlineData(true, false, "/journal/v1/attachments/cross-chat/send-handle", "cross-token")]
    [InlineData(true, true, "/journal/v1/attachments/cross-chat/content", "cross-token")]
    public async Task Send_UsesDedicatedPathAndPurposeWithoutMarker(bool cross, bool content, string path, string token)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest-token", readToken: "read-token", crossChatToken: "cross-token");
        using var response = await client.OpenSendAsync(new() { MessageId = "01ARZ3NDEKTSV4RRFFQ69G5FAV" }, cross, content, default);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(path, handler.Path);
        Assert.Equal("Bearer " + token, handler.Authorization);
        Assert.False(handler.SendMarker);
    }

    [Fact]
    public async Task Fetch_KeepsOriginalContentPathAndReadPurpose()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest-token", readToken: "read-token");
        using var response = await client.OpenAttachmentContentAsync(new() { TelegramMessageId = 5 }, default);
        Assert.Equal("/journal/v1/attachments/content", handler.Path);
        Assert.Equal("Bearer read-token", handler.Authorization);
        Assert.False(handler.SendMarker);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.NoContent;
        public string ResponseBody = "";
        public HttpMethod? Method;
        public string? Path, Authorization, Body;
        public bool SendMarker;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SendMarker = request.Headers.Contains("X-Journal-Send");
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization!.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(Status) { Content = new StringContent(ResponseBody) };
        }
    }
}
