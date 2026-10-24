using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleet.Agent.Services;

/// <summary>Bootstrap has no activation handler or reference to the normal registration graph.</summary>
public sealed class OpenShellStartupGuard
{
    public Uri Ingress { get; }
    public Guid LaunchId { get; }
    public Guid IdentityEpoch { get; }
    public string PlanHash { get; }
    public DateTime Deadline { get; }
    private readonly byte[] _capabilityHash;
    private int _probeStarted;

    public OpenShellStartupGuard(IConfiguration configuration) : this(configuration, File.ReadAllText) { }

    internal OpenShellStartupGuard(IConfiguration configuration, Func<string, string> readOwnedCapability)
    {
        var ingress = configuration["OpenShell:BootstrapIngress"];
        if (!Uri.TryCreate(ingress, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.AbsolutePath != "/identity/ready" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != ""
            || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns
            || !Guid.TryParseExact(configuration["OpenShell:LaunchId"], "D", out var launch) || launch == Guid.Empty
            || !Guid.TryParseExact(configuration["OpenShell:IdentityEpoch"], "D", out var epoch) || epoch == Guid.Empty
            || configuration["OpenShell:PlanHash"] is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigitLower)
            || !DateTimeOffset.TryParse(configuration["OpenShell:Deadline"], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var deadline) || deadline.Offset != TimeSpan.Zero
            || deadline.UtcDateTime <= DateTime.UtcNow || deadline.UtcDateTime > DateTime.UtcNow.AddSeconds(60)
            || configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("bootstrap_plan_invalid");
        var path = configuration["OpenShell:BootstrapCapabilityFile"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException("bootstrap_capability_missing");
        var capability = readOwnedCapability(path);
        if (capability.Length is < 32 or > 256 || capability.Any(c => c <= ' ' || c > '~'))
            throw new InvalidOperationException("bootstrap_capability_invalid");
        _capabilityHash = SHA256.HashData(Encoding.UTF8.GetBytes(capability));
        Ingress = uri; LaunchId = launch; IdentityEpoch = epoch; PlanHash = hash; Deadline = deadline.UtcDateTime;
    }

    public async Task ProbeAsync(HttpContext context, HttpClient client)
    {
        if (DateTime.UtcNow >= Deadline) { context.Response.StatusCode = 409; return; }
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } bearer || bearer.Length > 1024
            || !bearer.StartsWith("Bearer ", StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(_capabilityHash, SHA256.HashData(Encoding.UTF8.GetBytes(bearer[7..]))))
        { context.Response.StatusCode = 401; return; }
        if (context.Request.QueryString.HasValue || context.Request.ContentLength is > 0
            || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        { context.Response.StatusCode = 409; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var remaining = Deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) { context.Response.StatusCode = 409; return; }
        deadline.CancelAfter(remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5));
        try
        {
            if (await context.Request.Body.ReadAsync(new byte[1], deadline.Token) != 0
                || Interlocked.CompareExchange(ref _probeStarted, 1, 0) != 0)
            { context.Response.StatusCode = 409; return; }
            // No workload identity assertion, admin token or session is authored here. The authenticated
            // supervisor hook must attach the outside proof; direct or missing-hook traffic is denied there.
            using var request = new HttpRequestMessage(HttpMethod.Post, Ingress) { Content = new ByteArrayContent([]) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).WaitAsync(deadline.Token);
            context.Response.StatusCode = response.StatusCode == HttpStatusCode.NoContent ? 204 : 503;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { context.Response.StatusCode = 503; }
    }

    public static void MapBootstrap(WebApplication app)
    {
        app.MapPost("/bootstrap/probe", (HttpContext context, OpenShellStartupGuard guard, HttpClient client) => guard.ProbeAsync(context, client));
        // Deliberately no health-as-running, activation, CLI, tool, journal, broker or poller endpoint.
    }
}

public sealed class OpenShellBootstrapDeadline(OpenShellStartupGuard guard, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromTicks(Math.Max(1, (guard.Deadline - DateTime.UtcNow).Ticks)), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        lifetime.StopApplication();
    }
}
