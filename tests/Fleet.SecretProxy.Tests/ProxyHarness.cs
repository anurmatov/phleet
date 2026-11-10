using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.SecretProxy.Tests;

/// <summary>One request the forwarder sent upstream, captured before the fake upstream answered.</summary>
internal sealed record UpstreamCall(
    HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string[]> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? string.Join(",", v) : null;
    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>The fake upstream: records every request and answers from a caller-supplied function.</summary>
internal sealed class RecordingUpstream : HttpMessageHandler
{
    public ConcurrentQueue<UpstreamCall> Calls { get; } = new();
    public Func<UpstreamCall, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("upstream-ok") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        var call = new UpstreamCall(request.Method, request.RequestUri!, headers, body);
        Calls.Enqueue(call);
        return Respond(call);
    }
}

internal sealed class CapturingLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void Dispose() { }
    public string All => string.Join("\n", Lines);

    private sealed class Sink(CapturingLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{level} {category} {formatter(state, exception)} {exception}");
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The real forwarder on a loopback port with a fake upstream. Clients ask for
/// <c>https://&lt;bound host&gt;/…</c> and are dialled to the forwarder, as an agent's /etc/hosts does.
/// </summary>
internal sealed class ProxyHarness : IAsyncDisposable
{
    public const string HostA = "api.example.test";
    public const string HostB = "other.example.test";
    public const string KeyA = "EXAMPLE_API_KEY";
    public const string KeyB = "OTHER_API_KEY";
    public const string SecretA = "synthetic-secret-value-A-7f3a91";
    public const string SecretB = "synthetic-secret-value-B-2c8e44";
    public const string Loopback = "127.0.0.1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-secrets-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;

    public RecordingUpstream Upstream { get; } = new();
    public CapturingLogs Logs { get; } = new();
    public ManualTime Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public int Port { get; private set; }
    public string StateFile => Path.Combine(_directory, "state", SecretProxyState.FileName);
    public string CaDirectory => Path.Combine(_directory, "ca");
    public string PlaceholderA { get; } = SecretPlaceholders.Create();
    public string PlaceholderB { get; } = SecretPlaceholders.Create();
    public X509Certificate2 Ca { get; private set; } = null!;

    public static async Task<ProxyHarness> StartAsync(SecretProxyState? state = null, bool writeState = true)
    {
        var harness = new ProxyHarness();
        Directory.CreateDirectory(Path.GetDirectoryName(harness.StateFile)!);
        if (writeState)
            harness.WriteState(state ?? harness.DefaultState());

        harness._app = SecretProxyApp.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProxy:StateFile"] = harness.StateFile,
                ["SecretProxy:CaDirectory"] = harness.CaDirectory,
                ["SecretProxy:ListenAddress"] = Loopback,
                ["SecretProxy:Port"] = "0",
                ["SecretProxy:HealthPort"] = "0",
            });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(harness.Logs);
            builder.Services.AddSingleton<TimeProvider>(harness.Time);
        }, new HttpMessageInvoker(harness.Upstream));

        await harness._app.StartAsync();
        var address = harness._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        harness.Port = new Uri(address).Port;
        harness.Ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(harness.CaDirectory, SecretProxyState.CaCertificateFile));
        return harness;
    }

    /// <summary>Key A bound to host A (Authorization), key B to host B (X-Api-Key); one agent on loopback holds both.</summary>
    public SecretProxyState DefaultState() => new()
    {
        Version = SecretProxyState.CurrentVersion,
        GeneratedAtUtc = Time.GetUtcNow(),
        Bindings =
        [
            new SecretProxyBinding(KeyA, SecretBindingRules.KindStatic, [HostA], "Authorization", SecretA),
            new SecretProxyBinding(KeyB, SecretBindingRules.KindStatic, [HostB], "X-Api-Key", SecretB),
        ],
        Grants =
        [
            new SecretProxyGrant("agent1", Loopback, KeyA, SecretPlaceholders.Hash(PlaceholderA)),
            new SecretProxyGrant("agent1", Loopback, KeyB, SecretPlaceholders.Hash(PlaceholderB)),
        ],
    };

    /// <summary>Replaces the state file atomically, as the orchestrator does.</summary>
    public void WriteState(SecretProxyState state) => WriteRawState(state.Serialize());

    public void WriteRawState(string content)
    {
        var temp = StateFile + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, StateFile, overwrite: true);
    }

    public void DeleteState() => File.Delete(StateFile);

    /// <summary>
    /// A client that trusts only the forwarder's CA and is dialled to the forwarder for every host,
    /// keeping one connection per host alive between requests.
    /// </summary>
    public HttpClient CreateClient(bool http2 = false)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(Ca);
            return certificate is not null && chain.Build(new X509Certificate2(certificate));
        };
        return new HttpClient(handler)
        {
            DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    /// <summary>
    /// One raw HTTP/1.1 request over a TLS connection opened for <paramref name="serverName"/>, so the
    /// Host header can differ from the name the handshake asked for. Returns the response head.
    /// </summary>
    public async Task<string> RawAsync(string serverName, string hostHeader, string authorization)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, Port);
        await using var tls = new SslStream(new NetworkStream(socket), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(serverName);
        await tls.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /v1/items HTTP/1.1\r\nHost: {hostHeader}\r\nAuthorization: {authorization}\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(tls, Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    public static HttpRequestMessage Get(string url, string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        Ca?.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
