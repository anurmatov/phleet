using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

public class ContainerProvisioningServiceTests
{
    // ── WithAgentParam ────────────────────────────────────────────────────────
    // Covers the idempotency invariant: any pre-existing query string is stripped
    // before ?agent= is appended, so re-provisioning an agent whose DB URL already
    // carries ?agent= (or any other query param) never produces a double-query URL.

    [Fact]
    public void WithAgentParam_NoQuery_AppendsAgent()
    {
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100", "myagent");

        Assert.Equal("http://fleet-memory:3100?agent=myagent", result);
    }

    [Fact]
    public void WithAgentParam_ExistingAgentQuery_ReplacesWithNewAgent()
    {
        // This is the actual bug: re-provisioning an agent whose DB URL already
        // had ?agent=old produced http://...?agent=old?agent=new (malformed).
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100?agent=old", "new");

        Assert.Equal("http://fleet-memory:3100?agent=new", result);
    }

    [Fact]
    public void WithAgentParam_TrailingSlashAndQuery_StripsSlashAndQuery()
    {
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100/?agent=old", "foo");

        Assert.Equal("http://fleet-memory:3100?agent=foo", result);
    }

    [Fact]
    public void WithAgentParam_PathSegmentsPreserved()
    {
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-temporal-bridge:3001/mcp", "adev");

        Assert.Equal("http://fleet-temporal-bridge:3001/mcp?agent=adev", result);
    }

    [Fact]
    public void WithAgentParam_UnrelatedQueryParams_AllDropped()
    {
        // WithAgentParam strips ALL query params, not just ?agent=.
        // Fleet-internal MCP URLs never carry other params — this is intentional.
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-telegram:3800/mcp?foo=bar&baz=qux", "myagent");

        Assert.Equal("http://fleet-telegram:3800/mcp?agent=myagent", result);
    }

    [Fact]
    public void WithAgentParam_IdempotentOnAlreadyCorrectUrl()
    {
        // Calling WithAgentParam twice (simulate two reprovisions) yields the same URL.
        var once = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100", "foo");
        var twice = ContainerProvisioningService.WithAgentParam(once, "foo");

        Assert.Equal(once, twice);
    }

    // ── NormalizeFleetMemoryMcpUrl: defensive strip ───────────────────────────
    // Ensures malformed FleetMemory:McpUrl values (from old config or misconfiguration)
    // are cleaned before being written to .mcp.json.

    [Theory]
    [InlineData("http://fleet-memory:3100/mcp",   "http://fleet-memory:3100")]  // canonical broken config
    [InlineData("http://fleet-memory:3100/mcp/",  "http://fleet-memory:3100")]  // trailing slash variant
    [InlineData("http://fleet-memory:3100/MCP",   "http://fleet-memory:3100")]  // case-insensitive
    [InlineData("http://fleet-memory:3100/",      "http://fleet-memory:3100")]  // root trailing slash
    [InlineData("http://fleet-memory:3100",       "http://fleet-memory:3100")]  // already correct — no-op
    public void NormalizeFleetMemoryMcpUrl_StripsMcpSuffix(string input, string expected)
    {
        var result = ContainerProvisioningService.NormalizeFleetMemoryMcpUrl(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("http://fleet-memory:3100/mcp/v1")]  // deeper path — left intact
    public void NormalizeFleetMemoryMcpUrl_PreservesNonTrailingMcpPath(string input)
    {
        var result = ContainerProvisioningService.NormalizeFleetMemoryMcpUrl(input);
        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void NormalizeFleetMemoryMcpUrl_InvalidInput_FallsBackToDefault(string? input)
    {
        var result = ContainerProvisioningService.NormalizeFleetMemoryMcpUrl(input);
        Assert.Equal("http://fleet-memory:3100", result);
    }

    // ── GenerateMcpJson: auto-inject transport and URL ────────────────────────
    // Agents with no explicit fleet-memory DB row must receive type=http (not sse)
    // and a URL with no /mcp segment in the auto-injected fleet-memory entry.

    private static (string type, string url) GetAutoInjectedFleetMemory(Agent agent, string mcpUrl)
    {
        var json = ContainerProvisioningService.GenerateMcpJson(agent, mcpUrl);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("fleet-memory");
        return (server.GetProperty("type").GetString()!, server.GetProperty("url").GetString()!);
    }

    [Fact]
    public void GenerateMcpJson_NoExplicitFleetMemoryRow_UsesHttpTransport()
    {
        // Agent with no explicit fleet-memory endpoint row → auto-inject must use type=http
        var agent = MinimalAgent("atester", "claude");
        var (type, _) = GetAutoInjectedFleetMemory(agent, "http://fleet-memory:3100");
        Assert.Equal("http", type);
    }

    [Fact]
    public void GenerateMcpJson_NoExplicitFleetMemoryRow_UrlHasNoMcpSegment()
    {
        // Auto-injected URL must not contain /mcp
        var agent = MinimalAgent("atester", "claude");
        var (_, url) = GetAutoInjectedFleetMemory(agent, "http://fleet-memory:3100");
        Assert.DoesNotContain("/mcp", url);
        Assert.Contains("?agent=atester", url);
    }

    [Fact]
    public void GenerateMcpJson_BrokenMcpUrlConfig_StillProducesCorrectUrl()
    {
        // Defensive strip: even if McpUrl in config still has /mcp, normalization fixes it.
        var agent = MinimalAgent("atester", "claude");
        var (type, url) = GetAutoInjectedFleetMemory(agent,
            ContainerProvisioningService.NormalizeFleetMemoryMcpUrl("http://fleet-memory:3100/mcp"));
        Assert.Equal("http", type);
        Assert.DoesNotContain("/mcp", url);
    }

    // ── GenerateAppsettingsJson: codex auto-grants ────────────────────────────
    // entrypoint.sh reads AllowedTools from appsettings.json to generate config.toml
    // enabled_tools for each MCP server. So the baseline grants (memory_get, notify_cto)
    // that GenerateSettingsJson injects for claude/gemini must also appear in AllowedTools
    // for codex agents — but ONLY for codex (other providers don't read AllowedTools this way).

    private static List<string> GetAllowedTools(Agent agent, string ctoAgentName = "acto")
    {
        var json = ContainerProvisioningService.GenerateAppsettingsJson(agent, ctoAgentName);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("Agent")
            .GetProperty("AllowedTools")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static Agent MinimalAgent(string name, string provider) => new()
    {
        Name = name,
        DisplayName = name,
        Role = "test",
        Model = "test-model",
        ContainerName = $"fleet-{name}",
        Provider = provider,
    };

    [Fact]
    public void GenerateAppsettingsJson_CodexAgent_AutoGrantsBothBaselineTools()
    {
        var agent = MinimalAgent("acanary", "codex");
        var tools = GetAllowedTools(agent, "acto");

        Assert.Contains("mcp__fleet-memory__memory_get", tools);
        Assert.Contains("mcp__fleet-temporal__notify_cto", tools);
    }

    [Fact]
    public void GenerateAppsettingsJson_CodexCtoAgent_GetsMemoryGetButNotNotifyCto()
    {
        // CTO self-loop guard: notify_cto is not granted to the CTO agent itself.
        var agent = MinimalAgent("acto", "codex");
        var tools = GetAllowedTools(agent, "acto");

        Assert.Contains("mcp__fleet-memory__memory_get", tools);
        Assert.DoesNotContain("mcp__fleet-temporal__notify_cto", tools);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("gemini")]
    public void GenerateAppsettingsJson_NonCodexAgent_NoAutoGrantsInAllowedTools(string provider)
    {
        // claude/gemini get their baseline grants via settings.json (GenerateSettingsJson).
        // AllowedTools in appsettings.json is not the right place for those providers.
        var agent = MinimalAgent("adev", provider);
        var tools = GetAllowedTools(agent, "acto");

        Assert.DoesNotContain("mcp__fleet-memory__memory_get", tools);
        Assert.DoesNotContain("mcp__fleet-temporal__notify_cto", tools);
    }

    [Fact]
    public void GenerateAppsettingsJson_CodexAgent_NoDeduplication_WhenToolAlreadyPresent()
    {
        // If the tool is already in the DB list, don't add it a second time.
        var agent = MinimalAgent("abot", "codex");
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-memory__memory_get", IsEnabled = true, AgentId = 0 });

        var tools = GetAllowedTools(agent, "acto");

        Assert.Single(tools, t => t == "mcp__fleet-memory__memory_get");
    }

    // ── #335: hosted-provider flags and the codex credential bind ─────────────

    private static (bool Hosted, JsonElement KeyEnv) GetHostedFlags(Agent agent)
    {
        var doc = JsonDocument.Parse(ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto"));
        var a = doc.RootElement.GetProperty("Agent");
        return (a.GetProperty("HostedProvider").GetBoolean(), a.GetProperty("HostedProviderKeyEnv").Clone());
    }

    [Theory]
    [InlineData("codex", "zai/glm-5.3", true, "ZAI_CODING_PLAN_API_KEY")]
    [InlineData("codex", "acme/x", false, null)]
    [InlineData("codex", "gpt-5.4", false, null)]
    [InlineData("codex", "ollama/gpt-oss:20b", false, null)]
    [InlineData("claude", "zai/glm-5.3", false, null)]
    [InlineData("gemini", "zai/glm-5.3", false, null)]
    public void GenerateAppsettingsJson_EmitsHostedProviderFlags(
        string provider, string model, bool expectedHosted, string? expectedKeyEnv)
    {
        var agent = MinimalAgent("ahosted", provider);
        agent.Model = model;

        var (hosted, keyEnv) = GetHostedFlags(agent);

        Assert.Equal(expectedHosted, hosted);
        if (expectedKeyEnv is null)
            Assert.Equal(JsonValueKind.Null, keyEnv.ValueKind);
        else
            Assert.Equal(expectedKeyEnv, keyEnv.GetString());
    }

    [Theory]
    [InlineData("zai/glm-5.3", false)]
    [InlineData("acme/x", true)]
    [InlineData("gpt-5.4", true)]
    public void BuildBinds_HostedCodexAgent_GetsNoCodexCredentialBind(string model, bool expectBind)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"prov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        try
        {
            File.WriteAllText(Path.Combine(baseDir, ".codex-credentials.json"), "{}");
            var agent = MinimalAgent("ahosted", "codex");
            agent.Model = model;

            var binds = ContainerProvisioningService.BuildBinds(agent, baseDir);

            Assert.Equal(expectBind, binds.Any(b => b.Contains(".codex-credentials.json", StringComparison.Ordinal)));
            // Everything else a codex agent needs is still mounted.
            Assert.Contains(binds, b => b.EndsWith(":/root/.codex", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    // ── BuildBinds: MountDockerSock flag ─────────────────────────────────────
    // Verifies the exact security invariant introduced by #186: docker.sock is
    // mounted only when the agent's MountDockerSock flag is true.

    [Fact]
    public void BuildBinds_MountDockerSockTrue_IncludesDockerSockBind()
    {
        var agent = MinimalAgent("adev", "claude");
        agent.MountDockerSock = true;

        var binds = ContainerProvisioningService.BuildBinds(agent, "/fake/base");

        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", binds);
    }

    [Fact]
    public void BuildBinds_MountDockerSockFalse_ExcludesDockerSockBind()
    {
        var agent = MinimalAgent("acanary", "claude");
        agent.MountDockerSock = false;

        var binds = ContainerProvisioningService.BuildBinds(agent, "/fake/base");

        Assert.DoesNotContain("/var/run/docker.sock:/var/run/docker.sock", binds);
    }

    // ── #340: Claude local model mode ────────────────────────────────────────

    private const string LocalUrl = "http://inference-host:11434";

    private static Agent LocalAgent(string? baseUrl = LocalUrl)
    {
        var agent = MinimalAgent("alocal", "claude");
        agent.Model = "qwen3.8:27b-agent";
        agent.AnthropicBaseUrl = baseUrl;
        return agent;
    }

    private static List<string> BindsWithAllHostCredentialFiles(Agent agent)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"prov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        try
        {
            foreach (var file in new[] { ".claude-credentials.json", ".codex-credentials.json", ".gemini-credentials.json" })
                File.WriteAllText(Path.Combine(baseDir, file), "{}");
            return ContainerProvisioningService.BuildBinds(agent, baseDir);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    // The binds main produced for MinimalAgent("agolden", provider) with every host credential file
    // present, captured from main at 9a2b6cc.
    private static string[] MainBinds(string credentialBind) =>
    [
        "./workspaces/fleet-agolden:/workspace",
        "./workspaces/fleet-agolden/.generated/projects:/app/projects:ro",
        "./workspaces/fleet-agolden/.generated/appsettings.json:/app/appsettings.json:ro",
        "./workspaces/fleet-agolden/claude:/root/.claude",
        "./workspaces/fleet-agolden/.generated/settings.json:/root/.claude/settings.json:ro",
        "./workspaces/fleet-agolden/codex:/root/.codex",
        "./workspaces/fleet-agolden/.generated/.mcp.json:/workspace/.mcp.json:ro",
        "./workspaces/fleet-agolden/.generated/roles:/app/roles:ro",
        credentialBind,
    ];

    [Fact]
    public void BuildBinds_LocalClaudeAgent_GetsNoClaudeCredentialBind_EvenWhenTheHostFileExists()
    {
        var binds = BindsWithAllHostCredentialFiles(LocalAgent());

        Assert.DoesNotContain(binds, b => b.Contains(".claude-credentials.json", StringComparison.Ordinal));
        Assert.DoesNotContain(binds, b => b.Contains("/root/.claude-host", StringComparison.Ordinal));
        // Everything else a claude agent needs is still mounted.
        Assert.Contains(binds, b => b.EndsWith(":/root/.claude", StringComparison.Ordinal));
        Assert.Contains(binds, b => b.EndsWith(":/root/.claude/settings.json:ro", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("claude", "./.claude-credentials.json:/root/.claude-host/.credentials.json:ro")]
    [InlineData("codex", "./.codex-credentials.json:/root/.codex-host/auth.json:ro")]
    [InlineData("gemini", "./.gemini-credentials.json:/root/.gemini/oauth_creds.json:rw")]
    public void BuildBinds_AgentsWithoutTheField_EqualMain(string provider, string credentialBind)
    {
        var binds = BindsWithAllHostCredentialFiles(MinimalAgent("agolden", provider));

        Assert.Equal(MainBinds(credentialBind), binds);
    }

    // GenerateAppsettingsJson for MinimalAgent("agolden", "claude") on main at 9a2b6cc, verbatim.
    private const string MainClaudeAppsettings = """
        {
          "Agent": {
            "Name": "agolden",
            "ContainerName": "fleet-agolden",
            "Role": "test",
            "Model": "test-model",
            "Provider": "claude",
            "Projects": [],
            "AllowedTools": [],
            "PermissionMode": "acceptEdits",
            "MaxTurns": 50,
            "WorkDir": "/workspace",
            "ProactiveIntervalMinutes": 0,
            "GroupListenMode": "mention",
            "GroupDebounceSeconds": 15,
            "ShortName": "",
            "ShowStats": true,
            "PrefixMessages": false,
            "FormattingMode": 0,
            "SuppressToolMessages": false,
            "Effort": null,
            "JsonSchema": null,
            "AgentsJson": null,
            "CodexSandboxMode": null,
            "HostedProvider": false,
            "HostedProviderKeyEnv": null,
            "InstructionOrder": []
          },
          "Telegram": {
            "AllowedUserIds": [],
            "AllowedGroupIds": [],
            "SendOnly": false,
            "CanReceiveChatRequests": false,
            "RequestReceivedMessage": null
          }
        }
        """;

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void GenerateAppsettingsJson_AgentsWithoutTheField_DifferFromMainOnlyByOneNullLine(string provider)
    {
        const string addedLine = "    \"AnthropicBaseUrl\": null,\n";
        var expectedMain = MainClaudeAppsettings.Replace("\"Provider\": \"claude\"", $"\"Provider\": \"{provider}\"");
        if (provider == "codex")
            expectedMain = expectedMain.Replace("\"AllowedTools\": [],",
                "\"AllowedTools\": [\n      \"mcp__fleet-memory__memory_get\",\n      \"mcp__fleet-temporal__notify_cto\"\n    ],");

        var json = ContainerProvisioningService.GenerateAppsettingsJson(MinimalAgent("agolden", provider), "acto")
            .ReplaceLineEndings("\n");

        Assert.Equal(1, json.Split(addedLine).Length - 1);
        Assert.Equal(expectedMain.ReplaceLineEndings("\n"), json.Replace(addedLine, ""));
    }

    [Theory]
    [InlineData("http://inference-host:11434", "http://inference-host:11434")]
    [InlineData("HTTP://Inference-Host:11434/", "http://inference-host:11434")]
    public void GenerateAppsettingsJson_LocalAgent_EmitsTheCanonicalBaseUrl(string stored, string emitted)
    {
        var doc = JsonDocument.Parse(ContainerProvisioningService.GenerateAppsettingsJson(LocalAgent(stored), "acto"));

        Assert.Equal(emitted, doc.RootElement.GetProperty("Agent").GetProperty("AnthropicBaseUrl").GetString());
    }

    [Fact]
    public void GenerateAppsettingsJson_LocalAgent_NeverCarriesThePlaceholderToken()
    {
        var json = ContainerProvisioningService.GenerateAppsettingsJson(LocalAgent(), "acto");

        Assert.DoesNotContain(Fleet.Shared.ClaudeLocalModel.PlaceholderAuthToken, json);
    }

    [Theory]
    // V1 — a hand-edited row: the field left behind on a provider change.
    [InlineData("codex", "qwen3.8:27b-agent", LocalUrl, null, "applies only to provider claude")]
    // V2, V3
    [InlineData("claude", "qwen3.8:27b-agent", "http://inference-host:11434/v1", null, "has a path")]
    [InlineData("claude", "qwen3.8:27b-agent", "  ", null, "has surrounding whitespace")]
    [InlineData("claude", "qwen3.8:27b-agent", "http://127.0.0.1:11434", null, "is the container")]
    // V4–V7
    [InlineData("claude", "qwen 27b", LocalUrl, null, "not allowed in a CLI argument")]
    [InlineData("claude", "sonnet", LocalUrl, null, "is a Claude model id")]
    [InlineData("claude", "lmstudio/qwen3", LocalUrl, null, "selects the codex path")]
    [InlineData("claude", "qwen3.8:27b-agent", LocalUrl, "high", "Effort is not supported")]
    public void GenerateAppsettingsJson_RefusesAnInvalidLocalConfig(
        string provider, string model, string baseUrl, string? effort, string expected)
    {
        var agent = MinimalAgent("alocal", provider);
        agent.Model = model;
        agent.AnthropicBaseUrl = baseUrl;
        agent.Effort = effort;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto"));

        Assert.Contains("alocal", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("/root/.claude/.credentials.json")]
    [InlineData("/root/.claude-host/.credentials.json")]
    [InlineData("/root/.claude.json")]
    public void GenerateAppsettingsJson_LocalAgent_RefusesAClaudeCredentialMount(string mountPath)
    {
        var agent = LocalAgent();
        agent.CredentialMounts.Add(new AgentCredentialMount
        {
            MountPath = mountPath,
            CredentialFile = new CredentialFile { Name = "c", FileName = "c", FilePath = "/tmp/c" },
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto"));

        Assert.Contains(mountPath, ex.Message);
    }

    [Fact]
    public void GenerateAppsettingsJson_LocalAgent_AllowsAnUnrelatedCredentialMount()
    {
        var agent = LocalAgent();
        agent.CredentialMounts.Add(new AgentCredentialMount
        {
            MountPath = "/workspace/.ssh/server.key",
            CredentialFile = new CredentialFile { Name = "k", FileName = "k", FilePath = "/tmp/k" },
        });

        ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto");
    }
}
