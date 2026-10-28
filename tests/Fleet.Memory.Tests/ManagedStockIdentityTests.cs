using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Shared.Security;
using Fleet.StockIdentity;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Openshell.Middleware.V1;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Struct = Google.Protobuf.WellKnownTypes.Struct;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Fleet.Memory.Tests;

/// <summary>#492 multi-agent authority lookup: the sandbox hint selects, the record still decides.</summary>
public sealed class ManagedStockIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("managed-authority-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string agent, string sandbox, string state = "active", int days = 1)
    {
        var path = Path.Combine(_dir, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            State = state, Agent = agent, SandboxId = sandbox, Epoch = Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(days), GatewayIssuer = "synthetic-gateway", GatewayAudience = "synthetic-hook",
            GatewayJwksPath = "/trust/gateway.jwks", AssertionPrivateKeyPath = "/outside/private.pem",
            AssertionPublicKeyPath = "/outside/public.pem", MemoryHost = "memory.example.test", BrokerHost = "broker.example.test",
            PolicySha256 = new string('b', 64), StockCommit = "bdb956f2cd65a635f1294fccf1d138d263bf650f",
        }));
        return path;
    }

    [Fact]
    public void Two_agents_resolve_to_their_own_records_by_sandbox()
    {
        Write("agent1", "agent1", "sandbox-1");
        Write("agent2", "agent2", "sandbox-2");
        Assert.Equal("agent1", StockAuthoritySource.Resolve("/unused", _dir, "sandbox-1").Agent);
        Assert.Equal("agent2", StockAuthoritySource.Resolve("/unused", _dir, "sandbox-2").Agent);
    }

    [Fact]
    public void Missing_ambiguous_revoked_expired_and_linked_records_never_match()
    {
        Write("revoked", "agent1", "sandbox-r", state: "revoked");
        Write("expired", "agent1", "sandbox-e", days: -1);
        Assert.Equal("authority_mapping_missing", Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.Resolve("/unused", _dir, "sandbox-r")).Message);
        Assert.Equal("authority_mapping_missing", Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.Resolve("/unused", _dir, "sandbox-e")).Message);

        var outside = Directory.CreateTempSubdirectory("outside-").FullName;
        try
        {
            var target = Path.Combine(outside, "x.json");
            File.Copy(Write("tmp", "agent3", "sandbox-l"), target);
            File.Delete(Path.Combine(_dir, "tmp.json"));
            File.CreateSymbolicLink(Path.Combine(_dir, "linked.json"), target);
            Assert.Equal("authority_mapping_missing", Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.Resolve("/unused", _dir, "sandbox-l")).Message);
        }
        finally { Directory.Delete(outside, recursive: true); }

        Write("a", "agent4", "sandbox-dup");
        Write("b", "agent5", "sandbox-dup");
        Assert.Equal("authority_mapping_ambiguous", Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.Resolve("/unused", _dir, "sandbox-dup")).Message);
    }

    [Fact]
    public void Without_a_directory_the_single_file_path_is_unchanged()
    {
        var file = Write("single", "agent1", "sandbox-1");
        Assert.Equal("agent1", StockAuthoritySource.Resolve(file, null, "any-hint").Agent);
    }

    [Fact]
    public void Assertion_sandbox_hint_is_read_but_never_trusted_alone()
    {
        static string Part(object value) => StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(value));
        var token = Part(new { alg = "ES256", typ = "stock-memory-v1" }) + "." + Part(new { Sandbox = "sandbox-1" }) + ".sig";
        Assert.Equal("sandbox-1", StockAuthoritySource.SandboxHint(token));
        Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.SandboxHint("not-a-token"));
        Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.SandboxHint(Part(new { }) + "." + Part(new { Agent = "x" }) + ".sig"));
    }
}

/// <summary>
/// #492 non-memory first-party receivers (temporal, telegram) on the shared receiving boundary: proofs bind
/// the receiving service, attribution comes only from the selected record, and tools/call names are limited
/// to the record's grants. Synthetic keys, hosts and records only; not a gateway or TLS qualification.
/// </summary>
public sealed class ManagedStockIdentityServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("managed-service-").FullName;
    private readonly Ed25519PrivateKeyParameters _gateway = new(RandomNumberGenerator.GetBytes(32));
    private readonly ECDsa _assertion = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, int> _executions = new();
    private readonly List<TestServer> _servers = [];
    private readonly StockSupervisorMiddleware _hook;

    private string Records => Path.Combine(_dir, "records");
    private string RecordPath => Path.Combine(Records, "agent1.json");
    private string Keys => Path.Combine(_dir, "keys");

    public ManagedStockIdentityServiceTests()
    {
        Directory.CreateDirectory(Records);
        Directory.CreateDirectory(Keys);
        File.WriteAllText(Path.Combine(Keys, "jwks.json"), JsonSerializer.Serialize(new { keys = new[] {
            new { kty = "OKP", crv = "Ed25519", kid = "synthetic", x = StockRequestProof.Encode(_gateway.GeneratePublicKey().GetEncoded()) } } }));
        File.WriteAllText(Path.Combine(Keys, "private.pem"), _assertion.ExportECPrivateKeyPem());
        File.WriteAllText(Path.Combine(Keys, "public.pem"), _assertion.ExportSubjectPublicKeyInfoPem());
        Save(RecordPath, Record());
        _hook = new StockSupervisorMiddleware(new StockAuthorityFile(RecordPath, Records));
    }

    public void Dispose()
    {
        foreach (var server in _servers) server.Dispose();
        _assertion.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private StockEnrollmentAuthority Record(string agent = "agent1", bool services = true) => new()
    {
        State = "active", Agent = agent, SandboxId = "sandbox-1", Epoch = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GatewayIssuer = "synthetic-gateway", GatewayAudience = "synthetic-hook",
        GatewayJwksPath = Path.Combine(Keys, "jwks.json"), AssertionPrivateKeyPath = Path.Combine(Keys, "private.pem"),
        AssertionPublicKeyPath = Path.Combine(Keys, "public.pem"), MemoryHost = "memory.example.test", BrokerHost = "broker.example.test",
        Services = services ? new Dictionary<string, StockServiceRoute>
        {
            ["temporal"] = new() { Host = "temporal.example.test", Tools = ["temporal_start_workflow"] },
            ["telegram"] = new() { Host = "telegram.example.test", Tools = ["send_message"], Query = "formatting_mode=0" },
        } : null,
    };

    private static void Save(string path, StockEnrollmentAuthority record) => File.WriteAllText(path, JsonSerializer.Serialize(record));

    private static byte[] Body(string rpc, string tool = "temporal_start_workflow") => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", id = 1, method = rpc, @params = rpc == "initialize"
            ? (object)new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "synthetic", version = "1" } }
            : new { name = tool, arguments = new { } },
    }));

    private async Task<HttpRequestResult> Hook(string service, string host, string rpc, string? session = null, string tool = "temporal_start_workflow")
    {
        var request = new HttpRequestEvaluation
        {
            Phase = SupervisorMiddlewarePhase.PreCredentials,
            Config = new Struct { Fields = { ["service"] = ProtoValue.ForString(service) } },
            Context = new RequestContext { SandboxId = "sandbox-1" },
            Target = new HttpRequestTarget { Method = "POST", Scheme = "https", Host = host, Port = 443, Path = "/mcp", Query = "" },
            Body = ByteString.CopyFrom(Body(rpc, tool)),
        };
        if (session is not null) request.Headers.Add(new HttpHeader { Name = "Mcp-Session-Id", Value = session });
        return await _hook.EvaluateHttpRequest(request, new CallContext(GatewayToken("supervisor")));
    }

    private async Task<string> Token(string service, string host, string rpc = "initialize")
    {
        var result = await Hook(service, host, rpc);
        Assert.Equal(Decision.Allow, result.Decision);
        return result.HeaderMutations.Single().Write.Value;
    }

    private string GatewayToken(string kind)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = "synthetic-gateway", ["aud"] = "synthetic-hook", ["iat"] = now - 2, ["exp"] = now + 60,
            ["jti"] = Guid.NewGuid().ToString("N"), ["caller_kind"] = kind,
            ["sub"] = kind == "gateway" ? "synthetic-gateway" : "spiffe://openshell/sandbox/sandbox-1",
        };
        if (kind == "supervisor") claims["sandbox_id"] = "sandbox-1";
        var unsigned = StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", typ = "openshell-ext+jwt", kid = "synthetic" }))
            + "." + StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signer = new Ed25519Signer(); signer.Init(true, _gateway);
        var bytes = Encoding.ASCII.GetBytes(unsigned); signer.BlockUpdate(bytes, 0, bytes.Length);
        return unsigned + "." + StockRequestProof.Encode(signer.GenerateSignature());
    }

    /// <summary>The real opt-in wiring (<see cref="StockServiceIdentityExtensions.UseStockServiceIdentity"/>) before an echoing tool host.</summary>
    private HttpClient Receiver(string service, string host, bool configured = true)
    {
        var settings = new Dictionary<string, string?>();
        if (configured)
        {
            settings[StockServiceIdentityMiddleware.AuthorityFileKey] = RecordPath;
            settings[StockServiceIdentityMiddleware.AuthorityDirectoryKey] = Records;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var server = new TestServer(new WebHostBuilder().Configure(app =>
        {
            app.Use((context, next) =>
            {
                // TestServer leaves RawTarget empty; production refuses an absent/noncanonical raw target.
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget = context.Request.Path + context.Request.QueryString;
                return next(context);
            });
            Assert.Equal(configured, app.UseStockServiceIdentity(configuration, service));
            app.Run(async context =>
            {
                _executions[service] = _executions.GetValueOrDefault(service) + 1;
                if (!context.Request.Headers.ContainsKey("Mcp-Session-Id")) context.Response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
                // What the service's existing tools read as the caller (Request.Query["agent"], formatting_mode).
                await context.Response.WriteAsync(context.Request.QueryString.Value ?? "");
            });
        }));
        _servers.Add(server);
        var client = server.CreateClient();
        client.BaseAddress = new Uri("https://" + host);
        return client;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, byte[] body, string? token, string url = "/mcp",
        string? session = null, (string Name, string Value)? header = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new("application/json");
        if (token is not null) message.Headers.Add(StockRequestProof.Header, token);
        if (session is not null) message.Headers.Add("Mcp-Session-Id", session);
        if (header is { } extra) message.Headers.TryAddWithoutValidation(extra.Name, extra.Value);
        return client.SendAsync(message);
    }

    [Fact]
    public async Task Valid_service_proof_attributes_only_the_record_agent_and_refuses_caller_identity()
    {
        var temporal = Receiver("temporal", "temporal.example.test");
        var telegram = Receiver("telegram", "telegram.example.test");

        var ok = await Post(temporal, Body("initialize"), await Token("temporal", "temporal.example.test"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("?agent=agent1", await ok.Content.ReadAsStringAsync());
        // Telegram's fixed formatting mode comes from the record too, never the workload.
        var sent = await Post(telegram, Body("initialize"), await Token("telegram", "telegram.example.test"));
        Assert.Equal("?agent=agent1&formatting_mode=0", await sent.Content.ReadAsStringAsync());

        // A caller-supplied agent query or X-Fleet-* identity never reaches the tools, even with a valid proof.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(temporal, Body("initialize"), await Token("temporal", "temporal.example.test"), "/mcp?agent=agent2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(temporal, Body("initialize"), await Token("temporal", "temporal.example.test"),
            header: ("X-Fleet-Agent", "agent2"))).StatusCode);
        // Legacy/internal paths and unsigned requests are inaccessible in this mode.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(temporal, Body("initialize"), await Token("temporal", "temporal.example.test"), "/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(temporal, Body("initialize"), null)).StatusCode);
        Assert.Equal((1, 1), (_executions["temporal"], _executions["telegram"]));
    }

    [Fact]
    public async Task Proof_issued_for_one_service_is_refused_at_another()
    {
        var telegram = Receiver("telegram", "telegram.example.test");
        var temporal = Receiver("temporal", "temporal.example.test");

        // Correct receiving host and a valid signature: only the bound service differs.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(telegram, Body("initialize"), await Token("temporal", "temporal.example.test"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(temporal, Body("initialize"), await Token("memory", "memory.example.test"))).StatusCode);
        Assert.Throws<StockIdentityDenied>(() => StockRequestProof.Verify(Record(), "telegram",
            StockRequestProof.Issue(Record(), "temporal", "POST", Body("initialize"), null), "POST", Body("initialize"), null));
        Assert.False(_executions.ContainsKey("telegram") || _executions.ContainsKey("temporal"));
    }

    [Fact]
    public async Task Ungranted_tool_or_unenrolled_service_is_refused_at_the_hook_and_receiver()
    {
        Assert.Equal(Decision.Allow, (await Hook("temporal", "temporal.example.test", "tools/call", "s1", "temporal_start_workflow")).Decision);
        Assert.Equal(Decision.Deny, (await Hook("temporal", "temporal.example.test", "tools/call", "s1", "temporal_terminate_workflow")).Decision);
        // Grants are per service: a tool granted on telegram is not callable on temporal.
        Assert.Equal(Decision.Deny, (await Hook("temporal", "temporal.example.test", "tools/call", "s1", "send_message")).Decision);
        Assert.Equal(Decision.Deny, (await Hook("temporal", "telegram.example.test", "initialize")).Decision); // host from the record only
        Assert.Equal(Decision.Deny, (await Hook("journal", "temporal.example.test", "initialize")).Decision);
        // The fixed first-party set is valid hook configuration; anything else is not.
        foreach (var (service, valid) in new[] { ("memory", true), ("temporal", true), ("telegram", true), ("journal", false) })
        {
            var response = await _hook.ValidateConfig(new ValidateConfigRequest { Config = new Struct { Fields = { ["service"] = ProtoValue.ForString(service) } } },
                new CallContext(GatewayToken("gateway")));
            Assert.Equal(valid, response.Valid);
        }

        // Even a validly signed proof for an ungranted tool refuses before session lookup or execution.
        var temporal = Receiver("temporal", "temporal.example.test");
        var body = Body("tools/call", "temporal_terminate_workflow");
        var forged = StockRequestProof.Issue(Record(), "temporal", "POST", body, "s1");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(temporal, body, forged, session: "s1")).StatusCode);

        // A memory-only record (no Services) enrolls no other service.
        Save(RecordPath, Record(services: false));
        Assert.Equal(Decision.Deny, (await Hook("temporal", "temporal.example.test", "initialize")).Decision);
        Assert.False(_executions.ContainsKey("temporal"));
    }

    [Fact]
    public async Task Missing_or_ambiguous_record_refuses_at_the_hook_and_receiver()
    {
        var temporal = Receiver("temporal", "temporal.example.test");
        var token = await Token("temporal", "temporal.example.test");
        Save(Path.Combine(Records, "agent2.json"), Record(agent: "agent2")); // a second record claims the same sandbox
        Assert.Equal(Decision.Deny, (await Hook("temporal", "temporal.example.test", "initialize")).Decision);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(temporal, Body("initialize"), token)).StatusCode);

        File.Delete(Path.Combine(Records, "agent2.json"));
        token = await Token("temporal", "temporal.example.test");
        File.Delete(RecordPath);
        Assert.Equal(Decision.Deny, (await Hook("temporal", "temporal.example.test", "initialize")).Decision);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(temporal, Body("initialize"), token)).StatusCode);
        Assert.False(_executions.ContainsKey("temporal"));
    }

    [Fact]
    public async Task Unconfigured_receiver_keeps_the_ordinary_route_unchanged()
    {
        var temporal = Receiver("temporal", "temporal.example.test", configured: false);
        var ordinary = await Post(temporal, Body("initialize"), null, "/?agent=agent2");
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        Assert.Equal("?agent=agent2", await ordinary.Content.ReadAsStringAsync());

        var relative = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [StockServiceIdentityMiddleware.AuthorityFileKey] = "relative.json" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ApplicationBuilder(new ServiceCollection().BuildServiceProvider())
            .UseStockServiceIdentity(relative, "telegram"));
        Assert.Throws<ArgumentException>(() => new StockServiceIdentityMiddleware("journal", RecordPath));
    }

    [Fact]
    public void Records_without_services_still_load_and_malformed_routes_refuse()
    {
        var plain = JsonSerializer.SerializeToNode(Record(services: false))!.AsObject();
        plain.Remove("Services"); // an older record has no such member at all
        File.WriteAllText(RecordPath, plain.ToJsonString());
        Assert.Null(StockEnrollmentAuthority.Load(RecordPath).Services);
        Save(RecordPath, Record());
        Assert.Equal(["temporal_start_workflow"], StockEnrollmentAuthority.Load(RecordPath).ServiceTools("temporal"));

        StockServiceRoute route = new() { Host = "temporal.example.test", Tools = ["temporal_start_workflow"] };
        foreach (var bad in new Dictionary<string, StockServiceRoute>[]
        {
            new() { ["journal"] = route },                                                  // no receiving identity
            new() { ["temporal"] = route with { Host = "memory.example.test" } },          // host shared with memory
            new() { ["temporal"] = route with { Host = "not a host" } },
            new() { ["temporal"] = route with { Tools = ["a", "a"] } },
            new() { ["temporal"] = route with { Tools = ["bad/tool"] } },
            new() { ["temporal"] = route with { Query = "formatting_mode=1" } },            // only telegram has a fixed query
            new() { ["telegram"] = route with { Host = "telegram.example.test", Query = "agent=agent2" } },
        })
        {
            Save(RecordPath, Record() with { Services = bad });
            Assert.Equal("enrollment_denied", Assert.Throws<StockIdentityDenied>(() => StockEnrollmentAuthority.Load(RecordPath)).Message);
        }
        var duplicate = JsonSerializer.SerializeToNode(Record())!.ToJsonString()
            .Replace("\"telegram\":{", "\"temporal\":{\"Host\":\"other.example.test\",\"Tools\":[],\"Query\":\"\"},\"telegram\":{");
        File.WriteAllText(RecordPath, duplicate);
        Assert.Throws<StockIdentityDenied>(() => StockEnrollmentAuthority.Load(RecordPath));
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
