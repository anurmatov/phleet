namespace Fleet.Shared;

/// <summary>
/// One hosted model vendor that a codex agent can reach through the loopback forwarder (#335).
/// </summary>
/// <param name="Prefix">The <c>Model</c> prefix before the first <c>/</c>, e.g. <c>zai</c>.</param>
/// <param name="CodexProviderId">
/// The codex <c>modelProvider</c> id. Always <c>phleet_</c>-prefixed: built-in ids cannot be
/// overridden, and an unprefixed id could collide with a built-in codex adds later.
/// </param>
/// <param name="DisplayName">The codex provider <c>name</c>.</param>
/// <param name="Upstream">
/// The vendor base URL. Fixed and HTTPS: a configurable upstream would let a config edit send the
/// key anywhere, and a wrong Z.ai endpoint bills the balance instead of the subscription.
/// </param>
/// <param name="KeyEnvVar">The <c>.env</c> key that holds this vendor's API key.</param>
/// <param name="ForwardedEfforts">The codex reasoning efforts sent on (D7).</param>
public sealed record HostedModelProvider(
    string Prefix,
    string CodexProviderId,
    string DisplayName,
    Uri Upstream,
    string KeyEnvVar,
    IReadOnlySet<string> ForwardedEfforts)
{
    /// <summary>True when the executor may send <paramref name="effort"/> to this vendor (D7).</summary>
    public bool ForwardsEffort(string effort) => ForwardedEfforts.Contains(effort);
}

/// <summary>
/// The hosted-provider registry (#335 D2). The single source for the agent and the orchestrator,
/// so the two cannot disagree about which models route through the forwarder.
/// </summary>
/// <remarks>
/// Code, not DB, on purpose: every row here decides where a subscription key is sent.
/// </remarks>
public static class HostedModelProviders
{
    /// <summary>
    /// Where <c>entrypoint.sh</c> hands the key to the agent. The agent reads it once and deletes it.
    /// </summary>
    public const string KeyFilePath = "/run/phleet-hosted-key";

    /// <summary>The literal the orchestrator injects for an env ref missing from <c>.env</c>.</summary>
    public const string MissingSecretPlaceholder = "<secret>";

    /// <summary>
    /// GLM on the Z.ai GLM Coding Plan, through Z.ai's documented Codex route: the Responses
    /// endpoint. The other Coding Plan endpoints are for other tools, and a wrong endpoint does not
    /// draw on the subscription quota.
    /// </summary>
    public static readonly HostedModelProvider Zai = new(
        Prefix: "zai",
        CodexProviderId: "phleet_zai",
        DisplayName: "Z.ai GLM Coding Plan (phleet)",
        Upstream: new Uri("https://api.z.ai/api/v1"),
        KeyEnvVar: "ZAI_CODING_PLAN_API_KEY",
        // Z.ai's Codex catalog declares low, high and max; codex has no max.
        ForwardedEfforts: new HashSet<string>(StringComparer.Ordinal) { "low", "high" });

    public static IReadOnlyList<HostedModelProvider> All { get; } = [Zai];

    /// <summary>
    /// Every key env var in the registry. These names are reserved for hosted routing:
    /// <c>entrypoint.sh</c> unsets them on every agent, and <c>CodexExecutor</c> strips them from
    /// codex's environment.
    /// </summary>
    public static IReadOnlyList<string> KeyEnvVars { get; } = All.Select(p => p.KeyEnvVar).ToArray();

    /// <summary>
    /// Resolves a hosted provider for an agent, or returns false. Hosted routing applies to the
    /// codex provider only (D1). The model is split at the first <c>/</c>, so
    /// <c>zai/glm-5.3</c> yields the bare model <c>glm-5.3</c>.
    /// </summary>
    public static bool TryResolve(
        string? agentProvider, string? model, out HostedModelProvider provider, out string bareModel)
    {
        provider = null!;
        bareModel = model ?? "";

        if (!string.Equals(agentProvider, "codex", StringComparison.OrdinalIgnoreCase) || model is null)
            return false;

        var slash = model.IndexOf('/');
        if (slash <= 0 || slash == model.Length - 1)
            return false;

        var prefix = model[..slash];
        var match = All.FirstOrDefault(p => string.Equals(p.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        provider = match;
        bareModel = model[(slash + 1)..];
        return true;
    }

    /// <summary>
    /// Describes why <paramref name="value"/> is not a usable key for <paramref name="keyEnvVar"/>,
    /// or returns null. Never includes the value.
    /// </summary>
    public static string? DescribeKeyFault(string keyEnvVar, string? value)
    {
        if (value is null)
            return $"{keyEnvVar} was not handed to the agent.";
        if (string.IsNullOrWhiteSpace(value))
            return $"{keyEnvVar} is empty or whitespace-only.";
        if (value.Trim() == MissingSecretPlaceholder)
            return $"{keyEnvVar} is the '{MissingSecretPlaceholder}' placeholder: the env ref is attached "
                 + "but the key is missing from .env.";
        return null;
    }
}
