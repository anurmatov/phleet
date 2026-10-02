using System.Text.Json;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class PriorityHeartbeatTests
{
    [Fact]
    public void RegistryRetainsPriorityAndBindingHealthFromWireAndLegacyDefaults()
    {
        var registry = new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance);
        var wire = """
        {"AgentName":"agent1","Status":"busy","Timestamp":"2026-01-01T00:00:00Z",
         "QueuedCount":6,"PriorityQueuedCount":2,"QueuedMessages":[
         {"Preview":"synthetic","Source":"usermessage","QueuedAt":"2026-01-01T00:00:00Z","Priority":true}],
         "Journal":{"enabled":true,"spoolDepth":0,"oldestAgeSeconds":0,"dropped":0,"dead":0,"authFailed":0,"bindingFailed":1}}
        """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var heartbeat = JsonSerializer.Deserialize<AgentHeartbeat>(wire, options)!;
        registry.UpdateAgent(heartbeat);
        var state = registry.Get("agent1")!;
        Assert.Equal(6, state.QueuedCount); Assert.Equal(2, state.PriorityQueuedCount);
        Assert.True(Assert.Single(state.QueuedMessages!).Priority); Assert.Equal(1, state.Journal!.BindingFailed);
        registry.UpdateAgent(heartbeat with { PriorityQueuedCount = 0, QueuedMessages = [] });
        Assert.Equal(0, registry.Get("agent1")!.PriorityQueuedCount);
        var legacy = JsonSerializer.Deserialize<AgentHeartbeat>("""
        {"AgentName":"agent2","Status":"idle","Timestamp":"2026-01-01T00:00:00Z","QueuedMessages":[
        {"Preview":"synthetic","Source":"usermessage","QueuedAt":"2026-01-01T00:00:00Z"}]}
        """, options)!;
        Assert.Equal(0, legacy.PriorityQueuedCount); Assert.False(Assert.Single(legacy.QueuedMessages!).Priority);
    }
}
