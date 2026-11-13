using System.Net.WebSockets;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Http;

namespace Fleet.Orchestrator.Tests.Auth;

public class ControlSocketAccessTests
{
    [Theory]
    [InlineData("https://console.example.invalid", true)]
    [InlineData("https://sibling.example.invalid", false)]
    [InlineData("http://console.example.invalid", false)]
    [InlineData("https://console.example.invalid/extra", false)]
    [InlineData("", false)]
    public void CookieUpgrade_RequiresExactOrigin(string origin, bool allowed)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("console.example.invalid");
        http.Request.Headers[OrchestratorAuth.EdgeHeader] = "1";
        http.Request.Headers["X-Forwarded-Proto"] = "https";
        http.Request.Headers.Origin = origin;
        Assert.Equal(allowed, ControlSocketAccess.SameOrigin(http.Request));
    }

    [Fact]
    public void CookieUpgrade_PreservesThePublicPort()
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("console.example.invalid", 8443);
        http.Request.Headers[OrchestratorAuth.EdgeHeader] = "1";
        http.Request.Headers["X-Forwarded-Proto"] = "https";
        http.Request.Headers.Origin = "https://console.example.invalid:8443";
        Assert.True(ControlSocketAccess.SameOrigin(http.Request));
    }

    [Theory]
    [InlineData("http://localhost", true)]
    [InlineData("http://sibling.invalid", false)]
    public async Task BrowserUpgrade_EnforcesOriginInTheRealMiddleware(string origin, bool allowed)
    {
        await using var host = await SignInHost.StartAsync(sockets: true);
        var cookie = await host.SignInBrowserAsync();
        var client = host.SocketClient();
        client.ConfigureRequest = request => {
            request.Headers["Cookie"] = CallerAuthenticator.SessionCookie + "=" + cookie;
            request.Headers["Origin"] = origin;
            request.Headers[OrchestratorAuth.EdgeHeader] = "1";
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (!allowed)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri("ws://localhost/ws"), deadline.Token));
            return;
        }
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), deadline.Token);
        Assert.Equal(WebSocketState.Open, socket.State);
        socket.Abort();
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("expiry")]
    [InlineData("unavailable")]
    public async Task LiveSocket_StopsDespiteContinuousTraffic(string reason)
    {
        await using var host = await SignInHost.StartAsync(sockets: true);
        var (cookie, device, _, access) = await host.EnrolledAppAsync();
        var client = host.SocketClient();
        client.ConfigureRequest = request => {
            request.Headers["Authorization"] = "Bearer " + access;
            request.Headers[OrchestratorAuth.EdgeHeader] = "1";
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(28));
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), deadline.Token);
        var buffer = new byte[128];
        _ = await socket.ReceiveAsync(buffer, deadline.Token);
        if (reason == "revoke")
            (await host.SendAsync(HttpMethod.Post, $"/api/auth/devices/{device}/revoke", cookie: cookie, requestHeader: true)).EnsureSuccessStatusCode();
        else if (reason == "expiry") host.Time.Advance(AuthService.AccessTokenTtl);
        else host.MemoryStore!.FailEveryOperation = true;
        try
        {
            while ((await socket.ReceiveAsync(buffer, deadline.Token)).MessageType != WebSocketMessageType.Close) { }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException && !deadline.IsCancellationRequested) { }
        Assert.False(deadline.IsCancellationRequested, "auth must end the active socket before 30 seconds");
    }
}
