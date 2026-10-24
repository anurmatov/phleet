using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleet.Identity.Configuration;
using Fleet.Identity.Services;
using Fleet.Shared.Security;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Identity").Get<IdentityOptions>() ?? new();
builder.Services.AddSingleton(options);
if (options.Enabled)
{
    options.Validate();
    var signingKey = ECDsa.Create(); signingKey.ImportFromPem(File.ReadAllText(options.AssertionPrivateKeyPath));
    // Prove private key custody at startup, never generate a replacement.
    _ = signingKey.ExportECPrivateKey();
    var issuer = new RequestAssertionIssuer(options, signingKey);
    var assertions = new FleetRequestAssertionVerifier(signingKey.ExportSubjectPublicKeyInfo(), options.Issuer, "memory:/mcp", options.KeyId);
    builder.Services.AddSingleton(signingKey); builder.Services.AddSingleton(issuer); builder.Services.AddSingleton(assertions);
    builder.Services.AddSingleton(new GatewayTokenVerifier(options, File.ReadAllText(options.GatewayJwksPath)));
    builder.Services.AddSingleton<IIdentityConnectionFactory, MySqlIdentityConnectionFactory>();
    builder.Services.AddSingleton<LaunchIdentityStore>(); builder.Services.AddSingleton<ReplayStore>();
    builder.Services.AddSingleton(ReceiverTransport.Create(options)); builder.Services.AddSingleton<IdentityIngress>();
    builder.Services.AddSingleton<BootstrapIdentityIngress>(sp => new(options,
        new FleetRequestAssertionVerifier(signingKey.ExportSubjectPublicKeyInfo(), options.Issuer, "orchestrator:/identity/ready", options.KeyId),
        sp.GetRequiredService<ReplayStore>(), sp.GetRequiredService<HttpClient>()));
    builder.Services.AddGrpc(o => { o.MaxReceiveMessageSize = 400000; o.EnableDetailedErrors = false; });
    var certificate = X509Certificate2.CreateFromPemFile(options.ServerCertificatePath, options.ServerKeyPath);
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.ListenAnyIP(options.HookPort, listen => { listen.Protocols = HttpProtocols.Http2; listen.UseHttps(certificate); });
        server.ListenAnyIP(options.IngressPort, listen => { listen.Protocols = HttpProtocols.Http1AndHttp2; listen.UseHttps(certificate); });
    });
}
var app = builder.Build();
if (options.Enabled)
{
    using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await app.Services.GetRequiredService<LaunchIdentityStore>().CheckAvailableAsync(startup.Token);
    // Do not expose either interface on the other listener or any configuration-added plaintext listener.
    app.Use(async (context, next) =>
    {
        var hook = context.Request.Path.StartsWithSegments("/openshell.middleware.v1.SupervisorMiddleware");
        if (!context.Request.IsHttps || context.Connection.LocalPort != (hook ? options.HookPort : options.IngressPort))
        { context.Response.StatusCode = 403; return; }
        await next();
    });
    app.MapGrpcService<SupervisorMiddlewareService>();
    app.MapPost("/identity/ready", (HttpContext context, BootstrapIdentityIngress ingress) => ingress.HandleAsync(context));
    app.MapMethods("/mcp", ["POST", "GET", "DELETE"], (HttpContext context, IdentityIngress ingress) => ingress.HandleAsync(context));
}
else app.MapGet("/health", () => Results.Ok(new { status = "disabled" }));
await app.RunAsync();
