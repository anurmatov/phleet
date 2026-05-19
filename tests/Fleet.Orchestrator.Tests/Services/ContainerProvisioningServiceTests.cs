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
            "http://fleet-memory:3100/mcp", "myagent");

        Assert.Equal("http://fleet-memory:3100/mcp?agent=myagent", result);
    }

    [Fact]
    public void WithAgentParam_ExistingAgentQuery_ReplacesWithNewAgent()
    {
        // This is the actual bug: re-provisioning an agent whose DB URL already
        // had ?agent=old produced http://...?agent=old?agent=new (malformed).
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100/mcp?agent=old", "new");

        Assert.Equal("http://fleet-memory:3100/mcp?agent=new", result);
    }

    [Fact]
    public void WithAgentParam_TrailingSlashAndQuery_StripsSlashAndQuery()
    {
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100/mcp/?agent=old", "foo");

        Assert.Equal("http://fleet-memory:3100/mcp?agent=foo", result);
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
            "http://fleet-memory:3100/mcp", "foo");
        var twice = ContainerProvisioningService.WithAgentParam(once, "foo");

        Assert.Equal(once, twice);
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
}
