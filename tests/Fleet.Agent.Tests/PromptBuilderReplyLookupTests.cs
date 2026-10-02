using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public sealed class PromptBuilderReplyLookupTests
{
    public static IEnumerable<object[]> Cases =>
        from provider in new[] { "claude", "codex", "gemini" }
        from state in new[] { "available", "journal_capture_off", "provider_headers_unsupported", "tool_not_granted" }
        select new object[] { provider, state };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Build_StateAndProvider_EqualsPinnedFixture(string provider, string state)
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reply-lookup-prompt", state + ".txt"));
        Assert.Equal(expected, Build(provider, state, bot: true));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void Build_LegacyBotWithoutField_ReportsToolNotGranted(string provider) =>
        Assert.Equal(Build(provider, "tool_not_granted", bot: true), Build(provider, null, bot: true));

    [Fact]
    public void Build_NoBotAndNoField_HasNoBlock() => Assert.Equal("", Build("claude", null, bot: false));

    private static string Build(string provider, string? state, bool bot)
    {
        var config = Options.Create(new AgentOptions
        {
            Name = "agent1", Role = "role", WorkDir = "/workspace", Provider = provider, ReplyLookup = state,
        });
        var telegram = Options.Create(new TelegramOptions { BotToken = bot ? "configured" : "" });
        return new PromptBuilder(config, NullLogger<PromptBuilder>.Instance, telegram)
        {
            ContentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        }.BuildSystemPrompt();
    }
}
