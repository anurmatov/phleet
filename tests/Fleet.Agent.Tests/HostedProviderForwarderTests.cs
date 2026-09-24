using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services.HostedProviders;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// #335 "Forwarder contract" (AC5, AC6, AC6a), exercised over real HTTP against the real nested
/// loopback host. The upstream is the handler seam: it sees exactly the <see cref="HttpRequestMessage"/>
/// the socket handler would put on the wire, and nothing here contacts Z.ai (D9).
/// </summary>
/// <remarks>
/// The <c>zai-*.sse</c> fixtures under <c>Fixtures/HostedProviders/</c> are scrubbed Phase 0
/// captures: Z.ai's real answers to real Codex 0.153.4 requests relayed by this forwarder. Scrubbing
/// replaced the request ids and changed nothing else. Both are <c>response.failed</c> streams (an
/// unfunded quota and an unsupported effort); successful captures are added when the probe can run
/// on an active plan. The forwarder copies bytes, so the assertions do not depend on which.
/// </remarks>
public sealed class HostedProviderForwarderTests : IDisposable
{
    private const string StoredKey = "stored-forwarder-key-value";
    private const string CodexUserAgent = "phleet/0.153.4 (Linux 6.8.0; x86_64) unknown (phleet; 0.1.0)";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hosted-forwarder-{Guid.NewGuid():N}");

    public HostedProviderForwarderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* teardown only */ }
    }

    // ── AC5: transparency ───────────────────────────────────────────────────

    [Theory]
    [InlineData("zai-response-failed-insufficient-quota.sse")]
    [InlineData("zai-response-failed-unsupported-effort.sse")]
    public async Task Forwards_BytesFramingAndHeadersUnchanged_ExceptAuthorizationAndToken(string fixture)
    {
        var sse = HostedProviderFixtureBytes(fixture);
        var upstream = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = SseContent(sse),
        }));
        await using var host = await StartHostAsync(upstream);
        var endpoint = await host.Endpoint;

        var body = CodexRequestBody();
        using var client = new HttpClient();
        using var request = CodexRequest(endpoint, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "inbound-credential-must-not-pass");
        request.Headers.ExpectContinue = true;
        request.Headers.Connection.Add("x-hop");
        request.Headers.TryAddWithoutValidation("x-hop", "1");

        using var response = await client.SendAsync(request);
        var received = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(upstream.Requests);

        Assert.Equal(new Uri("https://api.z.ai/api/v1/responses"), sent.Uri);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)), Convert.ToHexString(SHA256.HashData(sent.Body)));
        Assert.Equal(body.Length, sent.ContentLength);
        Assert.NotEqual(true, sent.Chunked);
        Assert.False(sent.Headers.ContainsKey("Transfer-Encoding"));

        // Every end-to-end header Codex sent arrives with the identical value.
        Assert.Equal(CodexUserAgent, sent.Headers["User-Agent"]);
        Assert.Equal("phleet", sent.Headers["originator"]);
        Assert.Equal("text/event-stream", sent.Headers["Accept"]);
        Assert.Equal("application/json", sent.Headers["Content-Type"]);
        Assert.Equal("019a0000-0000-7000-8000-000000000001", sent.Headers["session_id"]);

        // The listed exceptions never arrive.
        Assert.False(sent.Headers.ContainsKey(HostedProviderForwarder.TokenHeader));
        Assert.False(sent.Headers.ContainsKey("Expect"));
        Assert.False(sent.Headers.ContainsKey("x-hop"));
        Assert.False(sent.Headers.ContainsKey("Connection"));
        Assert.Equal($"Bearer {StoredKey}", sent.Headers["Authorization"]);
        Assert.DoesNotContain(sent.Headers.Values, v => v.Contains("inbound-credential-must-not-pass"));

        Assert.Equal(sse, received);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Non2xxStatusAndBody_PassThroughUnchanged()
    {
        var body429 = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"1302\",\"message\":\"rate limited\"}}");
        var upstream = new RecordingHandler((_, _) =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new ByteArrayContent(body429) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            r.Headers.TryAddWithoutValidation("Retry-After", "7");
            return Task.FromResult(r);
        });
        await using var host = await StartHostAsync(upstream);

        using var client = new HttpClient();
        using var request = CodexRequest(await host.Endpoint, CodexRequestBody());
        using var response = await client.SendAsync(request);

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal(body429, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("7", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task ContentEncoding_PassesThroughWithTheCompressedBytes()
    {
        var gzipped = new byte[] { 0x1f, 0x8b, 0x08, 0x00, 1, 2, 3, 4, 5 };
        var upstream = new RecordingHandler((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gzipped) };
            r.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(r);
        });
        await using var host = await StartHostAsync(upstream);

        // Automatic decompression off on the client, so it sees the bytes the forwarder wrote.
        using var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None });
        using var request = CodexRequest(await host.Endpoint, CodexRequestBody());
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.SendAsync(request);

        Assert.Equal("gzip", Assert.Single(upstream.Requests).Headers["Accept-Encoding"]);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(gzipped, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ResponseHopByHopAndConnectionNamedHeaders_AreNotReturned()
    {
        var upstream = new RecordingHandler((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            r.Headers.Connection.Add("x-upstream-hop");
            r.Headers.TryAddWithoutValidation("x-upstream-hop", "1");
            r.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
            r.Headers.TryAddWithoutValidation("x-request-id", "abc");
            return Task.FromResult(r);
        });
        await using var host = await StartHostAsync(upstream);

        using var client = new HttpClient();
        using var request = CodexRequest(await host.Endpoint, CodexRequestBody());
        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("x-upstream-hop"));
        Assert.False(response.Headers.Contains("Keep-Alive"));
        Assert.Equal("abc", response.Headers.GetValues("x-request-id").Single());
    }

    [Fact]
    public async Task GatedUpstreamStream_FirstEventReachesTheClientBeforeTheUpstreamCompletes()
    {
        var pipe = new Pipe();
        var upstream = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = SseContent(pipe.Reader.AsStream()),
        }));
        await using var host = await StartHostAsync(upstream);

        using var client = new HttpClient();
        using var request = CodexRequest(await host.Endpoint, CodexRequestBody());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();

        var first = "event: response.created\ndata: {\"type\":\"response.created\"}\n\n"u8.ToArray();
        await pipe.Writer.WriteAsync(first);

        var buffer = new byte[first.Length];
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await stream.ReadExactlyAsync(buffer, cts.Token);
        Assert.Equal(first, buffer);

        // Only now does the upstream finish.
        await pipe.Writer.WriteAsync("event: response.completed\ndata: {}\n\n"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        Assert.Equal("event: response.completed\ndata: {}\n\n", Encoding.UTF8.GetString(rest.ToArray()));
    }

    // ── AC6: routing and errors ─────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/zai/models")]
    [InlineData("POST", "/acme/responses")]
    [InlineData("GET", "/zai/responses")]
    [InlineData("POST", "/zai/responses/")]
    public async Task OtherMethodOrPath_Is404_WithNoUpstreamCall(string method, string path)
    {
        var upstream = NeverCalled();
        await using var host = await StartHostAsync(upstream);
        var endpoint = await host.Endpoint;

        using var client = new HttpClient { BaseAddress = endpoint.BaseAddress };
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.TryAddWithoutValidation(HostedProviderForwarder.TokenHeader, endpoint.Token);
        if (method == "POST")
            request.Content = new ByteArrayContent(CodexRequestBody());
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task MissingToken_Is401_WithNoUpstreamCall()
    {
        var upstream = NeverCalled();
        await using var host = await StartHostAsync(upstream);
        var endpoint = await host.Endpoint;

        using var client = new HttpClient();
        using var request = CodexRequest(endpoint, CodexRequestBody(), token: null);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("phleet adapter: missing or invalid forwarder token", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task WrongTokenOfTheSameLength_Is401_WithNoUpstreamCall()
    {
        var upstream = NeverCalled();
        await using var host = await StartHostAsync(upstream);
        var endpoint = await host.Endpoint;
        var wrong = HostedProviderAdapterHost.NewToken();
        Assert.Equal(endpoint.Token.Length, wrong.Length);
        Assert.NotEqual(endpoint.Token, wrong);

        using var client = new HttpClient();
        using var request = CodexRequest(endpoint, CodexRequestBody(), token: wrong);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task TwoTokenHeaders_Are401_WithNoUpstreamCall()
    {
        var upstream = NeverCalled();
        await using var host = await StartHostAsync(upstream);
        var endpoint = await host.Endpoint;

        // Two separate header lines, both carrying the valid token. HttpClient would fold them into
        // one line, so this goes over a raw socket.
        var status = await RawStatusAsync(endpoint,
            "POST /zai/responses HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n"
            + $"{HostedProviderForwarder.TokenHeader}: {endpoint.Token}\r\n"
            + $"{HostedProviderForwarder.TokenHeader}: {endpoint.Token}\r\n"
            + "Content-Length: 2\r\nConnection: close\r\n\r\n{}");

        Assert.Equal(401, status);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task ChunkedRequestWithNoContentLength_Is411_WithNoUpstreamCall()
    {
        var upstream = NeverCalled();
        await using var host = await StartHostAsync(upstream);
        var endpoint = await host.Endpoint;

        var status = await RawStatusAsync(endpoint,
            "POST /zai/responses HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n"
            + $"{HostedProviderForwarder.TokenHeader}: {endpoint.Token}\r\n"
            + "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n2\r\n{}\r\n0\r\n\r\n");

        Assert.Equal(411, status);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task TokenlessChunkedRequest_Is401_TheTokenIsCheckedFirst()
    {
        var upstream = NeverCalled();
        await using var host = await StartHostAsync(upstream);

        var status = await RawStatusAsync(await host.Endpoint,
            "POST /zai/responses HTTP/1.1\r\nHost: 127.0.0.1\r\n"
            + "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n2\r\n{}\r\n0\r\n\r\n");

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task BodyOverTheKestrelLimit_Is413_WithNoUpstreamCall()
    {
        var upstream = NeverCalled();
        await using var host = await StartHostAsync(upstream);
        var endpoint = await host.Endpoint;

        // Only the headers are sent: the forwarder must refuse on the declared length alone.
        var status = await RawStatusAsync(endpoint,
            "POST /zai/responses HTTP/1.1\r\nHost: 127.0.0.1\r\n"
            + $"{HostedProviderForwarder.TokenHeader}: {endpoint.Token}\r\n"
            + "Content-Length: 40000000\r\nConnection: close\r\n\r\n");

        Assert.Equal(413, status);
        Assert.Equal(0, upstream.Calls);
    }

    [Theory]
    [InlineData("connect", "SocketException")]
    [InlineData("timeout", "TimeoutException")]
    public async Task ConnectFailureOrTimeout_Is502_WithTheDocumentedMessage(string failure, string exceptionType)
    {
        var upstream = new RecordingHandler((_, _) => failure == "connect"
            ? throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))
            // What SocketsHttpHandler throws when ConnectTimeout elapses.
            : throw new TaskCanceledException("connect timed out", new TimeoutException()));
        await using var host = await StartHostAsync(upstream);

        using var client = new HttpClient();
        using var request = CodexRequest(await host.Endpoint, CodexRequestBody());
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains($"phleet adapter: upstream zai unreachable: {exceptionType}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void UpstreamHandler_HasTheD4Settings()
    {
        using var handler = HostedProviderAdapterHost.CreateUpstreamHandler();

        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task ClientAbort_CancelsTheUpstreamRequestWithin2Seconds()
    {
        var upstreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamCancelled = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingHandler(async (_, ct) =>
        {
            upstreamStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                upstreamCancelled.TrySetResult(DateTime.UtcNow);
                throw;
            }
            throw new InvalidOperationException("unreachable");
        });
        await using var host = await StartHostAsync(upstream);

        using var client = new HttpClient();
        using var cts = new CancellationTokenSource();
        using var request = CodexRequest(await host.Endpoint, CodexRequestBody());
        var send = client.SendAsync(request, cts.Token);

        await upstreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var abortedAt = DateTime.UtcNow;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        var cancelledAt = await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(cancelledAt - abortedAt < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Upstream_IsFixedHttpsOnly()
    {
        Assert.Equal(new Uri("https://api.z.ai/api/v1/responses"), HostedProviderForwarder.ResponsesUri(HostedModelProviders.Zai));
        var plain = HostedModelProviders.Zai with { Upstream = new Uri("http://api.z.ai/api/v1") };
        Assert.Throws<InvalidOperationException>(() => HostedProviderForwarder.ResponsesUri(plain));
    }

    // ── AC6a: token lifecycle ───────────────────────────────────────────────

    [Fact]
    public async Task EachStart_GetsADifferent32ByteToken()
    {
        await using var first = await StartHostAsync(NeverCalled());
        await using var second = await StartHostAsync(NeverCalled());
        var a = (await first.Endpoint).Token;
        var b = (await second.Endpoint).Token;

        Assert.NotEqual(a, b);
        Assert.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(a).Length);
        Assert.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(b).Length);
        Assert.DoesNotContain(a, (await first.Endpoint).ToString());
    }

    [Fact]
    public async Task Logs_FromAStartAndARelayedRequest_ContainNeitherTokenNorKey()
    {
        var logger = new ListLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logger).SetMinimumLevel(LogLevel.Trace));
        var upstream = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = SseContent(HostedProviderFixtureBytes("zai-response-failed-unsupported-effort.sse")),
        }));
        await using var host = await StartHostAsync(upstream, loggerFactory);
        var endpoint = await host.Endpoint;

        using var client = new HttpClient();
        using (var ok = CodexRequest(endpoint, CodexRequestBody()))
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ok)).StatusCode);
        using (var refused = CodexRequest(endpoint, CodexRequestBody(), token: null))
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(refused)).StatusCode);
        await host.StopAsync(CancellationToken.None);

        var lines = logger.Lines.ToList();
        Assert.Contains(lines, l => l.StartsWith("Codex hosted provider zai via loopback adapter 127.0.0.1:"));
        Assert.Contains(lines, l => l.StartsWith("HostedProviderAdapter provider=zai status=200 durationMs="));
        Assert.Contains(lines, l => l.StartsWith("HostedProviderAdapter provider=zai status=401 durationMs="));
        Assert.DoesNotContain(lines, l => l.Contains(endpoint.Token));
        Assert.DoesNotContain(lines, l => l.Contains(StoredKey));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<HostedProviderAdapterHost> StartHostAsync(
        RecordingHandler upstream, ILoggerFactory? loggerFactory = null)
    {
        var path = Path.Combine(_dir, $"key-{Guid.NewGuid():N}");
        File.WriteAllText(path, StoredKey);
        var store = new HostedProviderKeyStore(path);
        store.Load(HostedModelProviders.Zai.KeyEnvVar);

        var host = new HostedProviderAdapterHost(ZaiAgent(), store,
            loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, upstream);
        await host.StartAsync(CancellationToken.None);
        return host;
    }

    internal static IOptions<AgentOptions> ZaiAgent(string model = "zai/glm-5.3") => Options.Create(new AgentOptions
    {
        Name = "fleet-agent1",
        Role = "generic-role",
        WorkDir = "/workspace",
        Provider = "codex",
        Model = model,
        HostedProvider = true,
        HostedProviderKeyEnv = HostedModelProviders.Zai.KeyEnvVar,
    });

    /// <summary>A Codex-shaped request body: namespace tools, as Codex sends MCP tools.</summary>
    private static byte[] CodexRequestBody() => Encoding.UTF8.GetBytes(
        "{\"model\":\"glm-5.3\",\"instructions\":\"system prompt\",\"input\":[{\"type\":\"message\",\"role\":\"user\","
        + "\"content\":[{\"type\":\"input_text\",\"text\":\"hello\"}]}],\"tools\":[{\"type\":\"namespace\","
        + "\"name\":\"mcp__probe\",\"description\":\"probe\",\"tools\":[{\"type\":\"function\",\"name\":\"get_probe_value\","
        + "\"parameters\":{\"type\":\"object\",\"properties\":{}}}]}],\"tool_choice\":\"auto\",\"parallel_tool_calls\":true,"
        + "\"reasoning\":{\"effort\":\"high\",\"summary\":\"auto\"},\"store\":false,\"stream\":true,"
        + "\"include\":[\"reasoning.encrypted_content\"],\"prompt_cache_key\":\"019a0000-0000-7000-8000-000000000001\"}");

    /// <summary>The request as Codex sends it, carrying Codex's own headers and the token.</summary>
    private static HttpRequestMessage CodexRequest(HostedProviderEndpoint endpoint, byte[] body, string? token = "valid")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseAddress, "/zai/responses"))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", CodexUserAgent);
        request.Headers.TryAddWithoutValidation("originator", "phleet");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        request.Headers.TryAddWithoutValidation("session_id", "019a0000-0000-7000-8000-000000000001");
        if (token is not null)
            request.Headers.TryAddWithoutValidation(HostedProviderForwarder.TokenHeader, token == "valid" ? endpoint.Token : token);
        return request;
    }

    private static async Task<int> RawStatusAsync(HostedProviderEndpoint endpoint, string rawRequest)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, endpoint.BaseAddress.Port);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(rawRequest));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return int.Parse(statusLine!.Split(' ')[1]);
    }

    private static RecordingHandler NeverCalled() =>
        new((_, _) => throw new InvalidOperationException("the upstream must not be called"));

    private static HttpContent SseContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return content;
    }

    private static HttpContent SseContent(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return content;
    }

    internal static byte[] HostedProviderFixtureBytes(string name) =>
        File.ReadAllBytes(RepoPaths.Resolve($"tests/Fleet.Agent.Tests/Fixtures/HostedProviders/{name}"));

    /// <summary>One upstream request as the socket handler would have sent it.</summary>
    internal sealed record SentRequest(
        Uri? Uri, Dictionary<string, string> Headers, long? ContentLength, bool? Chunked, byte[] Body);

    internal sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // NonValidated: the raw strings as they go on the wire. Enumerating the validated view
            // would parse (and re-join) values such as User-Agent.
            foreach (var (name, values) in request.Headers.NonValidated)
                headers[name] = string.Join(", ", values);
            long? length = null;
            var body = Array.Empty<byte>();
            if (request.Content is not null)
            {
                foreach (var (name, values) in request.Content.Headers.NonValidated)
                    headers[name] = string.Join(", ", values);
                length = request.Content.Headers.ContentLength;
                body = await request.Content.ReadAsByteArrayAsync(ct);
            }
            lock (Requests)
                Requests.Add(new SentRequest(request.RequestUri, headers, length, request.Headers.TransferEncodingChunked, body));
            return await respond(request, ct);
        }
    }

    internal sealed class ListLoggerProvider : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = [];
        public IEnumerable<string> Lines { get { lock (_lines) return _lines.ToList(); } }
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }
}
