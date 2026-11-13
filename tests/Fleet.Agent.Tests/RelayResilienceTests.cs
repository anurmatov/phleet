using System.Reflection;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

/// <summary>
/// The relay is how a workflow reaches an agent and how the answer gets back. These tests cover
/// the ways it used to fail without saying so: a start-up failure that was final, a publish that
/// returned quietly while not connected, and a completion that was tried once.
/// </summary>
public class RelayResilienceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private const string Payload = "PAYLOAD-THAT-MUST-NOT-BE-LOGGED";

    [Fact]
    public async Task InitializeAsync_WhenTheBrokerIsDown_ReturnsAndKeepsTryingUntilItConnects()
    {
        var (connection, publishChannel, consumeChannel) = Broker();
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var brokerUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var logger = new SnapshotLogger<GroupRelayService>();
        await using var relay = new GroupRelayService(AgentOptions(), RabbitOptions(), logger)
        {
            Connect = async (_, ct) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("broker unreachable");
                secondAttempt.TrySetResult();
                await brokerUp.Task.WaitAsync(ct);
                return connection;
            },
            RetryDelay = _ => TimeSpan.Zero,
        };
        Assert.Equal(GroupRelayService.States.Connecting, relay.State);

        // Returns instead of throwing or waiting: the host has to finish starting.
        await relay.InitializeAsync(CancellationToken.None).WaitAsync(Wait);

        await secondAttempt.Task.WaitAsync(Wait);
        Assert.Equal(GroupRelayService.States.Retrying, relay.State);
        Assert.False(relay.IsInitializedForTesting);
        Assert.False(await relay.TryPublishToAgentAsync("peer", 1, Payload));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("retrying in the background"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("relay disabled"));

        brokerUp.SetResult();
        await WaitUntilAsync(() => relay.State == GroupRelayService.States.Connected);

        Assert.True(relay.IsInitializedForTesting);
        Assert.Same(connection, relay.Connection);
        await consumeChannel.Received(1).BasicConsumeAsync(
            "fleet.agent.agent1", true, Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<IDictionary<string, object?>>(), Arg.Any<IAsyncBasicConsumer>(), Arg.Any<CancellationToken>());
        Assert.True(await relay.TryPublishToAgentAsync("peer", 1, Payload));
        await publishChannel.Received(1).BasicPublishAsync(
            "fleet.messages", "peer", false, Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializeAsync_AttemptThatFailsAfterConnecting_ReleasesItsChannelsAndReusesTheConnection()
    {
        var connection = Substitute.For<IConnection>();
        var failedPublish = Substitute.For<IChannel>();
        var failedConsume = Substitute.For<IChannel>();
        var goodPublish = Substitute.For<IChannel>();
        var goodConsume = Substitute.For<IChannel>();
        failedConsume.QueueDeclareAsync(default!, default, default, default, default, default, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("declare refused"));
        connection.CreateChannelAsync(default, default)
            .ReturnsForAnyArgs(failedPublish, failedConsume, goodPublish, goodConsume);
        var connects = 0;
        await using var relay = new GroupRelayService(AgentOptions(), RabbitOptions(), NullLogger<GroupRelayService>.Instance)
        {
            Connect = (_, _) =>
            {
                Interlocked.Increment(ref connects);
                return Task.FromResult(connection);
            },
            RetryDelay = _ => TimeSpan.Zero,
        };

        await relay.InitializeAsync(CancellationToken.None).WaitAsync(Wait);
        await WaitUntilAsync(() => relay.State == GroupRelayService.States.Connected);

        // One connection for the life of the service: other consumers hold channels on it.
        Assert.Equal(1, connects);
        await failedPublish.Received(1).DisposeAsync();
        await failedConsume.Received(1).DisposeAsync();
        await goodPublish.DidNotReceive().DisposeAsync();
        await goodConsume.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task PublishWhileNotConnected_ReturnsFalseAndLogsIdentifiersOnly()
    {
        var logger = new SnapshotLogger<GroupRelayService>();
        await using var relay = new GroupRelayService(AgentOptions(), RabbitOptions(), logger);

        var published = await relay.TryPublishToAgentAsync("bridge", 7, Payload,
            type: RelayMessageType.BridgeResponse, correlationId: "c-1", taskId: "wf/step");

        Assert.False(published);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Relay message to bridge not published: relay is connecting", warning.Message);
        Assert.Contains("hasTaskId=True", warning.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(Payload));
    }

    [Fact]
    public async Task Completion_WhenThePublishFails_IsTriedAgainUntilTheBrokerTakesIt()
    {
        var channel = Substitute.For<IChannel>();
        var publishes = 0;
        channel.BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref publishes) <= 2
                ? ValueTask.FromException(new InvalidOperationException("connection is closed"))
                : ValueTask.CompletedTask);
        var counter = new RelayCompletionCounter();
        var manager = BuildManager();
        await using var relay = ConnectedRelay(channel);
        using var publisher = new RelayCompletionPublisher(manager, relay, NullLogger<RelayCompletionPublisher>.Instance, counter)
        {
            RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
        };

        manager.RaiseTaskCompletedForTest(chatId: 3, result: Payload, relaySender: "peer",
            source: TaskSource.Relay, taskId: "wf/step");

        await WaitUntilAsync(() => counter.GetPublished(RelayMessageType.Response) == 1);
        Assert.Equal(3, Volatile.Read(ref publishes));
    }

    [Fact]
    public async Task Completion_ThatTheBrokerNeverTakes_IsGivenUpOnAfterTheLastAttemptAndSaidSo()
    {
        var channel = Substitute.For<IChannel>();
        var publishes = 0;
        channel.BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref publishes);
                return ValueTask.FromException(new InvalidOperationException("connection is closed"));
            });
        var counter = new RelayCompletionCounter();
        var logger = new SnapshotLogger<RelayCompletionPublisher>();
        var manager = BuildManager();
        await using var relay = ConnectedRelay(channel);
        using var publisher = new RelayCompletionPublisher(manager, relay, logger, counter)
        {
            RetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        };

        manager.RaiseTaskCompletedForTest(chatId: 3, result: Payload, relaySender: "bridge",
            source: TaskSource.Bridge, correlationId: "c-1", taskId: "wf/step");

        await WaitUntilAsync(() => logger.Entries.Any(e => e.Level == LogLevel.Error));
        var lost = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("was not published after 3 attempts and is lost", lost.Message);
        Assert.Contains("target=bridge", lost.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(Payload));
        Assert.Equal(3, Volatile.Read(ref publishes));
        Assert.Equal(0, counter.GetPublished(nameof(RelayMessageType.BridgeResponse)));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static IOptions<AgentOptions> AgentOptions() => Options.Create(new AgentOptions
    {
        Name = "fleet-agent1",
        ShortName = "agent1",
        Role = "test",
        WorkDir = "/tmp",
        Provider = "claude",
    });

    private static IOptions<RabbitMqOptions> RabbitOptions() =>
        Options.Create(new RabbitMqOptions { Host = "broker", Exchange = "fleet.messages" });

    private static (IConnection Connection, IChannel Publish, IChannel Consume) Broker()
    {
        var connection = Substitute.For<IConnection>();
        var publish = Substitute.For<IChannel>();
        var consume = Substitute.For<IChannel>();
        connection.CreateChannelAsync(default, default).ReturnsForAnyArgs(publish, consume);
        return (connection, publish, consume);
    }

    /// <summary>A relay with a publish channel in place, the way the other relay tests build one.</summary>
    private static GroupRelayService ConnectedRelay(IChannel channel)
    {
        var relay = new GroupRelayService(AgentOptions(), RabbitOptions(), NullLogger<GroupRelayService>.Instance);
        SetPrivateField(relay, "_publishChannel", channel);
        SetPrivateField(relay, "_initialized", true);
        return relay;
    }

    private static void SetPrivateField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static TaskManager BuildManager() =>
        new(AgentOptions(), Substitute.For<IAgentExecutor>(), new SessionManager(), NullLogger<TaskManager>.Instance,
            sink: Substitute.For<IMessageSink>());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (!condition())
            await Task.Delay(10, cts.Token);
    }

    private sealed class SnapshotLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
