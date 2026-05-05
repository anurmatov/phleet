using Fleet.Telegram.Tools;

namespace Fleet.Agent.Tests;

public class SetReactionToolTests
{
    // ── known-good standard emoji ────────────────────────────────────────────

    [Fact]
    public void IsAllowedEmoji_ThumbsUp_ReturnsTrue()
        => Assert.True(SetReactionTool.IsAllowedEmoji("👍"));

    [Fact]
    public void IsAllowedEmoji_Heart_ReturnsTrue()
        => Assert.True(SetReactionTool.IsAllowedEmoji("❤"));

    [Fact]
    public void IsAllowedEmoji_Fire_ReturnsTrue()
        => Assert.True(SetReactionTool.IsAllowedEmoji("🔥"));

    // ── known-bad emoji ──────────────────────────────────────────────────────

    [Fact]
    public void IsAllowedEmoji_FreeformEmoji_ReturnsFalse()
        => Assert.False(SetReactionTool.IsAllowedEmoji("🦊")); // fox — not in Telegram standard list

    [Fact]
    public void IsAllowedEmoji_EmptyString_ReturnsFalse()
        => Assert.False(SetReactionTool.IsAllowedEmoji(""));

    [Fact]
    public void IsAllowedEmoji_ArbitraryText_ReturnsFalse()
        => Assert.False(SetReactionTool.IsAllowedEmoji("not_an_emoji"));
}
