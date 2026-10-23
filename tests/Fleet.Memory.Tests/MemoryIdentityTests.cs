using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Fleet.Memory.Tools;
using Fleet.Shared.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Tests;

// Real signature/certificate/body/session/middleware/tool code with owned fake keys and HTTP/DB seams.
// Not an actual TLS listener, MySQL restore, upstream hook or daemon integration acceptance.
public sealed class MemoryIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "identity-test-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _client;
    private readonly MemoryIdentityTrust _trust;
    private readonly FleetIdentityOptions _options;
    private readonly VerifiedAgentIdentity _identity = new("agent1", "gateway1", "sandbox1", Guid.NewGuid(), Guid.NewGuid(), "memory", "/mcp");
    private readonly MemoryIdentitySessions _sessions = new();
    private const string Body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";

    public MemoryIdentityTests()
    {
        Directory.CreateDirectory(_dir);
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=fixture-root", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clientRequest = new CertificateRequest("CN=fixture-client", clientKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("ingress.example.invalid");
        clientRequest.CertificateExtensions.Add(san.Build());
        clientRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        _client = clientRequest.Create(ca, DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddMinutes(10), RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(_dir, "ca.pem"), ca.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_dir, "assertion.pem"), _key.ExportSubjectPublicKeyInfoPem());
        _options = new() { Enabled = true, Issuer = "https://issuer.example.invalid", Audience = "memory:/mcp", KeyId = "key1",
            AssertionPublicKeyPath = Path.Combine(_dir, "assertion.pem"), IngressCaPath = Path.Combine(_dir, "ca.pem"),
            IngressDnsName = "ingress.example.invalid", ConnectionString = "fixture-only-never-opened" };
        _trust = new(_options);
    }

    public void Dispose() { _trust.Dispose(); _client.Dispose(); _key.Dispose(); Directory.Delete(_dir, true); }

    [Fact]
    public async Task RealProofAndCertificateYieldRequestBoundFreshAcl_NotLegacyWarmCache()
    {
        await using var acl = Acl();
        acl.InjectAclForTesting(new() { ["agent1"] = ["*"] });
        Assert.True(_trust.AcceptCertificate(_client));
        var context = Context();
        var called = false;
        await Invoke(context, acl, c =>
        {
            called = true;
            var verified = Assert.IsType<VerifiedMemoryRequest>(VerifiedMemoryRequest.Get(c));
            Assert.Equal(_identity, verified.Identity);
            Assert.True(acl.CanRead(verified.Snapshot, "agent1", "alpha").allowed);
            Assert.False(acl.CanRead(verified.Snapshot, "agent1", "private").allowed);
            return Task.CompletedTask;
        });
        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task ActualGetAndWriteGatesUseFreshSnapshot_NotWarmWildcardOrBodyAttribution()
    {
        var files = new MemoryFileStore(Options.Create(new StorageOptions { Path = Path.Combine(_dir, "memories") }), NullLogger<MemoryFileStore>.Instance);
        files.EnsureDirectories();
        var allowed = await files.SaveAsync(new MemoryDocument { Id = Guid.NewGuid().ToString(), Type = "learning", Title = "allowed", Content = "owned synthetic content", Project = "alpha" });
        var denied = await files.SaveAsync(new MemoryDocument { Id = Guid.NewGuid().ToString(), Type = "learning", Title = "denied", Content = "forbidden synthetic content", Project = "private" });
        var service = new MemoryService(files, null!, null!, Options.Create(new QdrantOptions { Url = "https://unused.example.invalid" }),
            new MemorySizeGuidance(0, NullLogger.Instance), NullLogger<MemoryService>.Instance);
        await using var acl = Acl();
        acl.InjectAclForTesting(new() { ["agent1"] = ["*"] });
        var context = Context();
        var called = false;
        await Invoke(context, acl, async c =>
        {
            called = true;
            var accessor = new HttpContextAccessor { HttpContext = c };
            var get = new MemoryGetTool(service, new(), acl, accessor, NullLogger<MemoryGetTool>.Instance);
            Assert.Contains("owned synthetic content", await get.GetAsync(allowed.Id));
            Assert.Equal("memory_get: access denied.", await get.GetAsync(denied.Id));
            Assert.Contains("access denied", await new MemoryStoreTool(null!, acl, accessor).StoreAsync("learning", "title", "content", agent: "operator1", project: "private"));
            Assert.Equal("memory_update: access denied.", await new MemoryUpdateTool(service, acl, accessor).UpdateAsync(denied.Id, content: "changed"));
            Assert.Equal("memory_delete: access denied.", await new MemoryDeleteTool(service, acl, accessor, NullLogger<MemoryDeleteTool>.Instance).DeleteAsync(denied.Id));
        });
        Assert.True(called);
        Assert.Equal("forbidden synthetic content", (await service.GetAsync(denied.Id))!.Content);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("method")]
    [InlineData("target")]
    [InlineData("aud")]
    [InlineData("epoch")]
    [InlineData("certificate")]
    [InlineData("cleartext")]
    [InlineData("override-query")]
    [InlineData("non-json")]
    [InlineData("oversize")]
    [InlineData("override")]
    [InlineData("legacy")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task DenialsNeverReachToolOrContent(string fault)
    {
        await using var acl = Acl();
        var context = Context();
        var active = true;
        switch (fault)
        {
            case "body": context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body + " ")); break;
            case "method": context.Request.Method = "DELETE"; break;
            case "target": context.Request.QueryString = new("?x=1"); context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/mcp?x=1"; break;
            case "aud": context.Request.Headers["X-Fleet-Assertion"] = Sign(audience: "other:/mcp"); break;
            case "epoch": active = false; break;
            case "certificate": context.Connection.ClientCertificate = null; break;
            case "cleartext": context.Request.Scheme = "http"; break;
            case "override-query": context.Request.QueryString = new("?agent=operator1"); break;
            case "non-json": context.Request.ContentType = "application/octet-stream"; break;
            case "oversize": context.Request.ContentLength = 262145; break;
            case "override": context.Request.Headers["X-Fleet-Agent"] = "agent2"; break;
            case "legacy": context.Connection.LocalPort = 3100; break;
            case "expired": context.Request.Headers["X-Fleet-Assertion"] = Sign(age: 20); break;
            case "signature":
                var parts = context.Request.Headers["X-Fleet-Assertion"].ToString().Split('.');
                parts[2] = (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..];
                context.Request.Headers["X-Fleet-Assertion"] = string.Join('.', parts);
                break;
        }
        var called = false;
        await Invoke(context, acl, _ => { called = true; return Task.CompletedTask; }, active);
        Assert.False(called);
        Assert.True(context.Response.StatusCode >= 400);
        Assert.Null(VerifiedMemoryRequest.Get(context));
    }

    [Fact]
    public async Task WarmAclFetchFailureReturns503BeforeMapMcp()
    {
        await using var acl = Acl(fail: true);
        acl.InjectAclForTesting(new() { ["agent1"] = ["*"] });
        var context = Context();
        await Invoke(context, acl, _ => throw new Exception("must not reach downstream"));
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Null(VerifiedMemoryRequest.Get(context));
    }

    [Fact]
    public void ForeignCaAndWrongIngressSanAreRejected()
    {
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=untrusted", otherKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(_options.IngressDnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var untrusted = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.False(_trust.AcceptCertificate(untrusted));
        var expected = _options.IngressDnsName;
        _options.IngressDnsName = "other.example.invalid";
        using var wrongSan = new MemoryIdentityTrust(_options);
        Assert.False(wrongSan.AcceptCertificate(_client));
        _options.IngressDnsName = expected;
    }

    [Fact]
    public async Task SessionCannotCrossIdentityOrSurviveReceiverRestart()
    {
        var hash = MemoryIdentityMiddleware.Hash(Encoding.UTF8.GetBytes("session1"));
        Assert.True(_sessions.Bind(hash, _identity));
        Assert.False(_sessions.Bind(hash, _identity with { IdentityEpoch = Guid.NewGuid() }));
        Assert.False(new MemoryIdentitySessions().Matches(hash, _identity));
        await using var acl = Acl();
        var context = Context("session1");
        var called = false;
        await Invoke(context, acl, _ => { called = true; return Task.CompletedTask; });
        Assert.True(called);
    }

    [Fact]
    public void SessionAndConcurrentRequestBoundsDoNotEvictIntoAcceptance()
    {
        for (var i = 0; i < 100; i++) Assert.True(_sessions.Bind("session" + i, _identity));
        Assert.False(_sessions.Bind("overflow", _identity));
        var leases = Enumerable.Range(0, 64).Select(_ => _sessions.TryEnter(_identity.LaunchId)!).ToArray();
        Assert.All(leases, Assert.NotNull);
        Assert.Null(_sessions.TryEnter(_identity.LaunchId));
        foreach (var lease in leases) lease.Dispose();
        using var recovered = _sessions.TryEnter(_identity.LaunchId);
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task AllSixToolsRejectMissingVerifiedRequest_EvenWithDisabledLegacyAcl()
    {
        var context = new DefaultHttpContext(); context.Connection.LocalPort = 3101;
        var accessor = new HttpContextAccessor { HttpContext = context };
        await using var acl = new AclCacheService(Options.Create(new AclOptions()), Options.Create(new OrchestratorOptions()), NullLogger<AclCacheService>.Instance);
        var results = new[] {
            await new MemoryGetTool(null!, new(), acl, accessor, NullLogger<MemoryGetTool>.Instance).GetAsync("id1"),
            await new MemoryListTool(null!, acl, accessor, new(), NullLogger<MemoryListTool>.Instance).ListAsync(),
            await new MemorySearchTool(null!, acl, accessor, new(), NullLogger<MemorySearchTool>.Instance).SearchAsync("query"),
            await new MemoryStoreTool(null!, acl, accessor).StoreAsync("learning", "title", "content"),
            await new MemoryUpdateTool(null!, acl, accessor).UpdateAsync("id1"),
            await new MemoryDeleteTool(null!, acl, accessor, NullLogger<MemoryDeleteTool>.Instance).DeleteAsync("id1") };
        Assert.All(results, result => Assert.Contains("verified request authorization unavailable", result));
    }

    private DefaultHttpContext Context(string? session = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 3101; context.Connection.ClientCertificate = _client;
        context.Request.Scheme = "https"; context.Request.Path = "/mcp"; context.Request.Method = "POST"; context.Request.ContentType = "application/json";
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/mcp";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body));
        if (session is not null) context.Request.Headers["Mcp-Session-Id"] = session;
        context.Request.Headers["X-Fleet-Assertion"] = Sign(session: session);
        return context;
    }
    private Task Invoke(HttpContext context, AclCacheService acl, RequestDelegate next, bool active = true) =>
        new MemoryIdentityMiddleware(next).InvokeAsync(context, Options.Create(_options), _trust, new LaunchReader(_identity, active), _sessions, acl);
    private string Sign(string? audience = null, int age = 0, string? session = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - age;
        var payload = new Dictionary<string, object> { ["iss"] = _options.Issuer, ["aud"] = audience ?? _options.Audience,
            ["sub"] = _identity.AgentName, ["gateway_id"] = _identity.GatewayId, ["sandbox_id"] = _identity.SandboxId,
            ["launch_id"] = _identity.LaunchId.ToString("D"), ["identity_epoch"] = _identity.IdentityEpoch.ToString("D"),
            ["iat"] = now, ["exp"] = now + 15, ["jti"] = Guid.NewGuid().ToString("N"), ["method"] = "POST", ["target"] = "/mcp",
            ["body_sha256"] = MemoryIdentityMiddleware.Hash(Encoding.UTF8.GetBytes(Body)),
            ["session_hash"] = MemoryIdentityMiddleware.Hash(Encoding.UTF8.GetBytes(session ?? "")) };
        var prefix = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "fleet-request+jwt", kid = "key1" })) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        return prefix + "." + B64(_key.SignData(Encoding.ASCII.GetBytes(prefix), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
    private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static AclCacheService Acl(bool fail = false) => new(Options.Create(new AclOptions { EnableProjectScopedAcl = true }),
        Options.Create(new OrchestratorOptions { BaseUrl = "https://authority.example.invalid" }), NullLogger<AclCacheService>.Instance, new AclHandler(fail));
    private sealed class AclHandler(bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("{\"acl\":{\"agent1\":[\"alpha\"]}}") });
    }
    private sealed class LaunchReader(VerifiedAgentIdentity expected, bool active) : IVerifiedLaunchReader
    {
        public Task<bool> IsCurrentAsync(VerifiedAgentIdentity identity, string consumedJti, CancellationToken ct) => Task.FromResult(active && expected == identity);
    }
}
