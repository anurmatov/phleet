using Fleet.Contracts;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

public sealed class ReplyCapabilityTests
{
    private static string Runtime => System.Text.Json.JsonSerializer.Serialize(new {
        Id = new string('a', 64), State = new { Running = true, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1) } });
    [Theory]
    [InlineData("idle", true, true)]
    [InlineData("busy", true, true)]
    [InlineData("idle", false, false)]
    [InlineData("stopped", true, false)]
    [InlineData("dead", true, false)]
    public void Reply_requires_a_live_runtime_that_advertises_quotation(string status, bool capable, bool expected)
    {
        var state = new AgentState { AgentName = "agent1", ReportedStatus = status,
            LastSeen = DateTimeOffset.UtcNow, ContainerName = new string('a', 12), Capabilities = capable ? [SignIn.ChatReplyCapability] : null };
        Assert.Equal(expected, ChatAgents.SupportsReply(state, Runtime));
        state.LastSeen = DateTimeOffset.MinValue;
        Assert.False(ChatAgents.SupportsReply(state, Runtime));
        state.LastSeen = DateTimeOffset.UtcNow;
        state.IsDbOnly = true;
        Assert.False(ChatAgents.SupportsReply(state, Runtime));
        Assert.False(ChatAgents.SupportsReply(null, Runtime));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"Id\":false}")]
    public void Missing_or_invalid_inspection_cannot_prove_Reply_support(string? inspection)
    {
        var state = new AgentState { AgentName = "agent1", ReportedStatus = "idle",
            LastSeen = DateTimeOffset.UtcNow, ContainerName = new string('a', 12),
            Capabilities = [SignIn.ChatReplyCapability] };
        Assert.False(ChatAgents.SupportsReply(state, inspection));
    }

    [Fact]
    public void An_old_heartbeat_withdraws_the_previous_runtimes_capability()
    {
        var registry = new AgentRegistry(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance);
        registry.UpdateAgent(new AgentHeartbeat("agent1", "idle", DateTimeOffset.UtcNow,
            Capabilities: [SignIn.ChatReplyCapability], ContainerName: new string('a', 12)));
        Assert.True(ChatAgents.SupportsReply(registry.Get("agent1"), Runtime));
        registry.UpdateAgent(new AgentHeartbeat("agent1", "idle", DateTimeOffset.UtcNow));
        Assert.False(ChatAgents.SupportsReply(registry.Get("agent1"), Runtime));
    }
}
