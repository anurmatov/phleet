namespace Fleet.Shared;

/// <summary>
/// Provider <c>pi</c> (#386): Pi 1.0.4 in RPC mode against a local Anthropic Messages server. The
/// constants, the configuration rules PI1–PI5 and the credential environment denylist, read by the
/// orchestrator and the agent alike.
/// </summary>
/// <remarks>
/// <para>
/// A pi agent always runs on a local server: it stores the origin in <c>LocalBaseUrl</c>, the bare
/// model tag in <c>Model</c>, the server's <c>--ctx</c> in <c>ContextWindow</c> and <c>""</c>/off/on
/// in <c>Effort</c>. There is no migration.
/// </para>
/// <para>
/// Isolation is absence: no provider credential exists anywhere the model's shell can read. Pi has
/// no approval step, sandbox or path limit, so nothing here describes the shell as sandboxed.
/// </para>
/// </remarks>
public static class PiLocalModel
{
    public const string Provider = AgentProviders.Pi;

    /// <summary>The pinned Pi release (<c>@earendil-works/pi-coding-agent</c>, tag <c>v1.0.4</c>).</summary>
    public const string PinnedVersion = "1.0.4";

    /// <summary>Pi's compaction reserve R: compaction triggers at <c>W − R</c>.</summary>
    public const int ReserveTokens = 16384;

    /// <summary>Pi's <c>keepRecentTokens</c> T: the raw tail target (not a cap).</summary>
    public const int KeepRecentTokens = 8192;

    /// <summary>The fast-path summary cap S; thinking is off for that call.</summary>
    public const int SummaryMaxTokens = 1280;

    /// <summary>L2/L2c apply only to kept tails up to this size; a larger tail logs a Warning.</summary>
    public const int TailBudgetTokens = 10240;

    /// <summary>PI3: below this the agent would compact on almost every turn.</summary>
    public const int MinContextWindow = 65536;

    /// <summary>The per-result MCP text cap (48 KiB).</summary>
    public const int ToolResultMaxBytes = 49152;

    /// <summary>The MCP <c>tools/call</c> timeout.</summary>
    public const int ToolCallTimeoutSeconds = 300;

    /// <summary>Pi tool names are declared only up to this length; longer MCP names are skipped, never shortened.</summary>
    public const int MaxToolNameLength = 64;

    /// <summary>The maximum output tokens declared for the model in <c>models.json</c>.</summary>
    public const int ModelMaxTokens = 16384;

    /// <summary>Pi's agent directory inside the container (<c>PI_CODING_AGENT_DIR</c>).</summary>
    public const string AgentDir = "/root/.pi/agent";

    /// <summary>The Fleet extension directory baked into the image.</summary>
    public const string ExtensionDir = "/opt/fleet-pi-extension";

    /// <summary>The MCP config the extension reads.</summary>
    public const string McpConfigPath = "/workspace/.mcp.json";

    /// <summary>The provider id Fleet writes into <c>models.json</c>.</summary>
    public const string ProviderId = "fleet-local";

    /// <summary>Pi's built-in tools, fixed. <c>grep</c> and <c>find</c> stay off (no rg/fd download).</summary>
    public static IReadOnlyList<string> BuiltInTools { get; } = ["read", "bash", "edit", "write"];

    /// <summary>PI4: the effort vocabulary. <c>""</c> and <c>on</c> run <c>--thinking medium</c>.</summary>
    public static IReadOnlyList<string> EffortLevels { get; } = ["", "off", "on"];

    /// <summary>The <c>--thinking</c> argument for an effort that passed PI4.</summary>
    public static string ThinkingArgument(string? effort) => effort == "off" ? "off" : "medium";

    public const string Pi1Text = "Pi runs only on a local server; set the local server URL.";
    public const string Pi2Text = "Pi takes the bare model tag the server serves (no ollama/ or lmstudio/ prefix).";
    public const string Pi3Text = "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx.";
    public const string Pi4Text = "Pi effort is off or on.";

    /// <summary>PI5's text for a credential mount at <paramref name="mountPath"/>.</summary>
    public static string Pi5Text(string mountPath) =>
        $"Pi agents must hold no provider credential, but a credential mount targets {mountPath}. Remove it, then reprovision.";

    /// <summary>Credential mount roots PI5 refuses for a pi agent.</summary>
    public static IReadOnlyList<string> CredentialMountRoots { get; } =
        ["/root/.claude", "/root/.codex", "/root/.gemini", "/root/.pi"];

    public static bool IsPi(string? provider) => string.Equals(provider, Provider, StringComparison.Ordinal);

    /// <summary>
    /// The first fault in a pi configuration, in the order PI1, L2, PI2, PI3, PI4; null when sound.
    /// A null <paramref name="contextWindow"/> is unset (PI3).
    /// </summary>
    public static string? DescribeConfigFault(string? baseUrl, string? model, string? effort, int? contextWindow)
    {
        // PI1
        if (string.IsNullOrEmpty(baseUrl))
            return Pi1Text;

        // L2, shared with claude and codex local mode.
        if (LocalModel.DescribeBaseUrlFault(baseUrl) is { } urlFault)
            return urlFault;

        // PI2
        if (DescribeModelFault(model) is { } modelFault)
            return modelFault;

        // PI3
        if (contextWindow is not int window || window < MinContextWindow)
            return Pi3Text;

        // PI4
        if (!EffortLevels.Contains(effort ?? "", StringComparer.Ordinal))
            return Pi4Text;

        return null;
    }

    /// <summary>PI2: a bare tag in the V4 charset whose first <c>/</c> segment names no codex or hosted route.</summary>
    public static string? DescribeModelFault(string? model)
    {
        if (string.IsNullOrEmpty(model) || LocalModel.DescribeModelTagFault(model) is not null)
            return Pi2Text;

        var slash = model.IndexOf('/');
        if (slash > 0)
        {
            var prefix = model[..slash];
            if (CodexLocalModelProviders.Ids.Contains(prefix, StringComparer.OrdinalIgnoreCase)
                || HostedModelProviders.All.Any(p => string.Equals(p.Prefix, prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return Pi2Text;
            }
        }

        return null;
    }

    /// <summary>PI5 (provisioning only): the fault for a credential mount path, or null.</summary>
    public static string? DescribeCredentialMountFault(string? mountPath)
    {
        if (string.IsNullOrEmpty(mountPath))
            return null;

        return CredentialMountRoots.Any(root => mountPath.StartsWith(root, StringComparison.Ordinal))
            ? Pi5Text(mountPath)
            : null;
    }

    /// <summary>
    /// The exact credential environment names removed from PID 1 and from the Pi child (61): 46
    /// from <c>pi --help</c> at 0.87.1, 8 read by Pi but not in help, and 7 Fleet names (the first
    /// equals <see cref="HostedModelProviders.KeyEnvVars"/>). Re-checked against Pi 1.0.4
    /// (<c>7c10bd4</c>): 0 uncovered. Copied verbatim into <c>entrypoint.sh</c>; never parsed from
    /// <c>pi --help</c>.
    /// </summary>
    public static IReadOnlyList<string> CredentialEnvNames { get; } =
    [
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_OAUTH_TOKEN",
        "ANT_LING_API_KEY",
        "OPENAI_API_KEY",
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_BASE_URL",
        "AZURE_OPENAI_RESOURCE_NAME",
        "AZURE_OPENAI_API_VERSION",
        "AZURE_OPENAI_DEPLOYMENT_NAME_MAP",
        "DEEPSEEK_API_KEY",
        "NVIDIA_API_KEY",
        "GEMINI_API_KEY",
        "GROQ_API_KEY",
        "CEREBRAS_API_KEY",
        "XAI_API_KEY",
        "FIREWORKS_API_KEY",
        "TOGETHER_API_KEY",
        "BASETEN_API_KEY",
        "OPENROUTER_API_KEY",
        "AI_GATEWAY_API_KEY",
        "ZAI_API_KEY",
        "ZAI_CODING_CN_API_KEY",
        "MISTRAL_API_KEY",
        "MINIMAX_API_KEY",
        "MOONSHOT_API_KEY",
        "OPENCODE_API_KEY",
        "KIMI_API_KEY",
        "META_API_KEY",
        "CLOUDFLARE_API_KEY",
        "CLOUDFLARE_ACCOUNT_ID",
        "CLOUDFLARE_GATEWAY_ID",
        "QWEN_TOKEN_PLAN_API_KEY",
        "QWEN_TOKEN_PLAN_CN_API_KEY",
        "XIAOMI_API_KEY",
        "XIAOMI_TOKEN_PLAN_CN_API_KEY",
        "XIAOMI_TOKEN_PLAN_AMS_API_KEY",
        "XIAOMI_TOKEN_PLAN_SGP_API_KEY",
        "AWS_PROFILE",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_BEARER_TOKEN_BEDROCK",
        "AWS_REGION",
        "PI_CODING_AGENT_SESSION_DIR",
        "PI_PACKAGE_DIR",
        "PI_SHARE_VIEWER_URL",
        "COPILOT_GITHUB_TOKEN",
        "GOOGLE_CLOUD_API_KEY",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "TYPESAFE_API_KEY",
        "RADIUS_API_KEY",
        "MINIMAX_CN_API_KEY",
        "HF_TOKEN",
        "GCLOUD_PROJECT",
        "ZAI_CODING_PLAN_API_KEY",
        "GOOGLE_API_KEY",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "CODEX_OSS_BASE_URL",
        "OPENAI_BASE_URL",
        "CLAUDE_CODE_MAX_CONTEXT_TOKENS"
    ];

    /// <summary>
    /// The credential environment prefixes (7). <c>AWS_</c> and <c>GOOGLE_</c> also cover names the
    /// AWS SDK and Pi read without listing them. <c>GH_*</c>, <c>GITHUB_*</c> and git settings are
    /// kept on purpose: the agent's <c>bash</c> needs them and they grant no model access.
    /// </summary>
    public static IReadOnlyList<string> CredentialEnvPrefixes { get; } =
        ["ANTHROPIC_", "CLAUDE_CODE_", "OPENAI_", "CODEX_", "GEMINI_", "AWS_", "GOOGLE_"];

    /// <summary>True when <paramref name="name"/> is on the denylist (exact name or prefix).</summary>
    public static bool IsCredentialEnvName(string name) =>
        CredentialEnvNames.Contains(name, StringComparer.Ordinal)
        || CredentialEnvPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    /// <summary>The names Fleet sets in the Pi child itself; never denylisted.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ChildEnvironment { get; } =
    [
        new("PI_OFFLINE", "1"),
        new("PI_TELEMETRY", "0"),
        new("PI_CODING_AGENT_DIR", AgentDir),
    ];
}
