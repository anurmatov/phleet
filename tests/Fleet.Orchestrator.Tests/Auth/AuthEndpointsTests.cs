using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Endpoints;

namespace Fleet.Orchestrator.Tests.Auth;

/// <summary>
/// Sign-in as a caller sees it: over HTTP, through the production pipeline and routes.
/// </summary>
public class AuthEndpointsTests
{
    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;

    // ── a browser ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ABrowserSignsInWithTheBootstrapToken_AndGetsAnHttpOnlyStrictCookie()
    {
        await using var host = await SignInHost.StartAsync();

        var response = await host.SendAsync(Post, "/api/auth/browser-session",
            body: new { token = SignInHost.ServiceToken, deviceName = "Kitchen laptop" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = SignInHost.SetCookieHeaderOf(response)!;
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        // Plain HTTP in the test, so not marked Secure; the next test covers HTTPS.
        Assert.DoesNotContain("secure", header, StringComparison.OrdinalIgnoreCase);

        var session = await SignInHost.JsonAsync(
            await host.SendAsync(Get, "/api/auth/session", cookie: SignInHost.SessionCookieOf(response)));
        Assert.True(session.GetProperty("signedIn").GetBoolean());
        Assert.Equal("browser", session.GetProperty("kind").GetString());
        Assert.Equal("owner", session.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task TheCookieIsMarkedSecure_WhenTheProxyReportsHttps()
    {
        await using var host = await SignInHost.StartAsync();

        var request = SignInHost.Request(Post, "/api/auth/browser-session", body: new { token = SignInHost.ServiceToken });
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await host.Client().SendAsync(request);

        Assert.Contains("secure", SignInHost.SetCookieHeaderOf(response)!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AWrongOrMissingBootstrapToken_IsRefused_AndSetsNoCookie(string? token)
    {
        await using var host = await SignInHost.StartAsync();

        var response = await host.SendAsync(Post, "/api/auth/browser-session", body: new { token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(SignInHost.SetCookieHeaderOf(response));
        Assert.Empty(host.MemoryStore!.Devices);
    }

    [Fact]
    public async Task SigningOut_RevokesThatBrowser_AndClearsTheCookie()
    {
        await using var host = await SignInHost.StartAsync();
        var cookie = await host.SignInBrowserAsync();

        var signOut = await host.SendAsync(HttpMethod.Delete, "/api/auth/browser-session", cookie: cookie, requestHeader: true);

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.NotNull(SignInHost.SetCookieHeaderOf(signOut));
        Assert.Null(SignInHost.SessionCookieOf(signOut));
        // At once, not after the remembered check lapses.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/agents", cookie: cookie)).StatusCode);
    }

    // ── the gate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FromOutside_AReadNeedsACredential()
    {
        await using var host = await SignInHost.StartAsync();
        var cookie = await host.SignInBrowserAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/ws")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", cookie: cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/ws", cookie: cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", bearer: SignInHost.ServiceToken)).StatusCode);
        // Health is how a proxy and an orchestration layer ask "are you up"; it names nothing.
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/health")).StatusCode);
    }

    [Fact]
    public async Task FromTheInternalNetwork_AReadNeedsNone_AndAWriteStillDoes()
    {
        await using var host = await SignInHost.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", edge: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.SendAsync(Post, "/api/agents/example/restart", edge: false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await host.SendAsync(Post, "/api/agents/example/restart", bearer: SignInHost.ServiceToken, edge: false)).StatusCode);
    }

    [Fact]
    public async Task ABrowsersWriteNeedsTheRequestHeader_AndABearersDoesNot()
    {
        await using var host = await SignInHost.StartAsync();
        var (cookie, _, _, accessToken) = await host.EnrolledAppAsync();

        var without = await host.SendAsync(Post, "/api/agents/example/restart", cookie: cookie);
        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Equal("request_header_required", (await SignInHost.JsonAsync(without)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.OK,
            (await host.SendAsync(Post, "/api/agents/example/restart", cookie: cookie, requestHeader: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await host.SendAsync(Post, "/api/agents/example/restart", bearer: accessToken)).StatusCode);
        // A read is never asked for it.
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", cookie: cookie)).StatusCode);
    }

    [Fact]
    public async Task EachCredentialOnlyWorksWhereItBelongs()
    {
        await using var host = await SignInHost.StartAsync();
        var (cookie, _, _, accessToken) = await host.EnrolledAppAsync();

        // A session token lifted out of the cookie jar is not a bearer, and an access token planted
        // in a cookie is not a session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/agents", bearer: cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/agents", cookie: accessToken)).StatusCode);
    }

    [Fact]
    public async Task ABearerIsJudgedAlone_AndNeverFallsBackToTheCookie()
    {
        await using var host = await SignInHost.StartAsync();
        var cookie = await host.SignInBrowserAsync();

        var response = await host.SendAsync(Get, "/api/agents", bearer: "not-a-token", cookie: cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SessionSaysSignedOut_WithA401_WhenSignInIsRequired()
    {
        await using var host = await SignInHost.StartAsync();

        var response = await host.SendAsync(Get, "/api/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True((await SignInHost.JsonAsync(response)).GetProperty("authRequired").GetBoolean());
    }

    // ── an enrolled app ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnAppEnrollsWithAOneTimeCode_MintsAToken_AndUsesTheApi()
    {
        await using var host = await SignInHost.StartAsync();
        var cookie = await host.SignInBrowserAsync();
        var code = await host.IssueEnrollmentCodeAsync(cookie, "Pocket phone");

        var registered = await host.RegisterAsync(code, "what the phone calls itself");
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var device = await SignInHost.JsonAsync(registered);
        var deviceId = device.GetProperty("deviceId").GetString()!;

        var minted = await host.TokenAsync(deviceId, device.GetProperty("deviceSecret").GetString());
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        var token = await SignInHost.JsonAsync(minted);
        Assert.Equal((int)AuthService.AccessTokenTtl.TotalSeconds, token.GetProperty("expiresInSeconds").GetInt32());

        var accessToken = token.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", bearer: accessToken)).StatusCode);

        var session = await SignInHost.JsonAsync(await host.SendAsync(Get, "/api/auth/session", bearer: accessToken));
        Assert.Equal("app", session.GetProperty("kind").GetString());
        Assert.Equal(deviceId, session.GetProperty("deviceId").GetString());

        // The name is the one the person adding the device chose, not the one the device sent.
        var listed = await SignInHost.JsonAsync(await host.SendAsync(Get, "/api/auth/devices", cookie: cookie));
        var names = listed.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("name").GetString()).ToList();
        Assert.Contains("Pocket phone", names);
        Assert.DoesNotContain("what the phone calls itself", names);
    }

    [Fact]
    public async Task AnEnrollmentCode_RegistersOneDevice_AndIsRefusedOnceThatDeviceHasProvedItself()
    {
        await using var host = await SignInHost.StartAsync();
        var cookie = await host.SignInBrowserAsync();
        var code = await host.IssueEnrollmentCodeAsync(cookie);

        var device = await SignInHost.JsonAsync(await host.RegisterAsync(code));
        await host.TokenAsync(device.GetProperty("deviceId").GetString(), device.GetProperty("deviceSecret").GetString());

        var reuse = await host.RegisterAsync(code);
        var unknown = await host.RegisterAsync(Credentials.Compose("nosuchid", Credentials.NewSecret()));

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await reuse.Content.ReadAsStringAsync());
        Assert.Single(host.MemoryStore!.Devices, d => d.Kind == DeviceKind.App);
    }

    [Fact]
    public async Task AnAppCannotManageDevices_ButCanSignItselfOut()
    {
        await using var host = await SignInHost.StartAsync();
        var (cookie, deviceId, deviceSecret, accessToken) = await host.EnrolledAppAsync();
        var browserId = (await SignInHost.JsonAsync(await host.SendAsync(Get, "/api/auth/session", cookie: cookie)))
            .GetProperty("deviceId").GetString();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendAsync(Post, "/api/auth/enrollments", bearer: accessToken, body: new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(Get, "/api/auth/devices", bearer: accessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendAsync(Post, $"/api/auth/devices/{browserId}/revoke", bearer: accessToken)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await host.SendAsync(Post, $"/api/auth/devices/{deviceId}/revoke", bearer: accessToken)).StatusCode);

        // Its token stops at once, and its secret mints no more.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/agents", bearer: accessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.TokenAsync(deviceId, deviceSecret)).StatusCode);
        // The browser it was enrolled from is untouched.
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", cookie: cookie)).StatusCode);
    }

    [Fact]
    public async Task ABrowserRevokesAPhone_AndThePhoneStopsAtOnce()
    {
        await using var host = await SignInHost.StartAsync();
        var (cookie, deviceId, _, accessToken) = await host.EnrolledAppAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", bearer: accessToken)).StatusCode);

        var revoke = await host.SendAsync(Post, $"/api/auth/devices/{deviceId}/revoke", cookie: cookie, requestHeader: true);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/agents", bearer: accessToken)).StatusCode);

        var listed = await SignInHost.JsonAsync(await host.SendAsync(Get, "/api/auth/devices", cookie: cookie));
        var phone = listed.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("deviceId").GetString() == deviceId);
        Assert.NotEqual(JsonValueKind.Null, phone.GetProperty("revokedAt").ValueKind);
    }

    [Fact]
    public async Task RevokingADeviceThatDoesNotExist_IsNotFound()
    {
        await using var host = await SignInHost.StartAsync();
        var cookie = await host.SignInBrowserAsync();

        var response = await host.SendAsync(Post, "/api/auth/devices/nosuchdevice/revoke", cookie: cookie, requestHeader: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NoDeviceRouteEverReturnsASecretOrAHash()
    {
        await using var host = await SignInHost.StartAsync();
        var (cookie, _, deviceSecret, accessToken) = await host.EnrolledAppAsync();

        var listed = await (await host.SendAsync(Get, "/api/auth/devices", cookie: cookie)).Content.ReadAsStringAsync();
        var session = await (await host.SendAsync(Get, "/api/auth/session", bearer: accessToken)).Content.ReadAsStringAsync();

        foreach (var body in new[] { listed, session })
        {
            Assert.DoesNotContain(deviceSecret, body);
            Assert.DoesNotContain(accessToken, body);
            Assert.DoesNotContain(cookie, body);
            Assert.DoesNotContain("argon2", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── the config API ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheConfigApi_TakesItsTokenOrABrowser_AndNeverAnApp()
    {
        await using var host = await SignInHost.StartAsync();
        var (cookie, _, _, accessToken) = await host.EnrolledAppAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/config/values", bearer: SignInHost.ConfigToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/config/values", cookie: cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await host.SendAsync(HttpMethod.Put, "/api/config/values", cookie: cookie, requestHeader: true)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Put, "/api/config/values", cookie: cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/config/values", bearer: accessToken)).StatusCode);
        // The service bearer is the admin token, not the config token; the two stay separate.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/config/values", bearer: SignInHost.ServiceToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/config/values")).StatusCode);
    }

    [Fact]
    public async Task TheConfigApi_StaysClosedWithoutItsToken_EvenForABrowser()
    {
        await using var host = await SignInHost.StartAsync(configToken: null);
        var cookie = await host.SignInBrowserAsync();

        var response = await host.SendAsync(Get, "/api/config/values", cookie: cookie);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ── nothing gated ────────────────────────────────────────────────────────

    [Fact]
    public async Task WithNoServiceToken_NothingIsGated_AndASessionIsIssuedWithoutOne()
    {
        await using var host = await SignInHost.StartAsync(serviceToken: null);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Post, "/api/agents/example/restart")).StatusCode);

        var session = await SignInHost.JsonAsync(await host.SendAsync(Get, "/api/auth/session"));
        Assert.False(session.GetProperty("authRequired").GetBoolean());
        Assert.False(session.GetProperty("signedIn").GetBoolean());

        // A page still needs something to present to the chat service.
        var cookie = await host.SignInBrowserAsync(token: null);
        var signedIn = await SignInHost.JsonAsync(await host.SendAsync(Get, "/api/auth/session", cookie: cookie));
        Assert.True(signedIn.GetProperty("signedIn").GetBoolean());

        // And a phone can be enrolled by whoever can reach the API, exactly as they can do
        // everything else on such a deployment.
        var enrollment = await host.SendAsync(Post, "/api/auth/enrollments", body: new { });
        Assert.Equal(HttpStatusCode.OK, enrollment.StatusCode);
    }

    // ── the chat service's question ──────────────────────────────────────────

    private static string CheckToken(SignInHost host) =>
        host.Tokens.Mint(AuthEndpoints.CheckTokenPurpose, AuthEndpoints.CheckTokenSubject);

    private static Task<HttpResponseMessage> CheckAsync(SignInHost host, string? serviceBearer, object body) =>
        host.SendAsync(Post, AuthEndpoints.CheckPath, bearer: serviceBearer, edge: false, body: body);

    [Fact]
    public async Task TheCheck_AnswersForACookieAndForAnAppToken()
    {
        await using var host = await SignInHost.StartAsync();
        var (cookie, deviceId, _, accessToken) = await host.EnrolledAppAsync();

        var app = await CheckAsync(host, CheckToken(host), new { authorization = $"Bearer {accessToken}", method = "POST" });
        Assert.Equal(HttpStatusCode.OK, app.StatusCode);
        var appBody = await SignInHost.JsonAsync(app);
        Assert.Equal("owner", appBody.GetProperty("principalId").GetString());
        Assert.Equal(deviceId, appBody.GetProperty("deviceId").GetString());
        Assert.Equal("app", appBody.GetProperty("kind").GetString());

        var browser = await CheckAsync(host, CheckToken(host),
            new { cookie = $"other=1; {CallerAuthenticator.SessionCookie}={cookie}", method = "GET" });
        Assert.Equal(HttpStatusCode.OK, browser.StatusCode);
        Assert.Equal("browser", (await SignInHost.JsonAsync(browser)).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TheCheck_AppliesTheBrowserWriteRule()
    {
        await using var host = await SignInHost.StartAsync();
        var cookie = await host.SignInBrowserAsync();
        var header = $"{CallerAuthenticator.SessionCookie}={cookie}";

        var without = await CheckAsync(host, CheckToken(host), new { cookie = header, method = "POST", requestHeader = false });
        var with = await CheckAsync(host, CheckToken(host), new { cookie = header, method = "POST", requestHeader = true });

        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    [Fact]
    public async Task TheCheck_RefusesTheServiceBearerAndAnythingUnverified()
    {
        await using var host = await SignInHost.StartAsync();

        foreach (var body in new object[]
                 {
                     new { authorization = $"Bearer {SignInHost.ServiceToken}", method = "GET" },
                     new { authorization = "Bearer not-a-token", method = "GET" },
                     new { method = "GET" },
                 })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await CheckAsync(host, CheckToken(host), body)).StatusCode);
        }
    }

    [Fact]
    public async Task TheCheck_IsOnlyForTheChatService()
    {
        await using var host = await SignInHost.StartAsync();
        var (_, _, _, accessToken) = await host.EnrolledAppAsync();
        var question = new { authorization = $"Bearer {accessToken}", method = "GET" };

        // No bearer, the admin bearer, a token minted for another purpose, and one for another subject.
        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckAsync(host, null, question)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckAsync(host, SignInHost.ServiceToken, question)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await CheckAsync(host, host.Tokens.Mint("ingest", AuthEndpoints.CheckTokenSubject), question)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await CheckAsync(host, host.Tokens.Mint(AuthEndpoints.CheckTokenPurpose, "someone-else"), question)).StatusCode);
    }

    // ── failure is never a pass ──────────────────────────────────────────────

    [Fact]
    public async Task AStoreThatCannotAnswer_IsA503_NeverAPass()
    {
        await using var host = await SignInHost.StartAsync();
        // Not signed in through the host first: a remembered check would answer without the store.
        host.MemoryStore!.FailEveryOperation = true;

        var response = await host.SendAsync(Get, "/api/agents",
            bearer: Credentials.Compose("sometoken", Credentials.NewSecret()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task TheDurableStore_RunsTheSameFlow()
    {
        await using var host = await SignInHost.StartAsync(durable: true);
        var (cookie, deviceId, _, accessToken) = await host.EnrolledAppAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", bearer: accessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(Get, "/api/agents", cookie: cookie)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await host.SendAsync(Post, $"/api/auth/devices/{deviceId}/revoke", cookie: cookie, requestHeader: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(Get, "/api/agents", bearer: accessToken)).StatusCode);
    }
}
