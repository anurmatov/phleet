using System.Text;
using System.Text.Json;
using Fleet.Agent.Prepare;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// <c>dotnet Fleet.Agent.dll prepare</c>: what <c>entrypoint.sh</c> learns from the generated
/// configuration and the provider CLI files written for it. The byte-level comparison with the
/// script's former inline Node and Python is <c>tests/entrypoint-prepare/run.sh</c>; these pin the
/// same text without needing either runtime.
/// </summary>
public sealed class EntrypointPrepareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"entrypoint-prepare-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> _environment = new() { ["HOME"] = "/root" };

    public EntrypointPrepareTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── facts ────────────────────────────────────────────────────────────────

    [Fact]
    public void Facts_AreTheNamesTheScriptReads_InOrder()
    {
        Appsettings("""{"Agent":{"Provider":"codex","HostedProvider":true,"HostedProviderKeyEnv":"ZAI_CODING_PLAN_API_KEY"}}""");

        var run = Prepare("facts");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            [
                "PROVIDER=codex",
                "HOSTED_PROVIDER=true",
                "HOSTED_KEY_ENV=ZAI_CODING_PLAN_API_KEY",
                $"HOSTED_KEY_FILE={Path.Combine(_root, "run/phleet-hosted-key")}",
                "HOSTED_KEY_MISSING=<secret>",
                "CLAUDE_LOCAL_MODEL=false",
                "GH_TOKEN_PLACEHOLDER=false",
            ],
            run.OutputLines);
        Assert.Equal(EntrypointPrepare.FactNames, run.OutputLines.Select(l => l[..l.IndexOf('=')]));
        Assert.Empty(run.Error);
    }

    [Fact]
    public void Facts_InTheContainer_NameTheSharedKeyFilePath()
    {
        Assert.Equal(HostedModelProviders.KeyFilePath, new PreparePaths(root: null, home: "/root").HostedKeyFile);
        Assert.Equal("/app/appsettings.json", new PreparePaths(root: null, home: "/root").Appsettings);
        Assert.Equal("/root/.codex/config.toml", new PreparePaths(root: null, home: "/root").CodexConfig);
        Assert.Equal("/root/.gemini/settings.json", new PreparePaths(root: null, home: "/root").GeminiSettings);
        Assert.Equal("/workspace/.mcp.json", new PreparePaths(root: null, home: "/root").McpJson);
    }

    [Theory]
    [InlineData("""{"Agent":{"Name":"example"}}""", "claude")]
    [InlineData("""{"Agent":{"Name":"example","Provider":null}}""", "claude")]
    [InlineData("""{"Agent":{"Provider":""}}""", "claude")]
    [InlineData("""{"Agent":{"Provider":"claude"}}""", "claude")]
    [InlineData("""{"Agent":{"Provider":"codex"}}""", "codex")]
    [InlineData("""{"Agent":{"Provider":"gemini"}}""", "gemini")]
    public void Facts_Provider_MissingNullOrEmptyMeansClaude(string appsettings, string expected)
    {
        Appsettings(appsettings);

        var run = Prepare("facts");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"PROVIDER={expected}", run.OutputLines[0]);
    }

    [Theory]
    // Whitespace-only is a fault the startup gate rejects; here it must not copy credentials.
    [InlineData("""{"Agent":{"AnthropicBaseUrl":"http://inference.example:11434"}}""", "true")]
    [InlineData("""{"Agent":{"AnthropicBaseUrl":" "}}""", "true")]
    [InlineData("""{"Agent":{"AnthropicBaseUrl":""}}""", "false")]
    [InlineData("""{"Agent":{"AnthropicBaseUrl":null}}""", "false")]
    [InlineData("""{"Agent":{"Provider":"codex","AnthropicBaseUrl":"http://inference.example:11434"}}""", "false")]
    public void Facts_ClaudeLocalModel_IsTheSharedPredicate(string appsettings, string expected)
    {
        Appsettings(appsettings);

        var run = Prepare("facts");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains($"CLAUDE_LOCAL_MODEL={expected}", run.OutputLines);
    }

    [Fact]
    public void Facts_GhTokenPlaceholder_IsDecidedByTheSharedShape()
    {
        Appsettings("""{"Agent":{"Name":"example"}}""");

        _environment["GH_TOKEN"] = SecretPlaceholders.Create();
        Assert.Contains("GH_TOKEN_PLACEHOLDER=true", Prepare("facts").OutputLines);

        _environment["GH_TOKEN"] = "ghs_" + new string('a', 36);
        Assert.Contains("GH_TOKEN_PLACEHOLDER=false", Prepare("facts").OutputLines);

        _environment["GH_TOKEN"] = SecretPlaceholders.Create() + "\n";
        Assert.Contains("GH_TOKEN_PLACEHOLDER=false", Prepare("facts").OutputLines);
    }

    [Fact]
    public void Configuration_IsReadAsTheHostReadsIt_EnvironmentOverTheFile()
    {
        // The script's own readers saw only the file, so a container whose environment overrode it
        // had a script and a host that disagreed. The option used here is one the host never reads,
        // so setting it for a moment cannot disturb another test's host.
        Appsettings("""{"Agent":{"Provider":"codex","McpHeaderSupport":false}}""");
        Write("workspace/.mcp.json", """{"mcpServers":{"journal":{"url":"http://journal/mcp","headers":{"Authorization":"Bearer t"}}}}""");

        Assert.Equal(0, Prepare("config").ExitCode);
        Assert.DoesNotContain("[mcp_servers.journal]", Read("root/.codex/config.toml"));

        var run = PrepareWithProcessEnvironment("Agent__McpHeaderSupport", "true", "config");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("http_headers = { \"Authorization\" = \"Bearer t\" }", Read("root/.codex/config.toml"));
    }

    public static TheoryData<string, string, string> Refusals => new()
    {
        { "unreadable JSON", """{"Agent":{"Provider":"codex" """, "cannot read the agent configuration" },
        { "no Agent section", """{"Telegram":{"BotToken":""}}""", "the 'Agent' section is missing" },
        { "an empty Agent section", """{"Agent":{}}""", "the 'Agent' section is missing" },
        { "provider is an object", """{"Agent":{"Provider":{"name":"codex"}}}""", "'Agent:Provider' must be a single value" },
        { "provider is a list", """{"Agent":{"Provider":["codex"]}}""", "'Agent:Provider' must be a single value" },
        { "local URL is an object", """{"Agent":{"AnthropicBaseUrl":{"u":"http://x"}}}""", "'Agent:AnthropicBaseUrl' must be a single value" },
        { "unknown provider", """{"Agent":{"Provider":"foo"}}""", "ERROR: Unknown provider 'foo'; use claude, codex or gemini." },
        { "provider differs by case", """{"Agent":{"Provider":"Codex"}}""", "ERROR: Unknown provider 'Codex'; use claude, codex or gemini." },
        { "hosted without a key name", """{"Agent":{"Provider":"codex","HostedProvider":true}}""", "Agent.HostedProviderKeyEnv is missing or invalid" },
        { "hosted with a key name that is not a variable name", """{"Agent":{"Provider":"codex","HostedProvider":true,"HostedProviderKeyEnv":"zai key"}}""", "Agent.HostedProviderKeyEnv is missing or invalid" },
        { "a value of the wrong type", """{"Agent":{"MaxTurns":"many"}}""", "cannot read the agent configuration" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Facts_RefuseInsteadOfFallingBackToClaude(string _, string appsettings, string expected)
    {
        Appsettings(appsettings);

        var run = Prepare("facts");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Output);
        Assert.Contains(expected, run.Error);
    }

    [Fact]
    public void Facts_WithoutAConfigurationFile_Refuse()
    {
        var run = Prepare("facts");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Output);
        Assert.Contains("cannot read the agent configuration", run.Error);
    }

    [Fact]
    public void Facts_NeverPassOnAKeyNameThatIsNotOneName()
    {
        // Not hosted, so not refused; the script must still never see a second line it would read.
        Appsettings("""{"Agent":{"HostedProviderKeyEnv":"A\nPROVIDER=gemini"}}""");

        var run = Prepare("facts");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(EntrypointPrepare.FactNames.Count, run.OutputLines.Count);
        Assert.Equal("PROVIDER=claude", run.OutputLines[0]);
        Assert.Contains("HOSTED_KEY_ENV=", run.OutputLines);
    }

    [Theory]
    [InlineData]
    [InlineData("write")]
    [InlineData("facts", "--root")]
    [InlineData("facts", "--elsewhere", "/x")]
    [InlineData("facts", "--root", "/x", "more")]
    public void UnknownArguments_AreAUsageError(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = EntrypointPrepare.Run(args, stdout, stderr, _ => null);

        Assert.Equal(2, exit);
        Assert.Empty(stdout.ToString());
        Assert.Contains("usage: prepare facts|config", stderr.ToString());
    }

    // ── config ───────────────────────────────────────────────────────────────

    [Fact]
    public void Config_Codex_WritesRootThenMcpTables()
    {
        Appsettings("""{"Agent":{"Provider":"codex","AllowedTools":["mcp__alpha__memory_get","Read"]}}""");
        Write("app/codex-root.toml", "check_for_update_on_startup = false\n");
        Write("workspace/.mcp.json", """{"mcpServers":{"alpha":{"url":"http://alpha:3100"},"local":{"command":"node"}}}""");
        Write("root/.codex/config.toml", "stale = true\n");

        var run = Prepare("config");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        Assert.Equal(
            "check_for_update_on_startup = false\n\n[mcp_servers.alpha]\nurl = \"http://alpha:3100\"\nenabled_tools = [\"memory_get\"]\n\n",
            Read("root/.codex/config.toml"));
    }

    [Theory]
    [InlineData("missing", "ENOENT")]
    [InlineData("directory", "is a directory")]
    public void Config_Codex_WithoutAUsableRootFile_WarnsOnceAndKeepsTheMcpTables(string kind, string reason)
    {
        Appsettings("""{"Agent":{"Provider":"codex"}}""");
        Write("workspace/.mcp.json", """{"mcpServers":{"alpha":{"url":"http://alpha:3100"}}}""");
        if (kind == "directory")
            Directory.CreateDirectory(Path.Combine(_root, "app/codex-root.toml"));

        var run = Prepare("config");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"WARN: codex-root.toml unavailable: {reason}", run.Error.TrimEnd());
        Assert.Equal("[mcp_servers.alpha]\nurl = \"http://alpha:3100\"\n\n", Read("root/.codex/config.toml"));
    }

    [Fact]
    public void Config_Codex_WithoutMcpJson_StillReplacesAStaleFile()
    {
        Appsettings("""{"Agent":{"Provider":"codex"}}""");
        Write("app/codex-root.toml", "model = \"x\"\n");
        Write("root/.codex/config.toml", "[mcp_servers.gone]\nurl = \"http://gone\"\n");

        Assert.Equal(0, Prepare("config").ExitCode);

        Assert.Equal("model = \"x\"\n", Read("root/.codex/config.toml"));
    }

    [Fact]
    public void Config_Codex_WithUnusableMcpJson_WarnsAndWritesNothing()
    {
        Appsettings("""{"Agent":{"Provider":"codex"}}""");
        Write("app/codex-root.toml", "model = \"x\"\n");
        Write("workspace/.mcp.json", "{not json");

        var run = Prepare("config");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("WARN: codex config.toml not written: ", run.Error);
        Assert.False(File.Exists(Path.Combine(_root, "root/.codex/config.toml")));
    }

    [Fact]
    public void Config_Gemini_TranslatesMcpJsonAndKeepsWhatTheFileHeld()
    {
        Appsettings("""{"Agent":{"Provider":"gemini"}}""");
        Write("workspace/.mcp.json", """{"mcpServers":{"alpha":{"type":"http","url":"http://alpha:3100"},"local":{"command":"node"}}}""");
        Write("root/.gemini/settings.json", """{"theme":"Dracula","mcpServers":{"old":{"url":"http://old"}}}""");

        var run = Prepare("config");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("WARN: skipping MCP server 'local' (no URL — stdio transport not supported by gemini CLI)", run.Error.TrimEnd());
        Assert.Equal($"Gemini MCP: wrote 1 server(s) to {Path.Combine(_root, "root/.gemini/settings.json")}", run.Output.TrimEnd());
        Assert.Equal(
            """
            {
              "theme": "Dracula",
              "mcpServers": {
                "alpha": {
                  "url": "http://alpha:3100"
                }
              },
              "security": {
                "auth": {
                  "selectedType": "oauth-personal"
                }
              }
            }
            """.ReplaceLineEndings("\n"),
            Read("root/.gemini/settings.json"));
    }

    [Fact]
    public void Config_Gemini_WithoutMcpJson_SeedsOnlyTheAuthSelector()
    {
        Appsettings("""{"Agent":{"Provider":"gemini"}}""");

        var run = Prepare("config");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("not found; gemini agent will start without MCP tools", run.Error);
        Assert.Equal(
            "{\n  \"security\": {\n    \"auth\": {\n      \"selectedType\": \"oauth-personal\"\n    }\n  }\n}",
            Read("root/.gemini/settings.json"));
    }

    [Fact]
    public void Config_Gemini_WhenTheExistingFileCannotTakeTheResult_WarnsAndLeavesIt()
    {
        Appsettings("""{"Agent":{"Provider":"gemini"}}""");
        Write("workspace/.mcp.json", """{"mcpServers":{}}""");
        Write("root/.gemini/settings.json", "[1, 2]\n");

        var run = Prepare("config");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("gemini agent will start without MCP tools", run.Error);
        Assert.Equal("[1, 2]\n", Read("root/.gemini/settings.json"));
    }

    [Fact]
    public void Config_Claude_WritesNothing()
    {
        Appsettings("""{"Agent":{"Provider":"claude"}}""");
        Write("workspace/.mcp.json", """{"mcpServers":{"alpha":{"url":"http://alpha:3100"}}}""");

        var run = Prepare("config");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Output);
        Assert.Empty(run.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "root")));
    }

    // ── the exact text ───────────────────────────────────────────────────────

    [Fact]
    public void Codex_OddValues_AreWrittenAsTheNodeProgramWroteThem()
    {
        var mcp = File.ReadAllBytes(RepoPaths.Resolve("tests/entrypoint-prepare/fixtures/mcp/odd.json"));
        string[] allowed =
        [
            "Read", "Bash(git *)", "mcp__10__ten", "mcp__alpha__memory_get", "mcp__alpha__memory_search",
            "mcp__fleet-journal-files__fetch_attachment", "mcp__gamma__send", "mcp__quoted__a\"b", "mcp__ünï.code__tööl",
        ];

        var toml = CodexConfigToml.Build(rootToml: "", mcp, allowed, mcpHeaderSupport: true, _ => { });

        // Integer-like names first in ascending order, then the rest as declared; a repeated name
        // keeps its first place and its last value; a URL, a name and a tool are written as they
        // are; a header value goes through String() and JSON.stringify.
        var expected = """
            [mcp_servers.2]
            url = "http://two:2"

            [mcp_servers.10]
            url = "http://ten:10"
            enabled_tools = ["ten"]

            [mcp_servers.zeta]
            url = "http://zeta:1/mcp?q=ünï&x="y""
            http_headers = { "X-Quote" = "a\"b\\c", "X-Unicode" = "héllo ✓ 😀", "X-Control" = "tab\there\u0001", "X-Number" = "42", "X-Bool" = "true", "X-Null" = "null", "X-Float" = "1.5", "X-Big" = "1e+21", "X-Small" = "1e-7", "X-List" = "a,,2" }

            [mcp_servers.ünï.code]
            url = "http://unicode:3"
            enabled_tools = ["tööl"]

            [mcp_servers.quoted]
            url = "http://quoted:last"
            enabled_tools = ["a"b"]

            [mcp_servers.empty-headers]
            url = "http://empty:5"

            [mcp_servers.null-headers]
            url = "http://null:6"

            [mcp_servers.alpha]
            url = "http://alpha:3100?agent=last"
            enabled_tools = ["memory_get", "memory_search"]

            [mcp_servers.fleet-journal-files]
            url = "http://127.0.0.1:8091/journal-files/v1/mcp"
            enabled_tools = ["fetch_attachment"]
            """.ReplaceLineEndings("\n") + "\n\n";
        Assert.Equal(expected, toml);
    }

    [Fact]
    public void Gemini_ExistingValues_AreWrittenAsPythonWroteThem()
    {
        var existing = File.ReadAllBytes(RepoPaths.Resolve("tests/entrypoint-prepare/fixtures/settings/theme.json"));

        var text = GeminiSettingsJson.WithAuthSelectorOnly(existing);

        // Integers of any size unchanged, floats as repr(), non-ASCII and DEL escaped, empty
        // containers on one line, existing keys in place and the selector replaced where it was.
        var expected = """
            {
              "theme": "Dracula",
              "ui": {
                "scale": 1.5,
                "count": 3,
                "big": 12345678901234567890123,
                "tiny": 1e-05,
                "negzero": -0.0,
                "intnegzero": 0,
                "exp": 1e+22,
                "frac": 123456789.0,
                "edge16": 1e+16,
                "edge15": 1000000000000000.0,
                "small": 0.0001,
                "third": 0.1,
                "list": [
                  1,
                  [],
                  {},
                  "x",
                  null,
                  true
                ],
                "unicode": "h\u00e9llo \u2713 \ud83d\ude00",
                "ctl": "a\tb\u0001\u007f\"\\/"
              },
              "mcpServers": {
                "old": {
                  "url": "http://old"
                }
              },
              "security": {
                "auth": {
                  "selectedType": "oauth-personal",
                  "other": true
                },
                "keep": 1
              },
              "tail": "kept"
            }
            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(1.0, "1", "1.0")]
    [InlineData(-1.5, "-1.5", "-1.5")]
    [InlineData(0.1, "0.1", "0.1")]
    [InlineData(100.0, "100", "100.0")]
    [InlineData(123456789012345680000.0, "123456789012345680000", "1.2345678901234568e+20")]
    [InlineData(1e21, "1e+21", "1e+21")]
    [InlineData(1e16, "10000000000000000", "1e+16")]
    [InlineData(1e15, "1000000000000000", "1000000000000000.0")]
    [InlineData(0.0001, "0.0001", "0.0001")]
    [InlineData(0.00001, "0.00001", "1e-05")]
    [InlineData(0.000001, "0.000001", "1e-06")]
    [InlineData(0.0000001, "1e-7", "1e-07")]
    [InlineData(1.5e-10, "1.5e-10", "1.5e-10")]
    [InlineData(5e-324, "5e-324", "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308", "1.7976931348623157e+308")]
    public void Numbers_AreSpelledAsEachLanguageSpellsThem(double value, string javascript, string python)
    {
        Assert.Equal(javascript, ScriptCompat.JsNumber(value));
        Assert.Equal(python, ScriptCompat.PyFloatRepr(value));
    }

    [Fact]
    public void JsonStringify_EscapesOnlyWhatJavaScriptEscapes()
    {
        Assert.Equal("\"a\\\"b\\\\c\\n\\t\\u0001\u007f<>&'é😀\u2028\"", ScriptCompat.JsonStringify("a\"b\\c\n\t\u0001\u007f<>&'é😀\u2028"));
        Assert.Equal("\"\\ud83d\"", ScriptCompat.JsonStringify("\ud83d"));
    }

    // ── the script ───────────────────────────────────────────────────────────

    private static readonly string[] Script = File.ReadAllLines(RepoPaths.Resolve("entrypoint.sh"));

    private static IEnumerable<string> ScriptCode => Script.Where(l => !l.TrimStart().StartsWith('#'));

    [Fact]
    public void Script_ReadsExactlyTheFactsTheBinaryPrints()
    {
        var marker = Array.IndexOf(Script, "# phleet:prepare-facts");
        Assert.True(marker >= 0, "the '# phleet:prepare-facts' marker is missing from entrypoint.sh");
        Assert.StartsWith("while IFS='=' read -r", Script[marker + 1]);

        var names = Script.Skip(marker + 1)
            .TakeWhile(l => !l.StartsWith("done", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .Where(l => l.EndsWith(";;", StringComparison.Ordinal))
            .Select(l => l[..l.IndexOf(')')])
            .ToList();

        Assert.Equal(EntrypointPrepare.FactNames, names);
        // Each one assigns the variable of its own name, so a renamed fact cannot be read as another.
        foreach (var name in names)
            Assert.Contains(Script, l => l.Trim() == $"{name}) {name}=\"$_FACT_VALUE\" ;;");
    }

    [Fact]
    public void Script_BranchesOnExactlyTheKnownProviders()
    {
        var compared = ScriptCode
            .SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, "\"\\$PROVIDER\" !?= \"([^\"]*)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(AgentProviders.All.ToHashSet(StringComparer.Ordinal), compared);
    }

    [Fact]
    public void Script_HoldsNoCopyOfWhatTheBinaryOwns()
    {
        var code = string.Join('\n', ScriptCode);

        Assert.DoesNotMatch(@"(^|[^A-Za-z_])(node|python3?) ", code);
        Assert.DoesNotContain("appsettings", code);
        Assert.DoesNotContain(SecretPlaceholders.Prefix, code);
        Assert.DoesNotContain(HostedModelProviders.KeyFilePath, code);
        Assert.DoesNotContain(HostedModelProviders.MissingSecretPlaceholder, code);
        Assert.DoesNotContain(CodexConfigToml.McpToolPrefix, code);
        Assert.DoesNotContain("mcp_servers", code);
        Assert.DoesNotContain("mcpServers", code);
    }

    [Fact]
    public void Script_RemovesTheReservedNamesBeforeItStartsAnything()
    {
        // The binary's one-shot mode is a child process like any other: it must be started only
        // after the reserved names have left the environment, and it is the first thing started.
        var marker = Array.IndexOf(Script, "# phleet:reserved-key-names");
        var unset = Array.FindIndex(Script, marker, l => l.Trim() == "unset \"$_HOSTED_VAR\"");
        var loopEnd = Array.FindIndex(Script, marker, l => l == "done");
        Assert.True(marker >= 0 && unset > marker && unset < loopEnd, "the reserved-name loop no longer unsets each name");

        var code = Script.Select((line, index) => (line, index)).Where(l => !l.line.TrimStart().StartsWith('#')).ToList();
        var firstCall = code.First(l => l.line.Contains("fleet_prepare ") && !l.line.StartsWith("fleet_prepare()", StringComparison.Ordinal)).index;
        Assert.True(firstCall > loopEnd, "fleet_prepare is called before the reserved names are unset");

        // Nothing between the top of the script and that loop's end starts a process: no command
        // substitution, no pipe, no external command.
        var before = code.Where(l => l.index < loopEnd).Select(l => l.line.Trim()).Where(l => l.Length > 0).ToList();
        Assert.All(before, line => Assert.True(
            line == "#!/bin/bash" || line == "set -u" || line.StartsWith("fleet_prepare() {", StringComparison.Ordinal)
            || line == "_RESERVED_NAMES=\"\"" || line.StartsWith("for _HOSTED_VAR in ", StringComparison.Ordinal)
            || line.StartsWith("printf -v ", StringComparison.Ordinal) || line == "unset \"$_HOSTED_VAR\""
            || line.StartsWith("_RESERVED_NAMES=\"${_RESERVED_NAMES}", StringComparison.Ordinal),
            $"unexpected line before the reserved names are gone: {line}"));

        // And the binary is started in exactly two ways: through fleet_prepare, and as PID 1.
        Assert.Equal(
            ["fleet_prepare() { dotnet Fleet.Agent.dll prepare \"$@\"; }", "exec dotnet Fleet.Agent.dll"],
            ScriptCode.Where(l => l.Contains("dotnet ")).Select(l => l.Trim()));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record PrepareRun(int ExitCode, string Output, string Error)
    {
        public IReadOnlyList<string> OutputLines => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private PrepareRun Prepare(string verb)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = EntrypointPrepare.Run([verb, EntrypointPrepare.RootOption, _root], stdout, stderr, name => _environment.GetValueOrDefault(name));
        return new PrepareRun(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// The configuration's own environment source reads the process, so a test of it has to set the
    /// process variable. The name is this test's alone and is cleared again.
    /// </summary>
    private PrepareRun PrepareWithProcessEnvironment(string name, string value, string verb)
    {
        Environment.SetEnvironmentVariable(name, value);
        try { return Prepare(verb); }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    private void Appsettings(string json) => Write("app/appsettings.json", json);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative));
}
