using System.Net;
using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>
/// A claude agent running Claude Code against a local Anthropic-compatible server (#340). The one
/// definition of "local mode" and its validation, read by the orchestrator and the agent alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>Local mode ⇔ provider <c>claude</c> and a non-empty <c>AnthropicBaseUrl</c>.</b> <c>null</c>
/// and <c>""</c> are off. A whitespace-only value is on, and faulty — never quietly off.
/// </para>
/// <para>
/// Trust: <c>http</c> is accepted to any host. Whoever can write <c>AnthropicBaseUrl</c> can
/// already choose the agent's image, so this adds no reach; the value is an operator decision.
/// </para>
/// </remarks>
public static partial class ClaudeLocalModel
{
    /// <summary>
    /// The <c>ANTHROPIC_AUTH_TOKEN</c> the claude child sends. Local servers ignore it; it exists so
    /// Claude Code never falls back to an OAuth credential. A public constant, not a secret.
    /// </summary>
    public const string PlaceholderAuthToken = "phleet-local-no-auth";

    public const int MaxBaseUrlLength = 500;
    public const int MaxModelLength = 100;

    /// <summary>Removed from the claude child's environment in local mode (D2, #349).</summary>
    public static IReadOnlyList<string> RemovedEnvVars { get; } =
    [
        "CLAUDE_CODE_OAUTH_TOKEN",
        // #349: all five shape the thinking field the operator is choosing through Effort.
        // CLAUDE_CODE_EFFORT_LEVEL overrides --effort (captured on 2.1.280); the rest change or
        // remove the thinking field, which on Ollama leaves thinking ON rather than off.
        "CLAUDE_CODE_EFFORT_LEVEL",
        "CLAUDE_CODE_EXTRA_BODY",
        "MAX_THINKING_TOKENS",
        "CLAUDE_CODE_DISABLE_THINKING",
        "CLAUDE_CODE_DISABLE_ADAPTIVE_THINKING",
    ];

    /// <summary>
    /// The local-mode thinking vocabulary (#349): off, low, medium, xhigh. Pinned to the Qwen3.8
    /// descriptor on Ollama (false/low/medium/xhigh) — NOT discovered from the server, so no
    /// inference call sits on a write, provision or startup path (#340 invariant).
    /// </summary>
    public static IReadOnlyList<string> ThinkingLevels { get; } = ["off", "low", "medium", "xhigh"];

    /// <summary>
    /// What a null/empty Effort means in local mode (#349): send xhigh explicitly. Claude Code's
    /// own default (`high`) is xhigh on Ollama ≤ 0.34.2 but resolves to medium on ≥ 0.34.3, so the
    /// default must not depend on the server release.
    /// </summary>
    public const string DefaultThinkingLevel = "xhigh";

    /// <summary>
    /// The fixed body merged into every request when thinking is off (#349). A constant — no
    /// operator input ever reaches CLAUDE_CODE_EXTRA_BODY, because it merges into every request.
    /// </summary>
    public const string DisabledThinkingExtraBody = """{"thinking":{"type":"disabled"}}""";

    private static readonly string[] ClaudeModelAliases = ["opus", "sonnet", "haiku"];

    // --model is appended unquoted by ClaudeExecutor.BuildArgs, so the tag must be a single safe token.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$")]
    private static partial Regex ModelTagPattern();

    public static bool IsEnabled(string? provider, string? baseUrl) =>
        string.Equals(provider, "claude", StringComparison.Ordinal) && !string.IsNullOrEmpty(baseUrl);

    /// <summary>
    /// The first fault in a local-mode configuration (V1–V7), or null. Null as well when
    /// <paramref name="baseUrl"/> is null or empty: an agent without the field is not validated here.
    /// </summary>
    public static string? DescribeConfigFault(string? provider, string? baseUrl, string? model, string? effort)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return null;

        // V1
        if (!string.Equals(provider, "claude", StringComparison.Ordinal))
            return "AnthropicBaseUrl applies only to provider claude; clear it before changing provider.";

        // V2, V3
        if (DescribeBaseUrlFault(baseUrl) is { } urlFault)
            return urlFault;

        // V4
        if (string.IsNullOrEmpty(model) || model.Length > MaxModelLength || !ModelTagPattern().IsMatch(model))
            return $"The local model tag must be 1–{MaxModelLength} characters matching "
                 + "^[A-Za-z0-9][A-Za-z0-9._:/-]*$; it contains characters not allowed in a CLI argument.";

        // V5
        if (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            || ClaudeModelAliases.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            return $"Model '{model}' is a Claude model id; set the local server's model tag.";
        }

        // V6
        var slash = model.IndexOf('/');
        if (slash > 0)
        {
            var prefix = model[..slash];
            if (CodexLocalModelProviders.Ids.Contains(prefix, StringComparer.OrdinalIgnoreCase)
                || HostedModelProviders.All.Any(p => string.Equals(p.Prefix, prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return $"The '{prefix}/' prefix selects the codex path; use provider codex or the bare tag.";
            }
        }

        // V7 (#349)
        if (!string.IsNullOrEmpty(effort) && !ThinkingLevels.Contains(effort))
            return "Effort on a local Claude model must be empty (model default, sent as xhigh), off, low, medium or xhigh.";

        return null;
    }

    /// <summary>
    /// The cloud-side twin of V7 (#349 new V8): <c>off</c> exists only in local mode, and a local →
    /// cloud switch (base URL cleared) must not ship <c>--effort off</c> to Anthropic. Null unless
    /// the provider is claude, the agent is NOT in local mode, and the effort is exactly off.
    /// </summary>
    public static string? DescribeLocalOnlyEffortFault(string? provider, string? baseUrl, string? effort)
    {
        if (string.IsNullOrEmpty(baseUrl) &&
            string.Equals(provider, "claude", StringComparison.Ordinal) &&
            effort == "off")
        {
            return "Effort 'off' applies only to local Claude models; clear it or choose low, medium, high, xhigh or max.";
        }
        return null;
    }

    /// <summary>
    /// The <c>--effort</c> value for a local-mode claude child (#349): null/empty → xhigh (the
    /// version-independent default), <c>off</c> → no flag (thinking is disabled through
    /// <see cref="DisabledThinkingExtraBody"/> instead), otherwise the value verbatim.
    /// </summary>
    public static string? EffortArgument(string? effort) =>
        string.IsNullOrEmpty(effort) ? DefaultThinkingLevel
        : effort == "off" ? null
        : effort;

    /// <summary>
    /// <c>scheme://host[:port]</c>: scheme and host lowercased, default port dropped, trailing
    /// <c>/</c> removed. Only meaningful for a value that passed <see cref="DescribeConfigFault"/>.
    /// </summary>
    public static string CanonicalizeBaseUrl(string baseUrl) =>
        new Uri(baseUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// The claude child's environment in local mode, in order (D2). Mirrors <c>ollama launch
    /// claude</c> (<c>cmd/launch/claude.go</c> <c>envVars</c> + <c>modelEnvVars</c>, commit
    /// <c>01c0fbfd</c>); only the auth-token value differs.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BuildEnvironment(string baseUrl, string model, string? effort = null)
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            new("ANTHROPIC_BASE_URL", baseUrl),
            new("ANTHROPIC_API_KEY", ""),
            new("ANTHROPIC_AUTH_TOKEN", PlaceholderAuthToken),
            new("CLAUDE_CODE_ATTRIBUTION_HEADER", "0"),
            new("CLAUDE_CODE_TOTAL_TOKENS_REMINDER", "off"),
            new("DISABLE_ERROR_REPORTING", "1"),
            new("DISABLE_FEEDBACK_COMMAND", "1"),
            new("CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY", "1"),
            new("CLAUDE_CODE_AUTO_MODE_SERVER", "0"),
            new("ANTHROPIC_DEFAULT_OPUS_MODEL", model),
            new("ANTHROPIC_DEFAULT_SONNET_MODEL", model),
            new("ANTHROPIC_DEFAULT_HAIKU_MODEL", model),
            new("CLAUDE_CODE_SUBAGENT_MODEL", model),
        };

        // #349: appended only for off, after the thirteen inherited entries, whose order is
        // untouched. Every other level keeps the byte-identical thirteen-entry environment.
        if (effort == "off")
            entries.Add(new("CLAUDE_CODE_EXTRA_BODY", DisabledThinkingExtraBody));

        return entries;
    }

    // V2 and V3. The fault text never includes the value: it may carry credentials.
    private static string? DescribeBaseUrlFault(string baseUrl)
    {
        if (baseUrl.Length > MaxBaseUrlLength)
            return BaseUrlFault($"is longer than {MaxBaseUrlLength} characters");

        // Checked on the raw string: Uri parsing would trim it and hide the difference.
        if (baseUrl.Trim().Length != baseUrl.Length)
            return BaseUrlFault("has surrounding whitespace");

        // '@' only ever delimits userinfo in an origin. Checked raw because "http://@host" parses
        // with an empty UserInfo, and GetLeftPart would then keep the '@' in the canonical form.
        if (baseUrl.Contains('@'))
            return BaseUrlFault("has credentials");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return BaseUrlFault("is not an absolute URI");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return BaseUrlFault("must use http or https");

        if (string.IsNullOrEmpty(uri.Host))
            return BaseUrlFault("has no host");

        if (uri.UserInfo.Length > 0)
            return BaseUrlFault("has credentials");

        if (uri.Query.Length > 0)
            return BaseUrlFault("has a query");

        if (uri.Fragment.Length > 0)
            return BaseUrlFault("has a fragment");

        if (uri.AbsolutePath != "/")
            return BaseUrlFault("has a path");

        if (IsContainerLocal(uri))
            return "AnthropicBaseUrl points at localhost, which inside an agent container is the container "
                 + "itself; use the inference server's LAN address or host name.";

        return null;
    }

    private static string BaseUrlFault(string reason) =>
        $"AnthropicBaseUrl {reason}. It must be an http(s) origin such as http://<server-address>:11434 — "
      + "no path (Claude Code appends /v1/messages), credentials, query or fragment.";

    // V3: localhost (any case, trailing dot), 127.0.0.0/8, ::1, the unspecified addresses, and their
    // IPv4-mapped IPv6 forms. Uri has already folded 127.1 and 2130706433 into 127.0.0.1.
    private static bool IsContainerLocal(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var address))
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any);
    }
}
