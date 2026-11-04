using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Services;
using Fleet.Memory.Tools;
using Fleet.Shared.Security;
using Fleet.StockIdentity;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Openshell.Middleware.V1;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Fleet.Memory.Tests;

/// <summary>
/// #518: <c>memory_stats</c> through the authenticated stock hook (<see cref="StockSupervisorMiddleware"/>), the outside
/// signer's assertion and the dedicated receiver (<see cref="StockMemoryIdentityMiddleware"/>) into the actual MCP tool over
/// real synthetic files. Managed counts use the verified identity and a fresh request-local ACL snapshot, so grant, project
/// and session changes apply on the very next request; an ACL or config outage is 503 before any scan.
/// </summary>
public sealed class ManagedMemoryStatsTests
{
    // Codex 0.164.0 native MCP on its default 2025-06-18 lifecycle, as in StockIdentityPathTests.
    private const string NativeInitialize = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"elicitation":{}},"clientInfo":{"name":"codex-mcp-client","title":"Codex","version":"0.164.0"}}}""";
    private const string NativeInitialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string NativeToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
    private const string Stats = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"memory_stats","arguments":{}}}""";
    private static string Call(string tool) =>
        """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"TOOL","arguments":{"id":"00000000-0000-0000-0000-000000000001"}}}""".Replace("TOOL", tool);

    private static async Task<string> Session(Fixture fixture)
    {
        var initialized = await fixture.Native(NativeInitialize, null);
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        var session = initialized.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Native(NativeInitialized, session)).StatusCode);
        return session;
    }

    /// <summary>The stats text a 200 response carries, decoded from its JSON-RPC result (SSE or plain JSON).</summary>
    private static async Task<string> Text(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = body.Split('\n').Select(l => l.StartsWith("data:", StringComparison.Ordinal) ? l[5..].Trim() : l.Trim())
            .Single(l => l.StartsWith('{'));
        var result = JsonDocument.Parse(json).RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), json);
        return result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static void NoHiddenValues(string text)
    {
        foreach (var hidden in StatsRecords.Hidden) Assert.DoesNotContain(hidden, text, StringComparison.OrdinalIgnoreCase);
    }

    // A2: an explicitly granted stats read under either accepted name returns only the verified caller's project-a counts,
    // unchanged by hidden project-b additions and deletions; the same grant under the other name never counts.
    [Theory]
    [InlineData("memory")]
    [InlineData("fleet-memory")]
    public async Task ManagedMemoryStats_granted_stats_returns_only_the_verified_callers_scoped_counts(string name)
    {
        await using var fixture = await Fixture.CreateAsync(name, ["memory_stats"]);
        var session = await Session(fixture);
        Assert.Contains("memory_stats", await (await fixture.Native(NativeToolsList, session)).Content.ReadAsStringAsync());

        var first = await Text(await fixture.Native(Stats, session));
        Assert.Equal(StatsRecords.ProjectA, first);
        NoHiddenValues(first);

        await StatsRecords.SaveAsync(fixture.Files, 10, "codebase_knowledge", "project-b");
        fixture.Files.Delete((await StatsRecords.SaveAsync(fixture.Files, 11, "task_result", "project-b")).FilePath, permanent: false);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(fixture.Root, "learning"), "*synthetic-record-5.md")) File.Delete(file);
        Assert.Equal(StatsRecords.ProjectA, await Text(await fixture.Native(Stats, session)));

        fixture.GrantName = name == "memory" ? "fleet-memory" : "memory";
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Native(Stats, session)).StatusCode);
    }

    // A3: the managed path never reads the ordinary cache: a widened stale cache changes nothing, and removing the project
    // from the agent's fresh ACL yields 0/0 on the next request without any Apply or re-initialization.
    [Fact]
    public async Task ManagedMemoryStats_project_removal_applies_on_the_next_request_and_a_stale_ordinary_cache_never_widens()
    {
        await using var fixture = await Fixture.CreateAsync("memory", ["memory_stats"]);
        var session = await Session(fixture);
        fixture.InjectWildcardIntoOrdinaryCache = true;
        Assert.Equal(StatsRecords.ProjectA, await Text(await fixture.Native(Stats, session)));

        fixture.Projects = [];
        var removed = await Text(await fixture.Native(Stats, session));
        Assert.Equal(StatsRecords.Nothing, removed);
        NoHiddenValues(removed);
    }

    // A3: removing the stats grant, revoking or re-epoching the session denies the very next call before aggregation.
    [Theory]
    [InlineData("grant")]
    [InlineData("revoked")]
    [InlineData("epoch")]
    public async Task ManagedMemoryStats_grant_or_session_revocation_denies_the_next_call(string change)
    {
        await using var fixture = await Fixture.CreateAsync("fleet-memory", ["memory_get", "memory_stats"]);
        var session = await Session(fixture);
        Assert.Equal(StatsRecords.ProjectA, await Text(await fixture.Native(Stats, session)));
        // Signed under the current record and grants, delivered only after the change below.
        var signed = await fixture.NativeHook(Stats, session);
        Assert.Equal(Decision.Allow, signed.Decision);

        switch (change)
        {
            case "grant": fixture.GrantedTools = ["memory_get"]; break;
            case "revoked": fixture.Save(fixture.Record with { State = "revoked" }); break;
            default: fixture.Save(fixture.Record with { Epoch = Guid.NewGuid() }); break;
        }

        var denied = await fixture.Deliver(Stats, session, signed);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("Total", await denied.Content.ReadAsStringAsync());
        // A fresh request: a revoked record is refused at the hook; under a new epoch the old-epoch session is refused.
        if (change == "revoked") Assert.Equal(Decision.Deny, (await fixture.NativeHook(Stats, session)).Decision);
        else Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Native(Stats, session)).StatusCode);
    }

    // A3: a fresh ACL or agent-config outage is 503 from the receiver before the tool runs; no stale fallback, no totals.
    [Theory]
    [InlineData("acl")]
    [InlineData("config")]
    public async Task ManagedMemoryStats_acl_or_config_outage_is_503_before_any_scan(string outage)
    {
        await using var fixture = await Fixture.CreateAsync("memory", ["memory_stats"]);
        var session = await Session(fixture);
        fixture.InjectWildcardIntoOrdinaryCache = true;
        if (outage == "acl") fixture.AclUnavailable = true; else fixture.ConfigUnavailable = true;

        var response = await fixture.Native(Stats, session);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("Total", await response.Content.ReadAsStringAsync());
    }

    // A6/A7: unknown tools, writes and unsupported methods stop at the hook; unsigned or query-spoofed requests stop at the
    // receiver or hook; one legacy three-read selection keeps working and its stats call is refused for lack of a grant.
    [Fact]
    public async Task ManagedMemoryStats_unknown_writes_spoofs_and_the_legacy_three_read_selection()
    {
        await using var fixture = await Fixture.CreateAsync("memory", ["memory_get", "memory_search", "memory_list"]);
        var session = await Session(fixture);

        foreach (var tool in new[] { "memory_unsupported", "memory_store", "memory_delete", "Memory_stats" })
            Assert.Equal("tool_unsupported", (await fixture.NativeHook(Call(tool), session)).ReasonCode);
        Assert.Equal("rpc_unsupported", (await fixture.NativeHook("""{"jsonrpc":"2.0","id":4,"method":"resources/list"}""", session)).ReasonCode);
        Assert.Equal("request_scope", (await fixture.NativeHook(Stats, session, query: "agent=agent2")).ReasonCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Unsigned(Stats, session)).StatusCode);

        Assert.Contains("synthetic record 1", await (await fixture.Native(Call("memory_get"), session)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Native(Stats, session)).StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "managed-stats-" + Guid.NewGuid().ToString("N"));
        private readonly Ed25519PrivateKeyParameters _gateway = new(RandomNumberGenerator.GetBytes(32));
        private readonly ECDsa _assertion = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly AclCacheService _acl;
        private readonly StockSupervisorMiddleware _hook;
        private TestServer _server = null!;
        private HttpClient _client = null!;
        public StockEnrollmentAuthority Record { get; private set; }
        public MemoryFileStore Files { get; private set; } = null!;
        public string Root => Path.Combine(_directory, "memories");
        public string EndpointName = "memory";
        public string? GrantName;
        public string[] GrantedTools = [];
        public string[] Projects = ["project-a"];
        public bool AclUnavailable, ConfigUnavailable, InjectWildcardIntoOrdinaryCache;
        private string FilePath => Path.Combine(_directory, "enrollment.json");

        private Fixture()
        {
            Directory.CreateDirectory(_directory);
            Record = new StockEnrollmentAuthority { Agent = "agent1", SandboxId = "sandbox1", Epoch = Guid.NewGuid(), State = "active",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GatewayIssuer = "synthetic-gateway", GatewayAudience = "synthetic-hook",
                GatewayJwksPath = Path.Combine(_directory, "jwks.json"), AssertionPrivateKeyPath = Path.Combine(_directory, "private.pem"),
                AssertionPublicKeyPath = Path.Combine(_directory, "public.pem"), MemoryHost = "memory.example.test" };
            File.WriteAllText(Record.GatewayJwksPath, JsonSerializer.Serialize(new { keys = new[] {
                new { kty = "OKP", crv = "Ed25519", kid = "synthetic", x = StockRequestProof.Encode(_gateway.GeneratePublicKey().GetEncoded()) } } }));
            File.WriteAllText(Record.AssertionPrivateKeyPath, _assertion.ExportECPrivateKeyPem());
            File.WriteAllText(Record.AssertionPublicKeyPath, _assertion.ExportSubjectPublicKeyInfoPem());
            Save(Record);
            _acl = new AclCacheService(Options.Create(new AclOptions { EnableProjectScopedAcl = true }),
                Options.Create(new OrchestratorOptions { BaseUrl = "https://acl.example.test" }), NullLogger<AclCacheService>.Instance,
                new HttpClient(new OrchestratorHandler(this)));
            _hook = new StockSupervisorMiddleware(new StockAuthorityFile(FilePath));
        }

        public static async Task<Fixture> CreateAsync(string endpointName, string[] grantedTools)
        {
            var fixture = new Fixture { EndpointName = endpointName, GrantedTools = grantedTools };
            fixture.Files = await StatsRecords.SeedAsync(fixture.Root);
            fixture.Start();
            return fixture;
        }

        private void Start()
        {
            var qdrant = Options.Create(new QdrantOptions { Url = "http://127.0.0.1:1", SimilarityThreshold = 0 });
            var memory = new MemoryService(Files, new VectorStore(qdrant, Options.Create(new EmbeddingOptions()), NullLogger<VectorStore>.Instance),
                Substitute.For<IEmbeddingService>(), qdrant, new MemorySizeGuidance(0, NullLogger.Instance), NullLogger<MemoryService>.Instance);
            _server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
            {
                services.AddRouting(); services.AddHttpContextAccessor();
                services.AddSingleton(_acl); services.AddSingleton(memory); services.AddSingleton<ReadCounterService>();
                services.AddMcpServer().WithHttpTransport().WithTools<MemoryGetTool>().WithTools<MemoryStatsTool>();
            }).Configure(app =>
            {
                // TestServer leaves RawTarget empty; production refuses an absent or noncanonical raw target.
                app.Use((context, next) =>
                {
                    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget = context.Request.Path + context.Request.QueryString;
                    return next(context);
                });
                app.Use(next =>
                {
                    var receiver = new StockMemoryIdentityMiddleware(FilePath, next);
                    return context => receiver.InvokeAsync(context, _acl);
                });
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
            }));
            _client = _server.CreateClient();
            _client.BaseAddress = new Uri("https://memory.example.test");
        }

        public void Save(StockEnrollmentAuthority record) { Record = record; File.WriteAllText(FilePath, JsonSerializer.Serialize(record)); }

        /// <summary>The evaluation the pinned stock supervisor builds for one native request, with a supervisor token.</summary>
        public Task<HttpRequestResult> NativeHook(string body, string? session, string query = "")
        {
            var request = new HttpRequestEvaluation { Phase = SupervisorMiddlewarePhase.PreCredentials,
                Config = new Struct { Fields = { ["service"] = Value.ForString("memory") } },
                Context = new RequestContext { RequestId = Guid.NewGuid().ToString(), SandboxId = "sandbox1", Sandbox = "synthetic-sandbox", Workspace = "synthetic-workspace" },
                Target = new HttpRequestTarget { Method = "POST", Scheme = "https", Host = Record.MemoryHost, Port = 443, Path = "/mcp", Query = query },
                Body = ByteString.CopyFromUtf8(body) };
            foreach (var (name, value) in NativeHeaders(session)) request.Headers.Add(new HttpHeader { Name = name, Value = value });
            return _hook.EvaluateHttpRequest(request, SupervisorContext());
        }

        /// <summary>The native request through the hook, then to the receiver with the original headers plus the hook's one mutation.</summary>
        public async Task<HttpResponseMessage> Native(string body, string? session)
        {
            var hook = await NativeHook(body, session);
            Assert.Equal(Decision.Allow, hook.Decision);
            return await Send(body, session, hook.HeaderMutations.Single().Write);
        }

        /// <summary>Delivers a request the hook already signed, so the receiver re-checks it against the current state.</summary>
        public Task<HttpResponseMessage> Deliver(string body, string? session, HttpRequestResult signed) =>
            Send(body, session, signed.HeaderMutations.Single().Write);

        /// <summary>A direct receiver request with no signed assertion.</summary>
        public Task<HttpResponseMessage> Unsigned(string body, string? session) => Send(body, session, null);

        private Task<HttpResponseMessage> Send(string body, string? session, WriteHeader? mutation)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in NativeHeaders(session).Where(h => h.Name != "content-type"))
                message.Headers.TryAddWithoutValidation(name, value);
            if (mutation is not null) message.Headers.Add(mutation.Name, mutation.Value);
            return _client.SendAsync(message);
        }

        // rmcp-client http_client_adapter.rs: default user-agent, protocol version once negotiated, accept, content type, session.
        private static IEnumerable<(string Name, string Value)> NativeHeaders(string? session)
        {
            yield return ("user-agent", "codex-mcp-client/0.164.0");
            if (session is not null) yield return ("mcp-protocol-version", "2025-06-18");
            yield return ("accept", "text/event-stream, application/json");
            yield return ("content-type", "application/json");
            if (session is not null) yield return ("mcp-session-id", session);
        }

        private CallContext SupervisorContext()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new Dictionary<string, object> {
                ["iss"] = Record.GatewayIssuer, ["aud"] = Record.GatewayAudience, ["sub"] = "spiffe://openshell/sandbox/sandbox1",
                ["iat"] = now - 2, ["exp"] = now + 60, ["jti"] = Guid.NewGuid().ToString("N"), ["caller_kind"] = "supervisor", ["sandbox_id"] = "sandbox1" };
            var unsigned = StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", typ = "openshell-ext+jwt", kid = "synthetic" }))
                + "." + StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
            var signer = new Ed25519Signer();
            signer.Init(true, _gateway);
            var bytes = Encoding.ASCII.GetBytes(unsigned); signer.BlockUpdate(bytes, 0, bytes.Length);
            return new CallContext(unsigned + "." + StockRequestProof.Encode(signer.GenerateSignature()));
        }

        public async ValueTask DisposeAsync() { _client.Dispose(); _server.Dispose(); await _acl.DisposeAsync(); _assertion.Dispose(); Directory.Delete(_directory, true); }

        /// <summary>The orchestrator as the receiver sees it: the agent's current config/grants and the fresh ACL map.</summary>
        private sealed class OrchestratorHandler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath.StartsWith("/api/agents/", StringComparison.Ordinal))
                {
                    // A widened ordinary cache that the managed path must never consult.
                    if (fixture.InjectWildcardIntoOrdinaryCache) fixture._acl.InjectAclForTesting(new() { ["agent1"] = ["*"] });
                    if (fixture.ConfigUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                        name = "agent1", isEnabled = true,
                        tools = fixture.GrantedTools.Select(tool => new { toolName = $"mcp__{fixture.GrantName ?? fixture.EndpointName}__{tool}", isEnabled = true }).ToArray(),
                        mcpEndpoints = new[] { new { mcpName = fixture.EndpointName, url = "https://memory.example.test/mcp", transportType = "http" } },
                    }) });
                }
                return Task.FromResult(new HttpResponseMessage(fixture.AclUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) {
                    Content = JsonContent.Create(new { acl = new Dictionary<string, string[]> { ["agent1"] = fixture.Projects } }) });
            }
        }
    }

    private sealed class CallContext : ServerCallContext
    {
        public CallContext(string token) { UserState["__HttpContext"] = new DefaultHttpContext { Request = { Scheme = "https" } }; Headers.Add("authorization", "Bearer " + token); }
        private Metadata Headers { get; } = new();
        protected override string MethodCore => "synthetic";
        protected override string HostCore => "hook.example.test";
        protected override string PeerCore => "synthetic";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddSeconds(2);
        protected override Metadata RequestHeadersCore => Headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new("synthetic", new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
