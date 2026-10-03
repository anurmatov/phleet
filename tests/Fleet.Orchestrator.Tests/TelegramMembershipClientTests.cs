using System.Net;
using System.Text;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

public sealed class TelegramMembershipClientTests
{
    [Theory]
    [InlineData("creator", null, "member")]
    [InlineData("administrator", null, "member")]
    [InlineData("member", null, "member")]
    [InlineData("restricted", true, "member")]
    [InlineData("restricted", false, "not_member")]
    [InlineData("restricted", null, "not_member")]
    [InlineData("left", null, "not_member")]
    [InlineData("kicked", null, "not_member")]
    [InlineData("unexpected", null, "unverified")]
    public async Task MembershipStatus_FailsClosed(string status, bool? isMember, string expected)
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("getMe")
            ? "{\"ok\":true,\"result\":{\"id\":101}}"
            : $"{{\"ok\":true,\"result\":{{\"status\":\"{status}\"{(isMember is null ? "" : $",\"is_member\":{isMember.ToString()!.ToLowerInvariant()}")}}}}}");
        using var client = new TelegramMembershipClient(handler, new Uri("http://localhost/"));
        Assert.Equal(expected, await client.CheckAsync("synthetic", 101, -202, 303, default));
        Assert.Equal(expected, await client.CheckAsync("synthetic", 101, -202, 303, default));
        Assert.Equal(1, handler.MeCalls);
        Assert.Equal(2, handler.MemberCalls); // membership is never cached
    }

    [Fact]
    public async Task WrongBot_StopsBeforeMembershipRequest()
    {
        var handler = new Handler(_ => "{\"ok\":true,\"result\":{\"id\":102}}");
        using var client = new TelegramMembershipClient(handler, new Uri("http://localhost/"));
        Assert.Equal("unverified", await client.CheckAsync("synthetic", 101, -202, 303, default));
        Assert.Equal(0, handler.MemberCalls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"ok\":false}")]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":true,\"result\":null}")]
    public async Task BrokenResponse_IsUnverified(string json)
    {
        using var client = new TelegramMembershipClient(new Handler(_ => json), new Uri("http://localhost/"));
        Assert.Equal("unverified", await client.CheckAsync("synthetic", 101, -202, 303, default));
    }

    [Fact]
    public async Task ExcessConcurrentRequest_IsUnverifiedWithoutHttp()
    {
        var handler = new BlockingHandler();
        using var client = new TelegramMembershipClient(handler, new Uri("http://localhost/"));
        var active = Enumerable.Range(0, 8).Select(i => client.CheckAsync($"synthetic-{i}", 101, -202, 303, default)).ToArray();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("unverified", await client.CheckAsync("ninth", 101, -202, 303, default));
        Assert.Equal(8, handler.Count);
        handler.Release.SetResult();
        Assert.All(await Task.WhenAll(active), value => Assert.Equal("unverified", value));
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Count;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Count) == 8) Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    internal sealed class Handler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        public int MeCalls; public int MemberCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getMe")) MeCalls++; else MemberCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
        }
    }
}
