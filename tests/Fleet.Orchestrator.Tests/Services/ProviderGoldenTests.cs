using Fleet.Orchestrator.Data;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// #386 A3: adding provider <c>pi</c> leaves every other agent's generated output byte for byte as
/// it was. Claude cloud (also with an empty provider), claude local, codex cloud, codex local,
/// hosted codex and gemini each go through <see cref="Fleet.Orchestrator.Services.ContainerProvisioningService.ProvisionAsync"/>,
/// and the generated <c>appsettings.json</c>, <c>settings.json</c>, <c>.mcp.json</c> (as
/// <c>mcp.json</c>), the container environment and the binds must equal the fixtures.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures under <c>Fixtures/ProviderGolden/&lt;config&gt;/</c> were produced by this exact
/// scenario on <c>main</c> 340b252, the merge base, before any #386 orchestrator change, and
/// committed on their own. They are never regenerated from the new code: that would compare the
/// change with itself.
/// </para>
/// <para>
/// The scenario sets every input a provider branch reads: host credential files for all three
/// providers, an operator credential mount outside <c>/root</c>, a Telegram bot ref, an output
/// style, a context window, an effort, tools and MCP endpoints. The binds are listed before path
/// expansion, with the temporary base directory written as <c>&lt;base&gt;</c>.
/// </para>
/// <para>
/// #455 A2: the same scenario with <c>PiImageInput</c> set true leaves every one of those configs
/// byte-identical and logs one "ignored" line. <c>pi-text</c> and <c>pi-image</c> are a pi agent
/// with the flag off and on; <c>pi-text/appsettings.json</c> was produced by this scenario at
/// 29d24eb (orchestrator source equal to <c>f10ccd8</c>) before the #455 provisioning change, and
/// <c>pi-image</c> differs from it only by <c>"PiImageInput": true</c>.
/// </para>
/// </remarks>
public class ProviderGoldenTests
{
    private const string Url = "http://inference-host:8000";
    private const string BotRef = "TELEGRAM_GOLDEN_BOT_TOKEN";

    internal const string AgentName = "agent-golden";
    internal const string ContainerName = "fleet-agent-golden";

    public static TheoryData<string> Configs =>
        ["claude-cloud", "claude-empty", "claude-local", "codex-cloud", "codex-local", "codex-hosted", "gemini"];

    internal static readonly string[] Files = ["appsettings.json", "settings.json", "mcp.json", "env.txt", "binds.txt"];

    internal static string FixtureDir(string config) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProviderGolden", config);

    private static (string Provider, string Model, string? Url, string? Effort) Shape(string config) => config switch
    {
        "claude-cloud" => ("claude", "claude-opus-5-5", null, "high"),
        "claude-empty" => ("", "claude-opus-5-5", null, "high"),
        "claude-local" => ("claude", "qwen3.8-flash-next", Url, "off"),
        "codex-cloud"  => ("codex", "gpt-5.5", null, "high"),
        "codex-local"  => ("codex", "ollama/qwen3.8-flash-next", Url, "low"),
        "codex-hosted" => ("codex", "zai/glm-5.3", null, "high"),
        "gemini"       => ("gemini", "gemini-2.5-pro", null, null),
        "pi-text" or "pi-image" => ("pi", "qwen3.8-flash-next", Url, "off"),
        _ => throw new ArgumentOutOfRangeException(nameof(config), config, null),
    };

    /// <summary>Provisions <paramref name="config"/> and returns each generated artifact by fixture file name.</summary>
    internal static async Task<Dictionary<string, string>> GenerateAsync(string config) =>
        (await GenerateWithLogsAsync(config, piImageInput: config == "pi-image")).Files;

    /// <summary>
    /// <see cref="GenerateAsync"/> with <c>PiImageInput</c> chosen by the caller (#455), also
    /// returning every line the provisioning service logged.
    /// </summary>
    internal static async Task<(Dictionary<string, string> Files, IReadOnlyList<(LogLevel Level, string Message)> Logs)>
        GenerateWithLogsAsync(string config, bool piImageInput)
    {
        var (provider, model, url, effort) = Shape(config);

        await using var h = ProvisioningHarness.Create();
        File.AppendAllText(Path.Combine(h.BaseDir, ".env"),
            $"{BotRef}=bot-token-placeholder\nZAI_CODING_PLAN_API_KEY=zai-key-placeholder\n");
        foreach (var file in new[] { ".claude-credentials.json", ".codex-credentials.json", ".gemini-credentials.json" })
            File.WriteAllText(Path.Combine(h.BaseDir, file), "{}");
        var credentialPath = Path.Combine(h.BaseDir, "credentials", "creds.json");
        Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
        File.WriteAllText(credentialPath, "{}");

        await h.SeedAsync(db =>
        {
            db.OutputStyles.Add(new OutputStyle
            {
                Name = "terse",
                Body = "---\nname: terse\ndescription: Short answers\n---\nAnswer in one paragraph.\n",
            });

            var instruction = new Instruction { Name = "base", CurrentVersion = 1 };
            instruction.Versions.Add(new InstructionVersion { VersionNumber = 1, Content = "# base\n" });
            db.Instructions.Add(instruction);

            var agent = new Agent
            {
                Name = AgentName,
                DisplayName = "Agent Golden",
                Role = "developer",
                Model = model,
                Provider = provider,
                ContainerName = ContainerName,
                MemoryLimitMb = 1024,
                ShortName = "golden",
                Effort = effort,
                LocalBaseUrl = url,
                ContextWindow = 131_072,
                OutputStyle = "terse",
                AutoMemoryEnabled = false,
                MountDockerSock = config == "codex-cloud",
                PiImageInput = piImageInput,
            };
            agent.Tools.Add(new AgentTool { ToolName = "Read", IsEnabled = true });
            agent.Tools.Add(new AgentTool { ToolName = "Bash", IsEnabled = true });
            agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-memory__memory_search", IsEnabled = true });
            agent.Tools.Add(new AgentTool { ToolName = "Write", IsEnabled = false });
            agent.Projects.Add(new AgentProject { ProjectName = "project-a" });
            agent.McpEndpoints.Add(new AgentMcpEndpoint
            {
                McpName = "fleet-telegram", Url = "http://fleet-telegram:3800/mcp", TransportType = "http",
            });
            agent.McpEndpoints.Add(new AgentMcpEndpoint
            {
                McpName = "fleet-temporal", Url = "http://fleet-temporal-bridge:3001/mcp", TransportType = "http",
            });
            agent.McpEndpoints.Add(new AgentMcpEndpoint
            {
                McpName = "example-tools", Url = "http://example-tools:9000/sse", TransportType = "sse",
            });
            agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = BotRef });
            if (config == "codex-hosted")
                agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "ZAI_CODING_PLAN_API_KEY" });
            agent.TelegramUsers.Add(new AgentTelegramUser { UserId = 1000001 });
            agent.TelegramGroups.Add(new AgentTelegramGroup { GroupId = -100000000001 });
            agent.Instructions.Add(new AgentInstruction { Instruction = instruction, LoadOrder = 0 });
            agent.CredentialMounts.Add(new AgentCredentialMount
            {
                MountPath = "/workspace/creds.json",
                CredentialFile = new CredentialFile { Name = "creds", FileName = "creds.json", FilePath = credentialPath },
            });
            db.Agents.Add(agent);
        });

        var preview = await h.Service.PreviewAsync(AgentName);
        var result = await h.Service.ProvisionAsync(AgentName);
        Assert.True(result.Success, result.Message);

        var dir = h.GeneratedDir(ContainerName);
        return (new Dictionary<string, string>
        {
            ["appsettings.json"] = await File.ReadAllTextAsync(Path.Combine(dir, "appsettings.json")),
            ["settings.json"] = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json")),
            ["mcp.json"] = await File.ReadAllTextAsync(Path.Combine(dir, ".mcp.json")),
            ["env.txt"] = string.Join('\n', preview.Desired.Env) + "\n",
            ["binds.txt"] = string.Join('\n', preview.Desired.Binds).Replace(h.BaseDir, "<base>") + "\n",
        }, h.Logs.Entries);
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public async Task GeneratedOutput_EqualsTheMergeBase(string config)
    {
        var generated = await GenerateAsync(config);

        Assert.Equal(Files.Order(), generated.Keys.Order());
        foreach (var file in Files)
        {
            var expected = await File.ReadAllTextAsync(Path.Combine(FixtureDir(config), file));
            Assert.True(expected == generated[file], $"{config}/{file} differs from the merge base:\n{generated[file]}");
        }
    }

    // ── #455 A2: PiImageInput ────────────────────────────────────────────────────────────────────

    private const string IgnoredLog = $"PiImageInput ignored for '{AgentName}': pi only";

    [Theory]
    [MemberData(nameof(Configs))]
    public async Task GeneratedOutput_WithPiImageInputOnANonPiAgent_EqualsTheMergeBase_AndLogsIgnoredOnce(string config)
    {
        var (generated, logs) = await GenerateWithLogsAsync(config, piImageInput: true);

        foreach (var file in Files)
        {
            var expected = await File.ReadAllTextAsync(Path.Combine(FixtureDir(config), file));
            Assert.True(expected == generated[file], $"{config}/{file} differs from the merge base with PiImageInput on:\n{generated[file]}");
        }
        Assert.Single(logs, e => e.Message == IgnoredLog);
        Assert.Equal(LogLevel.Information, logs.Single(e => e.Message == IgnoredLog).Level);
    }

    [Theory]
    [InlineData("pi-text")]
    [InlineData("pi-image")]
    public async Task PiAppsettings_EqualsItsFixture_AndLogsNoIgnoredLine(string config)
    {
        var (generated, logs) = await GenerateWithLogsAsync(config, piImageInput: config == "pi-image");

        var expected = await File.ReadAllTextAsync(Path.Combine(FixtureDir(config), "appsettings.json"));
        Assert.True(expected == generated["appsettings.json"], $"{config}/appsettings.json differs:\n{generated["appsettings.json"]}");
        Assert.DoesNotContain(logs, e => e.Message.StartsWith("PiImageInput ignored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PiImage_DiffersFromPiText_OnlyByPiImageInputTrue()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(FixtureDir("pi-text"), "appsettings.json"));
        var image = await File.ReadAllTextAsync(Path.Combine(FixtureDir("pi-image"), "appsettings.json"));
        Assert.Equal(text, image.Replace(",\n    \"PiImageInput\": true\n", "\n"));

        // Every other artifact is the same with the flag on or off.
        var off = (await GenerateWithLogsAsync("pi-text", piImageInput: false)).Files;
        var on = (await GenerateWithLogsAsync("pi-image", piImageInput: true)).Files;
        foreach (var file in Files.Where(f => f != "appsettings.json"))
            Assert.True(off[file] == on[file], $"pi {file} changes with PiImageInput:\n{on[file]}");
    }
}
