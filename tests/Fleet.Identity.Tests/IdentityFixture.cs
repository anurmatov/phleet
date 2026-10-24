using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fleet.Identity.Configuration;
using Fleet.Identity.Services;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Fleet.Memory.Tools;
using Fleet.Shared.Security;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Openshell.Extension.V1;
using Openshell.Middleware.V1;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Fleet.Identity.Tests;

// Owned TestServer TLS seam + actual generated gRPC, SQL authority, ingress, memory middleware and MapMcp.
// SQLite exercises relational transactions, NOT MySQL/provider/real TLS or gateway qualification.
internal sealed class IdentityFixture : IAsyncDisposable, IIdentityConnectionFactory
{
    public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "identity-owned-" + Guid.NewGuid().ToString("N"));
    public readonly VerifiedAgentIdentity Identity = new("agent1", "gateway1", "sandbox1", Guid.NewGuid(), Guid.NewGuid(), "memory", "/mcp");
    public readonly ECDsa AssertionKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Ed25519PrivateKeyParameters _gatewayKey = new(RandomNumberGenerator.GetBytes(32));
    public readonly IdentityOptions Options = new() { GatewayId = "gateway1", GatewayIssuer = "gateway-issuer1", GatewayAudience = "urn:identity:hook",
        Issuer = "issuer1", KeyId = "key1", IngressHost = "ingress.example.invalid", MemoryReceiver = "https://memory.example.invalid/mcp" };
    public LaunchIdentityStore Store { get; private set; } = null!;
    public SupervisorMiddleware.SupervisorMiddlewareClient Hook { get; private set; } = null!;
    public HttpClient Ingress { get; private set; } = null!;
    public bool Grants = true, AclUnavailable, Current = true, OwnershipUnavailable;
    public HttpClient ReceiverClient { get; private set; } = null!;
    public int ReceiverRequests, ReceiverAdmitted;
    public readonly CapturedLogger Captured = new();
    public string DocumentId = "";
    public MemoryIdentitySessions ReceiverSessions = new();
    public readonly ReadCounterService Reads = new();
    public readonly DelayedEmbedding Embedding = new();
    public MemoryFileStore Files = null!;
    public TimeSpan ReadDispatchDelay;
    private WebApplication _memory = null!, _hook = null!, _ingress = null!;
    private GrpcChannel _channel = null!;
    private MemoryIdentityTrust _trust = null!;
    private X509Certificate2 _certificate = null!;
    private AclCacheService _acl = null!;
    public DbConnection Create() => new SqliteConnection($"Data Source={Path.Combine(DirectoryPath, "authority.db")};Default Timeout=1");
    public async Task Sql(string sql)
    {
        await using var connection = Create(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    public async Task<long> Count(string table)
    {
        await using var connection = Create(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    public async Task StartAsync()
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);
        // Relevant columns/keys from AddOpenShellExecution; no alternative identity logic in the fixture.
        await Sql($"""
            CREATE TABLE agents (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);
            CREATE TABLE agent_open_shell_launches (Id TEXT PRIMARY KEY,AgentId INTEGER,IdentityEpoch TEXT,GatewayId TEXT,SandboxId TEXT,
                ActiveAgentId INTEGER UNIQUE,RevokedAt TEXT,Phase TEXT);
            CREATE TABLE open_shell_retired_ids (GatewayId TEXT,SandboxId TEXT,PRIMARY KEY(GatewayId,SandboxId));
            CREATE TABLE identity_request_nonces (Jti TEXT PRIMARY KEY,LaunchId TEXT NOT NULL REFERENCES agent_open_shell_launches(Id),ExpiresAt TEXT);
            CREATE TABLE agent_identity_sessions (SessionHash TEXT,Service TEXT,Route TEXT,AgentId INTEGER,LaunchId TEXT REFERENCES agent_open_shell_launches(Id),
                IdentityEpoch TEXT,ExpiresAt TEXT,PRIMARY KEY(SessionHash,Service,Route));
            INSERT INTO agents VALUES (1,'agent1');
            INSERT INTO agent_open_shell_launches VALUES ('{Identity.LaunchId:D}',1,'{Identity.IdentityEpoch:D}','gateway1','sandbox1',1,NULL,'activated');
            """);
        Store = new(this);
        var signer = new RequestAssertionIssuer(Options, AssertionKey);
        var verifier = new FleetRequestAssertionVerifier(AssertionKey.ExportSubjectPublicKeyInfo(), Options.Issuer, "memory:/mcp", Options.KeyId);
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=owned-root", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=owned-ingress", clientKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("ingress.example.invalid"); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        _certificate = request.Create(ca, DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(DirectoryPath, "ca.pem"), ca.ExportCertificatePem());
        File.WriteAllText(Path.Combine(DirectoryPath, "public.pem"), AssertionKey.ExportSubjectPublicKeyInfoPem());
        var memoryOptions = new FleetIdentityOptions { Enabled = true, ProtectedAgents = "agent1", Issuer = Options.Issuer, Audience = "memory:/mcp",
            KeyId = Options.KeyId, AssertionPublicKeyPath = Path.Combine(DirectoryPath, "public.pem"), IngressCaPath = Path.Combine(DirectoryPath, "ca.pem"),
            IngressDnsName = "ingress.example.invalid", ConnectionString = "unused-owned-fixture" };
        _trust = new(memoryOptions);
        var files = new MemoryFileStore(Microsoft.Extensions.Options.Options.Create(new StorageOptions { Path = Path.Combine(DirectoryPath, "memories") }), NullLogger<MemoryFileStore>.Instance);
        files.EnsureDirectories(); Files = files;
        DocumentId = (await files.SaveAsync(new MemoryDocument { Id = Guid.NewGuid().ToString(), Type = "learning", Title = "fixture", Content = "owned secret content", Project = "alpha" })).Id;
        var memory = new MemoryService(files, null!, Embedding, Microsoft.Extensions.Options.Options.Create(new QdrantOptions { Url = "https://unused.example.invalid", SimilarityThreshold = 0 }),
            new MemorySizeGuidance(0, NullLogger.Instance), NullLogger<MemoryService>.Instance);
        _acl = new(Microsoft.Extensions.Options.Options.Create(new AclOptions { EnableProjectScopedAcl = true }),
            Microsoft.Extensions.Options.Options.Create(new OrchestratorOptions { BaseUrl = "https://authority.example.invalid" }), NullLogger<AclCacheService>.Instance, new AclHandler(this));
        _acl.InjectAclForTesting(new() { ["agent1"] = ["*"] });
        var builder = Builder();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(memoryOptions)); builder.Services.AddSingleton(_trust);
        builder.Services.AddSingleton<IVerifiedLaunchReader>(new ReceiverReader(this));
        builder.Services.AddTransient(_ => ReceiverSessions); builder.Services.AddSingleton(_acl); builder.Services.AddSingleton(memory);
        builder.Services.AddSingleton(Reads); builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<MemoryGetTool>().WithTools<MemoryStoreTool>();
        _memory = builder.Build();
        _memory.Use(async (context, next) => {
            Interlocked.Increment(ref ReceiverRequests);
            context.Connection.LocalPort = memoryOptions.VerifiedPort; context.Connection.ClientCertificate = _certificate;
            context.Request.Scheme = "https";
            context.Features.Get<IHttpRequestFeature>()!.RawTarget = context.Request.Path + context.Request.QueryString;
            await next();
        });
        _memory.UseMiddleware<MemoryIdentityMiddleware>();
        _memory.Use(async (context, next) => {
            Interlocked.Increment(ref ReceiverAdmitted);
            if (ReadDispatchDelay > TimeSpan.Zero && context.Request.Method == "POST")
            {
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var text = await reader.ReadToEndAsync(); context.Request.Body.Position = 0;
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.TryGetProperty("params", out var parameters)
                    && parameters.TryGetProperty("name", out var name) && name.GetString() == "memory_get")
                    await Task.Delay(ReadDispatchDelay, context.RequestAborted);
            }
            await next();
        });
        _memory.MapMcp("/mcp"); await _memory.StartAsync();
        builder = Builder(); builder.Services.AddGrpc(); builder.Services.AddSingleton(Options);
        builder.Services.AddSingleton(new GatewayTokenVerifier(Options, Jwks())); builder.Services.AddSingleton(Store); builder.Services.AddSingleton(signer);
        _hook = builder.Build(); _hook.MapGrpcService<SupervisorMiddlewareService>(); await _hook.StartAsync();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _hook.GetTestServer().CreateHandler() });
        Hook = new(_channel);
        ReceiverClient = _memory.GetTestClient();
        var ingress = CreateIngress(ReceiverClient);
        builder = Builder(); _ingress = builder.Build();
        _ingress.Use(async (context, next) => {
            // TestServer does not populate Kestrel's RawTarget feature.
            context.Features.Get<IHttpRequestFeature>()!.RawTarget = context.Request.Path + context.Request.QueryString;
            await next();
        });
        _ingress.Run(context => ingress.HandleAsync(context)); await _ingress.StartAsync();
        Ingress = _ingress.GetTestClient(); Ingress.BaseAddress = new Uri($"https://{Options.IngressHost}:{Options.IngressPort}");
    }
    public IdentityIngress CreateIngress(HttpClient receiver) => new(Options,
        new FleetRequestAssertionVerifier(AssertionKey.ExportSubjectPublicKeyInfo(), Options.Issuer, "memory:/mcp", Options.KeyId),
        Store, new ReplayStore(Store), receiver, Captured);
    public async Task<HttpResponseMessage> Delete(string session)
    {
        var evaluation = Evaluation("", session); evaluation.Target.Method = "DELETE";
        var result = await Evaluate(evaluation); Assert.Equal(Decision.Allow, result.Decision);
        using var message = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        message.Headers.Add("Mcp-Session-Id", session); message.Headers.Add("X-Fleet-Assertion", result.HeaderMutations.Single().Write.Value);
        return await Ingress.SendAsync(message);
    }
    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders(); return builder;
    }
    public string Jwks() => JsonSerializer.Serialize(new { keys = new[] { new { kty = "OKP", crv = "Ed25519", kid = "gateway-key1", x = Encode(_gatewayKey.GeneratePublicKey().GetEncoded()) } } });
    public string Token(string kind = "supervisor", string? sandbox = "sandbox1", string? fault = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object> { ["iss"] = Options.GatewayIssuer, ["aud"] = Options.GatewayAudience, ["sub"] = "supervisor1",
            ["iat"] = now, ["exp"] = now + 300, ["jti"] = Guid.NewGuid().ToString("N"), ["caller_kind"] = kind };
        if (sandbox is not null) claims["sandbox_id"] = sandbox;
        if (fault == "audience") claims["aud"] = "other";
        if (fault == "issuer") claims["iss"] = "other";
        if (fault == "expired") { claims["iat"] = now - 500; claims["exp"] = now - 1; }
        if (fault == "lifetime") claims["exp"] = now + 901;
        var header = new { alg = "EdDSA", typ = fault == "type" ? "JWT" : "openshell-ext+jwt", kid = fault == "kid" ? "unknown" : "gateway-key1" };
        var input = Encode(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signer = new Ed25519Signer(); signer.Init(true, _gatewayKey); var bytes = Encoding.ASCII.GetBytes(input); signer.BlockUpdate(bytes, 0, bytes.Length);
        var signature = signer.GenerateSignature(); if (fault == "signature") signature[0] ^= 1;
        return input + "." + Encode(signature);
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public HttpRequestEvaluation Evaluation(string body, string? session = null)
    {
        var result = new HttpRequestEvaluation { Phase = SupervisorMiddlewarePhase.PreCredentials,
            Context = new RequestContext { SandboxId = "sandbox1" }, Config = new Struct { Fields = { ["service"] = Value.ForString("memory") } },
            Target = new HttpRequestTarget { Scheme = "https", Host = Options.IngressHost, Port = (uint)Options.IngressPort, Method = "POST", Path = "/mcp" },
            Body = ByteString.CopyFromUtf8(body), Headers = { new HttpHeader { Name = "content-type", Value = "application/json" } } };
        if (session is not null) result.Headers.Add(new HttpHeader { Name = "mcp-session-id", Value = session }); return result;
    }
    public async Task<HttpRequestResult> Evaluate(HttpRequestEvaluation evaluation, string? token = null) =>
        await Hook.EvaluateHttpRequestAsync(evaluation, new Metadata { { "authorization", "Bearer " + (token ?? Token()) } });
    public async Task<HttpResponseMessage> Send(string body, string? session = null, string? assertion = null)
    {
        var evaluated = assertion is null ? await Evaluate(Evaluation(body, session)) : null;
        if (evaluated is not null) Assert.Equal(Decision.Allow, evaluated.Decision);
        var message = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        message.Headers.Add("Accept", new[] { "application/json", "text/event-stream" });
        if (session is not null) message.Headers.Add("Mcp-Session-Id", session);
        message.Headers.Add("X-Fleet-Assertion", assertion ?? evaluated!.HeaderMutations.Single().Write.Value);
        return await Ingress.SendAsync(message);
    }
    public const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"fixture","version":"1"}}}""";
    public async Task<string> InitializeAsync()
    {
        using var response = await Send(Initialize); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {string.Join(",", Captured.Messages)}; receiver={ReceiverRequests}; " + await response.Content.ReadAsStringAsync());
        return response.Headers.GetValues("Mcp-Session-Id").Single();
    }
    public string Call => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "memory_get", arguments = new { id = DocumentId } } });
    private sealed class AclHandler(IdentityFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(fixture.AclUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(fixture.Grants ? "{\"acl\":{\"agent1\":[\"alpha\"]}}" : "{\"acl\":{}}") });
    }
    private sealed class ReceiverReader(IdentityFixture fixture) : IVerifiedLaunchReader
    {
        public async Task<bool> OwnsSessionAsync(VerifiedAgentIdentity identity, string sessionHash, string jti, CancellationToken ct)
        {
            if (!await IsCurrentAsync(identity, jti, ct)) return false;
            if (fixture.OwnershipUnavailable) throw new InvalidOperationException("owned authority fixture unavailable");
            await using var connection = fixture.Create(); await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM agent_identity_sessions WHERE SessionHash=@hash AND LaunchId=@launch AND IdentityEpoch=@epoch AND AgentId=1 AND Service='memory' AND Route='/mcp'";
            foreach (var (name, value) in new[] { ("@hash", sessionHash), ("@launch", identity.LaunchId.ToString("D")), ("@epoch", identity.IdentityEpoch.ToString("D")) })
            { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
        }
        public async Task<bool> IsCurrentAsync(VerifiedAgentIdentity identity, string consumedJti, CancellationToken ct)
        {
            if (!fixture.Current || identity != await fixture.Store.ResolveAsync(identity.GatewayId, identity.SandboxId, null, ct)) return false;
            await using var connection = fixture.Create(); await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM identity_request_nonces WHERE Jti=@jti AND LaunchId=@launch AND ExpiresAt>@now";
            var jti = command.CreateParameter(); jti.ParameterName = "@jti"; jti.Value = consumedJti; command.Parameters.Add(jti);
            var launch = command.CreateParameter(); launch.ParameterName = "@launch"; launch.Value = identity.LaunchId.ToString("D"); command.Parameters.Add(launch);
            var now = command.CreateParameter(); now.ParameterName = "@now"; now.Value = DateTime.UtcNow; command.Parameters.Add(now);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
        }
    }
    public async ValueTask DisposeAsync()
    {
        Ingress?.Dispose(); ReceiverClient?.Dispose(); _channel?.Dispose();
        foreach (var app in new[] { _ingress, _hook, _memory }) if (app is not null) await app.DisposeAsync();
        if (_acl is not null) await _acl.DisposeAsync(); _trust?.Dispose(); _certificate?.Dispose(); AssertionKey.Dispose();
        SqliteConnection.ClearAllPools(); if (System.IO.Directory.Exists(DirectoryPath)) System.IO.Directory.Delete(DirectoryPath, true);
    }
}

internal sealed class CapturedLogger : ILogger<IdentityIngress>
{
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
}

internal sealed class DelayedEmbedding : IEmbeddingService
{
    public TimeSpan Delay;
    public int Executions;
    public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Executions);
        try { await Task.Delay(Delay, ct); return [0]; }
        finally { Finished.TrySetResult(); }
    }
}
