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
}
