using System.Globalization;
using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>Where one <see cref="HarnessExtra"/> line lands in the <see cref="HarnessPlan"/>.</summary>
public enum HarnessExtraTarget
{
    /// <summary>A Codex <c>config.toml</c> root key or root table key (<c>codex-root.toml</c>).</summary>
    CodexRoot,
    /// <summary>A Codex <c>thread/start</c> <c>config</c> entry.</summary>
    CodexThread,
    /// <summary>A Claude CLI flag that is added or set (<c>--flag</c> or <c>--flag value</c>).</summary>
    ClaudeFlag,
    /// <summary>A removable Claude default flag turned off (<c>!--flag</c>).</summary>
    ClaudeFlagRemoved,
    /// <summary>A Claude child environment entry (<c>NAME=value</c>).</summary>
    ClaudeEnv,
}

/// <summary>
/// One accepted <c>harnessExtra</c> line. <see cref="Value"/> is a <c>string</c>, <c>bool</c> or
/// <c>int</c> for Codex keys, the one value token (or null) for a Claude flag, and the text after
/// <c>=</c> for a Claude env entry.
/// </summary>
public sealed record HarnessExtraLine(int Line, HarnessExtraTarget Target, string Name, object? Value);

/// <summary>
/// Why a <c>harnessExtra</c> value is refused. <see cref="Code"/> is
/// <see cref="HarnessExtra.UnsupportedProviderCode"/>, <see cref="HarnessExtra.TooLargeCode"/> or
/// <see cref="HarnessExtra.InvalidLineCode"/>; <see cref="Message"/> names the field, the line and
/// the reason.
/// </summary>
public sealed record HarnessExtraFault(string Code, int? Line, string Message);

/// <summary>The accepted lines, or the first fault (then no line applies).</summary>
public sealed record HarnessExtraParse(IReadOnlyList<HarnessExtraLine> Lines, HarnessExtraFault? Fault);

/// <summary>
/// The one per-agent harness field (#460 Part 3b): its size limits, its line formats and the
/// allowlist with each value's type and range. The orchestrator validates every write with
/// <see cref="Parse"/>, and <see cref="HarnessDefaults.Build"/> runs the same parser at startup, so a
/// value the running image no longer accepts is skipped as a whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>An allowlist, never a denylist (MUST NOT 8).</b> A line may only set a setting the Part 1
/// tables already own, with a value of the stated type and range. Everything else — session,
/// permission, MCP, provider, URL or credential inputs — has no syntax here at all.
/// </para>
/// <para>
/// <c>--tools</c> only adds names to the computed list; it never removes one (MUST NOT 4). When the
/// mode defaults compute no <c>--tools</c> flag, the agent already keeps every built-in, so an extra
/// <c>--tools</c> line is a no-op rather than a way to narrow it.
/// </para>
/// </remarks>
public static partial class HarnessExtra
{
    public const int MaxChars = 4_096;
    public const int MaxLines = 50;

    public const string UnsupportedProviderCode = "unsupported_provider";
    public const string TooLargeCode = "too_large";
    public const string InvalidLineCode = "invalid_line";

    public const string ToolsFlag = "--tools";
    public const string DisallowedToolsFlag = "--disallowedTools";
    public const string ExcludeDynamicSectionsFlag = "--exclude-dynamic-system-prompt-sections";
    public const string StrictMcpConfigFlag = "--strict-mcp-config";
    public const string MaxMcpOutputTokensEnv = "MAX_MCP_OUTPUT_TOKENS";
    public const string NonessentialTrafficEnv = "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC";

    public const int MinTokenLimit = 1_000;
    public const int MaxTokenLimit = 100_000;

    /// <summary>
    /// The built-in tool names an extra <c>--tools</c> line may add. A fixed constant: the
    /// orchestrator validates without the CLI. Read from the pinned Claude CLI 2.1.280's
    /// <c>system/init</c> tool list (<c>Agent</c> is reported as <c>Task</c>), plus the always-kept
    /// <c>ToolSearch</c> and <c>TodoWrite</c>; the agent-image PR re-verifies it (AC-C2).
    /// </summary>
    public static IReadOnlyList<string> KnownBuiltInTools { get; } =
    [
        "Agent", "Bash", "CronCreate", "CronDelete", "CronList", "Edit", "EnterWorktree",
        "ExitWorktree", "Glob", "Grep", "ListAgents", "NotebookEdit", "Read", "ReportFindings",
        "ScheduleWakeup", "SendMessage", "Skill", "Task", "TaskCreate", "TaskGet", "TaskList",
        "TaskStop", "TaskUpdate", "TodoWrite", "ToolSearch", "WebFetch", "WebSearch", "Workflow",
        "Write",
    ];

    /// <summary>The providers the field applies to; pi and gemini refuse a non-empty value.</summary>
    public static bool IsSupportedProvider(string? provider) =>
        AgentProviders.Normalize(provider) is AgentProviders.Claude or AgentProviders.Codex;

    private sealed record CodexKey(string Name, HarnessExtraTarget Target, string Allowed,
        Func<string, bool, (object? Value, string? Fault)> Read);

    private static readonly IReadOnlyList<CodexKey> CodexKeys =
    [
        // Local mode removes these two tools; nothing may enable them again there.
        new("web_search", HarnessExtraTarget.CodexThread, "\"disabled\", \"cached\" or \"live\" (local: \"disabled\" only)",
            (raw, local) => ReadEnum(raw, ["disabled", "cached", "live"]) is (string mode, null) && local && mode != "disabled"
                ? (null, "cannot enable web search in local mode")
                : ReadEnum(raw, ["disabled", "cached", "live"])),
        new("tools.view_image", HarnessExtraTarget.CodexThread, "true or false (local: false only)",
            (raw, local) => ReadBool(raw) is (true, null) && local
                ? (null, "cannot enable the image tool in local mode")
                : ReadBool(raw)),
        new("model_reasoning_summary", HarnessExtraTarget.CodexThread, "\"none\", \"auto\", \"concise\" or \"detailed\"",
            (raw, _) => ReadEnum(raw, ["none", "auto", "concise", "detailed"])),
        new("tool_output_token_limit", HarnessExtraTarget.CodexThread, $"an integer {MinTokenLimit}-{MaxTokenLimit}",
            (raw, _) => ReadInt(raw, MinTokenLimit, MaxTokenLimit)),
        new("features.apps", HarnessExtraTarget.CodexThread, "true or false", (raw, _) => ReadBool(raw)),
        new("features.remote_plugin", HarnessExtraTarget.CodexThread, "true or false", (raw, _) => ReadBool(raw)),
        new("features.skill_mcp_dependency_install", HarnessExtraTarget.CodexThread, "true or false", (raw, _) => ReadBool(raw)),
        new("check_for_update_on_startup", HarnessExtraTarget.CodexRoot, "true or false", (raw, _) => ReadBool(raw)),
        new("analytics.enabled", HarnessExtraTarget.CodexRoot, "true or false", (raw, _) => ReadBool(raw)),
        new("history.persistence", HarnessExtraTarget.CodexRoot, "\"none\" or \"save-all\"",
            (raw, _) => ReadEnum(raw, ["none", "save-all"])),
    ];

    private static readonly string CodexAllowed = string.Join(", ", CodexKeys.Select(k => k.Name));

    private const string ClaudeAllowed =
        $"{ToolsFlag} <names>, {DisallowedToolsFlag} WebSearch, !{ExcludeDynamicSectionsFlag}, "
        + $"{MaxMcpOutputTokensEnv}=<integer>, {NonessentialTrafficEnv}=0|1";

    /// <summary>The allowed names for <paramref name="provider"/>, as listed in error messages.</summary>
    public static string AllowedNames(string? provider) =>
        AgentProviders.Normalize(provider) == AgentProviders.Codex ? CodexAllowed : ClaudeAllowed;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvNamePattern();

    /// <summary>
    /// Parses and validates <paramref name="text"/> for an agent with <paramref name="provider"/> in
    /// local (<paramref name="local"/>) or cloud mode. <c>null</c> or <c>""</c> is no extra. Blank
    /// lines and lines starting with <c>#</c> are ignored. The first fault refuses the whole value.
    /// </summary>
    public static HarnessExtraParse Parse(string? text, string? provider, bool local)
    {
        if (string.IsNullOrEmpty(text))
            return new([], null);

        if (!IsSupportedProvider(provider))
            return Refuse(UnsupportedProviderCode, null,
                $"{UnsupportedProviderCode}: harnessExtra applies only to claude and codex agents, not "
                + $"'{AgentProviders.Normalize(provider)}'; clear it (send an empty string).");

        if (text.Length > MaxChars)
            return Refuse(TooLargeCode, null,
                $"harnessExtra is {text.Length} characters; the limit is {MaxChars}.");

        var lines = text.Split('\n');
        var lineCount = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (lineCount > MaxLines)
            return Refuse(TooLargeCode, null,
                $"harnessExtra has {lineCount} lines; the limit is {MaxLines}.");

        var codex = AgentProviders.Normalize(provider) == AgentProviders.Codex;
        var accepted = new List<HarnessExtraLine>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lineCount; i++)
        {
            var number = i + 1;
            var line = lines[i].TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var (parsed, reason) = codex ? ReadCodexLine(number, line, local) : ReadClaudeLine(number, line, local);
            if (parsed is null)
                return Refuse(InvalidLineCode, number, $"harnessExtra line {number}: {reason}");

            if (seen.TryGetValue(parsed.Name, out var first))
                return Refuse(InvalidLineCode, number,
                    $"harnessExtra line {number}: '{parsed.Name}' is already set on line {first}.");
            seen[parsed.Name] = number;
            accepted.Add(parsed);
        }

        return new(accepted, null);
    }

    private static HarnessExtraParse Refuse(string code, int? line, string message) =>
        new([], new HarnessExtraFault(code, line, message));

    private static (HarnessExtraLine? Line, string? Reason) ReadCodexLine(int number, string line, bool local)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0)
            return (null, $"expected 'key = value'. Allowed keys: {CodexAllowed}.");

        var name = line[..eq].Trim();
        var raw = line[(eq + 1)..].Trim();
        var key = CodexKeys.FirstOrDefault(k => k.Name == name);
        if (key is null)
            return (null, $"'{name}' is not an allowed setting for codex. Allowed keys: {CodexAllowed}.");

        var (value, fault) = key.Read(raw, local);
        return value is null
            ? (null, $"'{name}' {fault ?? "has an invalid value"}; allowed: {key.Allowed}.")
            : (new HarnessExtraLine(number, key.Target, name, value), null);
    }

    private static (HarnessExtraLine? Line, string? Reason) ReadClaudeLine(int number, string line, bool local)
    {
        if (line.StartsWith('!'))
        {
            var flag = line[1..].Trim();
            return flag == ExcludeDynamicSectionsFlag
                ? (new HarnessExtraLine(number, HarnessExtraTarget.ClaudeFlagRemoved, flag, null), null)
                : (null, $"'!{flag}' cannot be removed; only !{ExcludeDynamicSectionsFlag} is removable. Allowed: {ClaudeAllowed}.");
        }

        if (line.StartsWith('-'))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var flag = tokens[0];
            if (tokens.Length > 2)
                return (null, $"'{flag}' takes at most one value token.");
            var value = tokens.Length == 2 ? tokens[1] : null;

            switch (flag)
            {
                case ToolsFlag:
                    return ReadToolsValue(number, value, local);
                case DisallowedToolsFlag:
                    return value == "WebSearch"
                        ? (new HarnessExtraLine(number, HarnessExtraTarget.ClaudeFlag, flag, value), null)
                        : (null, $"'{DisallowedToolsFlag}' accepts only the value WebSearch.");
                default:
                    return (null, $"'{flag}' is not an allowed setting for claude. Allowed: {ClaudeAllowed}.");
            }
        }

        var eq = line.IndexOf('=');
        if (eq <= 0)
            return (null, $"expected '--flag [value]', '!--flag' or 'NAME=value'. Allowed: {ClaudeAllowed}.");

        var name = line[..eq].Trim();
        var raw = line[(eq + 1)..].Trim();
        if (!EnvNamePattern().IsMatch(name))
            return (null, $"'{name}' is not a valid environment variable name. Allowed: {ClaudeAllowed}.");

        switch (name)
        {
            case MaxMcpOutputTokensEnv:
            {
                var (value, fault) = ReadInt(raw, MinTokenLimit, MaxTokenLimit);
                return value is int tokens
                    ? (new HarnessExtraLine(number, HarnessExtraTarget.ClaudeEnv, name,
                        tokens.ToString(CultureInfo.InvariantCulture)), null)
                    : (null, $"'{name}' {fault}; allowed: an integer {MinTokenLimit}-{MaxTokenLimit}.");
            }
            case NonessentialTrafficEnv:
                return raw is "0" or "1"
                    ? (new HarnessExtraLine(number, HarnessExtraTarget.ClaudeEnv, name, raw), null)
                    : (null, $"'{name}' must be 0 or 1.");
            default:
                return (null, $"'{name}' is not an allowed setting for claude. Allowed: {ClaudeAllowed}.");
        }
    }

    private static (HarnessExtraLine? Line, string? Reason) ReadToolsValue(int number, string? value, bool local)
    {
        if (string.IsNullOrEmpty(value))
            return (null, $"'{ToolsFlag}' needs built-in tool names to add, e.g. {ToolsFlag} Edit,Write; it can never remove tools.");

        var names = value.Split(',', StringSplitOptions.TrimEntries);
        foreach (var name in names)
        {
            if (!KnownBuiltInTools.Contains(name, StringComparer.Ordinal))
                return (null, $"'{ToolsFlag}' only adds known built-in tools and can never remove one; "
                            + $"'{name}' is not one. Known: {string.Join(", ", KnownBuiltInTools)}.");
            if (local && name == "WebSearch")
                return (null, $"local mode removes WebSearch; '{ToolsFlag}' cannot add it back.");
        }

        return (new HarnessExtraLine(number, HarnessExtraTarget.ClaudeFlag, ToolsFlag,
            string.Join(',', names.Distinct(StringComparer.Ordinal))), null);
    }

    // ── value readers: TOML scalars, exact spelling ───────────────────────────────────────────────

    private static (object? Value, string? Fault) ReadBool(string raw) => raw switch
    {
        "true" => (true, null),
        "false" => (false, null),
        _ => (null, "must be true or false"),
    };

    private static (object? Value, string? Fault) ReadInt(string raw, int min, int max)
    {
        if (raw.Length == 0 || raw.Length > 9 || !raw.All(char.IsAsciiDigit))
            return (null, "must be an integer");
        var value = int.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
        return value < min || value > max ? (null, $"is {value}, outside {min}-{max}") : (value, null);
    }

    private static (object? Value, string? Fault) ReadEnum(string raw, IReadOnlyList<string> allowed)
    {
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
            return (null, "must be a quoted string");
        var value = raw[1..^1];
        return allowed.Contains(value, StringComparer.Ordinal)
            ? (value, null)
            : (null, $"cannot be \"{value}\"");
    }
}
