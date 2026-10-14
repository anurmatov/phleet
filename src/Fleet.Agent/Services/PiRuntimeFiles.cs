using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Shared;

namespace Fleet.Agent.Services;

/// <summary>
/// Everything Fleet generates for one Pi start (#386 §4.2): the three files in
/// <c>PI_CODING_AGENT_DIR</c>, the process arguments and the child environment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Immutability.</b> <see cref="PiExecutor"/> rewrites all three files before <b>every</b> Pi
/// start, and the arguments make Pi ignore what it would otherwise discover (<c>-ne</c>,
/// <c>--no-mcp</c>, <c>-ns</c>, <c>-np</c>, <c>-nc</c>, <c>--no-approve</c>, <c>--offline</c>). An edit
/// the model makes through <c>bash</c> therefore cannot survive a restart.
/// </para>
/// <para>
/// The bytes are golden-file stable: fixed key order, two-space indentation, LF line ends and one
/// trailing LF, on every platform. Nothing here reads the clock, the environment or the disk.
/// </para>
/// </remarks>
public static class PiRuntimeFiles
{
    public const string ModelsFileName = "models.json";
    public const string SettingsFileName = "settings.json";
    public const string ExtensionConfigFileName = "fleet-pi.json";

    /// <summary>
    /// Pi 1.0.4 writes <c>{}</c> to this file on every start (<c>AuthStorage.ensureFileExists</c>,
    /// <c>coding-agent/src/core/auth-storage.ts:63-65</c>) and offers no flag to skip it. Fleet removes
    /// it before every start and refuses a running Pi whose copy holds anything but that <c>{}</c>.
    /// </summary>
    public const string AuthFileName = "auth.json";

    /// <summary>Pi's MCP OAuth store; never written with <c>--no-mcp</c>, so it must not exist.</summary>
    public const string McpAuthFileName = "mcp-auth.json";

    /// <summary>The exact content Pi writes to an empty <see cref="AuthFileName"/>: no credential.</summary>
    public const string EmptyAuthContent = "{}";

    /// <summary>The Pi binary, resolved on PATH.</summary>
    public const string PiBin = "pi";

    private const string McpToolPrefix = "mcp__";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
    };

    /// <summary>
    /// <c>models.json</c>: one provider, <see cref="PiLocalModel.ProviderId"/>, speaking Anthropic
    /// Messages at <paramref name="baseUrl"/>, with one model that takes text and images (#458).
    /// A server that rejects an image gets the turn again text-only (<see cref="PiExecutor"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="apiKey"/> is <see cref="ClaudeLocalModel.PlaceholderAuthToken"/>, a public
    /// non-secret literal; the parameter exists so the generator's refusal can be tested (A32).
    /// </remarks>
    public static string BuildModelsJson(
        string baseUrl, string tag, int contextWindow, string apiKey = ClaudeLocalModel.PlaceholderAuthToken)
    {
        if (DescribeApiKeyFault(apiKey) is { } fault)
            throw new InvalidOperationException(fault);

        var root = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                [PiLocalModel.ProviderId] = new JsonObject
                {
                    ["baseUrl"] = baseUrl,
                    ["api"] = "anthropic-messages",
                    ["apiKey"] = apiKey,
                    ["compat"] = new JsonObject
                    {
                        ["supportsStrictTools"] = false,
                        ["supportsEagerToolInputStreaming"] = false,
                    },
                    ["models"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = tag,
                            ["name"] = tag,
                            ["reasoning"] = true,
                            ["input"] = new JsonArray { "text", "image" },
                            ["contextWindow"] = contextWindow,
                            ["maxTokens"] = PiLocalModel.ModelMaxTokens,
                            ["cost"] = new JsonObject
                            {
                                ["input"] = 0,
                                ["output"] = 0,
                                ["cacheRead"] = 0,
                                ["cacheWrite"] = 0,
                            },
                        },
                    },
                },
            },
        };
        return Serialize(root);
    }

    /// <summary>
    /// <c>settings.json</c>: compaction R/T, three retries, steering <c>all</c>, cache warming off
    /// and no telemetry. <c>steeringMode</c> comes from this file, so no <c>set_*</c> RPC is sent.
    /// </summary>
    public static string BuildSettingsJson() => Serialize(new JsonObject
    {
        ["compaction"] = new JsonObject
        {
            ["enabled"] = true,
            ["reserveTokens"] = PiLocalModel.ReserveTokens,
            ["keepRecentTokens"] = PiLocalModel.KeepRecentTokens,
        },
        ["retry"] = new JsonObject
        {
            ["enabled"] = true,
            ["maxRetries"] = 3,
        },
        ["steeringMode"] = "all",
        // A warming replay would be an extra full request against the local server.
        ["cacheWarming"] = "off",
        ["enableInstallTelemetry"] = false,
        ["enableAnalytics"] = false,
    });

    /// <summary>
    /// <c>fleet-pi.json</c>, the extension's configuration. <c>allowedTools</c> is the agent's own
    /// <c>AllowedTools</c> MCP grants, exactly — the same source as every provider.
    /// </summary>
    public static string BuildExtensionConfigJson(
        IEnumerable<string> allowedTools, string mcpConfigPath = PiLocalModel.McpConfigPath)
    {
        var granted = new JsonArray();
        foreach (var name in GrantedMcpTools(allowedTools))
            granted.Add(name);

        return Serialize(new JsonObject
        {
            ["mcpConfigPath"] = mcpConfigPath,
            ["allowedTools"] = granted,
            ["toolResultMaxBytes"] = PiLocalModel.ToolResultMaxBytes,
            ["toolCallTimeoutSeconds"] = PiLocalModel.ToolCallTimeoutSeconds,
            ["summaryMaxTokens"] = PiLocalModel.SummaryMaxTokens,
        });
    }

    /// <summary>
    /// The granted MCP names: every <c>mcp__…</c> entry of <c>AllowedTools</c>, verbatim, first
    /// occurrence order, duplicates dropped. Never renamed, shortened or mapped.
    /// </summary>
    public static IReadOnlyList<string> GrantedMcpTools(IEnumerable<string> allowedTools) =>
        allowedTools
            .Where(name => name.StartsWith(McpToolPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A32: the generator refuses a key Pi would resolve as a shell command (<c>!…</c>) or an
    /// environment reference (<c>$…</c>), and an OAuth token that would make Pi present as Claude
    /// Code. The text names the rule, never the value.
    /// </summary>
    public static string? DescribeApiKeyFault(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            return "The Pi models.json apiKey is empty.";
        if (apiKey.StartsWith('!'))
            return "The Pi models.json apiKey must not start with '!' (Pi would run it as a command).";
        if (apiKey.StartsWith('$'))
            return "The Pi models.json apiKey must not start with '$' (Pi would read it from the environment).";
        if (apiKey.Contains("sk-ant-oat", StringComparison.Ordinal))
            return "The Pi models.json apiKey must not be an Anthropic OAuth token.";
        return null;
    }

    /// <summary>
    /// The process arguments after the binary, exactly per §4.2. <c>--tools</c> lists the four
    /// built-ins and then every granted MCP name exactly: without an <c>mcp__</c> entry Pi allows
    /// extension tools named <c>mcp__…</c> but never declares them to the model.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        string tag, string? effort, string systemPromptFile, IEnumerable<string> allowedTools,
        string? sessionFile, string extensionDir = PiLocalModel.ExtensionDir)
    {
        var tools = PiLocalModel.BuiltInTools.Concat(GrantedMcpTools(allowedTools));
        var args = new List<string>
        {
            "--mode", "rpc", "--offline", "--no-approve",
            "--provider", PiLocalModel.ProviderId, "--model", tag,
            "--thinking", PiLocalModel.ThinkingArgument(effort),
            "--system-prompt", systemPromptFile,
            "-nc", "-ns", "-np", "-ne", "--no-mcp",
            "-e", extensionDir,
            "--tools", string.Join(',', tools),
        };

        // A full path, never an id: an id from another project makes Pi prompt y/N on stdin.
        if (sessionFile is not null)
        {
            args.Add("--session");
            args.Add(Path.GetFullPath(sessionFile));
        }

        return args;
    }

    /// <summary>
    /// The child environment: the inherited one minus every denylisted credential name or prefix,
    /// plus <c>PI_OFFLINE=1</c>, <c>PI_TELEMETRY=0</c> and <c>PI_CODING_AGENT_DIR</c>.
    /// <c>GH_*</c>, <c>GITHUB_*</c> and git settings stay. <c>PI_SKIP_VERSION_CHECK</c> is never
    /// set: offline mode already sets it inside Pi.
    /// </summary>
    public static void ApplyChildEnvironment(IDictionary<string, string?> environment, string agentDir = PiLocalModel.AgentDir)
    {
        foreach (var name in environment.Keys.Where(PiLocalModel.IsCredentialEnvName).ToList())
            environment.Remove(name);

        foreach (var (name, value) in PiLocalModel.ChildEnvironment)
            environment[name] = name == "PI_CODING_AGENT_DIR" ? agentDir : value;
    }

    /// <summary>
    /// Writes the three files into <paramref name="agentDir"/>, each through a temporary file and
    /// a rename, so Pi never reads a partial file. Throws <see cref="PiRuntimeFileWriteException"/>
    /// naming the file that failed.
    /// </summary>
    public static void WriteAll(
        string agentDir, string baseUrl, string tag, int contextWindow, IEnumerable<string> allowedTools,
        string mcpConfigPath = PiLocalModel.McpConfigPath)
    {
        // Whatever the model's shell wrote into Pi's credential files does not survive a restart.
        foreach (var name in new[] { AuthFileName, McpAuthFileName })
        {
            try
            {
                if (Directory.Exists(agentDir))
                    File.Delete(Path.Combine(agentDir, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PiRuntimeFileWriteException(name, ex);
            }
        }

        var files = new (string Name, string Content)[]
        {
            (ModelsFileName, BuildModelsJson(baseUrl, tag, contextWindow)),
            (SettingsFileName, BuildSettingsJson()),
            (ExtensionConfigFileName, BuildExtensionConfigJson(allowedTools, mcpConfigPath)),
        };

        foreach (var (name, content) in files)
        {
            var path = Path.Combine(agentDir, name);
            try
            {
                Directory.CreateDirectory(agentDir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PiRuntimeFileWriteException(name, ex);
            }
        }
    }

    /// <summary>
    /// The credential file a started Pi must not hold, or null: <see cref="AuthFileName"/> with any
    /// content but Pi's own <see cref="EmptyAuthContent"/>, or any <see cref="McpAuthFileName"/>.
    /// Names the file, never its content.
    /// </summary>
    public static string? DescribeCredentialFileFault(string agentDir)
    {
        var auth = Path.Combine(agentDir, AuthFileName);
        if (File.Exists(auth) || Directory.Exists(auth))
        {
            try
            {
                if (!File.Exists(auth) || File.ReadAllText(auth) != EmptyAuthContent)
                    return AuthFileName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return AuthFileName;
            }
        }

        var mcpAuth = Path.Combine(agentDir, McpAuthFileName);
        return File.Exists(mcpAuth) || Directory.Exists(mcpAuth) ? McpAuthFileName : null;
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(Format) + "\n";
}

/// <summary>A runtime file could not be written; <see cref="FileName"/> names it, never its content.</summary>
public sealed class PiRuntimeFileWriteException(string fileName, Exception inner)
    : IOException($"could not write {fileName}", inner)
{
    public string FileName { get; } = fileName;
}
