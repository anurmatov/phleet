using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Comms.Auth;
using Fleet.Conversations.Journal;
using Fleet.Shared;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

/// <summary>
/// The client this service asks "who is this caller?" with (#530 D7, MUST NOT 1, 3 and 6).
/// </summary>
public sealed class OrchestratorSignInCheckTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static readonly PresentedCredential AppCredential = new("Bearer access-token-secret", null);

    private const string SignedBody =
        """{"principalId":"owner","deviceId":"d_1","credentialId":"t_1","kind":"app","agentChatEnabled":true}""";

    [Fact]
    public async Task It_asks_the_check_route_with_its_own_token_and_forwards_only_the_credential()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, SignedBody));
        using var check = Build(handler);

        var answer = await check.CheckAsync(
            new PresentedCredential("Bearer access-token-secret", "session-secret"), "example-agent", fresh: false, CancellationToken.None);

        Assert.Equal(SignInOutcome.Signed, answer.Outcome);
        Assert.Equal("owner", answer.Caller!.PrincipalId);
        Assert.False(answer.Caller.IsBrowser);
        Assert.True(answer.AgentChatEnabled);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://orchestrator.test" + SignIn.CheckPath, request.Uri);

        // Its own token, under the shared key, with the purpose and subject the orchestrator checks.
        Assert.True(JournalTokens.TryVerify(
            request.Bearer, SignIn.CheckTokenPurpose, [Key], out var subject));
        Assert.Equal(SignIn.CheckTokenSubject, subject);

        // The caller's credential travels in the body, never in the URL (MUST NOT 6).
        Assert.DoesNotContain("secret", request.Uri, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("Bearer access-token-secret", body.RootElement.GetProperty("authorization").GetString());
        Assert.Equal($"{SignIn.SessionCookie}=session-secret", body.RootElement.GetProperty("cookie").GetString());
        Assert.Equal("example-agent", body.RootElement.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task A_browser_answer_is_a_browser_caller()
    {
        using var check = Build(new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"principalId":"owner","deviceId":"d_2","credentialId":"s_2","kind":"browser"}""")));

        var answer = await check.CheckAsync(new PresentedCredential(null, "session"), null, false, CancellationToken.None);

        Assert.True(answer.Caller!.IsBrowser);
        Assert.Null(answer.AgentChatEnabled);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SignInOutcome.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, SignInOutcome.Busy)]
    [InlineData(HttpStatusCode.Forbidden, SignInOutcome.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SignInOutcome.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, SignInOutcome.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, SignInOutcome.Unavailable)]
    [InlineData(HttpStatusCode.NoContent, SignInOutcome.Unavailable)]
    public async Task Every_status_but_200_and_401_is_never_a_pass(HttpStatusCode status, SignInOutcome expected)
    {
        using var check = Build(new StubHandler(_ => Json(status, SignedBody)));

        var answer = await check.CheckAsync(AppCredential, null, false, CancellationToken.None);

        Assert.Equal(expected, answer.Outcome);
        Assert.Null(answer.Caller);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"principalId":"owner","deviceId":"d_1","credentialId":"t_1","kind":"service"}""")]
    [InlineData("""{"principalId":"","deviceId":"d_1","credentialId":"t_1","kind":"app"}""")]
    [InlineData("""{"principalId":7,"deviceId":"d_1","credentialId":"t_1","kind":"app"}""")]
    public async Task A_200_that_does_not_name_a_device_is_unavailable(string body)
    {
        using var check = Build(new StubHandler(_ => Json(HttpStatusCode.OK, body)));

        var answer = await check.CheckAsync(AppCredential, null, false, CancellationToken.None);

        Assert.Equal(SignInOutcome.Unavailable, answer.Outcome);
    }

    [Fact]
    public async Task An_oversized_answer_is_unavailable()
    {
        var padded = SignedBody.TrimEnd('}') + $",\"pad\":\"{new string('x', 8192)}\"}}";
        using var check = Build(new StubHandler(_ => Json(HttpStatusCode.OK, padded)));

        Assert.Equal(SignInOutcome.Unavailable,
            (await check.CheckAsync(AppCredential, null, false, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_unreachable_orchestrator_is_unavailable()
    {
        using var check = Build(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        Assert.Equal(SignInOutcome.Unavailable,
            (await check.CheckAsync(AppCredential, null, false, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_unusable_orchestrator_address_is_unavailable_and_sends_nothing()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, SignedBody));
        using var check = Build(handler, url: "not a url");

        Assert.Equal(SignInOutcome.Unavailable,
            (await check.CheckAsync(AppCredential, null, false, CancellationToken.None)).Outcome);
        Assert.Empty(handler.Requests);
    }

    // ── the short memory ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_definite_answer_is_remembered_for_at_most_ten_seconds()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, SignedBody));
        var time = new TestTimeProvider();
        using var check = Build(handler, time);

        await check.CheckAsync(AppCredential, null, false, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(9));
        await check.CheckAsync(AppCredential, null, false, CancellationToken.None);
        Assert.Single(handler.Requests);

        time.Advance(TimeSpan.FromSeconds(1));
        await check.CheckAsync(AppCredential, null, false, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_fresh_question_always_asks()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, SignedBody));
        using var check = Build(handler);

        await check.CheckAsync(AppCredential, null, false, CancellationToken.None);
        await check.CheckAsync(AppCredential, null, fresh: true, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task The_memory_is_per_credential_and_per_agent()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, SignedBody));
        using var check = Build(handler);

        await check.CheckAsync(AppCredential, null, false, CancellationToken.None);
        await check.CheckAsync(AppCredential, "example-agent", false, CancellationToken.None);
        await check.CheckAsync(new PresentedCredential("Bearer another", null), null, false, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task An_outage_is_never_remembered_and_forgets_the_answer_before_it()
    {
        var status = HttpStatusCode.OK;
        var handler = new StubHandler(_ => Json(status, SignedBody));
        var time = new TestTimeProvider();
        using var check = Build(handler, time);

        Assert.Equal(SignInOutcome.Signed, (await check.CheckAsync(AppCredential, null, false, CancellationToken.None)).Outcome);

        // A fresh question meets an outage. The signed answer from before it is not trusted again.
        status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(SignInOutcome.Unavailable, (await check.CheckAsync(AppCredential, null, true, CancellationToken.None)).Outcome);
        Assert.Equal(SignInOutcome.Unavailable, (await check.CheckAsync(AppCredential, null, false, CancellationToken.None)).Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    // ── redaction (MUST NOT 1) ───────────────────────────────────────────────

    [Fact]
    public async Task No_credential_reaches_a_log_line()
    {
        var logger = new CapturingLogger();
        var responses = new Queue<Func<HttpResponseMessage>>(
        [
            () => Json(HttpStatusCode.Forbidden, ""),
            () => Json(HttpStatusCode.InternalServerError, "access-token-secret"),
            () => throw new HttpRequestException("failed for Bearer access-token-secret"),
        ]);
        using var check = Build(new StubHandler(_ => responses.Dequeue()()), logger: logger);

        for (var i = 0; i < 3; i++)
            await check.CheckAsync(new PresentedCredential("Bearer access-token-secret", "session-secret"), null, true, CancellationToken.None);

        Assert.NotEmpty(logger.Lines);
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain("access-token-secret", line, StringComparison.Ordinal);
            Assert.DoesNotContain("session-secret", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_credential_digest_does_not_contain_the_credential()
    {
        var digest = new PresentedCredential("Bearer access-token-secret", "session-secret").Digest();

        Assert.Equal(64, digest.Length);
        Assert.DoesNotContain("secret", digest, StringComparison.Ordinal);
        Assert.NotEqual(digest, new PresentedCredential("Bearer access-token-secret", null).Digest());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static OrchestratorSignInCheck Build(
        StubHandler handler, TestTimeProvider? time = null, string url = "http://orchestrator.test",
        ILogger<OrchestratorSignInCheck>? logger = null) =>
        new(new HttpClient(handler), url, Key, time ?? new TestTimeProvider(),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorSignInCheck>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Seen(HttpMethod Method, string Uri, string? Bearer, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.Parameter, body));
            return respond(request);
        }
    }

    private sealed class CapturingLogger : ILogger<OrchestratorSignInCheck>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null) Lines.Add(exception.ToString());
        }
    }
}
