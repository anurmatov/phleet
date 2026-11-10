using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>
/// The one file the orchestrator writes and <c>fleet-secrets</c> reads: which secrets are bound to
/// which hosts, their current values, and which agent holds which placeholder. It is replaced
/// atomically, so the forwarder always sees one consistent snapshot.
/// </summary>
/// <remarks>
/// It holds secret values, so it is written 0600 into a directory only the forwarder mounts. It never
/// holds a placeholder, only its SHA-256.
/// </remarks>
public sealed record SecretProxyState
{
    public const int CurrentVersion = 1;
    public const string FileName = "state.json";

    /// <summary>Relative to the fleet base directory.</summary>
    public const string StateDirectory = "secret-proxy/state";

    /// <summary>Relative to the fleet base directory. The forwarder creates the CA here on first start.</summary>
    public const string CaDirectory = "secret-proxy/ca";
    public const string CaCertificateFile = "ca.crt";
    public const string CaKeyFile = "ca.key";

    /// <summary>Where the CA certificate is mounted in an agent container, and what trusts it there.</summary>
    public const string AgentCaPath = "/usr/local/share/ca-certificates/fleet-secrets.crt";

    // All four are required in the file: an empty or truncated document is not an empty state.
    public required int Version { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required IReadOnlyList<SecretProxyBinding> Bindings { get; init; }
    public required IReadOnlyList<SecretProxyGrant> Grants { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Parses and validates a state file. Any fault returns null with <paramref name="fault"/> set to a
    /// fixed code: the forwarder then denies everything rather than run on a state it half understands.
    /// </summary>
    public static SecretProxyState? TryParse(string? json, out string? fault)
    {
        fault = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            fault = "state_empty";
            return null;
        }

        SecretProxyState? state;
        try
        {
            state = JsonSerializer.Deserialize<SecretProxyState>(json, Json);
        }
        catch (JsonException)
        {
            fault = "state_unreadable";
            return null;
        }

        if (state is null || state.Bindings is null || state.Grants is null)
        {
            fault = "state_unreadable";
            return null;
        }
        if (state.Version != CurrentVersion)
        {
            fault = "state_version_unsupported";
            return null;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in state.Bindings)
        {
            if (binding is null
                || SecretBindingRules.DescribeFault(binding.Key, binding.Kind, binding.Hosts, binding.Header) is not null
                || !keys.Add(binding.Key))
            {
                fault = "state_binding_invalid";
                return null;
            }
        }

        foreach (var grant in state.Grants)
        {
            if (grant is null
                || string.IsNullOrWhiteSpace(grant.Agent)
                || !keys.Contains(grant.Key ?? "")
                || !IPAddress.TryParse(grant.SourceIp, out _)
                || grant.PlaceholderSha256 is not { Length: 64 }
                || !grant.PlaceholderSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                fault = "state_grant_invalid";
                return null;
            }
        }

        return state;
    }
}

/// <summary>One bound secret: where it may be sent, in which header, and its current value.</summary>
/// <param name="Key">The <c>.env</c> key. Agents reference it as an Env Ref.</param>
/// <param name="Kind"><see cref="SecretBindingRules.KindStatic"/> or <see cref="SecretBindingRules.KindGitHubApp"/>.</param>
/// <param name="Hosts">Exact lowercase host names. The secret is sent nowhere else.</param>
/// <param name="Header">The one request header the secret may be injected into.</param>
/// <param name="Value">
/// The value from <c>.env</c>: the secret itself, or for a GitHub App the base64 private key. Null
/// when <c>.env</c> has no usable value, in which case the forwarder refuses rather than forwards.
/// </param>
/// <param name="AppId">GitHub App only: the App id from <c>.env</c>.</param>
public sealed record SecretProxyBinding(
    string Key,
    string Kind,
    IReadOnlyList<string> Hosts,
    string Header,
    string? Value,
    string? AppId = null);

/// <summary>One agent's hold on one bound secret.</summary>
/// <param name="Agent">The agent name. Logged; never used for the match.</param>
/// <param name="SourceIp">The agent's fixed address on the secrets network. A request from elsewhere never matches.</param>
/// <param name="Key">The bound secret.</param>
/// <param name="PlaceholderSha256">Lowercase hex SHA-256 of the placeholder minted at the last provision.</param>
public sealed record SecretProxyGrant(string Agent, string SourceIp, string Key, string PlaceholderSha256);

/// <summary>
/// The field rules of a secret binding, enforced when it is written (orchestrator) and again when it
/// is loaded (forwarder).
/// </summary>
public static partial class SecretBindingRules
{
    public const string KindStatic = "static";
    public const string KindGitHubApp = "github_app";
    public const string DefaultHeader = "Authorization";
    public const int MaxHosts = 8;

    /// <summary>Request headers a secret may never be injected into: they route or frame the request.</summary>
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "TE", "Trailer",
        "Keep-Alive", "Expect", "Cookie", "Proxy-Authorization", "Proxy-Connection", "Content-Type",
        "Accept", "User-Agent", "Referer", "Origin", "Location",
    };

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EnvKey();

    // One DNS name: lowercase labels, at least one dot, no wildcard, no port, no trailing dot.
    [GeneratedRegex(@"\A(?=.{1,253}\z)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();

    [GeneratedRegex(@"\A[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderToken();

    public static bool IsEnvKey(string? value) => value is not null && EnvKey().IsMatch(value);

    public static bool IsHostName(string? value) => value is not null && HostName().IsMatch(value);

    /// <summary>Why these fields are not a valid binding, or null. Fixed text: no value is echoed.</summary>
    public static string? DescribeFault(string? key, string? kind, IReadOnlyList<string>? hosts, string? header)
    {
        if (!IsEnvKey(key))
            return "secret_binding_key_invalid: use an environment variable name (letters, digits, underscore)";
        if (kind is not (KindStatic or KindGitHubApp))
            return $"secret_binding_kind_invalid: use '{KindStatic}' or '{KindGitHubApp}'";
        if (hosts is null || hosts.Count == 0)
            return "secret_binding_hosts_missing: name at least one host";
        if (hosts.Count > MaxHosts)
            return $"secret_binding_hosts_invalid: at most {MaxHosts} hosts";
        if (hosts.Any(h => !IsHostName(h)))
            return "secret_binding_hosts_invalid: each host must be one exact lowercase DNS name, "
                + "with no scheme, port, path, wildcard or IP address";
        if (hosts.Distinct(StringComparer.Ordinal).Count() != hosts.Count)
            return "secret_binding_hosts_invalid: a host is listed twice";
        if (header is null || !HeaderToken().IsMatch(header) || ForbiddenHeaders.Contains(header))
            return "secret_binding_header_invalid: name one request header that carries the credential, such as "
                + DefaultHeader;
        if (kind == KindGitHubApp && !string.Equals(header, DefaultHeader, StringComparison.OrdinalIgnoreCase))
            return $"secret_binding_header_invalid: a GitHub App binding always uses {DefaultHeader}";
        return null;
    }
}
