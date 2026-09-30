using System.Net;
using Fleet.Conversations.Journal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

public sealed class JournalMediaHealthTests
{
    [Fact]
    public async Task Recovery_requires_confirmed_anonymous_denial()
    {
        var denied = false;
        using var gate = new JournalMediaHealth(new FakeBucket(), NullLogger.Instance,
            anonymousProbe: _ => Task.FromResult(denied));
        Assert.False(await gate.ProbeOnceAsync(default));
        Assert.Equal("degraded", gate.MediaState);
        denied = true;
        Assert.True(await gate.ProbeOnceAsync(default));
        denied = false;
        Assert.False(await gate.ProbeOnceAsync(default));
    }

    [Fact]
    public async Task Anonymous_transport_failure_never_enables_media()
    {
        using var gate = new JournalMediaHealth(new FakeBucket(), NullLogger.Instance,
            anonymousProbe: _ => throw new HttpRequestException());
        Assert.False(await gate.ProbeOnceAsync(default));
        Assert.Equal("degraded", gate.MediaState);
    }

    [Theory]
    [InlineData(403, true)]
    [InlineData(200, false)]
    [InlineData(404, false)]
    [InlineData(302, false)]
    public async Task Only_anonymous_403_is_safe(int status, bool safe)
    {
        using var http = new HttpClient(new ResponseHandler((HttpStatusCode)status));
        if (safe)
            Assert.True(await JournalAnonymousProbe.CheckAsync("http://localhost", "synthetic", http));
        else
            await Assert.ThrowsAsync<JournalAnonymousAccessException>(() =>
                JournalAnonymousProbe.CheckAsync("http://localhost", "synthetic", http));
    }

    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
