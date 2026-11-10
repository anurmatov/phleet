using System.Net;
using System.Text;
using Fleet.Shared;
using static Fleet.SecretProxy.Tests.ProxyHarness;

namespace Fleet.SecretProxy.Tests;

/// <summary>What a granted agent's ordinary request looks like once it leaves the forwarder.</summary>
public sealed class ForwardingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BearerPlaceholder_IsReplacedByTheSecret_AndNothingElseChanges(bool http2)
    {
        await using var proxy = await StartAsync();
        proxy.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":7}""", Encoding.UTF8, "application/json"),
            Headers = { { "X-Upstream", "yes" } },
        };
        using var client = proxy.CreateClient(http2);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{HostA}/v1/items?page=2&q=a%2Fb")
        {
            Content = new StringContent("""{"name":"synthetic"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + proxy.PlaceholderA);
        request.Headers.TryAddWithoutValidation("User-Agent", "synthetic-client/1.0");
        request.Headers.TryAddWithoutValidation("X-Custom", "kept");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("""{"id":7}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("yes", response.Headers.GetValues("X-Upstream").Single());
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var call = Assert.Single(proxy.Upstream.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal($"https://{HostA}/v1/items?page=2&q=a%2Fb", call.Uri.AbsoluteUri);
        Assert.Equal("Bearer " + SecretA, call.Header("Authorization"));
        Assert.Equal("synthetic-client/1.0", call.Header("User-Agent"));
        Assert.Equal("kept", call.Header("X-Custom"));
        Assert.Equal("""{"name":"synthetic"}""", call.BodyText);
        Assert.Equal("20", call.Header("Content-Length"));
    }

    [Fact]
    public async Task BasicAuthPassword_IsReplaced_AndTheUserNameIsKept()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + proxy.PlaceholderA));

        using var response = await client.SendAsync(Get($"https://{HostA}/acme/repo.git/info/refs?service=git-upload-pack", "Basic " + basic));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(proxy.Upstream.Calls).Header("Authorization")!;
        Assert.StartsWith("Basic ", sent);
        Assert.Equal("x-access-token:" + SecretA, Encoding.UTF8.GetString(Convert.FromBase64String(sent[6..])));
    }

    [Fact]
    public async Task NamedHeaderBinding_ReplacesThePlaceholderInThatHeader()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var request = Get($"https://{HostB}/status");
        request.Headers.TryAddWithoutValidation("X-Api-Key", proxy.PlaceholderB);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SecretB, Assert.Single(proxy.Upstream.Calls).Header("X-Api-Key"));
    }

    [Fact]
    public async Task RequestWithoutAPlaceholder_IsRelayedUnchanged()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();

        using var anonymous = await client.SendAsync(Get($"https://{HostA}/public"));
        using var own = await client.SendAsync(Get($"https://{HostA}/public", "Bearer the-callers-own-token"));

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var calls = proxy.Upstream.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Null(calls[0].Header("Authorization"));
        Assert.Equal("Bearer the-callers-own-token", calls[1].Header("Authorization"));
    }

    [Fact]
    public async Task StreamedBodyWithoutALength_IsRelayedWhole()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{HostA}/upload")
        {
            Content = new StreamContent(new MemoryStream(payload)),
        };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + proxy.PlaceholderA);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(proxy.Upstream.Calls);
        Assert.Equal(payload, call.Body);
        Assert.Equal("Bearer " + SecretA, call.Header("Authorization"));
    }

    [Fact]
    public async Task UpstreamErrorStatusAndBody_ReachTheAgentAsSent()
    {
        await using var proxy = await StartAsync();
        proxy.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"message":"Bad credentials"}"""),
        };
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/me", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"message":"Bad credentials"}""", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains(InjectingForwarder.ErrorHeader));
    }

    [Fact]
    public async Task HopByHopAndAltSvcResponseHeaders_AreNotRelayed()
    {
        await using var proxy = await StartAsync();
        proxy.Upstream.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            response.Headers.TryAddWithoutValidation("Alt-Svc", "h3=\":443\"");
            response.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
            response.Headers.TryAddWithoutValidation("X-Kept", "1");
            return response;
        };
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/"));

        Assert.False(response.Headers.Contains("Alt-Svc"));
        Assert.False(response.Headers.Contains("Keep-Alive"));
        Assert.True(response.Headers.Contains("X-Kept"));
    }

    [Fact]
    public async Task SamePlaceholderTwiceInTheHeader_IsReplacedBothTimes()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var request = Get($"https://{HostB}/status");
        request.Headers.TryAddWithoutValidation("X-Api-Key", $"{proxy.PlaceholderB};{proxy.PlaceholderB}");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{SecretB};{SecretB}", Assert.Single(proxy.Upstream.Calls).Header("X-Api-Key"));
    }
}
