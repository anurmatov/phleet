using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Fleet.Shared;
using static Fleet.SecretProxy.Tests.ProxyHarness;

namespace Fleet.SecretProxy.Tests;

/// <summary>
/// A secret goes to its bound host, in its bound header, for its own agent, and nowhere else. Every
/// refusal here happens with no upstream call at all.
/// </summary>
public sealed class WrongDestinationTests
{
    private static async Task<string> AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, response.Headers.GetValues(InjectingForwarder.ErrorHeader).Single());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(code, body);
        return body;
    }

    [Fact]
    public async Task PlaceholderSentToAHostBoundToAnotherSecret_IsRefused()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();

        // Key A is bound to host A only. Host B is a bound host too, but for key B.
        using var response = await client.SendAsync(Get($"https://{HostB}/collect", "Bearer " + proxy.PlaceholderA));

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "secret_destination_mismatch");
        Assert.Empty(proxy.Upstream.Calls);
    }

    // The handshake asks for host A; the request then names another host. A client library sets both
    // from one URL, so these go over a raw connection.
    [Theory]
    [InlineData(HostB)]
    [InlineData("unbound.example.test")]
    [InlineData("evil.example.test")]
    [InlineData(HostA + ":8443")]
    [InlineData("127.0.0.1")]
    public async Task HostHeaderThatDiffersFromTheTlsServerName_IsRefused(string hostHeader)
    {
        await using var proxy = await StartAsync();

        var response = await proxy.RawAsync(HostA, hostHeader, "Bearer " + proxy.PlaceholderA);

        Assert.StartsWith("HTTP/1.1 421", response);
        Assert.Contains(InjectingForwarder.ErrorHeader + ": secret_host_mismatch", response);
        Assert.DoesNotContain(SecretA, response);
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task HostHeaderMatchingTheTlsServerName_OverARawConnection_IsRelayed()
    {
        await using var proxy = await StartAsync();

        var response = await proxy.RawAsync(HostA, HostA, "Bearer " + proxy.PlaceholderA);

        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Equal("Bearer " + SecretA, Assert.Single(proxy.Upstream.Calls).Header("Authorization"));
    }

    [Theory]
    [InlineData("unbound.example.test")]
    [InlineData("")]
    public async Task TlsHandshakeForAnUnboundOrMissingServerName_Fails(string serverName)
    {
        await using var proxy = await StartAsync();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, proxy.Port);
        await using var tls = new SslStream(new NetworkStream(socket), false, (_, _, _, _) => true);

        await Assert.ThrowsAnyAsync<Exception>(() => tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = serverName,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }));
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task CertificateServedForABoundHost_NamesExactlyThatHost_AndChainsToTheLocalCa()
    {
        await using var proxy = await StartAsync();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, proxy.Port);
        System.Security.Cryptography.X509Certificates.X509Certificate2? served = null;
        await using var tls = new SslStream(new NetworkStream(socket), false, (_, certificate, _, _) =>
        {
            served = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate!);
            return true;
        });
        await tls.AuthenticateAsClientAsync(HostA);

        Assert.NotNull(served);
        Assert.True(served!.MatchesHostname(HostA));
        Assert.False(served.MatchesHostname(HostB));
        Assert.Equal(proxy.Ca.SubjectName.RawData, served.IssuerName.RawData);
    }

    [Theory]
    [InlineData("/v1/items?token={0}")]
    [InlineData("/bot{0}/getMe")]
    public async Task PlaceholderInTheUrl_IsRefused(string target)
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}{string.Format(target, proxy.PlaceholderA)}"));

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "secret_slot_mismatch");
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Theory]
    [InlineData("X-Debug")]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    public async Task PlaceholderInAHeaderOtherThanItsBindings_IsRefused(string header)
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var request = Get($"https://{HostA}/v1/items");
        request.Headers.TryAddWithoutValidation(header, proxy.PlaceholderA);

        using var response = await client.SendAsync(request);

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "secret_slot_mismatch");
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task PlaceholderInItsHeaderAndAlsoElsewhere_IsRefusedAsAWhole()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var request = Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA);
        request.Headers.TryAddWithoutValidation("X-Copy", proxy.PlaceholderA);

        using var response = await client.SendAsync(request);

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "secret_slot_mismatch");
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task PlaceholderInTheBody_IsRelayedAsIs_AndNeverBecomesTheSecret()
    {
        // A secret written into a body could be posted somewhere readable and fetched back.
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{HostA}/v1/notes")
        {
            Content = new StringContent($$"""{"text":"{{proxy.PlaceholderA}}"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + proxy.PlaceholderA);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(proxy.Upstream.Calls);
        Assert.Contains(proxy.PlaceholderA, call.BodyText);
        Assert.DoesNotContain(SecretA, call.BodyText);
        Assert.Equal("Bearer " + SecretA, call.Header("Authorization"));
    }

    [Fact]
    public async Task UpstreamRedirect_IsReturnedToTheAgent_AndNeverFollowed()
    {
        await using var proxy = await StartAsync();
        proxy.Upstream.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example.test/capture");
            return response;
        };
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://evil.example.test/capture", response.Headers.Location!.AbsoluteUri);
        var call = Assert.Single(proxy.Upstream.Calls);
        Assert.Equal(HostA, call.Uri.Host);
    }

    [Fact]
    public async Task PlaceholderPresentedFromAnotherAddress_IsRefusedLikeAnUnknownOne()
    {
        // The same grant, but it belongs to an agent at a different address on the secrets network:
        // a placeholder that leaked into a chat or a memory cannot be replayed by another agent.
        var harness = await StartAsync(writeState: false);
        await using var proxy = harness;
        var state = proxy.DefaultState();
        proxy.WriteState(state with
        {
            Grants = [.. state.Grants.Select(g => g with { SourceIp = "10.213.96.77" })],
        });
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        var body = await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "secret_grant_unknown");
        Assert.DoesNotContain("10.213.96.77", body);
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task UnknownPlaceholder_IsRefused_AndNeverForwardedUpstream()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + SecretPlaceholders.Create()));

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "secret_grant_unknown");
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task ProtocolUpgrade_IsRefused()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var request = Get($"https://{HostA}/socket", "Bearer " + proxy.PlaceholderA);
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");

        using var response = await client.SendAsync(request);

        await AssertRefusedAsync(response, HttpStatusCode.NotImplemented, "secret_request_unsupported");
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task PlainHttpOnTheForwarderPort_GetsNoResponseFromTheForwarder()
    {
        await using var proxy = await StartAsync();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, proxy.Port);
        await socket.SendAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {HostA}\r\nAuthorization: Bearer {proxy.PlaceholderA}\r\n\r\n"));
        socket.ReceiveTimeout = 5000;
        var buffer = new byte[256];

        var read = 0;
        try { read = socket.Receive(buffer); } catch (SocketException) { }

        Assert.DoesNotContain("HTTP/1.1 200", Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Empty(proxy.Upstream.Calls);
    }
}
