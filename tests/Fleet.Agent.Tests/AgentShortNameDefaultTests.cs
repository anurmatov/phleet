namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for the ShortName defaulting rule applied in POST /api/agents and PUT /api/agents/{name}/config.
/// Rule: if ShortName is null or whitespace, fall back to the agent Name verbatim (no transformation).
/// </summary>
public class AgentShortNameDefaultTests
{
    // Inline expression used in both POST /api/agents and PUT /api/agents/{name}/config:
    //   string.IsNullOrWhiteSpace(shortName) ? agentName : shortName.Trim()
    static string ApplyDefault(string? shortName, string agentName) =>
        string.IsNullOrWhiteSpace(shortName) ? agentName : shortName.Trim();

    [Theory]
    [InlineData(null,   "mybot", "mybot")]   // null → falls back to name
    [InlineData("",    "mybot", "mybot")]   // empty → falls back to name
    [InlineData("  ",  "mybot", "mybot")]   // whitespace-only → falls back to name
    [InlineData("bot", "mybot", "bot")]     // explicit value → used as-is
    [InlineData("  bot  ", "mybot", "bot")] // explicit value with surrounding spaces → trimmed
    public void ShortName_DefaultsToName_WhenNullOrWhitespace(string? shortName, string agentName, string expected)
    {
        Assert.Equal(expected, ApplyDefault(shortName, agentName));
    }

    [Fact]
    public void ShortName_PreservesCase_WhenExplicitlySet()
    {
        // No lower-casing or other transformation — what you type is what you get.
        Assert.Equal("MyBot", ApplyDefault("MyBot", "mybot"));
    }
}
