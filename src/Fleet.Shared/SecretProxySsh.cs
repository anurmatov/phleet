using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>
/// Where a bound SSH key is used from. The key stays in <c>fleet-secrets</c>; an agent that holds a
/// grant gets one directory, mounted read-only, with a socket that speaks the ssh-agent protocol and
/// the host keys of the bound hosts. The orchestrator (which mounts the directory) and the forwarder
/// (which fills it) read the layout from here.
/// </summary>
public static partial class SecretProxySsh
{
    /// <summary>Relative to the fleet base directory: one subdirectory per agent.</summary>
    public const string Directory = "secret-proxy/ssh";

    /// <summary>Where an agent's own subdirectory is mounted in its container.</summary>
    public const string AgentMountPath = "/run/fleet-secrets/ssh";

    public const string SocketFile = "agent.sock";
    public const string KnownHostsFile = "known_hosts";

    /// <summary>The value of <c>SSH_AUTH_SOCK</c> in a container that holds an SSH grant.</summary>
    public const string AgentSocketPath = AgentMountPath + "/" + SocketFile;

    public const string SocketVariable = "SSH_AUTH_SOCK";

    // A unix socket path is at most 108 bytes on Linux; the directory name is the only variable part.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,47}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();

    /// <summary>
    /// The subdirectory for <paramref name="agent"/>, or null when the name cannot be a path segment
    /// (too long for a socket path, or not plain characters). Null means no SSH key for that agent.
    /// </summary>
    public static string? AgentDirectoryName(string? agent) =>
        agent is not null && SafeName().IsMatch(agent) && !agent.EndsWith('.') ? agent : null;

    /// <summary>The read-only mount of one agent's subdirectory, relative to the fleet base directory.</summary>
    public static string AgentBind(string directoryName) => $"./{Directory}/{directoryName}:{AgentMountPath}:ro";
}
