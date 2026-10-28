using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Creates and revokes one managed epoch's broker vhost and its three principals through the
/// configured private HTTPS RabbitMQ management API (#489 §6). The administrator credential is an
/// explicit trusted-control boundary read from host .env references; it never reaches a workload,
/// argv or a log. Partial writes are reported as owned partials, never activated or replayed.
/// </summary>
public sealed class StockBrokerProvisioner(IConfiguration config, ICredentialsReader credentials)
{
    public static readonly string[] Roles = ["workload", "control", "observer"];

    // Names come from the existing source declarations (agent queue, relay, group topic,
    // orchestrator heartbeats and the bridge reply queue). Every name is regex-escaped and anchored.
    internal const string RelayExchange = "fleet.relay";
    internal const string GroupExchange = "fleet.group";
    internal const string HeartbeatExchange = "fleet.orchestrator";
    internal const string HeartbeatQueue = "fleet.orchestrator.heartbeats";
    internal const string BridgeReplyQueue = "fleet.agent.bridge";

    public sealed record Outcome(bool Ok, string? Code, IReadOnlyList<string> OwnedPartials,
        IReadOnlyDictionary<string, string>? Passwords = null);

    /// <summary>Test seam: replaces the verified HTTPS handler.</summary>
    internal Func<HttpMessageHandler>? HandlerFactory { get; init; }

    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static string VirtualHost(Guid epoch) => "stock-" + epoch.ToString("N");
    public static string Principal(Guid epoch, string role) => VirtualHost(epoch) + "-" + role;

    /// <summary>Named prerequisite fault, checked before any external action.</summary>
    public string? DescribePreflightFault()
    {
        if (!Uri.TryCreate(config["StockOpenShell:BrokerManagementUrl"], UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            return "broker_management_https_required";
        var ca = config["StockOpenShell:BrokerCaFile"];
        if (string.IsNullOrEmpty(ca) || !Path.IsPathFullyQualified(ca) || !File.Exists(ca)) return "broker_management_trust_missing";
        try { if (LoadCa(ca).Count == 0) return "broker_management_trust_invalid"; }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        { return "broker_management_trust_invalid"; }
        return AdminCredential() is null ? "broker_management_credential_missing" : null;
    }

    /// <summary>
    /// Exact anchored permissions for one role. Workload: its own queue plus relay/topic exchanges;
    /// control: relay exchange and bridge reply queue; observer: heartbeat topic and queue.
    /// </summary>
    internal static (string Configure, string Write, string Read) Permissions(string role, string agentQueue)
    {
        static string Any(params string[] names) => "^(" + string.Join("|", names.Select(Regex.Escape)) + ")$";
        return role switch
        {
            "workload" => (Any(RelayExchange, GroupExchange, HeartbeatExchange, agentQueue),
                Any(RelayExchange, GroupExchange, HeartbeatExchange, agentQueue),
                Any(RelayExchange, agentQueue)),
            "control" => (Any(RelayExchange, BridgeReplyQueue), Any(RelayExchange, BridgeReplyQueue), Any(RelayExchange, BridgeReplyQueue)),
            "observer" => (Any(HeartbeatExchange, HeartbeatQueue), Any(HeartbeatQueue), Any(HeartbeatExchange, HeartbeatQueue)),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
    }

    /// <summary>
    /// PUT the epoch vhost, three non-administrator users with independent random passwords and
    /// their exact permissions. An existing vhost or user is not ours and refuses; no default-vhost
    /// or global grant is written.
    /// </summary>
    public async Task<Outcome> CreateEpochAsync(Guid epoch, string agentShortName, CancellationToken ct)
    {
        if (DescribePreflightFault() is { } fault) return new(false, fault, []);
        var owned = new List<string>();
        var vhost = VirtualHost(epoch);
        var agentQueue = "fleet.agent." + agentShortName;
        try
        {
            using var http = Client();
            if (await ExistsAsync(http, $"api/vhosts/{Uri.EscapeDataString(vhost)}", ct)) return new(false, "broker_resource_conflict", owned);
            foreach (var role in Roles)
                if (await ExistsAsync(http, $"api/users/{Uri.EscapeDataString(Principal(epoch, role))}", ct))
                    return new(false, "broker_resource_conflict", owned);

            await PutAsync(http, $"api/vhosts/{Uri.EscapeDataString(vhost)}", new { description = "fleet managed stock epoch" }, ct);
            owned.Add("vhost:" + vhost);
            var passwords = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var role in Roles)
            {
                var user = Principal(epoch, role);
                var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                await PutAsync(http, $"api/users/{Uri.EscapeDataString(user)}", new { password, tags = "" }, ct);
                owned.Add("user:" + user);
                var (configure, write, read) = Permissions(role, agentQueue);
                await PutAsync(http, $"api/permissions/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(user)}",
                    new { configure, write, read }, ct);
                owned.Add("permission:" + user);
                passwords[role] = password;
            }
            return new(true, null, owned, passwords);
        }
        catch (BrokerFault ex) { return new(false, ex.Message, owned); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        { return new(false, owned.Count == 0 ? "broker_management_unavailable" : "broker_management_unknown", owned); }
    }

    /// <summary>
    /// Removes the three users' permissions, closes their connections and deletes the users. The
    /// retired vhost and its queues are kept for custody. Every step runs; failures aggregate.
    /// </summary>
    public async Task<Outcome> RevokeEpochAsync(Guid epoch, CancellationToken ct)
    {
        if (DescribePreflightFault() is { } fault) return new(false, fault, []);
        var vhost = VirtualHost(epoch);
        var unresolved = new List<string>();
        try
        {
            using var http = Client();
            foreach (var role in Roles)
            {
                var user = Principal(epoch, role);
                await Step(() => DeleteAsync(http, $"api/permissions/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(user)}", ct), "permission:" + user);
                await Step(() => CloseConnectionsAsync(http, user, ct), "connections:" + user);
                await Step(() => DeleteAsync(http, $"api/users/{Uri.EscapeDataString(user)}", ct), "user:" + user);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        { unresolved.Add("broker_management_unavailable"); }
        return unresolved.Count == 0 ? new(true, null, []) : new(false, "broker_revoke_unresolved", unresolved);

        async Task Step(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception ex) when (ex is BrokerFault or HttpRequestException or TaskCanceledException or IOException)
            { unresolved.Add(label); }
        }
    }

    private (string User, string Password)? AdminCredential()
    {
        var userRef = config["StockOpenShell:BrokerAdminUserRef"];
        var passwordRef = config["StockOpenShell:BrokerAdminPasswordRef"];
        if (string.IsNullOrEmpty(userRef) || string.IsNullOrEmpty(passwordRef)) return null;
        var user = credentials.Get(userRef);
        var password = credentials.Get(passwordRef);
        return string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) ? null : (user, password);
    }

    private HttpClient Client()
    {
        var admin = AdminCredential() ?? throw new BrokerFault("broker_management_credential_missing");
        var url = new Uri(config["StockOpenShell:BrokerManagementUrl"]!.TrimEnd('/') + "/");
        var http = new HttpClient(HandlerFactory?.Invoke() ?? VerifiedHandler(url.IdnHost), disposeHandler: true)
        {
            BaseAddress = url,
            Timeout = RequestTimeout,
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(admin.User + ":" + admin.Password)));
        return http;
    }

    /// <summary>Operator CA only, exact server name, no TLS skip and no system-store fallback.</summary>
    private HttpMessageHandler VerifiedHandler(string serverName)
    {
        var authorities = LoadCa(config["StockOpenShell:BrokerCaFile"]!);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(new X509Certificate2(certificate));
                },
            },
        };
    }

    /// <summary>Operator CA certificates only (no private key is expected or read).</summary>
    private static X509Certificate2Collection LoadCa(string path)
    {
        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(path);
        return collection;
    }

    private static async Task<bool> ExistsAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (response.IsSuccessStatusCode) return true;
        throw new BrokerFault(Classify(response.StatusCode));
    }

    private static async Task PutAsync(HttpClient http, string path, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.PutAsync(path, content, ct);
        if (!response.IsSuccessStatusCode) throw new BrokerFault(Classify(response.StatusCode));
    }

    private static async Task DeleteAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(path, ct);
        // Already absent is the revoked state we want; anything else stays unresolved.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw new BrokerFault(Classify(response.StatusCode));
    }

    private static async Task CloseConnectionsAsync(HttpClient http, string user, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/connections/username/{Uri.EscapeDataString(user)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw new BrokerFault(Classify(response.StatusCode));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        foreach (var connection in document.RootElement.EnumerateArray())
        {
            if (connection.TryGetProperty("user", out var owner) && owner.GetString() != user) continue;
            var name = connection.GetProperty("name").GetString() ?? throw new BrokerFault("broker_connection_invalid");
            await DeleteAsync(http, $"api/connections/{Uri.EscapeDataString(name)}", ct);
        }
    }

    private static string Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "broker_management_auth_failed",
        HttpStatusCode.Forbidden => "broker_management_permission_denied",
        HttpStatusCode.Conflict => "broker_resource_conflict",
        _ => "broker_management_failed",
    };

    private sealed class BrokerFault(string code) : Exception(code);
}
