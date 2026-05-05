using Fleet.Agent.Interfaces;
using Telegram.Bot.Types;

namespace Fleet.Agent.Tests;

public class DiffReactionsTests
{
    // ── custom emoji / paid reactions are silently skipped ─────────────────────

    [Fact]
    public void DiffReactions_CustomEmojiOnly_ReturnsBothEmpty()
    {
        var newR = new ReactionType[] { new ReactionTypeCustomEmoji { CustomEmojiId = "abc" } };
        var oldR = Array.Empty<ReactionType>();

        var (added, removed) = AgentTransport.DiffReactions(newR, oldR);

        Assert.Empty(added);
        Assert.Empty(removed);
    }

    // ── no change ───────────────────────────────────────────────────────────────

    [Fact]
    public void DiffReactions_SameEmoji_NoChange()
    {
        var emoji = new ReactionType[] { new ReactionTypeEmoji { Emoji = "👍" } };
        var (added, removed) = AgentTransport.DiffReactions(emoji, emoji);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    // ── added only ──────────────────────────────────────────────────────────────

    [Fact]
    public void DiffReactions_NewEmojiAdded_AppearsInAdded()
    {
        var newR = new ReactionType[] { new ReactionTypeEmoji { Emoji = "👍" } };
        var oldR = Array.Empty<ReactionType>();

        var (added, removed) = AgentTransport.DiffReactions(newR, oldR);

        Assert.Contains("👍", added);
        Assert.Empty(removed);
    }

    // ── removed only ────────────────────────────────────────────────────────────

    [Fact]
    public void DiffReactions_EmojiRemoved_AppearsInRemoved()
    {
        var newR = Array.Empty<ReactionType>();
        var oldR = new ReactionType[] { new ReactionTypeEmoji { Emoji = "❤" } };

        var (added, removed) = AgentTransport.DiffReactions(newR, oldR);

        Assert.Empty(added);
        Assert.Contains("❤", removed);
    }

    // ── added and removed simultaneously ────────────────────────────────────────

    [Fact]
    public void DiffReactions_SwappedEmoji_CorrectAddedAndRemoved()
    {
        var newR = new ReactionType[] { new ReactionTypeEmoji { Emoji = "🔥" } };
        var oldR = new ReactionType[] { new ReactionTypeEmoji { Emoji = "👍" } };

        var (added, removed) = AgentTransport.DiffReactions(newR, oldR);

        Assert.Contains("🔥", added);
        Assert.Contains("👍", removed);
    }

    // ── null inputs ─────────────────────────────────────────────────────────────

    [Fact]
    public void DiffReactions_NullInputs_ReturnsBothEmpty()
    {
        var (added, removed) = AgentTransport.DiffReactions(null, null);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    // ── mixed standard + custom ─────────────────────────────────────────────────

    [Fact]
    public void DiffReactions_MixedTypes_OnlyStandardEmojiCounted()
    {
        var newR = new ReactionType[]
        {
            new ReactionTypeEmoji { Emoji = "👍" },
            new ReactionTypeCustomEmoji { CustomEmojiId = "custom_1" },
        };
        var oldR = Array.Empty<ReactionType>();

        var (added, removed) = AgentTransport.DiffReactions(newR, oldR);

        Assert.Single(added);
        Assert.Contains("👍", added);
        Assert.Empty(removed);
    }
}
