using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Shared;
using static Fleet.SecretProxy.Tests.ProxyHarness;

namespace Fleet.SecretProxy.Tests;

/// <summary>
/// A GitHub App binding: the forwarder holds the private key, mints installation tokens itself, and
/// the agent's placeholder becomes the token of the installation the request is for.
/// </summary>
public sealed class GitHubAppTests : IDisposable
{
    private const string Api = "api.github.com";
    private const string Git = "github.com";
    private const string Key = "GITHUB_APP_PEM";
    private const string AppId = "424242";

    private readonly RSA _rsa = RSA.Create(2048);
    private int _mints;

    public void Dispose() => _rsa.Dispose();

    private string StoredKey => Convert.ToBase64String(Encoding.UTF8.GetBytes(_rsa.ExportRSAPrivateKeyPem()));

    private async Task<ProxyHarness> StartGitHubAsync(Action<ProxyHarness>? tweak = null)
    {
        var proxy = await StartAsync(writeState: false);
        proxy.WriteState(new SecretProxyState
        {
            Version = SecretProxyState.CurrentVersion,
            GeneratedAtUtc = proxy.Time.GetUtcNow(),
            Bindings = [new SecretProxyBinding(Key, SecretBindingRules.KindGitHubApp, [Api, Git], "Authorization", StoredKey, AppId)],
            Grants = [new SecretProxyGrant("agent1", Loopback, Key, SecretPlaceholders.Hash(proxy.PlaceholderA))],
        });

        // GitHub's listing order: a personal account first, then two organisations.
        proxy.Upstream.Respond = call =>
        {
            if (call.Uri.AbsolutePath == "/app/installations")
                return Json("""
                    [{"id":11,"account":{"login":"solo-dev","type":"User"}},
                     {"id":22,"account":{"login":"acme","type":"Organization"}},
                     {"id":33,"account":{"login":"globex","type":"Organization"}}]
                    """);
            if (call.Uri.AbsolutePath.StartsWith("/app/installations/", StringComparison.Ordinal)
                && call.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                var id = call.Uri.AbsolutePath.Split('/')[3];
                var n = Interlocked.Increment(ref _mints);
                return Json($$"""{"token":"ghs_synthetic_{{id}}_{{n}}","expires_at":"{{proxy.Time.GetUtcNow().AddHours(1):yyyy-MM-ddTHH:mm:ssZ}}"}""",
                    HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("github-ok") };
        };
        tweak?.Invoke(proxy);
        return proxy;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static UpstreamCall[] Targets(ProxyHarness proxy) =>
        [.. proxy.Upstream.Calls.Where(c => !c.Uri.AbsolutePath.StartsWith("/app/", StringComparison.Ordinal))];

    private static UpstreamCall[] Minting(ProxyHarness proxy) =>
        [.. proxy.Upstream.Calls.Where(c => c.Uri.AbsolutePath.StartsWith("/app/", StringComparison.Ordinal))];

    [Theory]
    [InlineData("/repos/acme/widgets/pulls", "22")]
    [InlineData("/repos/solo-dev/notes/issues", "11")]
    [InlineData("/repos/SOLO-DEV/notes", "11")]
    [InlineData("/orgs/globex/repos", "33")]
    [InlineData("/users/solo-dev/repos", "11")]
    [InlineData("/graphql", "33")]
    [InlineData("/user", "33")]
    [InlineData("/repos/someone-else/public-repo", "33")]
    public async Task RestRequest_GetsTheTokenOfTheOwnersInstallation_ElseThePrimary(string path, string installation)
    {
        await using var proxy = await StartGitHubAsync();
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{Api}{path}", "token " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Targets(proxy)).Header("Authorization")!;
        Assert.StartsWith($"token ghs_synthetic_{installation}_", sent);
    }

    [Theory]
    [InlineData("/acme/widgets.git/info/refs?service=git-upload-pack", "22")]
    [InlineData("/solo-dev/notes.git/git-receive-pack", "11")]
    [InlineData("/login/oauth/access_token", "33")]
    public async Task GitRequest_WithBasicAuth_GetsTheTokenOfTheOwnersInstallation(string path, string installation)
    {
        await using var proxy = await StartGitHubAsync();
        using var client = proxy.CreateClient();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + proxy.PlaceholderA));

        using var response = await client.SendAsync(Get($"https://{Git}{path}", "Basic " + basic));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Targets(proxy)).Header("Authorization")!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(sent[6..]));
        Assert.StartsWith($"x-access-token:ghs_synthetic_{installation}_", decoded);
    }

    [Theory]
    [InlineData("/graphql", "solo-dev", "11")]
    [InlineData("/repos/acme/widgets", "solo-dev", "11")]
    [InlineData("/graphql", "ACME", "22")]
    [InlineData("/graphql", "no-such-account", "33")]
    public async Task OwnerHintAfterThePlaceholder_SelectsTheInstallation_AndIsNotSentUpstream(string path, string hint, string installation)
    {
        // The agent image's gh wrapper appends ".{owner}" for commands whose requests do not name one.
        await using var proxy = await StartGitHubAsync();
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{Api}{path}", $"token {proxy.PlaceholderA}.{hint}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Targets(proxy)).Header("Authorization")!;
        Assert.Matches($@"\Atoken ghs_synthetic_{installation}_\d+\z", sent);
        Assert.DoesNotContain(hint, sent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tokens_AreCachedPerInstallation_AndReMintedBeforeTheyExpire()
    {
        await using var proxy = await StartGitHubAsync();
        using var client = proxy.CreateClient();

        using var first = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));
        using var second = await client.SendAsync(Get($"https://{Api}/repos/acme/gadgets", "Bearer " + proxy.PlaceholderA));
        using var other = await client.SendAsync(Get($"https://{Api}/repos/solo-dev/notes", "Bearer " + proxy.PlaceholderA));

        var targets = Targets(proxy);
        Assert.Equal(targets[0].Header("Authorization"), targets[1].Header("Authorization"));
        Assert.NotEqual(targets[0].Header("Authorization"), targets[2].Header("Authorization"));
        Assert.Equal(1, Minting(proxy).Count(c => c.Uri.AbsolutePath == "/app/installations"));
        Assert.Equal(2, Minting(proxy).Count(c => c.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)));

        // 56 minutes later the token has under five minutes left. The orchestrator has kept the state fresh.
        proxy.Time.Advance(TimeSpan.FromMinutes(56));
        proxy.WriteState(new SecretProxyState
        {
            Version = SecretProxyState.CurrentVersion,
            GeneratedAtUtc = proxy.Time.GetUtcNow(),
            Bindings = [new SecretProxyBinding(Key, SecretBindingRules.KindGitHubApp, [Api, Git], "Authorization", StoredKey, AppId)],
            Grants = [new SecretProxyGrant("agent1", Loopback, Key, SecretPlaceholders.Hash(proxy.PlaceholderA))],
        });
        using var later = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));

        Assert.NotEqual(targets[0].Header("Authorization"), Targets(proxy)[^1].Header("Authorization"));
        Assert.Equal(3, Minting(proxy).Count(c => c.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MintingRequests_CarryAnRs256JwtSignedByTheStoredKey_AndGoToTheGitHubApiOnly()
    {
        await using var proxy = await StartGitHubAsync();
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));

        var minting = Minting(proxy);
        Assert.Equal(2, minting.Length);
        Assert.All(minting, call => Assert.Equal("https://api.github.com", call.Uri.GetLeftPart(UriPartial.Authority)));
        Assert.Equal(HttpMethod.Get, minting[0].Method);
        Assert.Equal(HttpMethod.Post, minting[1].Method);
        Assert.Equal("/app/installations/22/access_tokens", minting[1].Uri.AbsolutePath);

        foreach (var call in minting)
        {
            var jwt = call.Header("Authorization")!["Bearer ".Length..].Split('.');
            Assert.Equal(3, jwt.Length);
            Assert.Equal("""{"alg":"RS256","typ":"JWT"}""", Encoding.UTF8.GetString(FromBase64Url(jwt[0])));
            using var payload = JsonDocument.Parse(FromBase64Url(jwt[1]));
            Assert.Equal(AppId, payload.RootElement.GetProperty("iss").GetString());
            var iat = payload.RootElement.GetProperty("iat").GetInt64();
            var exp = payload.RootElement.GetProperty("exp").GetInt64();
            Assert.Equal(proxy.Time.GetUtcNow().ToUnixTimeSeconds() - 60, iat);
            Assert.Equal(600, exp - iat);
            Assert.True(_rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), FromBase64Url(jwt[2]),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task WhenGitHubRejectsTheApp_TheRequestIsRefused_AndNothingReachesTheTarget(HttpStatusCode status)
    {
        await using var proxy = await StartGitHubAsync(p => p.Upstream.Respond = _ => new HttpResponseMessage(status));
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));
        using var again = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("secret_unavailable", response.Headers.GetValues(InjectingForwarder.ErrorHeader).Single());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, again.StatusCode);
        Assert.Empty(Targets(proxy));
        // The second request falls inside the back-off: GitHub is asked once, not once per request.
        Assert.Single(Minting(proxy));
    }

    [Theory]
    [InlineData("not-a-key", AppId)]
    [InlineData(null, AppId)]
    [InlineData("VALID", null)]
    [InlineData("VALID", " ")]
    public async Task UnusableKeyOrAppId_IsRefused_WithNoCallAnywhere(string? key, string? appId)
    {
        await using var proxy = await StartGitHubAsync();
        proxy.WriteState(new SecretProxyState
        {
            Version = SecretProxyState.CurrentVersion,
            GeneratedAtUtc = proxy.Time.GetUtcNow(),
            Bindings = [new SecretProxyBinding(Key, SecretBindingRules.KindGitHubApp, [Api, Git], "Authorization", key == "VALID" ? StoredKey : key, appId)],
            Grants = [new SecretProxyGrant("agent1", Loopback, Key, SecretPlaceholders.Hash(proxy.PlaceholderA))],
        });
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("secret_unavailable", response.Headers.GetValues(InjectingForwarder.ErrorHeader).Single());
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task RotatedKey_DropsTheOldAppsCachedTokens()
    {
        await using var proxy = await StartGitHubAsync();
        using var client = proxy.CreateClient();
        using var first = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));
        var before = Minting(proxy).Length;

        using var rotated = RSA.Create(2048);
        proxy.WriteState(new SecretProxyState
        {
            Version = SecretProxyState.CurrentVersion,
            GeneratedAtUtc = proxy.Time.GetUtcNow(),
            Bindings = [new SecretProxyBinding(Key, SecretBindingRules.KindGitHubApp, [Api, Git], "Authorization", rotated.ExportRSAPrivateKeyPem(), AppId)],
            Grants = [new SecretProxyGrant("agent1", Loopback, Key, SecretPlaceholders.Hash(proxy.PlaceholderA))],
        });
        using var second = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(before + 2, Minting(proxy).Length);
        Assert.NotEqual(Targets(proxy)[0].Header("Authorization"), Targets(proxy)[1].Header("Authorization"));
    }

    [Fact]
    public async Task NeitherThePrivateKeyNorAnInstallationToken_ReachesTheAgentOrTheLogs()
    {
        await using var proxy = await StartGitHubAsync();
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));

        var seen = await response.Content.ReadAsStringAsync()
            + string.Join("\n", response.Headers.SelectMany(h => h.Value)) + proxy.Logs.All;
        Assert.DoesNotContain("ghs_synthetic_", seen);
        Assert.DoesNotContain(StoredKey, seen);
        Assert.DoesNotContain(proxy.PlaceholderA, seen);
        Assert.Contains("key=" + Key, proxy.Logs.All);
    }

    [Fact]
    public async Task RevokedGrant_StopsGitHubAccessAtOnce_EvenWithACachedToken()
    {
        await using var proxy = await StartGitHubAsync();
        using var client = proxy.CreateClient();
        using var before = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        proxy.WriteState(new SecretProxyState
        {
            Version = SecretProxyState.CurrentVersion,
            GeneratedAtUtc = proxy.Time.GetUtcNow(),
            Bindings = [new SecretProxyBinding(Key, SecretBindingRules.KindGitHubApp, [Api, Git], "Authorization", StoredKey, AppId)],
            Grants = [],
        });
        using var after = await client.SendAsync(Get($"https://{Api}/repos/acme/widgets", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Single(Targets(proxy));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/acme/widgets/pulls", "acme")]
    [InlineData("https://api.github.com/graphql", null)]
    [InlineData("https://api.github.com/repos/bad_name!/x", null)]
    [InlineData("https://uploads.github.com/repos/acme/widgets/releases/1/assets", "acme")]
    [InlineData("https://uploads.github.com/acme/widgets", null)]
    [InlineData("https://github.com/acme/widgets.git/info/refs", "acme")]
    [InlineData("https://github.com/acme", null)]
    [InlineData("https://github.com/", null)]
    public void OwnerOf_ReadsTheAccountFromTheUrlThatIsSent(string url, string? owner) =>
        Assert.Equal(owner, GitHubAppTokens.OwnerOf(new Uri(url)));

    private static byte[] FromBase64Url(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }
}
