using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleet.Shared;

/// <summary>What <see cref="HarnessDefaults.Build"/> reads. Mode is derived by the caller, never stored.</summary>
/// <remarks>
/// <see cref="Local"/>: the agent has a local server URL (Codex local provider, or
/// <see cref="ClaudeLocalModel.IsEnabled"/>). A hosted Codex provider is cloud.
/// <see cref="LocalBaseUrl"/>, <see cref="Model"/> and <see cref="Effort"/> are read only for a Claude
/// agent in local mode, where they form today's local-model environment.
/// </remarks>
public sealed record HarnessInput
{
    public required string? Provider { get; init; }
    public required bool Local { get; init; }
    public int? ContextWindow { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public string? HarnessExtra { get; init; }
    public string? LocalBaseUrl { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
}

/// <summary>One Codex setting: a dotted key and a <c>string</c>, <c>bool</c> or <c>int</c> value.</summary>
public sealed record HarnessSetting(string Name, object Value);

/// <summary>
/// The effective harness settings for one agent (#460 Part 3a). Codex parts are null for a Claude
/// agent and Claude parts are null for a Codex agent.
/// </summary>
/// <param name="CodexRootToml">Root keys plus the <c>[analytics]</c> and <c>[history]</c> tables;
/// written to <c>codex-root.toml</c> and placed first in <c>config.toml</c>.</param>
/// <param name="CodexThreadConfig">Merged into <c>thread/start</c> <c>config</c> without overwriting
/// keys already there (hosted-provider keys, <c>model_context_window</c>).</param>
/// <param name="ClaudeArgs">Appended to the Claude CLI arguments.</param>
/// <param name="ClaudeEnv">The Claude child's added environment: today's local-model entries first in
/// local mode, then the defaults.</param>
/// <param name="ExtraState"><c>none</c>, <c>applied</c> or <c>ignored</c>.</param>
/// <param name="ExtraReason">Why the extra was ignored, else null.</param>
/// <param name="Replaced">Default keys, flags or env names the extra replaced or removed.</param>
/// <param name="PlanSha256">SHA-256 over the canonical JSON of the effective settings.</param>
public sealed record HarnessPlan(
    string Provider,
    string Mode,
    string? CodexRootToml,
    IReadOnlyList<HarnessSetting>? CodexThreadConfig,
    IReadOnlyList<string>? ClaudeArgs,
    IReadOnlyList<KeyValuePair<string, string>>? ClaudeEnv,
    string ExtraState,
    string? ExtraReason,
    IReadOnlyList<string> Replaced,
    string PlanSha256)
{
    /// <summary>The line the agent prints once at startup, matching the effective-settings view.</summary>
    public string StartupLine => $"harness: mode={Mode} plan={PlanSha256} extra={ExtraState}";
}

/// <summary>
/// The one place harness settings are computed (#460, MUST NOT 9): opinionated Codex and Claude Code
/// defaults per local/cloud mode, with the agent's <c>harnessExtra</c> merged over them. The
/// orchestrator's effective-settings view and generated <c>codex-root.toml</c>, the executors and
/// the agent's started-plan report all call <see cref="Build"/>, so they cannot drift.
/// </summary>
/// <remarks>
/// Pure: no I/O, no clock, no environment. Values are fixed in code; the per-mode tables are in
/// <c>docs/providers/harness-defaults.md</c>.
/// </remarks>
public static class HarnessDefaults
{
    public const string ModeCloud = "cloud";
    public const string ModeLocal = "local";
    public const string ExtraNone = "none";
    public const string ExtraApplied = "applied";
    public const string ExtraIgnored = "ignored";

    /// <summary>The generated file, under the agent's <c>.generated/</c> directory.</summary>
    public const string CodexRootTomlFileName = "codex-root.toml";

    /// <summary>
    /// Where provisioning mounts <see cref="CodexRootTomlFileName"/> (read-only, Codex agents only) and
    /// where <c>entrypoint.sh</c> reads it. One path for both sides.
    /// </summary>
    public const string CodexRootTomlContainerPath = "/app/codex-root.toml";

    /// <summary>Env values shown as <see cref="MaskedValue"/> in the view and never logged (MUST NOT 10).</summary>
    public static IReadOnlyList<string> MaskedEnvNames { get; } = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];
    public const string MaskedValue = "***";

    /// <summary>
    /// Built-in tools <c>--tools</c> always keeps, because <c>AllowedTools</c> never names them but
    /// the harness needs them: sub-agents (<c>Agent</c>, which the pinned CLI reports as <c>Task</c>),
    /// deferred MCP tool loading, skills and todos. A fixed constant; AC-C2 verifies each name.
    /// </summary>
    public static IReadOnlyList<string> KeptTools { get; } = ["Agent", "Task", "ToolSearch", "Skill", "TodoWrite"];

    /// <summary>Kept as well in cloud mode only (MUST NOT 5).</summary>
    public static IReadOnlyList<string> CloudKeptTools { get; } = ["WebFetch", "WebSearch"];

    /// <summary><c>tool_output_token_limit</c> and <c>MAX_MCP_OUTPUT_TOKENS</c> in local mode, or null
    /// when the context window is unset.</summary>
    public static int? OutputTokenLimit(int? contextWindow) =>
        contextWindow is int window ? Math.Clamp(window / 16, 4_000, 25_000) : null;

    /// <summary>The value the view shows for <paramref name="name"/>.</summary>
    public static string DisplayValue(string name, string value) =>
        MaskedEnvNames.Contains(name, StringComparer.Ordinal) ? MaskedValue : value;

    /// <summary>
    /// The base names of the built-in tools an <c>AllowedTools</c> list names: the text before the
    /// first <c>(</c> (<c>Bash(git *)</c> → <c>Bash</c>), never an <c>mcp__</c> entry, deduplicated and
    /// sorted ordinally so the input order never changes the plan.
    /// </summary>
    public static IReadOnlyList<string> AllowedBuiltInTools(IEnumerable<string> allowedTools) =>
        allowedTools
            .Where(t => !t.StartsWith("mcp__", StringComparison.Ordinal))
            .Select(t => (t.IndexOf('(') is var paren and >= 0 ? t[..paren] : t).Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Computes the plan. Throws for a provider other than claude or codex.</summary>
    public static HarnessPlan Build(HarnessInput input)
    {
        var provider = AgentProviders.Normalize(input.Provider);
        if (!HarnessExtra.IsSupportedProvider(provider))
            throw new ArgumentException($"Harness defaults apply only to claude and codex, not '{provider}'.", nameof(input));

        var local = input.Local;
        var limit = local ? OutputTokenLimit(input.ContextWindow) : null;

        var parse = HarnessExtra.Parse(input.HarnessExtra, provider, local);
        var extras = parse.Fault is null ? parse.Lines : [];
        var state = parse.Fault is not null ? ExtraIgnored : extras.Count == 0 ? ExtraNone : ExtraApplied;
        var replaced = new List<string>();

        string? rootToml = null;
        List<HarnessSetting>? thread = null;
        List<string>? args = null;
        List<KeyValuePair<string, string>>? env = null;

        if (provider == AgentProviders.Codex)
        {
            var root = new List<HarnessSetting>
            {
                new("check_for_update_on_startup", false),
                new("analytics.enabled", false),
                new("history.persistence", "none"),
            };
            thread = [];
            if (local)
            {
                thread.Add(new("web_search", "disabled"));
                thread.Add(new("tools.view_image", false));
                thread.Add(new("model_reasoning_summary", "none"));
                if (limit is int tokens) thread.Add(new("tool_output_token_limit", tokens));
            }
            thread.Add(new("features.apps", false));
            thread.Add(new("features.remote_plugin", false));
            thread.Add(new("features.skill_mcp_dependency_install", false));

            foreach (var extra in extras)
                Merge(extra.Target == HarnessExtraTarget.CodexRoot ? root : thread,
                    new HarnessSetting(extra.Name, extra.Value!), replaced);

            rootToml = RenderToml(root);
        }
        else
        {
            var builtIns = AllowedBuiltInTools(input.AllowedTools);
            List<string>? tools = builtIns.Count == 0
                ? null // the agent keeps every built-in; no --tools flag
                : [.. KeptTools, .. local ? [] : CloudKeptTools];
            if (tools is not null)
                tools.AddRange(builtIns.Where(t => !tools.Contains(t, StringComparer.Ordinal)).ToList());
            var disallowWebSearch = local;
            var excludeDynamic = true;

            env = [];
            if (local)
                env.AddRange(ClaudeLocalModel.BuildEnvironment(
                    ClaudeLocalModel.CanonicalizeBaseUrl(input.LocalBaseUrl ?? ""), input.Model ?? "", input.Effort));
            env.Add(new(HarnessExtra.NonessentialTrafficEnv, "1"));
            if (limit is int tokens)
                env.Add(new(HarnessExtra.MaxMcpOutputTokensEnv, tokens.ToString(CultureInfo.InvariantCulture)));

            foreach (var extra in extras)
            {
                switch (extra.Target)
                {
                    case HarnessExtraTarget.ClaudeFlag when extra.Name == HarnessExtra.ToolsFlag:
                        // Additive only (MUST NOT 4); a no-op when no --tools flag is computed.
                        if (tools is null) break;
                        var added = ((string)extra.Value!).Split(',')
                            .Where(t => !tools.Contains(t, StringComparer.Ordinal)).ToList();
                        if (added.Count > 0)
                        {
                            tools.AddRange(added);
                            replaced.Add(HarnessExtra.ToolsFlag);
                        }
                        break;
                    case HarnessExtraTarget.ClaudeFlag when extra.Name == HarnessExtra.DisallowedToolsFlag:
                        disallowWebSearch = true;
                        break;
                    case HarnessExtraTarget.ClaudeFlagRemoved:
                        excludeDynamic = false;
                        replaced.Add(HarnessExtra.ExcludeDynamicSectionsFlag);
                        break;
                    case HarnessExtraTarget.ClaudeEnv:
                        var index = env.FindIndex(e => e.Key == extra.Name);
                        if (index >= 0)
                        {
                            env[index] = new(extra.Name, (string)extra.Value!);
                            replaced.Add(extra.Name);
                        }
                        else
                        {
                            env.Add(new(extra.Name, (string)extra.Value!));
                        }
                        break;
                }
            }

            args = [];
            if (tools is not null) args.AddRange([HarnessExtra.ToolsFlag, string.Join(',', tools)]);
            if (disallowWebSearch) args.AddRange([HarnessExtra.DisallowedToolsFlag, "WebSearch"]);
            if (excludeDynamic) args.Add(HarnessExtra.ExcludeDynamicSectionsFlag);
            args.Add(HarnessExtra.StrictMcpConfigFlag);
        }

        var mode = local ? ModeLocal : ModeCloud;
        var sha = Sha256(provider, mode, rootToml, thread, args, env, state);
        return new HarnessPlan(provider, mode, rootToml, thread, args, env, state,
            parse.Fault?.Message, replaced, sha);
    }

    private static void Merge(List<HarnessSetting> settings, HarnessSetting extra, List<string> replaced)
    {
        var index = settings.FindIndex(s => s.Name == extra.Name);
        if (index < 0)
        {
            settings.Add(extra);
            return;
        }
        settings[index] = extra;
        replaced.Add(extra.Name);
    }

    /// <summary>
    /// Root keys first, then one table per dotted prefix in first-seen order. A root key written
    /// after a table header would belong to that table, so the order is load-bearing.
    /// </summary>
    internal static string RenderToml(IReadOnlyList<HarnessSetting> settings)
    {
        var sb = new StringBuilder();
        foreach (var s in settings.Where(s => !s.Name.Contains('.')))
            sb.Append(s.Name).Append(" = ").Append(TomlValue(s.Value)).Append('\n');

        foreach (var table in settings.Where(s => s.Name.Contains('.')).GroupBy(s => s.Name[..s.Name.IndexOf('.')]))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append('[').Append(table.Key).Append("]\n");
            foreach (var s in table)
                sb.Append(s.Name[(table.Key.Length + 1)..]).Append(" = ").Append(TomlValue(s.Value)).Append('\n');
        }
        return sb.ToString();
    }

    private static string TomlValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        _ => throw new ArgumentException($"Unsupported TOML value type {value.GetType().Name}."),
    };

    private static string Sha256(string provider, string mode, string? rootToml, IReadOnlyList<HarnessSetting>? thread,
        IReadOnlyList<string>? args, IReadOnlyList<KeyValuePair<string, string>>? env, string extraState)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("provider", provider);
            w.WriteString("mode", mode);
            w.WriteString("codexRootToml", rootToml);
            w.WritePropertyName("codexThreadConfig");
            if (thread is null) w.WriteNullValue();
            else
            {
                w.WriteStartArray();
                foreach (var s in thread)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(s.Name);
                    WriteScalar(w, s.Value);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WritePropertyName("claudeArgs");
            if (args is null) w.WriteNullValue();
            else
            {
                w.WriteStartArray();
                foreach (var a in args) w.WriteStringValue(a);
                w.WriteEndArray();
            }
            w.WritePropertyName("claudeEnv");
            if (env is null) w.WriteNullValue();
            else
            {
                w.WriteStartArray();
                foreach (var e in env)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(e.Key);
                    w.WriteStringValue(e.Value);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteString("extraState", extraState);
            w.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>Writes a <see cref="HarnessSetting.Value"/> as a JSON scalar.</summary>
    public static void WriteScalar(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case string s: writer.WriteStringValue(s); break;
            default: throw new ArgumentException($"Unsupported setting value type {value.GetType().Name}.");
        }
    }
}
