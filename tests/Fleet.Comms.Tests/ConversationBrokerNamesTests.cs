using Fleet.Conversations;

namespace Fleet.Comms.Tests;

/// <summary>
/// The broker objects the chat service declares for an agent. The agent declares the same queue with
/// the same arguments (<c>Fleet.Agent.Tests.WireContractTests</c> holds its end to these values), and
/// a queue declared twice with different arguments is a channel error on whichever side is second.
/// </summary>
public sealed class ConversationBrokerNamesTests
{
    [Fact]
    public void The_command_exchange_queue_and_arguments_are_exactly_these()
    {
        Assert.Equal("fleet.conversations", ConversationBroker.CommandExchange);
        Assert.Equal("fleet.conversations.events", ConversationBroker.EventExchange);
        Assert.Equal("fleet.conversations.inbound.example-agent", ConversationBroker.CommandQueue("example-agent"));

        var arguments = ConversationBroker.CommandQueueArguments();
        var argument = Assert.Single(arguments);
        Assert.Equal("x-queue-type", argument.Key);
        Assert.Equal("quorum", argument.Value);
    }
}
