using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services.Push;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.Push;

/// <summary>
/// The provider token and the request Apple's push service receives. No network: the HTTP handler
/// is a double, and the key is generated for each test.
/// </summary>
public class ApnsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private const string DeviceToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static (ECDsa Key, string Base64) NewKey(ECCurve? curve = null)
    {
        var key = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem())));
    }

    private static byte[] Decode(string part)
    {
        var s = part.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    // ── The provider token ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AToken_NamesTheKeyAndTheTeam_AndVerifiesAgainstTheKey()
    {
        var (key, base64) = NewKey();
        using var token = new ApnsProviderToken(base64, " KEYID12345 ", " TEAMID1234 ");

        var parts = token.Current(Now).Split('.');

        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(Decode(parts[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("KEYID12345", header.RootElement.GetProperty("kid").GetString());
        using var claims = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("TEAMID1234", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(Now.ToUnixTimeSeconds(), claims.RootElement.GetProperty("iat").GetInt64());
        // r || s, 64 bytes: the form a JWT carries. A DER signature here is refused by Apple.
        var signature = Decode(parts[2]);
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void AToken_IsKeptForFiftyMinutes_ThenReplaced()
    {
        var (_, base64) = NewKey();
        using var token = new ApnsProviderToken(base64, "KEYID12345", "TEAMID1234");

        var first = token.Current(Now);

        // Apple throttles a provider that mints more often than every twenty minutes.
        Assert.Equal(first, token.Current(Now.AddMinutes(49)));
        // And rejects a token older than an hour.
        Assert.NotEqual(first, token.Current(Now.AddMinutes(50)));
    }

    [Fact]
    public void AToken_IsReplacedWhenInvalidated_OrWhenTheClockGoesBackwards()
    {
        var (_, base64) = NewKey();
        using var token = new ApnsProviderToken(base64, "KEYID12345", "TEAMID1234");
        var first = token.Current(Now);

        token.Invalidate();
        var second = token.Current(Now.AddSeconds(1));
        Assert.NotEqual(first, second);
        Assert.Equal(second, token.Current(Now.AddMinutes(10)));

        // A clock that has gone back to before the token was issued: its age can no longer be
        // trusted, so it is replaced rather than kept past Apple's hour.
        Assert.NotEqual(second, token.Current(Now.AddMinutes(-5)));
    }

    [Fact]
    public void TheKey_IsReadFromBase64OfTheFile_OrFromThePemText()
    {
        var (key, base64) = NewKey();
        using var fromBase64 = ApnsProviderToken.ImportKey(base64);
        using var fromPem = ApnsProviderToken.ImportKey(key.ExportPkcs8PrivateKeyPem());

        Assert.Equal(256, fromBase64.KeySize);
        Assert.Equal(256, fromPem.KeySize);
    }

    [Theory]
    [InlineData("not base64 at all !!")]
    [InlineData("c3ludGhldGljLW5vdC1hLWtleQ==")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nc3ludGhldGlj\n-----END PRIVATE KEY-----")]
    public void AnUnusableKey_IsRefused_WithoutRepeatingAnyOfIt(string value)
    {
        var ex = Assert.Throws<FormatException>(() => ApnsProviderToken.ImportKey(value));

        Assert.DoesNotContain("c3ludGhldGlj", ex.Message);
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void AKeyOnAnotherCurve_IsRefused()
    {
        var (_, base64) = NewKey(ECCurve.NamedCurves.nistP384);

        Assert.Throws<FormatException>(() => ApnsProviderToken.ImportKey(base64));
    }

    [Fact]
    public void AnotherCurveOfTheSameSize_IsRefused()
    {
        // 256 bits but not P-256: it imports, and Apple refuses every token it signs.
        var (_, base64) = NewKey(ECCurve.NamedCurves.brainpoolP256r1);

        var ex = Assert.Throws<FormatException>(() => ApnsProviderToken.ImportKey(base64));
        Assert.Equal("the APNs key is not a P-256 key", ex.Message);
    }

    [Fact]
    public void APublicKey_IsRefused_BecauseItCannotSign()
    {
        var (key, _) = NewKey();
        var publicOnly = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem()));

        var ex = Assert.Throws<FormatException>(() => ApnsProviderToken.ImportKey(publicOnly));
        Assert.Equal("the APNs key could not be read as a private key", ex.Message);
    }

    // ── The request ──────────────────────────────────────────────────────────────────────────

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Handler(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return answers[Math.Min(Seen.Count - 1, answers.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string? reason = null) =>
        new(status) { Content = new StringContent(reason is null ? "" : JsonSerializer.Serialize(new { reason })) };

    private static readonly PushMessage Message = new(
        Kind: "gate",
        Title: "Merge approval waiting",
        Body: "example/project · PR #42",
        Target: new Dictionary<string, string> { ["namespace"] = "fleet", ["workflowId"] = "pr-42", ["runId"] = "run-1", ["signal"] = "merge-approval" },
        Key: "gate\u001ffleet\u001fpr-42\u001frun-1");

    private static (ApnsClient Client, Handler Handler, ApnsProviderToken Token) NewClient(params Func<HttpRequestMessage, HttpResponseMessage>[] answers)
    {
        var (_, base64) = NewKey();
        var handler = new Handler(answers);
        var token = new ApnsProviderToken(base64, "KEYID12345", "TEAMID1234");
        var options = Options.Create(new PushOptions { Enabled = true, ApnsKeyBase64 = base64, ApnsKeyId = "KEYID12345", ApnsTeamId = "TEAMID1234", ApnsTopic = "com.example.app" });
        return (new ApnsClient(new HttpClient(handler), options, token, new FixedTime(Now)), handler, token);
    }

    [Fact]
    public async Task ANotification_GoesToTheDevicesPath_OverHttp2_WithTheHeadersAppleReads()
    {
        var (client, handler, _) = NewClient(_ => Answer(HttpStatusCode.OK));

        var result = await client.SendAsync(DeviceToken, Message, CancellationToken.None);

        Assert.Equal(ApnsOutcome.Accepted, result.Outcome);
        var request = Assert.Single(handler.Seen).Request;
        Assert.Equal($"https://api.push.apple.com/3/device/{DeviceToken}", request.RequestUri!.ToString());
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
        Assert.StartsWith("bearer ", Assert.Single(request.Headers.GetValues("authorization")));
        Assert.Equal("com.example.app", Assert.Single(request.Headers.GetValues("apns-topic")));
        Assert.Equal("alert", Assert.Single(request.Headers.GetValues("apns-push-type")));
        Assert.Equal(Now.AddHours(1).ToUnixTimeSeconds().ToString(), Assert.Single(request.Headers.GetValues("apns-expiration")));
        var collapse = Assert.Single(request.Headers.GetValues("apns-collapse-id"));
        Assert.InRange(Encoding.UTF8.GetByteCount(collapse), 1, 64);
    }

    [Fact]
    public async Task ThePayload_CarriesTheAlertAndWhatTheAppRoutesOn_AndNothingElse()
    {
        var (client, handler, _) = NewClient(_ => Answer(HttpStatusCode.OK));

        await client.SendAsync(DeviceToken, Message, CancellationToken.None);

        using var payload = JsonDocument.Parse(Assert.Single(handler.Seen).Body);
        var aps = payload.RootElement.GetProperty("aps");
        Assert.Equal("Merge approval waiting", aps.GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("example/project · PR #42", aps.GetProperty("alert").GetProperty("body").GetString());
        Assert.Equal("gate", aps.GetProperty("thread-id").GetString());
        var phleet = payload.RootElement.GetProperty("phleet");
        Assert.Equal("gate", phleet.GetProperty("kind").GetString());
        Assert.Equal("pr-42", phleet.GetProperty("workflowId").GetString());
        Assert.Equal("merge-approval", phleet.GetProperty("signal").GetString());
        Assert.Equal(["aps", "phleet"], payload.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task TheSameEvent_AlwaysCollapsesTheSameWay_AndADifferentOneDoesNot()
    {
        Assert.Equal(Message.CollapseId, (Message with { Title = "worded differently" }).CollapseId);
        Assert.NotEqual(Message.CollapseId, (Message with { Key = Message.Key + "\u001fgate-b" }).CollapseId);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "Unregistered")]
    [InlineData(HttpStatusCode.BadRequest, "BadDeviceToken")]
    [InlineData(HttpStatusCode.BadRequest, "DeviceTokenNotForTopic")]
    public async Task ATokenAppleSaysIsFinished_IsReportedAsUnregistered(HttpStatusCode status, string reason)
    {
        var (client, _, _) = NewClient(_ => Answer(status, reason));

        var result = await client.SendAsync(DeviceToken, Message, CancellationToken.None);

        Assert.Equal(ApnsOutcome.Unregistered, result.Outcome);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "TooManyRequests")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalServerError")]
    [InlineData(HttpStatusCode.Forbidden, "InvalidProviderToken")]
    [InlineData(HttpStatusCode.BadRequest, "TopicDisallowed")]
    public async Task AnyOtherRefusal_KeepsTheDevice(HttpStatusCode status, string reason)
    {
        var (client, _, _) = NewClient(_ => Answer(status, reason));

        var result = await client.SendAsync(DeviceToken, Message, CancellationToken.None);

        // Forgetting a phone because Apple was busy, or because OUR key is wrong, would silently
        // turn notifications off for good.
        Assert.Equal(ApnsOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task AnExpiredProviderToken_IsReplacedAndTheRequestRetriedOnce()
    {
        var (client, handler, _) = NewClient(
            _ => Answer(HttpStatusCode.Forbidden, "ExpiredProviderToken"),
            _ => Answer(HttpStatusCode.OK));

        var result = await client.SendAsync(DeviceToken, Message, CancellationToken.None);

        Assert.Equal(ApnsOutcome.Accepted, result.Outcome);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task AnExpiredProviderToken_Twice_IsAFailure_NotALoop()
    {
        var (client, handler, _) = NewClient(_ => Answer(HttpStatusCode.Forbidden, "ExpiredProviderToken"));

        var result = await client.SendAsync(DeviceToken, Message, CancellationToken.None);

        Assert.Equal(ApnsOutcome.Failed, result.Outcome);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("0123456789abcdef0123456789abcdef/../../x")]
    [InlineData("0123")]
    public async Task SomethingThatIsNotADeviceToken_NeverReachesAUrl(string token)
    {
        var (client, handler, _) = NewClient(_ => Answer(HttpStatusCode.OK));

        var result = await client.SendAsync(token, Message, CancellationToken.None);

        Assert.Equal(ApnsOutcome.Unregistered, result.Outcome);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task ARequestThatNeverCompletes_IsAFailure_AndTheDeviceIsKept()
    {
        var (client, _, _) = NewClient(_ => throw new HttpRequestException("synthetic"));

        var result = await client.SendAsync(DeviceToken, Message, CancellationToken.None);

        Assert.Equal(new ApnsResult(ApnsOutcome.Failed, "transport"), result);
    }

    [Fact]
    public async Task AReasonThatDoesNotLookLikeOneOfApples_IsNotRepeated()
    {
        var (client, _, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{\"reason\":\"<html>an error page</html>\"}") });

        var result = await client.SendAsync(DeviceToken, Message, CancellationToken.None);

        Assert.Equal(new ApnsResult(ApnsOutcome.Failed, "502"), result);
    }

    // ── Options ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMissingSetting_IsNamed_AndACompleteSetIsNot()
    {
        var complete = new PushOptions { ApnsKeyBase64 = "k", ApnsKeyId = "i", ApnsTeamId = "t", ApnsTopic = "com.example.app" };
        Assert.Null(complete.DescribeFault());

        Assert.Equal("push_apns_key_missing", new PushOptions { ApnsKeyId = "i", ApnsTeamId = "t", ApnsTopic = "x" }.DescribeFault());
        Assert.Equal("push_apns_key_id_missing", new PushOptions { ApnsKeyBase64 = "k", ApnsTeamId = "t", ApnsTopic = "x" }.DescribeFault());
        Assert.Equal("push_apns_team_id_missing", new PushOptions { ApnsKeyBase64 = "k", ApnsKeyId = "i", ApnsTopic = "x" }.DescribeFault());
        Assert.Equal("push_apns_topic_missing", new PushOptions { ApnsKeyBase64 = "k", ApnsKeyId = "i", ApnsTeamId = "t" }.DescribeFault());
        Assert.Equal("push_apns_host_invalid", new PushOptions { ApnsKeyBase64 = "k", ApnsKeyId = "i", ApnsTeamId = "t", ApnsTopic = "x", ApnsHost = "https://x/y" }.DescribeFault());
    }

    [Fact]
    public void Readiness_ReadsTheKey_OnlyWhenTheFeatureIsOn()
    {
        var (_, base64) = NewKey();
        PushOptions With(bool enabled, string key) => new() { Enabled = enabled, ApnsKeyBase64 = key, ApnsKeyId = "KEYID12345", ApnsTeamId = "TEAMID1234", ApnsTopic = "com.example.app" };

        var off = new PushReadiness(Options.Create(With(false, "c3ludGhldGlj")));
        var ready = new PushReadiness(Options.Create(With(true, base64)));
        var unusable = new PushReadiness(Options.Create(With(true, "c3ludGhldGlj")));

        Assert.False(off.IsReady);
        Assert.Null(off.Fault);
        Assert.True(ready.IsReady);
        Assert.False(unusable.IsReady);
        Assert.StartsWith("the APNs key ", unusable.Fault);
        Assert.DoesNotContain("c3ludGhldGlj", unusable.Fault);
    }

    [Fact]
    public void TheInterval_AndTheGrace_AreKeptWithinSaneBounds()
    {
        Assert.Equal(10, new PushOptions { IntervalSeconds = 0 }.EffectiveIntervalSeconds);
        Assert.Equal(600, new PushOptions { IntervalSeconds = 99_999 }.EffectiveIntervalSeconds);
        // Below the registry's own "dead" threshold the grace would mean nothing.
        Assert.Equal(90, new PushOptions { AgentDownGraceSeconds = 1 }.EffectiveAgentDownGraceSeconds);
    }
}
