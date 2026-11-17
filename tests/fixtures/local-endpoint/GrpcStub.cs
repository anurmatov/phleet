using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.LocalEndpoint;

/// <summary>
/// One request a <see cref="GrpcStub"/> received: its path, the scheme it arrived under
/// (<c>http</c> on the plaintext listener) and its headers, names in lower case.
/// </summary>
internal sealed record StubRequest(string Path, string Scheme, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A local endpoint that speaks just enough gRPC to be called: HTTP/2 on a loopback port, every call
/// answered UNIMPLEMENTED, every request recorded. Not a Temporal and not a Qdrant: it shows what a
/// client built by the production code puts on the wire (whether a request arrives at all, and with
/// which headers), and nothing about what a real server would then do. Compiled into each test
/// project that needs one (shared source, like <c>tests/fixtures/wire</c>).
/// </summary>
internal sealed class GrpcStub : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly X509Certificate2? _serverCertificate;

    public int Port { get; }
    public ConcurrentQueue<StubRequest> Requests { get; } = new();

    private GrpcStub(IHost host, X509Certificate2? serverCertificate)
    {
        _host = host;
        _serverCertificate = serverCertificate;
        Port = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
    }

    /// <summary>Plain HTTP/2, no TLS.</summary>
    public static Task<GrpcStub> StartPlaintextAsync() => StartAsync(null);

    /// <summary>
    /// TLS with a certificate for <paramref name="dnsName"/> and 127.0.0.1, issued by an authority
    /// made for this stub alone. Nothing trusts that authority, which is what the tests that use
    /// this are about. The keys are generated here and stay in memory; none is committed.
    /// </summary>
    public static Task<GrpcStub> StartUntrustedTlsAsync(string dnsName = "localhost") => StartAsync(UntrustedCertificate(dnsName));

    private static async Task<GrpcStub> StartAsync(X509Certificate2? serverCertificate)
    {
        GrpcStub? stub = null;

        // A bare host on purpose: the application builders read appsettings.json from the working
        // directory, which in a test project is the service's own, with the service's own port.
        var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                    if (serverCertificate is not null)
                        listen.UseHttps(serverCertificate);
                }))
                .Configure(app => app.Run(context =>
                {
                    stub!.Requests.Enqueue(new StubRequest(
                        context.Request.Path,
                        context.Request.Scheme,
                        context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString())));
                    // A trailers-only gRPC response: UNIMPLEMENTED.
                    context.Response.ContentType = "application/grpc";
                    context.Response.Headers["grpc-status"] = "12";
                    return Task.CompletedTask;
                })))
            .Build();
        await host.StartAsync();
        stub = new GrpcStub(host, serverCertificate);
        return stub;
    }

    /// <summary>Waits until a request has arrived, for a client that connects in the background.</summary>
    public async Task<StubRequest> FirstRequestAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        StubRequest? request;
        while (!Requests.TryPeek(out request))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No request reached the stub within {timeout.TotalSeconds:0} s.");
            await Task.Delay(25);
        }
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _host.StopAsync(limit.Token); } catch { /* best effort */ }
        _host.Dispose();
        _serverCertificate?.Dispose();
    }

    private static X509Certificate2 UntrustedCertificate(string dnsName)
    {
        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authority = new CertificateRequest("CN=Test authority nothing trusts", authorityKey, HashAlgorithmName.SHA256);
        authority.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authority.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var authorityCertificate = authority.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7f;
        using var issued = request.Create(
            authorityCertificate.SubjectName, X509SignatureGenerator.CreateForECDsa(authorityKey),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(6), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        // Through PKCS#12: a key attached in memory is not usable by the platform's TLS on every OS.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }
}
