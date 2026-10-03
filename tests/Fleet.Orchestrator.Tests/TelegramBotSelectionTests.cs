using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using System.Reflection;
namespace Fleet.Orchestrator.Tests;
public sealed class TelegramBotSelectionTests
{
    [Fact]
    public void SelectedBot_IsTheSameOneInjectedByBuildEnv()
    {
        var agent = JournalGrantsTests.NewAgent();
        agent.EnvRefs = [new() { EnvKeyName = "TELEGRAM_Z_BOT_TOKEN" }, new() { EnvKeyName = "TELEGRAM_A_BOT_TOKEN" }];
        var env = new Dictionary<string, string> { ["TELEGRAM_A_BOT_TOKEN"] = "first", ["TELEGRAM_Z_BOT_TOKEN"] = "last" };
        var method = typeof(ContainerProvisioningService).GetMethod("BuildEnv", BindingFlags.Static | BindingFlags.NonPublic)!;
        var values = (List<string>)method.Invoke(null, [agent, env, "", ""])!;
        Assert.Equal("last", JournalGrants.ResolveBotToken(agent, env));
        Assert.Equal("Telegram__BotToken=last", Assert.Single(values, v => v.StartsWith("Telegram__BotToken=")));
        env.Remove("TELEGRAM_Z_BOT_TOKEN");
        Assert.Null(JournalGrants.ResolveBotToken(agent, env)); // never silently switch bots
    }
}
