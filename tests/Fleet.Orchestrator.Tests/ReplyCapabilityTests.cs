using Fleet.Contracts;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

public sealed class ReplyCapabilityTests
{
    [Theory]
    [InlineData("idle", true, true)]
    [InlineData("busy", true, true)]
    [InlineData("idle", false, false)]
    [InlineData("stopped", true, false)]
    [InlineData("dead", true, false)]
    public void Reply_requires_a_live_runtime_that_advertises_quotation(string status, bool capable, bool expected)
    {
        var state = new AgentState { AgentName = "agent1", ReportedStatus = status,
            LastSeen = DateTimeOffset.UtcNow, Capabilities = capable ? [SignIn.ChatReplyCapability] : null };
        Assert.Equal(expected, ChatAgents.SupportsReply(state));
        state.LastSeen = DateTimeOffset.MinValue;
        Assert.False(ChatAgents.SupportsReply(state));
        state.LastSeen = DateTimeOffset.UtcNow;
        state.IsDbOnly = true;
        Assert.False(ChatAgents.SupportsReply(state));
        Assert.False(ChatAgents.SupportsReply(null));
    }

    [Fact]
    public void An_old_heartbeat_withdraws_the_previous_runtimes_capability()
    {
        var registry = new AgentRegistry(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance);
        registry.UpdateAgent(new AgentHeartbeat("agent1", "idle", DateTimeOffset.UtcNow,
            Capabilities: [SignIn.ChatReplyCapability]));
        Assert.True(ChatAgents.SupportsReply(registry.Get("agent1")));
        registry.UpdateAgent(new AgentHeartbeat("agent1", "idle", DateTimeOffset.UtcNow));
        Assert.False(ChatAgents.SupportsReply(registry.Get("agent1")));
    }
}
