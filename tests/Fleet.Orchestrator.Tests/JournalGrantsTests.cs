using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using System.Text.Json;
namespace Fleet.Orchestrator.Tests;
public sealed class JournalGrantsTests
{
    [Fact]
    public void CanonicalComparison_MatchesDashboardFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests/fixtures/journal-send-grant-names.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tests/fixtures/journal-send-grant-names.json")));
        foreach (var row in cases.RootElement.EnumerateArray())
        {
            var agent = NewAgent();
            agent.Tools = [new AgentTool { ToolName = row.GetProperty("name").GetString()!, IsEnabled = row.GetProperty("enabled").GetBoolean() }];
            Assert.Equal(row.GetProperty("present").GetBoolean(), JournalGrants.HasSendGrant(agent));
        }
    }
    internal static Agent NewAgent() => new() { Name = "agent1", DisplayName = "Synthetic", Role = "test", Model = "test", ContainerName = "synthetic" };
    [Fact]
    public void EffectiveSwitch_RequiresCaptureBotAndGrant()
    {
        var agent = NewAgent();
        agent.JournalCrossChatEnabled = true;
        agent.Tools = [new AgentTool { ToolName = JournalGrants.SendGrant }];
        Assert.False(JournalGrants.CrossChatEffective(agent));
        agent.JournalEnabled = true;
        Assert.False(JournalGrants.CrossChatEffective(agent));
        agent.EnvRefs = [new AgentEnvRef { EnvKeyName = "TELEGRAM_SYNTHETIC_BOT_TOKEN" }];
        Assert.True(JournalGrants.CrossChatEffective(agent));
        agent.Tools[0].IsEnabled = false;
        Assert.False(JournalGrants.CrossChatEffective(agent));
    }
}
