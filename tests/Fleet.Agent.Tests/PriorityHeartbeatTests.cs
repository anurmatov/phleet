using NSubstitute;
using System.Reflection;
using System.Text.Json;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

public sealed class PriorityHeartbeatTests
{
    [Fact]
    public void QueuePreviewSerializesPriorityWithoutIdentifiers()
    {
        var type = typeof(OrchestratorHeartbeatService).GetNestedType("QueuedMessageInfo", BindingFlags.NonPublic)!;
        var record = Activator.CreateInstance(type, "synthetic", "usermessage", DateTimeOffset.UnixEpoch, true)!;
        var json = JsonSerializer.SerializeToElement(record, type);
        Assert.True(json.GetProperty("Priority").GetBoolean());
        Assert.False(json.TryGetProperty("ChatId", out _));
        Assert.False(json.TryGetProperty("TelegramMessageId", out _));
    }

    [Fact]
    public async Task ActualHeartbeatCountsAllPriorityEntriesBeyondFivePreviews()
    {
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var manager = MidTurnIsolationTests.Manager("claude", executor);
        var channel = NSubstitute.Substitute.For<RabbitMQ.Client.IChannel>();
        byte[]? body = null;
        channel.BasicPublishAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<bool>(),
            NSubstitute.Arg.Any<RabbitMQ.Client.BasicProperties>(), NSubstitute.Arg.Any<ReadOnlyMemory<byte>>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(call => { body = call.Arg<ReadOnlyMemory<byte>>().ToArray(); return ValueTask.CompletedTask; });
        var heartbeat = new OrchestratorHeartbeatService(
            Microsoft.Extensions.Options.Options.Create(new Fleet.Agent.Configuration.AgentOptions { Name = "agent1", ShortName = "agent1", Role = "test", WorkDir = "/tmp" }),
            Microsoft.Extensions.Options.Options.Create(new Fleet.Agent.Configuration.RabbitMqOptions()), manager,
            NSubstitute.Substitute.For<IFleetConnectionState>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorHeartbeatService>.Instance);
        typeof(OrchestratorHeartbeatService).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(heartbeat, channel);
        try
        {
            await manager.StartTask(1, "relay", "relay", true, Fleet.Agent.Models.TaskSource.Relay); await executor.WaitStarted(1);
            for (var i = 0; i < 6; i++) await manager.StartTask(100 + i, "primary", "primary", true, priority: Fleet.Agent.Models.TaskPriority.PrimaryHuman);
            await manager.StartTask(2, "routine", "routine", true);
            await (Task)typeof(OrchestratorHeartbeatService).GetMethod("PublishAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(heartbeat, ["heartbeat", CancellationToken.None])!;
            using var json = JsonDocument.Parse(body!);
            Assert.Equal(7, json.RootElement.GetProperty("QueuedCount").GetInt32());
            Assert.Equal(6, json.RootElement.GetProperty("PriorityQueuedCount").GetInt32());
            var previews = json.RootElement.GetProperty("QueuedMessages"); Assert.Equal(5, previews.GetArrayLength());
            Assert.All(previews.EnumerateArray(), item => Assert.True(item.GetProperty("Priority").GetBoolean()));
            Assert.False(json.RootElement.TryGetProperty("Journal", out _));
        }
        finally { await manager.CancelAllAsync(); await heartbeat.DisposeAsync(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void JournalBindingFailureIsOneFixedFlag(int failed)
    {
        var type = typeof(OrchestratorHeartbeatService).GetNestedType("JournalHeartbeat", BindingFlags.NonPublic)!;
        var record = Activator.CreateInstance(type, true, 0, 0L, 0L, 0, 0, failed)!;
        Assert.Equal(failed, JsonSerializer.SerializeToElement(record, type).GetProperty("bindingFailed").GetInt32());
    }

    [Fact]
    public void LegacyHeartbeatDefaultsRemainRoutineAndHealthy()
    {
        var type = typeof(OrchestratorHeartbeatService).GetNestedType("OrchestratorMessage", BindingFlags.NonPublic)!;
        Assert.NotNull(type.GetProperty("PriorityQueuedCount"));
        var journal = type.GetProperty("Journal")!;
        Assert.Equal(System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            journal.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>()!.Condition);
    }
}
