using System.Net;
using System.Text.Json;
using Fleet.Comms.Routes;
using Fleet.Conversations.Journal;
namespace Fleet.Comms.Tests;
public sealed class JournalCrossChatAuthorizationClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)42, 32).ToArray();
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls; public HttpStatusCode Status = HttpStatusCode.OK; public string Body = "{\"switch\":\"effective\",\"member\":\"member\"}";
        public bool Fail; public string? RequestBody, Token;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Token = request.Headers.Authorization!.Parameter; RequestBody = await request.Content!.ReadAsStringAsync(ct);
            if (Fail) throw new HttpRequestException("synthetic private error");
            return new(Status) { Content = new StringContent(Body) };
        }
    }
    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("file:///tmp/untrusted")]
    public async Task BlankOrInvalidUrl_FailsClosedWithoutRequest(string url)
    {
        var handler = new Handler(); using var http = new HttpClient(handler);
        var client = new JournalCrossChatAuthorizationClient(http, url, Key);
        Assert.False((await client.CheckAsync("agent1", 101, -202, 303, default)).Available); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task Request_MintsOnlyServicePurposeAndDerivesPairedIds()
    {
        var handler = new Handler(); using var http = new HttpClient(handler);
        var client = new JournalCrossChatAuthorizationClient(http, "http://authorization.test", Key);
        Assert.Equal((true, true, "member"), await client.CheckAsync("agent1", 101, -202, 303, default));
        Assert.True(JournalTokens.TryVerify(handler.Token!, JournalTokens.PurposeCrossChatAuthz, [Key], out var subject)); Assert.Equal("fleet-comms", subject);
        using var json = JsonDocument.Parse(handler.RequestBody!); Assert.Equal(-202, json.RootElement.GetProperty("chatId").GetInt64()); Assert.Equal(303, json.RootElement.GetProperty("userId").GetInt64());
        await client.CheckAsync("agent1", 101, null, null, default);
        Assert.DoesNotContain("chatId", handler.RequestBody!); Assert.DoesNotContain("userId", handler.RequestBody!);
    }
    [Theory]
    [InlineData(500, "{}", false)]
    [InlineData(200, "invalid", false)]
    [InlineData(200, "{}", false)]
    [InlineData(200, "{\"switch\":\"wrong\"}", false)]
    [InlineData(200, "{}", true)]
    public async Task UntrustedResponse_FailsClosed(int status, string body, bool fail)
    {
        var handler = new Handler { Status = (HttpStatusCode)status, Body = body, Fail = fail }; using var http = new HttpClient(handler);
        var client = new JournalCrossChatAuthorizationClient(http, "http://authorization.test", Key);
        Assert.False((await client.CheckAsync("agent1", 101, null, null, default)).Available);
    }
    [Fact]
    public async Task Off_IsDistinctFromUnavailable()
    {
        var handler = new Handler { Body = "{\"switch\":\"off\"}" }; using var http = new HttpClient(handler);
        Assert.Equal((true, false, (string?)null), await new JournalCrossChatAuthorizationClient(http, "http://authorization.test", Key).CheckAsync("agent1", 101, null, null, default));
    }
}
