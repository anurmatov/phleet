using System.Text;
using System.Text.RegularExpressions;
using Fleet.Agent.Configuration;
using Fleet.Shared;
using Microsoft.Extensions.Configuration;

namespace Fleet.Agent.Prepare;

/// <summary>
/// <c>dotnet Fleet.Agent.dll prepare &lt;verb&gt;</c>: what <c>entrypoint.sh</c> needs from the
/// generated configuration before it starts the agent.
/// </summary>
/// <remarks>
/// <para>
/// The script used to read <c>appsettings.json</c> itself, through inline Node and Python, and so
/// carried its own copy of every provider name, option key and path it used. This reads the
/// configuration the way the host does (<see cref="AgentOptions"/>, the JSON file and then the
/// environment) and takes names and paths from the constants the rest of the code uses.
/// </para>
/// <para>
/// <see cref="FactsVerb"/> prints <c>NAME=value</c> lines the script acts on: which provider to
/// prepare credentials for, whether a hosted key is handed over and under which name. It refuses
/// (exit 1) a configuration it cannot read, an unknown provider (PV1) and a hosted provider without a
/// usable key name; the script then exits 1 as well, where it used to fall back to <c>claude</c>.
/// <see cref="ConfigVerb"/> writes the provider CLI's own configuration file: the Codex
/// <c>config.toml</c> or the Gemini <c>settings.json</c>. A file it cannot build is a warning and
/// exit 0, as it always was: the agent starts without those MCP tools.
/// </para>
/// <para>
/// Neither verb may see a hosted-provider key. The script removes the reserved names from its
/// environment before the first call (<c>tests/entrypoint-prepare/run.sh</c> proves it), and nothing
/// here opens <see cref="HostedModelProviders.KeyFilePath"/>: <see cref="FactsVerb"/> only names it.
/// </para>
/// </remarks>
public static partial class EntrypointPrepare
{
    public const string Command = "prepare";
    public const string FactsVerb = "facts";
    public const string ConfigVerb = "config";

    /// <summary>For tests: resolve every absolute path below this directory.</summary>
    public const string RootOption = "--root";

    /// <summary>The names <see cref="FactsVerb"/> prints, in order. <c>entrypoint.sh</c> reads exactly these.</summary>
    public static IReadOnlyList<string> FactNames { get; } =
    [
        "PROVIDER", "HOSTED_PROVIDER", "HOSTED_KEY_ENV", "HOSTED_KEY_FILE", "HOSTED_KEY_MISSING",
        "CLAUDE_LOCAL_MODEL", "GH_TOKEN_PLACEHOLDER",
    ];

    [GeneratedRegex("^[A-Z_][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvVarName();

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr) =>
        Run(args, stdout, stderr, Environment.GetEnvironmentVariable);

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, Func<string, string?> environment)
    {
        if (!TryParse(args, out var verb, out var root))
        {
            stderr.WriteLine($"ERROR: usage: {Command} {FactsVerb}|{ConfigVerb} [{RootOption} <directory>]");
            return 2;
        }

        var paths = new PreparePaths(root, environment("HOME") ?? "");
        AgentOptions agent;
        try
        {
            agent = LoadAgentOptions(paths, environment);
        }
        catch (Exception ex)
        {
            // The outer message names the file or the key, never a value.
            stderr.WriteLine($"ERROR: cannot read the agent configuration ({paths.Appsettings}): {ex.Message}");
            return 1;
        }

        try
        {
            return verb == FactsVerb ? Facts(agent, paths, stdout, stderr, environment) : Config(agent, paths, stdout, stderr);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"ERROR: {Command} {verb} failed: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParse(IReadOnlyList<string> args, out string verb, out string? root)
    {
        verb = args.Count > 0 ? args[0] : "";
        root = null;
        if (verb is not (FactsVerb or ConfigVerb))
            return false;
        if (args.Count == 1)
            return true;
        if (args.Count != 3 || args[1] != RootOption || args[2].Length == 0)
            return false;
        root = args[2];
        return true;
    }

    /// <summary>
    /// The <c>Agent</c> section as the host binds it: the generated JSON file, then the environment.
    /// </summary>
    internal static AgentOptions LoadAgentOptions(PreparePaths paths, Func<string, string?> environment)
    {
        var environmentName = environment("DOTNET_ENVIRONMENT") ?? environment("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(paths.AppDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var section = configuration.GetSection(AgentOptions.Section);
        // A scalar the script acts on must be a scalar. An object or array there binds as "not set",
        // which would pick Claude credentials for a configuration that says something else.
        foreach (var key in new[] { nameof(AgentOptions.Provider), nameof(AgentOptions.AnthropicBaseUrl), nameof(AgentOptions.HostedProvider), nameof(AgentOptions.HostedProviderKeyEnv) })
        {
            if (section.GetSection(key).GetChildren().Any())
                throw new InvalidOperationException($"'{AgentOptions.Section}:{key}' must be a single value.");
        }

        // Name, Role and WorkDir are `required` for the compiler only; the binder does not enforce
        // them and neither does this: the host's own startup gate does.
        return section.Get<AgentOptions>() ?? throw new InvalidOperationException($"the '{AgentOptions.Section}' section is missing.");
    }

    private static int Facts(AgentOptions agent, PreparePaths paths, TextWriter stdout, TextWriter stderr, Func<string, string?> environment)
    {
        var keyEnv = agent.HostedProviderKeyEnv ?? "";
        // `$` also matches before a final newline; a name is one line.
        var keyEnvUsable = EnvVarName().IsMatch(keyEnv) && !keyEnv.Contains('\n');
        if (agent.HostedProvider && !keyEnvUsable)
        {
            stderr.WriteLine("ERROR: hosted provider enabled but Agent.HostedProviderKeyEnv is missing or invalid.");
            return 1;
        }

        // PV1 (#386): an unknown provider fails closed. It used to fall through to the Claude branch
        // and receive Claude credentials in a container where the model drives a shell.
        if (AgentProviders.DescribeFault(agent.Provider) is { } fault)
        {
            stderr.WriteLine($"ERROR: {fault}");
            return 1;
        }
        var provider = AgentProviders.Normalize(agent.Provider);

        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PROVIDER"] = provider,
            ["HOSTED_PROVIDER"] = Flag(agent.HostedProvider),
            // Only a well-formed name is passed on; the script compares it and never evaluates it.
            ["HOSTED_KEY_ENV"] = keyEnvUsable ? keyEnv : "",
            ["HOSTED_KEY_FILE"] = paths.HostedKeyFile,
            ["HOSTED_KEY_MISSING"] = HostedModelProviders.MissingSecretPlaceholder,
            // Any non-empty URL counts, whitespace included: that is a fault the startup gate
            // rejects, so the script fails closed (no credentials copied) rather than open.
            ["CLAUDE_LOCAL_MODEL"] = Flag(ClaudeLocalModel.IsEnabled(provider, agent.AnthropicBaseUrl)),
            ["GH_TOKEN_PLACEHOLDER"] = Flag(SecretPlaceholders.IsPlaceholder(environment("GH_TOKEN"))),
        };
        foreach (var name in FactNames)
            stdout.Write($"{name}={facts[name]}\n");
        return 0;
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static int Config(AgentOptions agent, PreparePaths paths, TextWriter stdout, TextWriter stderr)
    {
        switch (AgentProviders.Normalize(agent.Provider))
        {
            case AgentProviders.Claude:
                return 0;
            case AgentProviders.Codex:
                WriteCodexConfig(agent, paths, stderr);
                return 0;
            case AgentProviders.Gemini:
                WriteGeminiSettings(agent, paths, stdout, stderr);
                return 0;
            default:
                stderr.WriteLine($"ERROR: {AgentProviders.UnknownProviderText(agent.Provider)}");
                return 1;
        }
    }

    /// <summary>
    /// Written even without <c>.mcp.json</c>, so a stale file from an earlier start never survives.
    /// A missing, unreadable or directory root file is one warning and the MCP tables are still
    /// written, so the agent never loses its MCP tools; the CLI then uses its built-in defaults.
    /// </summary>
    private static void WriteCodexConfig(AgentOptions agent, PreparePaths paths, TextWriter stderr)
    {
        try
        {
            var root = "";
            try
            {
                if (Directory.Exists(paths.CodexRootToml))
                    throw new IOException("is a directory");
                // Decoded leniently and byte-order mark kept, as the file is copied through.
                root = new UTF8Encoding(false).GetString(File.ReadAllBytes(paths.CodexRootToml));
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"WARN: codex-root.toml unavailable: {Reason(ex)}");
            }

            var mcpJson = File.Exists(paths.McpJson) || Directory.Exists(paths.McpJson) ? File.ReadAllBytes(paths.McpJson) : null;
            var text = CodexConfigToml.Build(root, mcpJson, agent.AllowedTools, agent.McpHeaderSupport, stderr.WriteLine);
            Directory.CreateDirectory(paths.CodexHome);
            File.WriteAllText(paths.CodexConfig, text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"WARN: codex config.toml not written: {ex.Message}");
        }
    }

    private static void WriteGeminiSettings(AgentOptions agent, PreparePaths paths, TextWriter stdout, TextWriter stderr)
    {
        if (File.Exists(paths.McpJson))
        {
            try
            {
                Directory.CreateDirectory(paths.GeminiHome);
                var result = GeminiSettingsJson.WithMcpServers(
                    File.ReadAllBytes(paths.McpJson), ReadExisting(paths.GeminiSettings), agent.McpHeaderSupport, stderr.WriteLine);
                File.WriteAllText(paths.GeminiSettings, result.Text, new UTF8Encoding(false));
                stdout.WriteLine($"Gemini MCP: wrote {result.ServerCount} server(s) to {paths.GeminiSettings}");
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"WARN: Failed to write {paths.GeminiSettings} — gemini agent will start without MCP tools ({ex.Message})");
            }
            return;
        }

        stderr.WriteLine($"WARN: {paths.McpJson} not found; gemini agent will start without MCP tools");
        try
        {
            Directory.CreateDirectory(paths.GeminiHome);
            File.WriteAllText(paths.GeminiSettings, GeminiSettingsJson.WithAuthSelectorOnly(ReadExisting(paths.GeminiSettings)), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"WARN: Failed to write {paths.GeminiSettings} ({ex.Message})");
        }
    }

    /// <summary>The current file's bytes; null when there is none; unparseable bytes when it cannot be read.</summary>
    private static byte[]? ReadExisting(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return null;
        try { return File.ReadAllBytes(path); }
        catch (Exception) { return []; }
    }

    private static string Reason(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "ENOENT",
        UnauthorizedAccessException => "EACCES",
        _ => ex.Message,
    };
}

/// <summary>
/// Where things are in the agent container. <paramref name="root"/> is null there; a test passes a
/// scratch directory and every path below resolves inside it.
/// </summary>
public sealed class PreparePaths(string? root, string home)
{
    /// <summary>The agent's install directory, which also holds the generated <c>appsettings.json</c>.</summary>
    public string AppDirectory { get; } = Resolve(root, Path.GetDirectoryName(HarnessDefaults.CodexRootTomlContainerPath)!);

    public string Appsettings => Path.Combine(AppDirectory, "appsettings.json");

    public string CodexRootToml { get; } = Resolve(root, HarnessDefaults.CodexRootTomlContainerPath);

    /// <summary>The fleet's MCP server list, mounted read-only by provisioning.</summary>
    public string McpJson { get; } = Resolve(root, "/workspace/.mcp.json");

    public string CodexHome { get; } = Resolve(root, "/root/.codex");

    public string CodexConfig => Path.Combine(CodexHome, "config.toml");

    public string GeminiHome { get; } = Resolve(root, home + "/.gemini");

    public string GeminiSettings => Path.Combine(GeminiHome, "settings.json");

    /// <summary>Where the script puts a hosted-provider key for the agent to read once and delete.</summary>
    public string HostedKeyFile { get; } = Resolve(root, HostedModelProviders.KeyFilePath);

    private static string Resolve(string? root, string path) =>
        root is null ? path : Path.Combine(root, path.TrimStart('/'));
}
