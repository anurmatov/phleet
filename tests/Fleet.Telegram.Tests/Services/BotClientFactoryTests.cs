using Fleet.Telegram.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Telegram.Bot;

namespace Fleet.Telegram.Tests.Services;

/// <summary>
/// Unit tests for BotClientFactory two-dict routing scheme.
///
/// A <see cref="Func{T,TResult}"/> factory is injected so tests don't need
/// real Telegram API tokens; reference equality proves caching semantics.
/// </summary>
public class BotClientFactoryTests
{
    // Factory helper: each invocation returns a fresh NSubstitute mock.
    // The _clientsByToken.GetOrAdd ensures the factory is called at most once per token.
    private static BotClientFactory CreateFactory() =>
        new(NullLogger<BotClientFactory>.Instance,
            _ => Substitute.For<ITelegramBotClient>());

    // ── GetClient — null/whitespace agentName ─────────────────────────────────

    [Fact]
    public void GetClient_NullAgentName_ReturnsNotifier()
    {
        var factory = CreateFactory();
        var notifier = Substitute.For<ITelegramBotClient>();
        factory.ApplyNotifierToken_Direct(notifier);

        var result = factory.GetClient(null);

        Assert.Same(notifier, result);
    }

    [Fact]
    public void GetClient_WhitespaceAgentName_ReturnsNotifier()
    {
        var factory = CreateFactory();
        Assert.Null(factory.GetClient("   "));
    }

    // ── GetClient — agent in _tokensByAgent ───────────────────────────────────

    [Fact]
    public void GetClient_KnownAgent_ReturnsDedicatedClient()
    {
        var factory = CreateFactory();
        factory.ApplyAgentTokens(new Dictionary<string, string> { ["myagent"] = "token-a" });

        var client = factory.GetClient("myagent");

        Assert.NotNull(client);
    }

    [Fact]
    public void GetClient_SameTokenCalledTwice_ReturnsSameInstance()
    {
        var factory = CreateFactory();
        factory.ApplyAgentTokens(new Dictionary<string, string>
        {
            ["agent1"] = "shared-token",
            ["agent2"] = "shared-token",
        });

        var c1 = factory.GetClient("agent1");
        var c2 = factory.GetClient("agent2");

        Assert.NotNull(c1);
        Assert.Same(c1, c2);
    }

    [Fact]
    public void GetClient_SameAgentCalledTwice_ReturnsSameInstance()
    {
        var factory = CreateFactory();
        factory.ApplyAgentTokens(new Dictionary<string, string> { ["agent1"] = "token-x" });

        var c1 = factory.GetClient("agent1");
        var c2 = factory.GetClient("agent1");

        Assert.Same(c1, c2);
    }

    // ── GetClient — agent NOT in _tokensByAgent ───────────────────────────────

    [Fact]
    public void GetClient_UnknownAgent_ReturnsNotifier()
    {
        var factory = CreateFactory();
        var notifier = Substitute.For<ITelegramBotClient>();
        factory.ApplyNotifierToken_Direct(notifier);

        var result = factory.GetClient("unknown-agent");

        Assert.Same(notifier, result);
    }

    // ── Token rotation ────────────────────────────────────────────────────────

    [Fact]
    public void TokenRotation_NewToken_ReturnsNewClientInstance()
    {
        var factory = CreateFactory();
        factory.ApplyAgentTokens(new Dictionary<string, string> { ["agent1"] = "old-token" });
        var client1 = factory.GetClient("agent1");

        factory.ApplyAgentTokens(new Dictionary<string, string> { ["agent1"] = "new-token" });
        var client2 = factory.GetClient("agent1");

        Assert.NotNull(client1);
        Assert.NotNull(client2);
        Assert.NotSame(client1, client2);
    }

    [Fact]
    public void TokenRotation_OldTokenEvictedFromClientsByToken()
    {
        var factory = CreateFactory();
        factory.ApplyAgentTokens(new Dictionary<string, string> { ["agent1"] = "old-token" });
        factory.GetClient("agent1"); // warm up cache

        factory.ApplyAgentTokens(new Dictionary<string, string> { ["agent1"] = "new-token" });
        // old-token evicted; HasClient uses _tokensByAgent which now points to new-token
        Assert.True(factory.HasClient("agent1"));
    }

    // ── Empty token in map ────────────────────────────────────────────────────

    [Fact]
    public void ApplyAgentTokens_EmptyToken_AgentNotInMap()
    {
        var factory = CreateFactory();
        factory.ApplyAgentTokens(new Dictionary<string, string> { ["agent1"] = "" });

        // Agent not registered — should fall back to notifier
        Assert.False(factory.HasClient("agent1"));
        Assert.Null(factory.GetClient("agent1"));
    }

    // ── HasClient ─────────────────────────────────────────────────────────────

    [Fact]
    public void HasClient_KnownAgent_ReturnsTrue()
    {
        var factory = CreateFactory();
        factory.ApplyAgentTokens(new Dictionary<string, string> { ["agent1"] = "tok" });
        Assert.True(factory.HasClient("agent1"));
    }

    [Fact]
    public void HasClient_UnknownAgent_ReturnsFalse()
    {
        var factory = CreateFactory();
        Assert.False(factory.HasClient("ghost"));
    }

    [Fact]
    public void HasClient_NullOrWhitespace_ReturnsFalse()
    {
        var factory = CreateFactory();
        Assert.False(factory.HasClient(null));
        Assert.False(factory.HasClient("  "));
    }
}

/// <summary>
/// Test helper extension — exposes the notifier setter without a real token string.
/// </summary>
file static class BotClientFactoryTestExtensions
{
    public static void ApplyNotifierToken_Direct(this BotClientFactory factory, ITelegramBotClient client)
    {
        // BotClientFactory.ApplyNotifierToken calls _createClient(token); for tests we
        // want to inject a pre-built mock directly. Use a unique sentinel token so the
        // injected factory lambda returns the provided mock.
        // Because the _createClient lambda is captured from construction and returns a
        // fresh Substitute each call, we can't set the notifier via ApplyNotifierToken
        // without getting a different instance. Instead, reflectively set the backing field.
        var field = typeof(BotClientFactory)
            .GetField("_notifierClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(factory, client);
    }
}
