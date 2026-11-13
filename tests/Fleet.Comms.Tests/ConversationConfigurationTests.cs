using Fleet.Comms.Configuration;

namespace Fleet.Comms.Tests;

/// <summary>
/// The conversation feature is opt-in, and a declining install is unchanged.
/// </summary>
public sealed class ConversationConfigurationTests
{
    /// <summary>
    /// With no connection string the feature is off, and being off is the DEFAULT rather than
    /// something an operator has to ask for.
    /// </summary>
    [Fact]
    public void Conversations_are_disabled_until_a_connection_string_is_configured()
    {
        var options = new CommsOptions();

        Assert.False(options.ConversationsEnabled);
        Assert.Equal(string.Empty, options.ConversationConnectionString);
        Assert.Equal(string.Empty, options.ConversationMigrationConnectionString);
    }

    /// <summary>
    /// A disabled install validates without complaint. Requiring the south credential of an install
    /// that will never open a south listener would make the feature opt-out in practice.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validation_is_a_no_op_while_the_feature_is_disabled(string connectionString)
    {
        var options = new CommsOptions { ConversationConnectionString = connectionString };

        Assert.False(options.ConversationsEnabled);
        options.ValidateConversations();
    }

    /// <summary>
    /// Enabled without the shared key fails at construction, naming the key and never a value.
    /// </summary>
    /// <remarks>
    /// The key signs every sign-in question this service asks the orchestrator and verifies every
    /// agent's south token (#530 D7, D8). There is no other credential on either surface.
    /// </remarks>
    [Fact]
    public void An_enabled_feature_without_the_shared_key_fails_and_names_the_key()
    {
        var options = new CommsOptions { ConversationConnectionString = "Server=db;Database=conversations;" };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateConversations);
        Assert.StartsWith("chat_key_missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("TokenKeys", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not base64url!")]
    [InlineData("c2hvcnQ")]
    public void An_unusable_shared_key_fails_and_never_echoes_it(string key)
    {
        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=db;Database=conversations;",
            Journal = new() { TokenKeys = key },
        };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateConversations);
        Assert.StartsWith("chat_key_invalid", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fleet-orchestrator:3600")]
    [InlineData("ftp://fleet-orchestrator")]
    public void An_unusable_orchestrator_address_fails_and_names_the_key(string url)
    {
        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=db;Database=conversations;",
            Journal = new() { TokenKeys = JournalTestHost.KeyA },
            OrchestratorUrl = url,
        };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateConversations);
        Assert.Contains("OrchestratorUrl", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enabled_feature_with_the_shared_key_is_accepted_and_needs_no_agent_name()
    {
        // No agent is named in this service's configuration any more: each conversation names its
        // own, and every chat-enabled agent is reachable (#530 D8, D9).
        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=db;Database=conversations;",
            Journal = new() { TokenKeys = JournalTestHost.KeyA },
        };

        options.ValidateConversations();
        Assert.True(options.ConversationsEnabled);
        Assert.Equal("http://fleet-orchestrator:3600", options.OrchestratorUrl);
    }

    /// <summary>
    /// The south listener binds the container network, unlike the ops listener which is
    /// loopback-only.
    /// </summary>
    /// <remarks>
    /// Not an oversight and not a weaker rule — a different one. The ops listener is called only by
    /// this container's own healthcheck, so loopback is right. The south caller is a DIFFERENT
    /// container, so a loopback bind would make the surface unreachable by construction. It is kept
    /// private by never being published as a host port and never being proxied.
    /// </remarks>
    [Fact]
    public void The_south_listener_binds_the_container_network_and_the_ops_listener_does_not()
    {
        var options = new CommsOptions();

        Assert.StartsWith("http://0.0.0.0:", options.SouthUrl, StringComparison.Ordinal);
        Assert.StartsWith("http://127.0.0.1:", options.OpsUrl, StringComparison.Ordinal);
        Assert.NotEqual(options.SouthUrl, options.OpsUrl);
    }
}
