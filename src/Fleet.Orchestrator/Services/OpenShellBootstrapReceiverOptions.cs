using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using MySqlConnector;

namespace Fleet.Orchestrator.Services;

public sealed class OpenShellBootstrapReceiverOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 3601;
    public string Issuer { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string AssertionPublicKeyPath { get; set; } = "";
    public string IngressCaPath { get; set; } = "";
    public string IngressDnsName { get; set; } = "";
    public string ServerCertificatePath { get; set; } = "";
    public string ServerKeyPath { get; set; } = "";
}

public static class OpenShellBootstrapRegistration
{
    public static void AddOpenShellBootstrapReceiver(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("OpenShellBootstrapReceiver").Get<OpenShellBootstrapReceiverOptions>() ?? new();
        builder.Services.AddSingleton(options);
        if (!options.Enabled) return;
        foreach (var endpoint in builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
            if (Uri.TryCreate(endpoint["Url"], UriKind.Absolute, out var configured) && configured.Port == options.Port)
                throw new InvalidOperationException("bootstrap_listener_collision");
        var trust = new OpenShellBootstrapTrust(options); builder.Services.AddSingleton(trust);
        builder.Services.AddSingleton<OpenShellBootstrapReader>(sp => new(
            () => new MySqlConnection(builder.Configuration.GetConnectionString("OrchestratorDb") ?? throw new InvalidOperationException("bootstrap_state_unavailable")),
            sp.GetRequiredService<IOpenShellGatewayClient>()));
        builder.Services.AddSingleton<OpenShellBootstrapReceiver>();
        builder.WebHost.ConfigureKestrel(server => server.ListenAnyIP(options.Port, listener => listener.UseHttps(tls =>
        {
            tls.ServerCertificate = X509Certificate2.CreateFromPemFile(options.ServerCertificatePath, options.ServerKeyPath);
            tls.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            tls.ClientCertificateValidation = (certificate, _, _) => trust.AcceptCertificate(certificate);
        })));
    }
    public static void UseOpenShellBootstrapReceiver(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<OpenShellBootstrapReceiverOptions>();
        app.Use(async (context, next) =>
        {
            if (options.Enabled && context.Connection.LocalPort == options.Port)
            {
                if (context.Request.Path != "/identity/ready") { context.Response.StatusCode = 404; return; }
                // No normal/admin endpoint is reachable on this listener. No operator-auth exemption.
                try
                {
                    var receiver = context.RequestServices.GetService<OpenShellBootstrapReceiver>();
                    if (receiver is null) { context.Response.StatusCode = 503; return; }
                    await receiver.HandleAsync(context);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { context.Response.StatusCode = 503; }
                return;
            }
            if (context.Request.Path == "/identity/ready") { context.Response.StatusCode = 404; return; }
            await next(context);
        });
    }
}
