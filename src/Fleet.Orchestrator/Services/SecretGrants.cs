using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// The orchestrator's startup-only view of <c>fleet-secrets</c>: whether it is on, and the internal
/// network the two share. Read once from the raw configuration; a change needs a recreate.
/// </summary>
/// <remarks>
/// A wrong value never turns the feature silently off. <see cref="Fault"/> is logged at startup and
/// returned by every provision of an agent that holds a bound secret, so that agent does not start
/// with the secret delivered some other way. Agents without bound secrets are unaffected.
/// </remarks>
public sealed class SecretProxySettings
{
    public bool Enabled { get; }
    public SecretProxyNetwork? Network { get; }
    public string? Fault { get; }

    private SecretProxySettings(bool enabled, SecretProxyNetwork? network, string? fault)
    {
        Enabled = enabled;
        Network = network;
        Fault = fault;
    }

    public static SecretProxySettings Disabled { get; } = new(false, null, null);

    public static SecretProxySettings FromConfiguration(IConfiguration config)
    {
        // The same reading as fleet_secrets_enabled in scripts/lib/comms-profiles.sh, which decides
        // whether setup and upgrade start the forwarder: true or false in any letter case, blanks and
        // one pair of quotes ignored. The two must agree, or one side waits for the other.
        var raw = (config["SecretProxy:Enabled"] ?? "").Trim();
        if (raw.Length >= 2 && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\'')))
            raw = raw[1..^1].Trim();
        if (raw.Length == 0 || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            return Disabled;
        if (!string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            return new(false, null, "secret_proxy_setting_invalid: FLEET_SECRETS_ENABLED must be true or false");

        var subnet = config["SecretProxy:Subnet"];
        var address = config["SecretProxy:Address"];
        var fault = SecretProxyNetwork.TryParse(
            string.IsNullOrWhiteSpace(subnet) ? SecretProxyNetwork.DefaultSubnet : subnet,
            string.IsNullOrWhiteSpace(address) ? SecretProxyNetwork.DefaultAddress : address,
            out var network);
        return fault is null ? new(true, network, null) : new(false, null, fault);
    }

    /// <summary>For tests: an enabled configuration on the default network.</summary>
    internal static SecretProxySettings ForTests(bool enabled = true)
    {
        if (!enabled) return Disabled;
        _ = SecretProxyNetwork.TryParse(SecretProxyNetwork.DefaultSubnet, SecretProxyNetwork.DefaultAddress, out var network);
        return new(true, network, null);
    }
}

/// <summary>One bound Env Ref of one agent, with the placeholder its container gets.</summary>
public sealed record SecretPlanGrant(string EnvKeyName, string Placeholder);

/// <summary>
/// Everything a container needs to use its bound secrets through placeholders: the environment
/// entries, the host steering, the CA mount and its fixed address on the secrets network.
/// </summary>
/// <param name="Grants">One per bound Env Ref. Empty when the agent holds no bound secret.</param>
/// <param name="Env">Placeholder variables and CA trust variables, <c>NAME=value</c>.</param>
/// <param name="ExtraHosts"><c>host:address</c> entries that point each bound host at the forwarder.</param>
/// <param name="Bind">The read-only CA certificate mount, relative to the fleet base directory.</param>
/// <param name="Address">The agent's fixed address on <see cref="SecretProxyNetwork.NetworkName"/>.</param>
/// <param name="SshBind">
/// The read-only mount of the agent's own signing-socket directory, relative to the fleet base
/// directory. Null unless the agent holds a bound SSH key.
/// </param>
public sealed record SecretPlan(
    IReadOnlyList<SecretPlanGrant> Grants,
    IReadOnlyList<string> Env,
    IReadOnlyList<string> ExtraHosts,
    string? Bind,
    string? Address,
    string? SshBind = null)
{
    public static SecretPlan None { get; } = new([], [], [], null, null);

    public bool HasGrants => Grants.Count > 0;
}

/// <summary>
/// Decides how one agent's Env Refs are delivered. A key with a <see cref="SecretBinding"/> is never
/// written into the container: the container gets a placeholder and the plan to reach the forwarder.
/// This is the only place that decision is made.
/// </summary>
public static class SecretGrantPlanner
{
    /// <summary>Where Node finds the extra CA; the system store is updated by <c>entrypoint.sh</c>.</summary>
    public const string NodeCaVariable = "NODE_EXTRA_CA_CERTS";

    /// <summary>Where Python's <c>requests</c> and pip find the system store (they ship their own bundle otherwise).</summary>
    public const string RequestsCaVariable = "REQUESTS_CA_BUNDLE";

    /// <summary>
    /// The system store by its conventional variable: for clients that carry their own roots unless
    /// told otherwise (rustls-based tools such as uv, Ruby, some Python builds). It names the path
    /// that is already the default, so a client that used the system store is unaffected.
    /// </summary>
    public const string OpenSslCaVariable = "SSL_CERT_FILE";
    public const string SystemCaBundle = "/etc/ssl/certs/ca-certificates.crt";

    public static string CaBind =>
        $"./{SecretProxyState.CaDirectory}/{SecretProxyState.CaCertificateFile}:{SecretProxyState.AgentCaPath}:ro";

    /// <summary>True when <paramref name="envKeyName"/> has a binding and so must never reach a container.</summary>
    public static bool IsBound(IEnumerable<SecretBinding> bindings, string envKeyName) =>
        bindings.Any(b => string.Equals(b.EnvKeyName, envKeyName, StringComparison.Ordinal));

    /// <summary>
    /// The plan for <paramref name="agent"/>, or a fault. <paramref name="mint"/> supplies each
    /// placeholder: a real provision passes <see cref="SecretPlaceholders.Create"/>, a preview passes a
    /// constant.
    /// </summary>
    /// <param name="caPresent">Whether the forwarder's CA certificate exists on the host.</param>
    public static (SecretPlan? Plan, string? Fault) Plan(
        Agent agent, IReadOnlyCollection<SecretBinding> bindings, SecretProxySettings settings,
        bool caPresent, Func<string> mint)
    {
        var bound = agent.EnvRefs
            .Select(r => (Ref: r, Binding: bindings.FirstOrDefault(b => string.Equals(b.EnvKeyName, r.EnvKeyName, StringComparison.Ordinal))))
            .Where(x => x.Binding is not null)
            .OrderBy(x => x.Ref.EnvKeyName, StringComparer.Ordinal)
            .ToList();

        if (bound.Count == 0)
            return (SecretPlan.None, null);

        var keys = string.Join(", ", bound.Select(x => x.Ref.EnvKeyName));
        if (settings.Fault is { } settingsFault)
            return (null, $"{settingsFault} (agent '{agent.Name}' holds bound secrets: {keys})");
        if (!settings.Enabled || settings.Network is null)
            return (null,
                $"secret_proxy_disabled: {keys} can only be delivered as placeholders, and FLEET_SECRETS_ENABLED is not true. "
                + "Enable fleet-secrets, or remove the Env Ref from this agent.");

        if (settings.Network.AgentAddress(agent.Id) is not { } address)
            return (null,
                $"secret_proxy_address_exhausted: the subnet {settings.Network.Subnet} has no address for agent id {agent.Id}. "
                + "Set a wider FLEET_SECRETS_SUBNET and recreate the network.");
        if (!caPresent)
            return (null,
                "secret_proxy_ca_missing: fleet-secrets has not created its CA certificate yet. Start it, then provision again.");

        var grants = new List<SecretPlanGrant>();
        var env = new List<string>();
        var exposed = new Dictionary<string, string>(StringComparer.Ordinal);
        var hosts = new SortedSet<string>(StringComparer.Ordinal);
        var rawKeys = agent.EnvRefs
            .Where(r => !IsBound(bindings, r.EnvKeyName))
            .Select(r => r.EnvKeyName)
            .ToHashSet(StringComparer.Ordinal);

        string? sshBind = null;
        foreach (var (reference, binding) in bound)
        {
            // Every grant has a placeholder hash, an SSH key's included: it is what marks the grant
            // as held by a running container. The placeholder of an SSH key goes nowhere.
            var placeholder = mint();
            grants.Add(new SecretPlanGrant(reference.EnvKeyName, placeholder));

            if (binding!.Kind == SecretBindingRules.KindSshKey)
            {
                // Not a variable and not a steered host: the agent reaches the SSH server itself and
                // asks fleet-secrets, over a socket in its own directory, to sign.
                if (SecretProxySsh.AgentDirectoryName(agent.Name) is not { } directory)
                    return (null,
                        $"secret_ssh_agent_name_unsupported: SSH key {reference.EnvKeyName} needs a socket directory named "
                        + $"after the agent, and '{agent.Name}' is not usable as one (letters, digits, '.', '_' and '-', at most 48).");
                if (rawKeys.Contains(SecretProxySsh.SocketVariable))
                    return (null,
                        $"secret_expose_conflict: SSH key {reference.EnvKeyName} sets {SecretProxySsh.SocketVariable}, "
                        + "which is also a plain Env Ref of this agent. Remove one.");
                sshBind = SecretProxySsh.AgentBind(directory);
                continue;
            }

            foreach (var name in binding.ExposeAsList)
            {
                if (rawKeys.Contains(name))
                    return (null,
                        $"secret_expose_conflict: bound secret {reference.EnvKeyName} is exposed as {name}, "
                        + "which is also a plain Env Ref of this agent. Remove one.");
                if (!exposed.TryAdd(name, reference.EnvKeyName))
                    return (null,
                        $"secret_expose_conflict: {exposed[name]} and {reference.EnvKeyName} are both exposed as {name}. "
                        + "Attach only one of them to this agent.");
                env.Add($"{name}={placeholder}");
            }
            foreach (var host in binding.HostList)
                hosts.Add(host);
        }

        env.Add($"{NodeCaVariable}={SecretProxyState.AgentCaPath}");
        env.Add($"{RequestsCaVariable}={SystemCaBundle}");
        env.Add($"{OpenSslCaVariable}={SystemCaBundle}");
        if (sshBind is not null)
        {
            if (exposed.ContainsKey(SecretProxySsh.SocketVariable))
                return (null,
                    $"secret_expose_conflict: {exposed[SecretProxySsh.SocketVariable]} is exposed as {SecretProxySsh.SocketVariable}, "
                    + "which a bound SSH key of this agent sets. Attach only one of them.");
            env.Add($"{SecretProxySsh.SocketVariable}={SecretProxySsh.AgentSocketPath}");
        }

        return (new SecretPlan(
            grants,
            env,
            [.. hosts.Select(h => $"{h}:{settings.Network.ProxyAddress}")],
            CaBind,
            address.ToString(),
            sshBind), null);
    }

    /// <summary>
    /// Why these fields cannot be saved as a binding, or null. Adds the orchestrator-only rules to
    /// <see cref="SecretBindingRules.DescribeFault"/>: which keys can be bound at all, and how the
    /// placeholder may be exposed.
    /// </summary>
    public static string? DescribeBindingFault(
        string? envKeyName, string? kind, IReadOnlyList<string>? hosts, string? header,
        IReadOnlyList<string>? exposeAs, string? appIdEnvKey, IReadOnlyList<string>? knownHosts = null)
    {
        if (SecretBindingRules.DescribeFault(envKeyName, kind, hosts, header, knownHosts) is { } fault)
            return fault;

        // A bot token is consumed by the agent process itself, in a URL path; a hosted-model key is
        // already isolated by the in-container forwarder. Neither can be a header placeholder.
        if (JournalGrants.IsBotRef(envKeyName!))
            return "secret_binding_key_unsupported: a Telegram bot token is used by the agent process in a URL path and cannot be bound";
        if (HostedModelProviders.KeyEnvVars.Contains(envKeyName!, StringComparer.Ordinal))
            return "secret_binding_key_unsupported: a hosted-model key is already kept out of the agent by its own forwarder";

        if (kind == SecretBindingRules.KindSshKey)
        {
            if (exposeAs is { Count: > 0 })
                return "secret_binding_expose_invalid: an SSH key is never exposed as an environment variable; "
                    + "the agent gets a signing socket instead";
            return string.IsNullOrEmpty(appIdEnvKey)
                ? null
                : "secret_binding_app_id_unexpected: only a GitHub App binding has an App id key";
        }

        if (exposeAs is { Count: > 0 })
        {
            if (exposeAs.Count > 4 || exposeAs.Any(n => !SecretBindingRules.IsEnvKey(n)))
                return "secret_binding_expose_invalid: list at most four environment variable names";
            if (exposeAs.Distinct(StringComparer.Ordinal).Count() != exposeAs.Count)
                return "secret_binding_expose_invalid: a name is listed twice";
        }

        // "Section__Key" names are configuration of the agent process itself: it would send the
        // placeholder straight to an internal service, which is not a bound host.
        IReadOnlyList<string> effective = exposeAs is { Count: > 0 } ? exposeAs : [envKeyName!];
        if (effective.Any(n => JournalGrants.IsBotRef(n) || n.Contains("__", StringComparison.Ordinal)))
            return "secret_binding_expose_invalid: a placeholder cannot be exposed under a name the agent process reads as its own configuration";

        if (kind == SecretBindingRules.KindGitHubApp)
        {
            if (!SecretBindingRules.IsEnvKey(appIdEnvKey))
                return "secret_binding_app_id_missing: a GitHub App binding names the .env key that holds the App id";
            if (exposeAs is not { Count: > 0 } || exposeAs.Contains(envKeyName!, StringComparer.Ordinal))
                return "secret_binding_expose_invalid: a GitHub App binding exposes its placeholder under other names, "
                    + "such as GH_TOKEN and GITHUB_TOKEN, never under the private key's own name";
        }
        else if (!string.IsNullOrEmpty(appIdEnvKey))
        {
            return "secret_binding_app_id_unexpected: only a GitHub App binding has an App id key";
        }

        return null;
    }
}
