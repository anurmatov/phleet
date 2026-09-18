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
    /// Enabled without a south credential fails at construction, naming the key.
    /// </summary>
    /// <remarks>
    /// The south listener carries an administrative surface. A default credential would be a
    /// credential every deployment shares, which is the same as none.
    /// </remarks>
    [Fact]
    public void An_enabled_feature_without_a_south_credential_fails_and_names_the_key()
    {
        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=db;Database=conversations;",
            AgentName = "example-agent",
        };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateConversations);
        Assert.Contains("SouthBearerToken", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enabled_feature_without_an_agent_name_fails_and_names_the_key()
    {
        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=db;Database=conversations;",
            SouthBearerToken = "a-credential",
        };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateConversations);
        Assert.Contains("AgentName", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent name becomes a routing key AND a queue-name segment, so it is validated rather
    /// than trusted.
    /// </summary>
    /// <remarks>
    /// A dot or a slash produces a queue name the consumer cannot address — and the failure would
    /// surface as "the agent never receives anything", which is a long way from its cause.
    /// </remarks>
    [Theory]
    [InlineData("has.a.dot")]
    [InlineData("has/a/slash")]
    [InlineData("has a space")]
    [InlineData("has#a#hash")]
    public void An_agent_name_that_cannot_be_a_queue_segment_is_refused(string agentName)
    {
        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=db;Database=conversations;",
            SouthBearerToken = "a-credential",
            AgentName = agentName,
        };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateConversations);
        Assert.Contains("routing key", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("example-agent")]
    [InlineData("example_agent")]
    [InlineData("Agent1")]
    public void A_usable_agent_name_is_accepted(string agentName)
    {
        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=db;Database=conversations;",
            SouthBearerToken = "a-credential",
            AgentName = agentName,
        };

        options.ValidateConversations();
        Assert.True(options.ConversationsEnabled);
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
