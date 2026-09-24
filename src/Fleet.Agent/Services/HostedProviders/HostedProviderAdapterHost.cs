using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using Fleet.Agent.Configuration;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services.HostedProviders;

/// <summary>
/// Where Codex reaches the forwarder: the loopback base address and the per-start token (#335 D10).
/// </summary>
/// <remarks>
/// Not a record, so a stray log of this object cannot print the token (MUST NOT 14).
/// </remarks>
public sealed class HostedProviderEndpoint(Uri baseAddress, string token)
{
    /// <summary><c>http://127.0.0.1:{port}</c>.</summary>
    public Uri BaseAddress { get; } = baseAddress;

    /// <summary>
    /// The token Codex must send as <c>x-phleet-forwarder-token</c>. Goes only into the
    /// <c>thread/start</c> config; never into an environment, file, argv or log.
    /// </summary>
    public string Token { get; } = token;

    public override string ToString() => BaseAddress.ToString();
}

/// <summary>
/// Runs the hosted-provider forwarder on its own loopback-only web app (#335 D4).
/// </summary>
/// <remarks>
/// <para>
/// A <b>separate nested</b> <see cref="WebApplication"/>, never routes on the main agent app. The
/// main app listens on the container network, and the forwarder attaches the subscription key to
/// whatever it relays: one missed check there would expose a key-attaching proxy. The nested app
/// has only one listener, <c>127.0.0.1:0</c>, and only the forwarder behind it.
/// </para>
/// <para>
/// Built with <see cref="WebApplication.CreateEmptyBuilder"/> rather than <c>CreateSlimBuilder</c>.
/// The slim builder loads <c>appsettings.json</c> and environment variables, and Kestrel adds any
/// endpoint named under a <c>Kestrel:Endpoints</c> key it finds there — which would give the
/// adapter a second, non-loopback listener. The empty builder has no configuration sources, so the
/// explicit <c>Listen(IPAddress.Loopback, 0)</c> is the only endpoint that can exist.
/// <c>ListenLocalhost</c> is not used because Kestrel rejects port 0 on <c>localhost</c>.
/// </para>
/// <para>
/// A fresh 32-byte token is generated on every start, before the app listens (D10). It raises the
/// bar for other processes in the container; it is not protection against root there, which can
/// read it from Codex.
/// </para>
/// <para>
/// Registered only when <c>Agent:HostedProvider</c> is true. A failure to bind, or a bound address
/// that is not exactly one <c>127.0.0.1</c> endpoint, throws from <see cref="StartAsync"/> so the
/// host exits non-zero.
/// </para>
/// </remarks>
public sealed class HostedProviderAdapterHost : IHostedService, IAsyncDisposable
{
    private readonly HostedModelProvider _provider;
    private readonly HostedProviderKeyStore _keyStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly HttpMessageHandler _upstreamHandler;
    private readonly TaskCompletionSource<HostedProviderEndpoint> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;
    private HttpMessageInvoker? _upstream;

    public HostedProviderAdapterHost(
        IOptions<AgentOptions> agent, HostedProviderKeyStore keyStore, ILoggerFactory loggerFactory)
        : this(agent, keyStore, loggerFactory, CreateUpstreamHandler())
    {
    }

    /// <summary>
    /// The upstream handler (D4). No redirects, a 10 s connect timeout, no cookie jar, and no
    /// decompression, so response bytes and <c>Content-Encoding</c> pass through together.
    /// </summary>
    internal static SocketsHttpHandler CreateUpstreamHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    };

    /// <summary>A new per-start token: 32 random bytes, base64url.</summary>
    internal static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    internal HostedProviderAdapterHost(
        IOptions<AgentOptions> agent,
        HostedProviderKeyStore keyStore,
        ILoggerFactory loggerFactory,
        HttpMessageHandler upstreamHandler)
    {
        var options = agent.Value;
        if (!HostedModelProviders.TryResolve(options.Provider, options.Model, out var provider, out _))
            throw new InvalidOperationException(
                $"HostedProviderAdapterHost registered for model '{options.Model}' (provider "
                + $"'{options.Provider}'), which resolves to no hosted provider.");

        _provider = provider;
        _keyStore = keyStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HostedProviderAdapterHost>();
        _upstreamHandler = upstreamHandler;
    }

    /// <summary>The hosted provider this forwarder serves.</summary>
    public HostedModelProvider Provider => _provider;

    /// <summary>
    /// Completes with the base address and token once the forwarder is listening; faults if it
    /// failed to start. <see cref="CodexExecutor"/> awaits it before <c>thread/start</c>.
    /// </summary>
    public Task<HostedProviderEndpoint> Endpoint => _endpoint.Task;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = NewToken();
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());

            // The main host's logger factory, so adapter lines share the container log's pipeline
            // and formatter instead of a second, unconfigured one.
            builder.Services.AddSingleton(_loggerFactory);
            builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Listen(IPAddress.Loopback, 0);
            });

            var app = builder.Build();

            _upstream = new HttpMessageInvoker(_upstreamHandler, disposeHandler: true);
            var forwarder = new HostedProviderForwarder(
                _provider, () => _keyStore.Key, token, _upstream, _loggerFactory.CreateLogger<HostedProviderForwarder>());

            // One terminal handler: the forwarder matches POST /{prefix}/responses itself and
            // answers 404 for everything else. No routing, so no other endpoint can be added by
            // accident.
            app.Run(forwarder.HandleAsync);

            await app.StartAsync(cancellationToken);
            _app = app;

            var addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
            var address = addresses.Count == 1 && Uri.TryCreate(addresses[0], UriKind.Absolute, out var parsed)
                ? parsed
                : null;

            if (address is null || address.Host != "127.0.0.1" || address.Port <= 0)
                throw new InvalidOperationException(
                    "HostedProviderAdapterHost: the adapter must report exactly one 127.0.0.1 address; "
                    + $"it reported [{string.Join(", ", addresses)}].");

            var baseAddress = new Uri($"http://127.0.0.1:{address.Port}");
            _logger.LogInformation(
                "Codex hosted provider {Prefix} via loopback adapter 127.0.0.1:{Port}",
                _provider.Prefix, address.Port);
            _endpoint.TrySetResult(new HostedProviderEndpoint(baseAddress, token));
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                "HostedProviderAdapterHost: the {Prefix} loopback adapter failed to start: {Error}",
                _provider.Prefix, ex.Message);
            _endpoint.TrySetException(new InvalidOperationException(
                $"The {_provider.Prefix} loopback adapter failed to start: {ex.Message}", ex));
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
            await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _app = null;
        _upstream?.Dispose();
        _upstream = null;
    }
}
