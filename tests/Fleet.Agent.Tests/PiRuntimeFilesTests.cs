using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="PiRuntimeFiles"/> (#386 §4.2): the three generated files against golden bytes (A9),
/// the argument list (A8) and the generator's refusal of a key Pi would resolve (A32).
/// </summary>
/// <remarks>
/// The goldens are kept here, byte for byte, rather than under <c>Fixtures/</c>: <c>fleet-pi.json</c>
/// carries the container path <c>/workspace/.mcp.json</c>, which the fixture hygiene sweep (F1)
/// rightly refuses in fixture files.
/// </remarks>
public sealed class PiRuntimeFilesTests
{
    private const string BaseUrl = "http://inference-host:8000";
    private const string Tag = "qwen3.8-flash-next";

    private const string ModelsGolden = """
        {
          "providers": {
            "fleet-local": {
              "baseUrl": "http://inference-host:8000",
              "api": "anthropic-messages",
              "apiKey": "phleet-local-no-auth",
              "compat": {
                "supportsStrictTools": false,
                "supportsEagerToolInputStreaming": false
              },
              "models": [
                {
                  "id": "qwen3.8-flash-next",
                  "name": "qwen3.8-flash-next",
                  "reasoning": true,
                  "input": [
                    "text"
                  ],
                  "contextWindow": 131072,
                  "maxTokens": 16384,
                  "cost": {
                    "input": 0,
                    "output": 0,
                    "cacheRead": 0,
                    "cacheWrite": 0
                  }
                }
              ]
            }
          }
        }

        """;

    private const string SettingsGolden = """
        {
          "compaction": {
            "enabled": true,
            "reserveTokens": 16384,
            "keepRecentTokens": 8192
          },
          "retry": {
            "enabled": true,
            "maxRetries": 3
          },
          "steeringMode": "all",
          "cacheWarming": "off",
          "enableInstallTelemetry": false,
          "enableAnalytics": false
        }

        """;

    private const string ExtensionConfigGolden = """
        {
          "mcpConfigPath": "/workspace/.mcp.json",
          "allowedTools": [
            "mcp__fleet-memory__memory_get",
            "mcp__fleet-temporal__notify_cto"
          ],
          "toolResultMaxBytes": 49152,
          "toolCallTimeoutSeconds": 300,
          "summaryMaxTokens": 1280
        }

        """;

    private static readonly string[] AllowedTools =
    [
        "Read", "Bash", "mcp__fleet-memory__memory_get", "mcp__fleet-temporal__notify_cto", "mcp__fleet-memory__memory_get",
    ];

    // ── A9 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A9_ModelsJson_EqualsItsGolden() =>
        Assert.Equal(ModelsGolden, PiRuntimeFiles.BuildModelsJson(BaseUrl, Tag, 131072));

    [Fact]
    public void A3_ImageInputOff_IsTheGoldenByteForByte() =>
        Assert.Equal(ModelsGolden, PiRuntimeFiles.BuildModelsJson(BaseUrl, Tag, 131072, imageInput: false));

    [Fact]
    public void A3_ImageInputOn_ChangesOnlyTheInputArray()
    {
        var on = PiRuntimeFiles.BuildModelsJson(BaseUrl, Tag, 131072, imageInput: true);

        var expected = ModelsGolden.Replace(
            "\"input\": [\n            \"text\"\n          ],",
            "\"input\": [\n            \"text\",\n            \"image\"\n          ],");
        Assert.NotEqual(ModelsGolden, expected);
        Assert.Equal(expected, on);
    }

    [Fact]
    public void A3_WriteAll_WritesTheImageInput_AndLeavesTheOtherFilesUnchanged()
    {
        using var temp = new PiTempDirectory();
        PiRuntimeFiles.WriteAll(temp.Path, BaseUrl, Tag, 131072, AllowedTools, imageInput: true);

        Assert.Equal(PiRuntimeFiles.BuildModelsJson(BaseUrl, Tag, 131072, imageInput: true),
            File.ReadAllText(Path.Combine(temp.Path, "models.json")));
        Assert.Equal(PiRuntimeFiles.BuildSettingsJson(), File.ReadAllText(Path.Combine(temp.Path, "settings.json")));
        Assert.Equal(PiRuntimeFiles.BuildExtensionConfigJson(AllowedTools), File.ReadAllText(Path.Combine(temp.Path, "fleet-pi.json")));
    }

    [Fact]
    public void A9_SettingsJson_EqualsItsGolden() =>
        Assert.Equal(SettingsGolden, PiRuntimeFiles.BuildSettingsJson());

    [Fact]
    public void A9_FleetPiJson_EqualsItsGolden_WithOnlyTheMcpGrantsOnce() =>
        Assert.Equal(ExtensionConfigGolden, PiRuntimeFiles.BuildExtensionConfigJson(AllowedTools));

    [Fact]
    public void A9_TheGoldensAreLfOnlyWithOneTrailingNewline()
    {
        foreach (var golden in new[] { ModelsGolden, SettingsGolden, ExtensionConfigGolden })
        {
            Assert.DoesNotContain('\r', golden);
            Assert.EndsWith("}\n", golden);
            Assert.False(golden.EndsWith("\n\n", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A9_TheFilesCarryTheSharedConstants()
    {
        Assert.Contains($"\"reserveTokens\": {PiLocalModel.ReserveTokens}", SettingsGolden);
        Assert.Contains($"\"keepRecentTokens\": {PiLocalModel.KeepRecentTokens}", SettingsGolden);
        Assert.Contains($"\"toolResultMaxBytes\": {PiLocalModel.ToolResultMaxBytes}", ExtensionConfigGolden);
        Assert.Contains($"\"toolCallTimeoutSeconds\": {PiLocalModel.ToolCallTimeoutSeconds}", ExtensionConfigGolden);
        Assert.Contains($"\"summaryMaxTokens\": {PiLocalModel.SummaryMaxTokens}", ExtensionConfigGolden);
        Assert.Contains($"\"apiKey\": \"{ClaudeLocalModel.PlaceholderAuthToken}\"", ModelsGolden);
    }

    [Fact]
    public void A9_WriteAll_WritesTheThreeGoldensAndOverwritesAnEdit()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pi-files-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"cacheWarming\":\"streaming\"}");

            PiRuntimeFiles.WriteAll(dir, BaseUrl, Tag, 131072, AllowedTools);

            Assert.Equal(ModelsGolden, File.ReadAllText(Path.Combine(dir, "models.json")));
            Assert.Equal(SettingsGolden, File.ReadAllText(Path.Combine(dir, "settings.json")));
            Assert.Equal(ExtensionConfigGolden, File.ReadAllText(Path.Combine(dir, "fleet-pi.json")));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.NotEqual(0xEF, File.ReadAllBytes(Path.Combine(dir, "models.json"))[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A9_AnUnwritableAgentDir_NamesTheFileNeverItsContent()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pi-not-a-dir-{Guid.NewGuid():N}");
        File.WriteAllText(file, "");
        try
        {
            var ex = Assert.Throws<PiRuntimeFileWriteException>(
                () => PiRuntimeFiles.WriteAll(file, BaseUrl, Tag, 131072, AllowedTools));

            Assert.Equal("models.json", ex.FileName);
            Assert.DoesNotContain(BaseUrl, ex.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ── A8 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A8_Arguments_NewSession()
    {
        Assert.Equal(
        [
            "--mode", "rpc", "--offline", "--no-approve",
            "--provider", "fleet-local", "--model", Tag,
            "--thinking", "medium",
            "--system-prompt", "/workspace/system-prompt.md",
            "-nc", "-ns", "-np", "-ne", "--no-mcp",
            "-e", "/opt/fleet-pi-extension",
            "--tools", "read,bash,edit,write,mcp__fleet-memory__memory_get,mcp__fleet-temporal__notify_cto",
        ], PiRuntimeFiles.BuildArguments(Tag, "", "/workspace/system-prompt.md", AllowedTools, sessionFile: null));
    }

    [Fact]
    public void A8_Arguments_ResumeWithTheAbsoluteSessionPath_AndThinkingOff()
    {
        var args = PiRuntimeFiles.BuildArguments(
            Tag, "off", "/workspace/system-prompt.md", ["mcp__fleet-memory__memory_get"],
            sessionFile: "/root/.pi/agent/sessions/--workspace--/s-1.jsonl");

        Assert.Equal("off", args[args.ToList().IndexOf("--thinking") + 1]);
        Assert.Equal(["--session", "/root/.pi/agent/sessions/--workspace--/s-1.jsonl"], args.TakeLast(2));
        Assert.Equal("read,bash,edit,write,mcp__fleet-memory__memory_get", args[args.ToList().IndexOf("--tools") + 1]);
    }

    [Fact]
    public void A8_McpNamesAreNeverRewritten_HyphensAndLongNamesStayExact()
    {
        var longName = "mcp__fleet-temporal__" + new string('x', 60);
        var args = PiRuntimeFiles.BuildArguments(Tag, "on", "/p", ["mcp__fleet-memory__memory_get", longName, "Bash"], null);

        Assert.Equal($"read,bash,edit,write,mcp__fleet-memory__memory_get,{longName}",
            args[args.ToList().IndexOf("--tools") + 1]);
    }

    [Fact]
    public void A8_NoGrantedMcpName_ListsOnlyTheBuiltIns()
    {
        var args = PiRuntimeFiles.BuildArguments(Tag, "", "/p", ["Read", "Bash"], null);

        Assert.Equal("read,bash,edit,write", args[args.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("""
            {
              "mcpConfigPath": "/workspace/.mcp.json",
              "allowedTools": [],
              "toolResultMaxBytes": 49152,
              "toolCallTimeoutSeconds": 300,
              "summaryMaxTokens": 1280
            }

            """, PiRuntimeFiles.BuildExtensionConfigJson(["Read", "Bash"]));
    }

    [Fact]
    public void A8_ChildEnvironment_DropsTheDenylistKeepsGitAndAddsThePiNames()
    {
        var environment = new Dictionary<string, string?>
        {
            ["ANTHROPIC_BASE_URL"] = "x",
            ["AWS_WEB_IDENTITY_TOKEN_FILE"] = "x",
            ["GOOGLE_CLOUD_LOCATION"] = "x",
            ["GCLOUD_PROJECT"] = "x",
            ["ZAI_CODING_PLAN_API_KEY"] = "x",
            ["GH_TOKEN"] = "gh",
            ["GITHUB_APP_ID"] = "1",
            ["GIT_SSH_COMMAND"] = "ssh",
            ["HOME"] = "/root",
            ["PI_TELEMETRY"] = "1",
        };

        PiRuntimeFiles.ApplyChildEnvironment(environment, "/tmp/agent");

        Assert.Equal(
            ["GH_TOKEN", "GITHUB_APP_ID", "GIT_SSH_COMMAND", "HOME", "PI_CODING_AGENT_DIR", "PI_OFFLINE", "PI_TELEMETRY"],
            environment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("0", environment["PI_TELEMETRY"]);
        Assert.Equal("1", environment["PI_OFFLINE"]);
        Assert.Equal("/tmp/agent", environment["PI_CODING_AGENT_DIR"]);
    }

    // ── A32 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("!cat /run/secret")]
    [InlineData("$ANTHROPIC_API_KEY")]
    [InlineData("sk-ant-oat01-synthetic")]
    [InlineData("prefix-sk-ant-oat-suffix")]
    [InlineData("")]
    public void A32_TheGeneratorRejectsAKeyPiWouldResolve(string apiKey)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PiRuntimeFiles.BuildModelsJson(BaseUrl, Tag, 131072, apiKey));

        if (apiKey.Length > 0)
            Assert.DoesNotContain(apiKey, ex.Message);
    }

    [Fact]
    public void A32_ThePlaceholderIsAccepted() =>
        Assert.Null(PiRuntimeFiles.DescribeApiKeyFault(ClaudeLocalModel.PlaceholderAuthToken));

    // ── A32 (amended): Pi's own empty auth.json is the only credential-named file ──

    [Fact]
    public void A32_WriteAll_RemovesAuthAndMcpAuthBeforeEveryStart()
    {
        var dir = Directory.CreateTempSubdirectory("pi-auth-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, PiRuntimeFiles.AuthFileName), "{\"anthropic\":{\"key\":\"marker-value\"}}");
            File.WriteAllText(Path.Combine(dir, PiRuntimeFiles.McpAuthFileName), "{}");

            PiRuntimeFiles.WriteAll(dir, "http://inference-host:8000", "qwen3.8-flash-next", 131072, []);

            Assert.False(File.Exists(Path.Combine(dir, PiRuntimeFiles.AuthFileName)));
            Assert.False(File.Exists(Path.Combine(dir, PiRuntimeFiles.McpAuthFileName)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("{}", null, null)]
    [InlineData("{}\n", null, "auth.json")]
    [InlineData("{\"anthropic\":{\"type\":\"api_key\",\"key\":\"marker-value\"}}", null, "auth.json")]
    [InlineData("{}", "{}", "mcp-auth.json")]
    [InlineData(null, "{}", "mcp-auth.json")]
    public void A32_OnlyPisOwnEmptyAuthFileIsAllowed(string? auth, string? mcpAuth, string? fault)
    {
        var dir = Directory.CreateTempSubdirectory("pi-auth-").FullName;
        try
        {
            if (auth is not null)
                File.WriteAllText(Path.Combine(dir, PiRuntimeFiles.AuthFileName), auth);
            if (mcpAuth is not null)
                File.WriteAllText(Path.Combine(dir, PiRuntimeFiles.McpAuthFileName), mcpAuth);

            Assert.Equal(fault, PiRuntimeFiles.DescribeCredentialFileFault(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A32_AnAuthDirectoryIsAFault()
    {
        var dir = Directory.CreateTempSubdirectory("pi-auth-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, PiRuntimeFiles.AuthFileName));
            Assert.Equal("auth.json", PiRuntimeFiles.DescribeCredentialFileFault(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
