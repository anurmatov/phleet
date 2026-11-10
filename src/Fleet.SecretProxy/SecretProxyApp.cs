using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fleet.SecretProxy;

/// <summary>Builds the <c>fleet-secrets</c> host. Separate from <c>Program.cs</c> so tests run the real thing.</summary>
public static class SecretProxyApp
{
    /// <param name="args">Command-line configuration.</param>
    /// <param name="configure">Extra builder configuration (tests: options, logging).</param>
    /// <param name="upstream">The upstream transport; tests substitute one. The default never follows redirects.</param>
    public static WebApplication Build(
        string[] args, Action<WebApplicationBuilder>? configure = null, HttpMessageInvoker? upstream = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection(SecretProxyOptions.Section).Get<SecretProxyOptions>() ?? new();
        if (!IPAddress.TryParse(options.ListenAddress, out var listenAddress))
            throw new InvalidOperationException("secret_proxy_listen_address_invalid: SecretProxy:ListenAddress must be an IP address.");
        if (!Uri.TryCreate(options.GitHubApiBaseUrl, UriKind.Absolute, out var gitHubApi) || gitHubApi.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("secret_proxy_github_api_invalid: SecretProxy:GitHubApiBaseUrl must be an https URL.");

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<StateStore>();
        builder.Services.AddSingleton(sp => CertificateAuthority.LoadOrCreate(
            options.CaDirectory, sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CertificateAuthority).FullName!)));
        builder.Services.AddSingleton(_ => upstream ?? new HttpMessageInvoker(CreateUpstreamHandler(), disposeHandler: true));
        builder.Services.AddSingleton<GitHubAppTokens>(sp => new GitHubAppTokens(
            sp.GetRequiredService<HttpMessageInvoker>(), options,
            sp.GetRequiredService<ILogger<GitHubAppTokens>>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<InjectingForwarder>();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Bodies are relayed as they arrive (a git push can be large); nothing is buffered here.
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.AddServerHeader = false;

            kestrel.Listen(listenAddress, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    // A certificate exists only for a bound host. Any other name, or none, fails
                    // the handshake: the forwarder cannot be made to answer for an arbitrary host.
                    https.ServerCertificateSelector = (_, name) =>
                    {
                        if (string.IsNullOrEmpty(name))
                            return null;
                        var host = name.ToLowerInvariant();
                        var services = listen.ApplicationServices;
                        return services.GetRequiredService<StateStore>().Current?.IsBoundHost(host) == true
                            ? services.GetRequiredService<CertificateAuthority>().LeafFor(host)
                            : null;
                    };
                });
            });

            if (options.HealthPort > 0)
                kestrel.Listen(IPAddress.Loopback, options.HealthPort, listen => listen.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();

        // Create or load the CA now: a missing or broken CA is a startup failure, not a per-request one.
        _ = app.Services.GetRequiredService<CertificateAuthority>();

        app.Run(context =>
        {
            if (context.Request.IsHttps)
                return context.RequestServices.GetRequiredService<InjectingForwarder>().HandleAsync(context);

            // The loopback health listener: nothing else is served without TLS.
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/health")
            {
                var fault = context.RequestServices.GetRequiredService<StateStore>().Fault;
                return context.Response.WriteAsJsonAsync(new
                {
                    status = "healthy",
                    service = "fleet-secrets",
                    state = fault ?? "loaded",
                });
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        return app;
    }

    /// <summary>
    /// The upstream transport: the bound host by name with ordinary certificate validation, no
    /// redirects, no cookies, no proxy, no decompression (bytes are relayed as sent).
    /// </summary>
    internal static SocketsHttpHandler CreateUpstreamHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}
