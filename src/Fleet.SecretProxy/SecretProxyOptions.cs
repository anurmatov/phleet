namespace Fleet.SecretProxy;

/// <summary>Startup-only settings of <c>fleet-secrets</c>. A change needs a restart.</summary>
public sealed class SecretProxyOptions
{
    public const string Section = "SecretProxy";

    /// <summary>The state file the orchestrator writes. Its directory is mounted read-only.</summary>
    public string StateFile { get; set; } = "/var/lib/fleet-secrets/state/state.json";

    /// <summary>Where the local CA lives. Created on first start; the key never leaves this directory.</summary>
    public string CaDirectory { get; set; } = "/var/lib/fleet-secrets/ca";

    /// <summary>The address the forwarder listens on: its fixed address on the secrets network.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 443;

    /// <summary>Loopback-only health listener. 0 disables it.</summary>
    public int HealthPort { get; set; } = 8080;

    /// <summary>
    /// A state older than this is refused whole (30 to 86400). The orchestrator refreshes the file
    /// every few seconds; when it stops, grants it could no longer revoke expire here.
    /// </summary>
    public int MaxStateAgeSeconds { get; set; } = Fleet.Shared.SecretProxyState.DefaultMaxAgeSeconds;

    /// <summary>Where GitHub App installation tokens are minted. HTTPS only.</summary>
    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com";
}
