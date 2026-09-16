using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

/// <summary>
/// #277 D-2 / T7 / T10: relay and bridge answers publish correctly with NO Telegram transport in
/// the process, and exactly once.
///
/// The failure these guard is the reason the effect moved at all. Relay publication used to be a
/// private method on the Telegram transport, so a Telegram-free deployment would have silently
/// dropped every workflow answer — and the #275 startup guard, which exists to prevent exactly
/// that, would have turned the drop into a crash loop instead.
///
/// Every test here builds the runtime without an <c>AgentTransport</c>.
/// </summary>
public class RelayCompletionPublisherTests
{
    private sealed record Harness(
        TaskManager Manager,
        GroupRelayService Relay,
        PublishedMessages Published);

    private static Harness Build()
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "test-agent", Role = "test", WorkDir = Path.GetTempPath(), Provider = "claude",
        });
        // A non-empty Host is what makes GroupRelayService.IsEnabled true.
        var rabbitOpts = Options.Create(new RabbitMqOptions { Host = "localhost" });
        var executor = Substitute.For<IAgentExecutor>();

        var published = new PublishedMessages();
        var relay = BuildCapturingRelay(agentOpts, rabbitOpts, published);
        var manager = new TaskManager(agentOpts, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, sink: new MessageSinkHolder());

        return new Harness(manager, relay, published);
    }

    /// <summary>
    /// T10 / P2. A relay delegation completes and its answer is published, with no transport.
    /// </summary>
    [Fact]
    public async Task RelayAnswer_IsPublishedWithoutATelegramTransport()
    {
        var h = Build();
        using var publisher = new RelayCompletionPublisher(h.Manager, h.Relay,
            NullLogger<RelayCompletionPublisher>.Instance);

        h.Manager.RaiseTaskCompletedForTest(
            chatId: 0, result: "the answer", relaySender: "peer",
            source: TaskSource.Relay, taskId: "task-1");

        var published = await h.Published.DequeueAsync();

        Assert.Contains("the answer", published.Payload.GetProperty("Text").GetString());
        Assert.Equal(RelayMessageType.Response, published.Payload.GetProperty("Type").GetString());
    }

    /// <summary>
    /// The bridge branch carries the correlation id, which is what the caller is waiting on. A
    /// bridge answer published without it resolves nothing and the delegation hangs.
    /// </summary>
    [Fact]
    public async Task BridgeAnswer_PreservesTheCorrelationId()
    {
        var h = Build();
        using var publisher = new RelayCompletionPublisher(h.Manager, h.Relay,
            NullLogger<RelayCompletionPublisher>.Instance);

        h.Manager.RaiseTaskCompletedForTest(
            chatId: 0, result: "bridge answer", relaySender: "bridge",
            source: TaskSource.Relay, correlationId: "corr-7", taskId: "task-7");

        var published = await h.Published.DequeueAsync();

        Assert.Equal("bridge answer", published.Payload.GetProperty("Text").GetString());
        Assert.Equal(RelayMessageType.BridgeResponse, published.Payload.GetProperty("Type").GetString());
        Assert.Equal("corr-7", published.Payload.GetProperty("CorrelationId").GetString());
    }

    /// <summary>
    /// T7 and #277 MUST NOT 4 — one completion, exactly one relay publish, asserted on the broker
    /// rather than on a call count.
    ///
    /// This is the defect the D-2 split could introduce: two components subscribed to
    /// <c>OnTaskCompleted</c>, both publishing. The mutation for it is "subscribe both new
    /// singletons to relay publication", and the second instance is what
    /// <c>AddSingleton</c> + factory-<c>AddHostedService</c> exists to prevent.
    /// </summary>
    [Fact]
    public async Task OneCompletion_PublishesExactlyOnce()
    {
        var h = Build();
        using var publisher = new RelayCompletionPublisher(h.Manager, h.Relay,
            NullLogger<RelayCompletionPublisher>.Instance);

        h.Manager.RaiseTaskCompletedForTest(
            chatId: 0, result: "single", relaySender: "peer",
            source: TaskSource.Relay, taskId: "task-1");

        await h.Published.DequeueAsync();
        // Give a duplicate publisher time to land a second message if one existed.
        await Task.Delay(150);

        Assert.Empty(h.Published.Messages);
    }

    /// <summary>
    /// A completion with no relay sender is a Telegram turn. It must publish nothing — the relay
    /// branch is not a broadcast path.
    /// </summary>
    [Fact]
    public async Task CompletionWithNoRelaySender_PublishesNothing()
    {
        var h = Build();
        using var publisher = new RelayCompletionPublisher(h.Manager, h.Relay,
            NullLogger<RelayCompletionPublisher>.Instance);

        h.Manager.RaiseTaskCompletedForTest(chatId: 1234, result: "telegram answer");

        await Task.Delay(150);

        Assert.Empty(h.Published.Messages);
    }

    /// <summary>
    /// Once detached, nothing publishes. Symmetry with the constructor-time subscription — without
    /// it a disposed publisher keeps answering on a dead instance.
    /// </summary>
    [Fact]
    public async Task AfterDispose_NothingIsPublished()
    {
        var h = Build();
        var publisher = new RelayCompletionPublisher(h.Manager, h.Relay,
            NullLogger<RelayCompletionPublisher>.Instance);
        publisher.Dispose();

        h.Manager.RaiseTaskCompletedForTest(
            chatId: 0, result: "ignored", relaySender: "peer",
            source: TaskSource.Relay, taskId: "task-1");

        await Task.Delay(150);

        Assert.Empty(h.Published.Messages);
    }

    // ── capturing relay (same shape as GroupBehaviorStatusTests) ─────────────────────────

    private static GroupRelayService BuildCapturingRelay(
        IOptions<AgentOptions> agentOptions,
        IOptions<RabbitMqOptions> rabbitOptions,
        PublishedMessages published)
    {
        var relay = new GroupRelayService(agentOptions, rabbitOptions, NullLogger<GroupRelayService>.Instance);
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask)
            .AndDoes(call =>
            {
                var routingKey = call.ArgAt<string>(1);
                var body = call.ArgAt<ReadOnlyMemory<byte>>(4);
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(body.Span));
                published.Enqueue(new PublishedMessage(routingKey, document.RootElement.Clone()));
            });

        SetPrivateField(relay, "_publishChannel", channel);
        SetPrivateField(relay, "_initialized", true);
        return relay;
    }

    private static void SetPrivateField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }

    private sealed record PublishedMessage(string RoutingKey, JsonElement Payload);

    private sealed class PublishedMessages
    {
        private readonly SemaphoreSlim _available = new(0);
        public ConcurrentQueue<PublishedMessage> Messages { get; } = new();

        public void Enqueue(PublishedMessage message)
        {
            Messages.Enqueue(message);
            _available.Release();
        }

        public async Task<PublishedMessage> DequeueAsync()
        {
            var acquired = await _available.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(acquired, "No relay message was published within the timeout.");
            Assert.True(Messages.TryDequeue(out var message));
            return message!;
        }
    }
}
