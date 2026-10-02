using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class PrimaryHumanProvisioningTests
{
    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", true)]
    [InlineData("claude", false)]
    public void SetKey_IsWrittenOnlyForBotAgents(string provider, bool bot)
    {
        var agent = new Agent { Name = "agent1", DisplayName = "Agent1", Role = "test", ContainerName = "fleet-agent1", Provider = provider, Model = "model", WorkDir = "/workspace" };
        if (bot) agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" });
        var json = JsonNode.Parse(ContainerProvisioningService.GenerateAppsettingsJson(agent, "", primaryHumanUserId: "42"))!;
        Assert.Equal(bot, json["Telegram"]!.AsObject().ContainsKey("PrimaryHumanUserId"));
        if (bot) Assert.Equal(42, json["Telegram"]!["PrimaryHumanUserId"]!.GetValue<long>());
        foreach (var blank in new string?[] { null, "", " " })
            Assert.False(JsonNode.Parse(ContainerProvisioningService.GenerateAppsettingsJson(agent, "", primaryHumanUserId: blank))!["Telegram"]!.AsObject().ContainsKey("PrimaryHumanUserId"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    [InlineData("garbage")]
    public async Task InvalidKey_RefusesBeforeDeprovisioning(string value)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, $"FLEET_PRIMARY_HUMAN_USER_ID={value}\n");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Provisioning:EnvFilePath"] = path }).Build();
            // Null collaborators would fail if either DB access or deprovisioning were reached.
            var service = new ContainerProvisioningService(null!, null!, config, null!, NullLogger<ContainerProvisioningService>.Instance);
            var result = await service.ReprovisionAsync("agent1");
            Assert.False(result.Success);
            Assert.Equal("primary_human_invalid", result.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SetupAndExampleCarryTheOptionalKey()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        Assert.Contains("FLEET_PRIMARY_HUMAN_USER_ID=", File.ReadAllText(Path.Combine(dir.FullName, ".env.example")));
        Assert.Contains("FLEET_PRIMARY_HUMAN_USER_ID", File.ReadAllText(Path.Combine(dir.FullName, "setup.sh")));
    }
}
