using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC11 and Constraint 2: the reserved band must be POSITIVE and above every possible Telegram
/// chat id.
/// </summary>
public class ConversationRegistryTests
{
    [Fact]
    public void ReservedBand_IsPositive()
    {
        // This is the whole reason the band is not at long.MinValue. Five live sites treat a
        // negative runtime key as "this is a group": the channel anchor renders `group` instead of
        // `dm`, and three `SuppressToolMessages && chatId < 0` checks suppress queue notices. A
        // negative band would have silently changed behaviour on paths nobody edited.
        Assert.True(ConversationRegistry.ReservedBandStart > 0);
        Assert.True(ConversationRegistry.ReservedBandEnd > ConversationRegistry.ReservedBandStart);
    }

    [Fact]
    public void ReservedBand_IsTheDocumentedRange()
    {
        Assert.Equal(1L << 56, ConversationRegistry.ReservedBandStart);
        Assert.Equal((1L << 56) + (1L << 32), ConversationRegistry.ReservedBandEnd);
    }

    [Theory]
    // Telegram chat identifiers fit in 52 significant bits, so 2^56 is unreachable from below.
    [InlineData(123456789L)]              // a DM (positive user id)
    [InlineData(4503599627370495L)]       // 2^52 - 1, the largest possible positive id
    [InlineData(-1001234567890L)]         // a -100… supergroup
    [InlineData(-4503599627370495L)]      // the largest-magnitude negative id
    [InlineData(0L)]                      // headless workflow delegation
    public void TelegramShapedIds_CannotCollideWithTheReservedBand(long telegramId)
    {
        Assert.False(ConversationRegistry.IsReservedKey(telegramId));
    }

    [Fact]
    public void AllocatedKeys_FallInsideTheBand()
    {
        var registry = new ConversationRegistry();

        for (var i = 0; i < 5; i++)
        {
            var key = registry.Resolve(new ConversationRef("example-adapter", $"c_{i}", "p_1"));
            Assert.True(ConversationRegistry.IsReservedKey(key), $"key {key} is outside the reserved band");
            Assert.True(key > 0);
        }
    }

    [Fact]
    public void Resolve_IsIdempotentForTheSameConversation()
    {
        var registry = new ConversationRegistry();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");

        Assert.Equal(registry.Resolve(reference), registry.Resolve(reference));
    }

    [Fact]
    public void Resolve_AllocatesDistinctKeysForDistinctConversations()
    {
        var registry = new ConversationRegistry();

        var a = registry.Resolve(new ConversationRef("example-adapter", "c_1", "p_1"));
        var b = registry.Resolve(new ConversationRef("example-adapter", "c_2", "p_1"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TelegramConversations_RegisterTheirRealIdVerbatim()
    {
        var registry = new ConversationRegistry();

        // Existing keys must be untouched — the whole runtime already keys on them.
        var key = registry.Resolve(new ConversationRef(ChannelIds.Telegram, "-1001234567890", "p_1"));

        Assert.Equal(-1001234567890L, key);
        Assert.False(ConversationRegistry.IsReservedKey(key));
    }

    [Fact]
    public void Lookup_ReverseResolvesAnAllocatedKey()
    {
        var registry = new ConversationRegistry();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);

        Assert.Equal(reference, registry.Lookup(key));
    }

    [Fact]
    public void Lookup_ReturnsNullForAnUnregisteredKey() =>
        Assert.Null(new ConversationRegistry().Lookup(987654321L));

    [Fact]
    public void Unregister_RemovesBothDirections()
    {
        var registry = new ConversationRegistry();
        var reference = new ConversationRef("example-adapter", "c_1", "p_1");
        var key = registry.Resolve(reference);

        Assert.True(registry.Unregister(key));
        Assert.Null(registry.Lookup(key));

        // A re-open allocates a FRESH key rather than resurrecting the old mapping.
        Assert.NotEqual(key, registry.Resolve(reference));
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("relay")]
    public void RuntimeOwnedChannels_AreRecognised(string channelId) =>
        Assert.True(ChannelIds.IsRuntimeOwned(channelId));

    [Fact]
    public void ClientChannels_AreNotRuntimeOwned() =>
        Assert.False(ChannelIds.IsRuntimeOwned("example-adapter"));
}
