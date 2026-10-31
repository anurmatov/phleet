using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Memory.Configuration;
using Fleet.Memory.Services;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Tools;
using NSubstitute;
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
using Openshell.Middleware.V1;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Fleet.Memory.Tests;

public sealed class StockIdentityPathTests
{
    [Fact]
    public async Task AuthenticatedHook_OverwritesSpoof_ReceiverUsesActualAcl_RevocationDenies()
    {
        await using var fixture = new Fixture();
        var initialized = await fixture.Send("initialize");
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        Assert.Equal("agent1", await initialized.Content.ReadAsStringAsync());
        var session = initialized.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.Equal(HttpStatusCode.OK, (await fixture.Send("tools/call", session)).StatusCode);
        fixture.ProjectAllowed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Send("tools/call", session)).StatusCode);
        fixture.ProjectAllowed = true;
        fixture.Save(fixture.Record with { State = "revoked" });
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Send("tools/call", session)).StatusCode);
        Assert.Equal(3, fixture.Executions); // initialize + the allowed and ACL-denied tool requests only.
        Assert.Equal(3, fixture.AclFetches);
    }

    [Theory]
    [InlineData("wrong_sandbox")]
    [InlineData("expired_gateway")]
    [InlineData("wrong_audience")]
    [InlineData("wrong_signature")]
    [InlineData("query_spoof")]
    public async Task HookWrongIdentityOrScope_DeniesBeforeReceiver(string fault)
    {
        await using var fixture = new Fixture();
        var result = await fixture.Hook("initialize", null, fault);
        Assert.Equal(Decision.Deny, result.Decision);
        Assert.Equal(0, fixture.Executions);
        Assert.Equal(0, fixture.AclFetches);
    }

    [Theory]
    [InlineData("missing_assertion")]
    [InlineData("query_spoof")]
    [InlineData("wrong_body")]
    [InlineData("wrong_session")]
    [InlineData("wrong_epoch")]
    [InlineData("expired_assertion")]
    [InlineData("plaintext")]
    [InlineData("internal_path")]
    public async Task ReceiverDirectOrChangedBinding_DeniesBeforeAclOrExecution(string fault)
    {
        await using var fixture = new Fixture();
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Send("initialize", null, fault)).StatusCode);
        Assert.Equal(0, fixture.Executions);
        Assert.Equal(0, fixture.AclFetches);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("agent")]
    [InlineData("sandbox")]
    public async Task ReceiverReloadsAuthority_AfterHookIssued_BeforeAnyExecution(string change)
    {
        await using var fixture = new Fixture();
        var hook = await fixture.Hook("initialize", null);
        Assert.Equal(Decision.Allow, hook.Decision);
        var token = hook.HeaderMutations.Single().Write.Value;
        fixture.Save(change switch
        {
            "revoked" => fixture.Record with { State = "revoked" },
            "expired" => fixture.Record with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) },
            "agent" => fixture.Record with { Agent = "agent2" },
            _ => fixture.Record with { SandboxId = "sandbox2" }
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Raw(Fixture.Body("initialize"), null, token)).StatusCode);
        Assert.Equal(0, fixture.Executions);
        Assert.Equal(0, fixture.AclFetches);
    }

    [Fact]
    public async Task Replay_UnknownSession_Termination_Reinitialize_AndAclFailure_AreExplicit()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Hook("initialize", null);
        var token = result.HeaderMutations.Single().Write.Value;
        Assert.Equal(HttpStatusCode.OK, (await fixture.Raw(Fixture.Body("initialize"), null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Raw(Fixture.Body("initialize"), null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Send("tools/call", "unknown-session")).StatusCode);
        var initialized = await fixture.Send("initialize");
        var session = initialized.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.Equal(HttpStatusCode.OK, (await fixture.Send("DELETE", session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Send("tools/call", session)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Send("initialize")).StatusCode);
        fixture.AclUnavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.Send("initialize")).StatusCode);
    }

    [Fact]
    public async Task ValidRpcBodyOrSessionDrift_AndPriorEpochSession_CannotReachExecution()
    {
        await using var fixture = new Fixture();
        var initialized = await fixture.Send("initialize");
        var session = initialized.Headers.GetValues("Mcp-Session-Id").Single();
        var hook = await fixture.Hook("tools/call", session);
        var token = hook.HeaderMutations.Single().Write.Value;
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Raw(Fixture.Body("tools/call", "foreign_project"), session, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Raw(Fixture.Body("tools/call"), session + "-changed", token)).StatusCode);
        var parts = token.Split('.');
        var signature = StockRequestProof.Decode(parts[2]); signature[0] ^= 1;
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Raw(Fixture.Body("tools/call"), session,
            parts[0] + "." + parts[1] + "." + StockRequestProof.Encode(signature))).StatusCode);
        fixture.Save(fixture.Record with { Epoch = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Send("tools/call", session)).StatusCode);
        Assert.Equal(1, fixture.Executions);
        Assert.Equal(1, fixture.GrantFetches);
    }

    [Fact]
    public async Task ExistingToolGrantRevocation_AndForeignEndpoint_RefuseBeforeExecution()
    {
        await using var fixture = new Fixture();
        var initialized = await fixture.Send("initialize");
        var session = initialized.Headers.GetValues("Mcp-Session-Id").Single();
        fixture.ToolAllowed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Send("tools/call", session)).StatusCode);
        fixture.ToolAllowed = true;
        fixture.EndpointAllowed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Send("tools/call", session)).StatusCode);
        Assert.Equal(1, fixture.Executions);
        Assert.Equal(3, fixture.GrantFetches);
    }

    [Fact]
    public async Task ActualMapMcp_MemoryGetUsesVerifiedIdentityAndFreshAcl_ThenRevoke()
    {
        await using var fixture = new Fixture(actualMcp: true);
        var initialized = await fixture.Send("initialize");
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        var session = initialized.Headers.GetValues("Mcp-Session-Id").Single();
        var read = await fixture.Send("tools/call", session);
        Assert.Contains("synthetic-visible-memory", await read.Content.ReadAsStringAsync());
        var foreign = await fixture.Send("tools/call", session, "foreign_project");
        Assert.Contains("access denied", await foreign.Content.ReadAsStringAsync());
        fixture.ProjectAllowed = false;
        fixture.InjectStaleAclOnGrantFetch = true;
        var changed = await fixture.Send("tools/call", session);
        Assert.Contains("access denied", await changed.Content.ReadAsStringAsync());
        fixture.Save(fixture.Record with { State = "revoked" });
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Send("tools/call", session)).StatusCode);
        Assert.Equal(4, fixture.AclFetches);
    }

    // #503 W11: Codex 0.164.0 (6aaac694) native MCP on its default legacy 2025-06-18 lifecycle.
    private const string NativeInitialize = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"elicitation":{}},"clientInfo":{"name":"codex-mcp-client","title":"Codex","version":"0.164.0"}}}""";
    private const string NativeInitialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string NativeToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
    private static string NativeCall(string tool) =>
        """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"TOOL","arguments":{"id":"00000000-0000-0000-0000-000000000001"}}}""".Replace("TOOL", tool);

    [Fact]
    public async Task NativeCodexInitialization_ReachesMemoryGet_AndRefusalsReportTheirExistingCode()
    {
        await using var fixture = new Fixture(actualMcp: true);
        var initialized = await fixture.Native(NativeInitialize, null);
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        var session = initialized.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Native(NativeInitialized, session)).StatusCode);
        Assert.Contains("memory_get", await (await fixture.Native(NativeToolsList, session)).Content.ReadAsStringAsync());
        Assert.Contains("synthetic-visible-memory", await (await fixture.Native(NativeCall("memory_get"), session)).Content.ReadAsStringAsync());
        Assert.Equal(Decision.Allow, (await fixture.NativeHook("", session, "GET")).Decision); // the standalone event stream

        // The same native shape keeps every restriction; a refusal now reports its existing code.
        Assert.Equal("identity_override", (await fixture.NativeHook(NativeInitialize, null, extra: ("x-fleet-agent", "agent2"))).ReasonCode);
        Assert.Equal("request_scope", (await fixture.NativeHook(NativeInitialize, null, query: "agent=agent2")).ReasonCode);
        Assert.Equal("gateway_token", (await fixture.NativeHook(NativeInitialize, null, sandbox: "sandbox2")).ReasonCode);
        Assert.Equal("session_required", (await fixture.NativeHook(NativeToolsList, null)).ReasonCode);
        Assert.Equal("tool_unsupported", (await fixture.NativeHook(NativeCall("memory_store"), session)).ReasonCode);
        // An unexpected failure (an unparseable body) keeps the generic code.
        Assert.Equal("identity_denied", (await fixture.NativeHook("{", session)).ReasonCode);
        Assert.Equal(4, fixture.AclFetches);
    }

    [Fact]
    public async Task UnsupportedWrite_DeniesAtHook()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Hook("tools/call", "a-session", "write");
        Assert.Equal(Decision.Deny, result.Decision);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("active")]
    public async Task GatewayBootstrap_WithNoAllocatedSandbox_NegotiatesButCannotEvaluateOrReceive(string state)
    {
        await using var fixture = new Fixture();
        var issued = await fixture.Hook("initialize", null);
        var signed = issued.HeaderMutations.Single().Write.Value;
        fixture.Save(fixture.Record with { State = state, SandboxId = "", Epoch = Guid.Empty,
            ExpiresAt = DateTimeOffset.MinValue });
        fixture.RemoveSigningKeys(); // Manifest/config authentication needs only configured gateway trust.
        Assert.Equal("stock-memory", (await fixture.Describe()).Name);
        Assert.True((await fixture.ValidateConfig()).Valid);
        Assert.Equal(Decision.Deny, (await fixture.Hook("initialize", null)).Decision);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Raw(Fixture.Body("initialize"), null, signed)).StatusCode);
        Assert.Equal(0, fixture.Executions);
        Assert.Equal(0, fixture.AclFetches);
    }

    [Theory]
    [InlineData("plaintext")]
    [InlineData("missing_jwt")]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_audience")]
    [InlineData("wrong_signature")]
    [InlineData("expired_gateway")]
    [InlineData("malformed_jwt")]
    [InlineData("duplicate_jwt")]
    [InlineData("missing_issuer")]
    [InlineData("missing_audience")]
    [InlineData("missing_jwks")]
    [InlineData("gateway_wrong_subject")]
    [InlineData("gateway_with_sandbox")]
    public async Task GatewayBootstrap_BadTrustOrAuthentication_RefusesBothMethods(string fault)
    {
        await using var fixture = new Fixture();
        if (fault == "missing_issuer") fixture.Save(fixture.Record with { GatewayIssuer = "" });
        if (fault == "missing_audience") fixture.Save(fixture.Record with { GatewayAudience = "" });
        if (fault == "missing_jwks") fixture.RemoveGatewayKey();
        var describe = await Assert.ThrowsAsync<RpcException>(() => fixture.Describe(fault));
        var validate = await Assert.ThrowsAsync<RpcException>(() => fixture.ValidateConfig(fault));
        Assert.Equal(StatusCode.Unauthenticated, describe.StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, validate.StatusCode);
        Assert.Equal(0, fixture.Executions);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("active")]
    public async Task SupervisorBootstrap_UsesPinnedSubjectWithoutActiveAllocation_ButCannotEvaluate(string state)
    {
        await using var fixture = new Fixture();
        fixture.Save(fixture.Record with { State = state, SandboxId = "", Epoch = Guid.Empty,
            ExpiresAt = DateTimeOffset.MinValue });
        Assert.Equal("stock-memory", (await fixture.Describe("supervisor")).Name);
        Assert.True((await fixture.ValidateConfig("supervisor")).Valid);
        Assert.Equal(Decision.Deny, (await fixture.Hook("initialize", null)).Decision);
        Assert.Equal(0, fixture.Executions);
        Assert.Equal(0, fixture.AclFetches);
    }

    [Theory]
    [InlineData("supervisor_foreign")]
    [InlineData("supervisor_no_id")]
    [InlineData("supervisor_empty_id")]
    [InlineData("supervisor_subject")]
    [InlineData("supervisor_gateway_subject")]
    [InlineData("supervisor_plaintext")]
    [InlineData("supervisor_missing_jwt")]
    [InlineData("supervisor_wrong_signature")]
    [InlineData("supervisor_wrong_audience")]
    [InlineData("supervisor_expired_gateway")]
    public async Task SupervisorBootstrap_RejectsForeignMalformedOrUnauthenticatedCallers(string fault)
    {
        await using var fixture = new Fixture();
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(() => fixture.Describe(fault))).StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(() => fixture.ValidateConfig(fault))).StatusCode);
        Assert.Equal(0, fixture.Executions);
    }

    // #503 C19: the gateway registration's audience must equal the outside GatewayAudience exactly; there is no default.
    [Theory]
    [InlineData("synthetic-hook", true)]
    [InlineData("Synthetic-Hook", false)]
    [InlineData("synthetic-hook ", false)]
    [InlineData("urn:openshell:extension:middleware:stock-memory", false)]
    public async Task GatewayNegotiation_AcceptsOnlyTheExactOutsideAudience(string audience, bool accepted)
    {
        await using var fixture = new Fixture();
        if (accepted)
        {
            Assert.Equal("synthetic-hook", (await fixture.Describe("aud:" + audience)).ExpectedAudience);
            Assert.True((await fixture.ValidateConfig("aud:" + audience)).Valid);
        }
        else
        {
            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(() => fixture.Describe("aud:" + audience))).StatusCode);
            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(() => fixture.ValidateConfig("aud:" + audience))).StatusCode);
        }
        Assert.Equal(0, fixture.Executions);
    }

    [Fact]
    public async Task GatewayNegotiation_StockDefaultAudienceWorksOnlyWhenItIsTheOutsideAudience()
    {
        const string stockDefault = "urn:openshell:extension:middleware:stock-memory";
        await using var fixture = new Fixture();
        fixture.Save(fixture.Record with { GatewayAudience = stockDefault });
        Assert.Equal(stockDefault, (await fixture.Describe("aud:" + stockDefault)).ExpectedAudience);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(() => fixture.Describe("aud:synthetic-hook"))).StatusCode);
    }

    [Fact]
    public async Task GatewayBootstrap_UnsupportedProtocol_StillRefuses()
    {
        await using var fixture = new Fixture();
        fixture.Save(fixture.Record with { State = "revoked", SandboxId = "" });
        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.Describe("wrong_protocol"));
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("wrong_type")]
    [InlineData("wrong_service")]
    public async Task GatewayBootstrap_MalformedConfig_RemainsInvalid(string fault)
    {
        await using var fixture = new Fixture();
        fixture.Save(fixture.Record with { State = "revoked", SandboxId = "" });
        Assert.False((await fixture.ValidateConfig(configFault: fault)).Valid);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "stock-identity-" + Guid.NewGuid().ToString("N"));
        private readonly Ed25519PrivateKeyParameters _gateway = new(RandomNumberGenerator.GetBytes(32));
        private readonly ECDsa _assertion = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly TestServer _server;
        private readonly AclCacheService _acl;
        private readonly HttpClient _client;
        private readonly StockSupervisorMiddleware _hook;
        public StockEnrollmentAuthority Record { get; private set; }
        public int Executions, AclFetches, GrantFetches;
        public bool ProjectAllowed = true, ToolAllowed = true, EndpointAllowed = true, AclUnavailable, InjectStaleAclOnGrantFetch;
        private string FilePath => Path.Combine(_directory, "enrollment.json");
        public Fixture(bool actualMcp = false)
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
            var aclHttp = new HttpClient(new AclHandler(this));
            _acl = new AclCacheService(Options.Create(new AclOptions { EnableProjectScopedAcl = true }),
                Options.Create(new OrchestratorOptions { BaseUrl = "https://acl.example.test" }), NullLogger<AclCacheService>.Instance, aclHttp);
            _hook = new StockSupervisorMiddleware(new StockAuthorityFile(FilePath));
            var files = new MemoryFileStore(Options.Create(new StorageOptions { Path = Path.Combine(_directory, "memories") }), NullLogger<MemoryFileStore>.Instance);
            files.EnsureDirectories();
            files.SaveAsync(new MemoryDocument { Id = "00000000-0000-0000-0000-000000000001", Title = "synthetic-visible-memory", Content = "synthetic content", Project = "alpha", Type = "learning", Agent = "agent1" }).GetAwaiter().GetResult();
            files.SaveAsync(new MemoryDocument { Id = "00000000-0000-0000-0000-000000000002", Title = "synthetic-foreign-memory", Content = "foreign content", Project = "beta", Type = "learning", Agent = "agent2" }).GetAwaiter().GetResult();
            var qdrant = Options.Create(new QdrantOptions { Url = "http://127.0.0.1:1", SimilarityThreshold = 0 });
            var memory = new MemoryService(files, new VectorStore(qdrant, Options.Create(new EmbeddingOptions()), NullLogger<VectorStore>.Instance),
                Substitute.For<IEmbeddingService>(), qdrant, new MemorySizeGuidance(0, NullLogger.Instance), NullLogger<MemoryService>.Instance);
            _server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
            {
                services.AddRouting(); services.AddHttpContextAccessor();
                services.AddSingleton(_acl); services.AddSingleton(memory); services.AddSingleton<ReadCounterService>();
                services.AddMcpServer().WithHttpTransport().WithTools<MemoryGetTool>();
            }).Configure(app =>
            {
                app.Use((context, next) =>
                {
                    // TestServer leaves RawTarget empty. Populate the synthetic wire target explicitly;
                    // production continues to refuse an absent/noncanonical raw target.
                    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget =
                        context.Request.Path + context.Request.QueryString;
                    return next(context);
                });
                app.Use(next =>
                {
                    var receiver = new StockMemoryIdentityMiddleware(FilePath, next);
                    return context => receiver.InvokeAsync(context, _acl);
                });
                if (actualMcp)
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
                }
                else app.Run(async context =>
                {
                    Executions++;
                    if (!context.Request.Headers.ContainsKey("Mcp-Session-Id")) context.Response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
                    var agent = context.Request.Query["agent"].ToString();
                    if (!_acl.CanRead(agent, "alpha").allowed) context.Response.StatusCode = 403;
                    await context.Response.WriteAsync(agent);
                });
            }));
            _client = _server.CreateClient(); _client.BaseAddress = new Uri("https://memory.example.test");
        }
        public void RemoveSigningKeys() { File.Delete(Record.AssertionPrivateKeyPath); File.Delete(Record.AssertionPublicKeyPath); }
        public void RemoveGatewayKey() => File.Delete(Record.GatewayJwksPath);
        private CallContext GatewayContext(string? fault)
        {
            var supervisor = fault?.StartsWith("supervisor", StringComparison.Ordinal) == true;
            if (supervisor && fault!.StartsWith("supervisor_", StringComparison.Ordinal)) fault = fault[11..];
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new Dictionary<string, object> {
                ["iss"] = fault == "wrong_issuer" ? "foreign" : Record.GatewayIssuer,
                ["aud"] = fault == "wrong_audience" ? "foreign" : fault?.StartsWith("aud:", StringComparison.Ordinal) == true ? fault[4..] : Record.GatewayAudience,
                ["sub"] = "synthetic-gateway", ["iat"] = now - 2,
                ["exp"] = fault == "expired_gateway" ? now - 1 : now + 60,
                ["jti"] = Guid.NewGuid().ToString("N"), ["caller_kind"] = supervisor ? "supervisor" : "gateway" };
            if (fault == "gateway_wrong_subject") claims["sub"] = "foreign-subject";
            if (fault == "gateway_with_sandbox") claims["sandbox_id"] = "sandbox1";
            if (supervisor)
            {
                var id = fault == "foreign" ? "sandbox2" : fault == "empty_id" ? "" : "sandbox1";
                if (fault != "no_id") claims["sandbox_id"] = id;
                claims["sub"] = fault == "subject" ? "foreign-subject" : fault == "gateway_subject"
                    ? Record.GatewayIssuer : "spiffe://openshell/sandbox/" + id;
            }
            var unsigned = StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", typ = "openshell-ext+jwt", kid = "synthetic" }))
                + "." + StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
            var signer = new Ed25519Signer();
            signer.Init(true, fault == "wrong_signature" ? new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32)) : _gateway);
            var bytes = Encoding.ASCII.GetBytes(unsigned); signer.BlockUpdate(bytes, 0, bytes.Length);
            var context = new CallContext(unsigned + "." + StockRequestProof.Encode(signer.GenerateSignature()));
            if (fault == "duplicate_jwt") context.RequestHeaders.Add("authorization", "Bearer duplicate");
            if (fault == "plaintext") context.GetHttpContext().Request.Scheme = "http";
            if (fault == "missing_jwt") context.RequestHeaders.Clear();
            if (fault == "malformed_jwt") { context.RequestHeaders.Clear(); context.RequestHeaders.Add("authorization", "Bearer malformed"); }
            return context;
        }
        public Task<MiddlewareManifest> Describe(string? fault = null) => _hook.Describe(new MiddlewareDescribeRequest {
            Gateway = new Openshell.Extension.V1.PeerMetadata {
                ProtocolVersion = new Openshell.Extension.V1.ProtocolVersion { Major = fault == "wrong_protocol" ? 2U : 1U, Minor = 0 },
                SupportedCapabilities = { "openshell.supervisor-middleware.contract" } } }, GatewayContext(fault));
        public Task<ValidateConfigResponse> ValidateConfig(string? fault = null, string? configFault = null)
        {
            var config = new Struct();
            if (configFault != "missing") config.Fields["service"] = configFault == "wrong_type" ? Value.ForBool(true)
                : Value.ForString(configFault == "wrong_service" ? "foreign" : "memory");
            if (configFault == "extra") config.Fields["extra"] = Value.ForString("foreign");
            return _hook.ValidateConfig(new ValidateConfigRequest { Config = config }, GatewayContext(fault));
        }
        public void Save(StockEnrollmentAuthority record) { Record = record; File.WriteAllText(FilePath, JsonSerializer.Serialize(record)); }
        public static byte[] Body(string method, string? fault = null) => method == "DELETE" ? [] : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            jsonrpc = "2.0", id = 1, method, @params = method == "initialize"
                ? (object)new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "synthetic", version = "1" } }
                : new { name = fault == "write" ? "memory_store" : "memory_get", arguments = new { id = fault == "foreign_project" ? "00000000-0000-0000-0000-000000000002" : "00000000-0000-0000-0000-000000000001" } } }));
        public Task<HttpRequestResult> Hook(string method, string? session, string? fault = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new { iss = Record.GatewayIssuer, aud = fault == "wrong_audience" ? "foreign" : Record.GatewayAudience,
                sub = "synthetic-supervisor", iat = now - 2, exp = fault == "expired_gateway" ? now - 1 : now + 60,
                jti = Guid.NewGuid().ToString("N"), caller_kind = "supervisor", sandbox_id = "sandbox1" };
            var unsigned = StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", typ = "openshell-ext+jwt", kid = "synthetic" }))
                + "." + StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
            var signer = new Ed25519Signer(); signer.Init(true, fault == "wrong_signature" ? new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32)) : _gateway);
            var bytes = Encoding.ASCII.GetBytes(unsigned); signer.BlockUpdate(bytes, 0, bytes.Length);
            var context = new CallContext(unsigned + "." + StockRequestProof.Encode(signer.GenerateSignature()));
            var request = new HttpRequestEvaluation { Phase = SupervisorMiddlewarePhase.PreCredentials,
                Config = new Struct { Fields = { ["service"] = Value.ForString("memory") } },
                Context = new RequestContext { SandboxId = fault == "wrong_sandbox" ? "sandbox2" : "sandbox1" },
                Target = new HttpRequestTarget { Method = method == "DELETE" ? "DELETE" : "POST", Scheme = "https", Host = Record.MemoryHost,
                    Port = 443, Path = "/mcp", Query = fault == "query_spoof" ? "agent=agent2" : "" },
                Body = ByteString.CopyFrom(Body(method, fault)), Headers = { new HttpHeader { Name = StockRequestProof.Header, Value = "spoof" } } };
            if (session is not null) request.Headers.Add(new HttpHeader { Name = "Mcp-Session-Id", Value = session });
            return _hook.EvaluateHttpRequest(request, context);
        }
        /// <summary>
        /// The evaluation the pinned stock supervisor (OpenShell bdb956f2) builds for one native request: lowercased wire-order
        /// headers without host/content-length/authorization (l7/middleware.rs safe_middleware_headers), a path-only target,
        /// the full request context and a supervisor token minted as mint_extension_token_with_metadata does.
        /// </summary>
        public Task<HttpRequestResult> NativeHook(string body, string? session, string method = "POST",
            (string Name, string Value)? extra = null, string query = "", string sandbox = "sandbox1")
        {
            var request = new HttpRequestEvaluation { Phase = SupervisorMiddlewarePhase.PreCredentials,
                Config = new Struct { Fields = { ["service"] = Value.ForString("memory") } },
                Context = new RequestContext { RequestId = Guid.NewGuid().ToString(), SandboxId = sandbox, Sandbox = "synthetic-sandbox", Workspace = "synthetic-workspace" },
                Target = new HttpRequestTarget { Method = method, Scheme = "https", Host = Record.MemoryHost, Port = 443, Path = "/mcp", Query = query },
                Body = ByteString.CopyFromUtf8(body) };
            foreach (var (name, value) in NativeHeaders(method, session)) request.Headers.Add(new HttpHeader { Name = name, Value = value });
            if (extra is { } header) request.Headers.Add(new HttpHeader { Name = header.Name, Value = header.Value });
            return _hook.EvaluateHttpRequest(request, GatewayContext("supervisor"));
        }
        /// <summary>The native request through the hook, then to the receiver with the original headers plus the hook's one mutation.</summary>
        public async Task<HttpResponseMessage> Native(string body, string? session)
        {
            var hook = await NativeHook(body, session);
            Assert.Equal(Decision.Allow, hook.Decision);
            var mutation = hook.HeaderMutations.Single().Write;
            var message = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in NativeHeaders("POST", session).Where(h => h.Name != "content-type"))
                message.Headers.TryAddWithoutValidation(name, value);
            message.Headers.Add(mutation.Name, mutation.Value);
            return await _client.SendAsync(message);
        }
        // rmcp-client http_client_adapter.rs: default user-agent, protocol version once negotiated, accept, content type, session.
        private static IEnumerable<(string Name, string Value)> NativeHeaders(string method, string? session)
        {
            yield return ("user-agent", "codex-mcp-client/0.164.0");
            if (session is not null) yield return ("mcp-protocol-version", "2025-06-18");
            yield return ("accept", method == "GET" ? "text/event-stream" : "text/event-stream, application/json");
            if (method == "POST") yield return ("content-type", "application/json");
            if (session is not null) yield return ("mcp-session-id", session);
        }
        public async Task<HttpResponseMessage> Send(string method, string? session = null, string? fault = null)
        {
            var hook = await Hook(method, session, fault == "foreign_project" ? fault : null);
            // For receiver-negative tests we sign once before changing the outside enrollment/body.
            if (hook.Decision != Decision.Allow) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            var token = hook.HeaderMutations.Single().Write.Value;
            if (fault == "wrong_epoch") Save(Record with { Epoch = Guid.NewGuid() });
            if (fault == "expired_assertion")
            {
                // Re-sign a synthetically expired assertion, never wait on time.
                var parts = token.Split('.'); var proof = JsonSerializer.Deserialize<StockRequestProof>(StockRequestProof.Decode(parts[1]))!;
                var unsigned = parts[0] + "." + StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(proof with { Expires = 1 }));
                token = unsigned + "." + StockRequestProof.Encode(_assertion.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
            return await Raw(fault == "wrong_body" ? Body("ping") : Body(method, fault), fault == "wrong_session" ? "foreign" : session,
                fault == "missing_assertion" ? null : token, fault, method == "DELETE" ? "DELETE" : "POST");
        }
        public Task<HttpResponseMessage> Raw(byte[] body, string? session, string? token, string? fault = null, string method = "POST")
        {
            var url = fault == "plaintext" ? "http://memory.example.test/mcp" : fault == "internal_path" ? "/internal/memory" : "/mcp";
            if (fault == "query_spoof") url += "?agent=agent2";
            var message = new HttpRequestMessage(new HttpMethod(method), url) { Content = new ByteArrayContent(body) };
            message.Content.Headers.ContentType = new("application/json");
            message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (token is not null) message.Headers.Add(StockRequestProof.Header, token);
            if (session is not null) message.Headers.Add("Mcp-Session-Id", session);
            return _client.SendAsync(message);
        }
        public async ValueTask DisposeAsync() { _client.Dispose(); _server.Dispose(); await _acl.DisposeAsync(); _assertion.Dispose(); Directory.Delete(_directory, true); }
        private sealed class AclHandler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath.StartsWith("/api/agents/", StringComparison.Ordinal))
                {
                    fixture.GrantFetches++;
                    if (fixture.InjectStaleAclOnGrantFetch)
                        fixture._acl.InjectAclForTesting(new() { ["agent1"] = ["alpha"] });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                        name = "agent1", isEnabled = true,
                        tools = new[] { new { toolName = "mcp__fleet-memory__memory_get", isEnabled = fixture.ToolAllowed } },
                        mcpEndpoints = new[] { new { mcpName = "fleet-memory", url = fixture.EndpointAllowed
                            ? "https://memory.example.test/mcp" : "https://foreign.example.test/mcp", transportType = "http" } }
                    }) });
                }
                fixture.AclFetches++;
                return Task.FromResult(new HttpResponseMessage(fixture.AclUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) {
                    Content = JsonContent.Create(new { acl = new Dictionary<string, string[]> { ["agent1"] = fixture.ProjectAllowed ? ["alpha"] : [] } }) });
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
