using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class ReplyLookupStateTests
{
    [Theory]
    [InlineData(false, false, false, false, "journal_capture_off")]
    [InlineData(true, false, false, false, "provider_headers_unsupported")]
    [InlineData(true, true, false, true, "tool_not_granted")]
    [InlineData(true, true, true, false, "tool_not_granted")]
    [InlineData(true, true, true, true, "available")]
    public void Generate_BotAgent_UsesPinnedPrecedence(bool capture, bool supported, bool endpoint, bool grant, string expected)
    {
        var agent = Build(supported ? "claude" : "gemini", bot: true);
        agent.JournalEnabled = capture;
        if (endpoint) agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-comms-journal", TransportType = "http", Url = "http://journal.test/journal/v1/mcp" });
        if (grant) agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-comms-journal__get_message", IsEnabled = true });
        var journal = new JournalProvisioning(capture ? "ingest" : null, [], null);
        var generated = JsonNode.Parse(ContainerProvisioningService.GenerateAppsettingsJson(agent, "", journal: journal))!;
        Assert.Equal(expected, generated["Agent"]!["ReplyLookup"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void Generate_BotWithoutIngestToken_CaptureIsOff(string provider)
    {
        var agent = Build(provider, bot: true);
        agent.JournalEnabled = true;
        var generated = JsonNode.Parse(ContainerProvisioningService.GenerateAppsettingsJson(agent, ""))!;
        Assert.Equal("journal_capture_off", generated["Agent"]!["ReplyLookup"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void Generate_BlankPriorityKey_DiffersOnlyByReplyLookupForBot(string provider)
    {
        var headless = Build(provider, bot: false);
        var bot = Build(provider, bot: true);
        var expected = JsonNode.Parse(ContainerProvisioningService.GenerateAppsettingsJson(headless, ""))!;
        var generated = JsonNode.Parse(ContainerProvisioningService.GenerateAppsettingsJson(bot, ""))!;
        Assert.False(expected["Agent"]!.AsObject().ContainsKey("ReplyLookup"));
        Assert.True(generated["Agent"]!.AsObject().Remove("ReplyLookup"));
        Assert.True(JsonNode.DeepEquals(expected, generated));
        Assert.False(generated["Telegram"]!.AsObject().ContainsKey("PrimaryHumanUserId"));
    }

    [Fact]
    public void Generate_MixedCaseGrantedTool_IsAvailable()
    {
        var agent = Build("claude", true);
        agent.JournalEnabled = true;
        agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-comms-journal", TransportType = "http", Url = "http://journal.test" });
        agent.Tools.Add(new AgentTool { ToolName = "MCP__FLEET-COMMS-JOURNAL__GET_MESSAGE", IsEnabled = true });
        Assert.Equal("available", ContainerProvisioningService.ReplyLookupState(agent, new JournalProvisioning("ingest", [], null)));
    }

    private static Agent Build(string provider, bool bot)
    {
        var agent = new Agent { Name = "agent1", DisplayName = "Agent1", ContainerName = "fleet-agent1", Role = "role", Model = "model", Provider = provider, WorkDir = "/workspace" };
        if (bot) agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" });
        return agent;
    }
}
