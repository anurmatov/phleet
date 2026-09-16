using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

public class GroupBehaviorStatusTests
{
    private const long ChatId = 12345;

    [Fact]
    public async Task OnRelayMessage_StatusCheckWhenIdle_PublishesCorrelatedBoundedResponseWithoutSideEffects()
    {
        await using var harness = new Harness();
        var bufferCount = harness.Behavior.GetGroupBuffer(ChatId).GetEntries().Count;
        harness.Behavior.ScheduleDebounce(ChatId);
        Assert.True(harness.HasDebounceTimer(ChatId));

        harness.Behavior.OnRelayMessage(ChatId, "bridge", "do not dispatch this",
            RelayMessageType.StatusCheck, correlationId: "status-1", taskId: "unrelated-task",
            workflowId: "unrelated-workflow", signalName: "unrelated-signal");

        var published = await harness.Published.DequeueAsync();

        Assert.Equal("bridge", published.RoutingKey);
        Assert.Equal(ChatId, published.Payload.GetProperty("ChatId").GetInt64());
        Assert.Equal("test", published.Payload.GetProperty("Sender").GetString());
        Assert.Equal("idle", published.Payload.GetProperty("Text").GetString());
        Assert.Equal(RelayMessageType.StatusResponse, published.Payload.GetProperty("Type").GetString());
        Assert.Equal("status-1", published.Payload.GetProperty("CorrelationId").GetString());
        Assert.Equal(JsonValueKind.Null, published.Payload.GetProperty("TaskId").ValueKind);
        Assert.Equal(JsonValueKind.Null, published.Payload.GetProperty("WorkflowId").ValueKind);
        Assert.Equal(JsonValueKind.Null, published.Payload.GetProperty("SignalName").ValueKind);
        Assert.True(published.Payload.GetProperty("Timestamp").TryGetDateTimeOffset(out _));
        Assert.Equal(bufferCount, harness.Behavior.GetGroupBuffer(ChatId).GetEntries().Count);
        Assert.Empty(harness.TaskManager.GetQueueSnapshot());
        Assert.False(harness.TaskManager.HasRunningTasks(ChatId));
        Assert.True(harness.HasDebounceTimer(ChatId));
        harness.Executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, default, default);
        await harness.Sink.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
    }

    [Fact]
    public async Task OnRelayMessage_StatusCheckWhenBusy_PublishesBusyWithoutTaskDetails()
    {
        await using var harness = new Harness();
        const string secretDescription = "confidential task description";
        const string secretTaskId = "private-task-id";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => BlockUntilReleased(started, release));

        _ = harness.TaskManager.StartTask(ChatId, "prompt", secretDescription, isSessionTask: true,
            source: TaskSource.UserMessage, taskId: secretTaskId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Behavior.OnRelayMessage(ChatId, "bridge", "status",
            RelayMessageType.StatusCheck, correlationId: "status-busy");
        var published = await harness.Published.DequeueAsync();
        var json = published.Payload.GetRawText();

        Assert.Equal("busy", published.Payload.GetProperty("Text").GetString());
        Assert.Equal("status-busy", published.Payload.GetProperty("CorrelationId").GetString());
        Assert.DoesNotContain(secretDescription, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretTaskId, json, StringComparison.Ordinal);

        release.TrySetResult();
        await harness.WaitUntilIdleAsync(ChatId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OnRelayMessage_StatusCheckWithInvalidCorrelation_DropsWithoutDispatch(string? correlationId)
    {
        await using var harness = new Harness();
        var bufferCount = harness.Behavior.GetGroupBuffer(ChatId).GetEntries().Count;

        harness.Behavior.OnRelayMessage(ChatId, "bridge", "/status",
            RelayMessageType.StatusCheck, correlationId: correlationId);
        await Task.Delay(100);

        Assert.Empty(harness.Published.Messages);
        Assert.Equal(bufferCount, harness.Behavior.GetGroupBuffer(ChatId).GetEntries().Count);
        Assert.Empty(harness.TaskManager.GetQueueSnapshot());
        Assert.False(harness.TaskManager.HasRunningTasks(ChatId));
        harness.Executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, default, default);
        await harness.Sink.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
    }

    [Fact]
    public async Task OnRelayMessage_ConcurrentStatusChecks_PreservesEachCorrelation()
    {
        await using var harness = new Harness();

        Parallel.Invoke(
            () => harness.Behavior.OnRelayMessage(ChatId, "bridge", "status",
                RelayMessageType.StatusCheck, correlationId: "status-a"),
            () => harness.Behavior.OnRelayMessage(ChatId + 1, "bridge", "status",
                RelayMessageType.StatusCheck, correlationId: "status-b"));

        var first = await harness.Published.DequeueAsync();
        var second = await harness.Published.DequeueAsync();
        var responses = new[] { first, second }.ToDictionary(
            x => x.Payload.GetProperty("CorrelationId").GetString()!,
            x => x.Payload.GetProperty("ChatId").GetInt64());

        Assert.Equal(ChatId, responses["status-a"]);
        Assert.Equal(ChatId + 1, responses["status-b"]);
        Assert.All(new[] { first, second }, x =>
            Assert.Equal(RelayMessageType.StatusResponse, x.Payload.GetProperty("Type").GetString()));
    }

    [Fact]
    public async Task OnRelayMessage_BridgeRequestCompletion_PreservesAnswerAndCorrelation()
    {
        await using var harness = new Harness(finalResult: "original answer");
        harness.AttachRelayCompletionPublisher();

        harness.Behavior.OnRelayMessage(ChatId, "bridge", "ordinary request",
            RelayMessageType.BridgeRequest, correlationId: "bridge-correlation", taskId: "bridge-task");

        var published = await harness.Published.DequeueAsync();

        Assert.Equal("original answer", published.Payload.GetProperty("Text").GetString());
        Assert.Equal(RelayMessageType.BridgeResponse, published.Payload.GetProperty("Type").GetString());
        Assert.Equal("bridge-correlation", published.Payload.GetProperty("CorrelationId").GetString());
        Assert.Equal("bridge-task", published.Payload.GetProperty("TaskId").GetString());
    }

    private static async IAsyncEnumerable<AgentProgress> BlockUntilReleased(
        TaskCompletionSource started,
        TaskCompletionSource release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        yield return Final("done");
    }

    private static async IAsyncEnumerable<AgentProgress> CompleteWith(
        string result,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return Final(result);
        await Task.CompletedTask;
    }

    private static AgentProgress Final(string result) => new()
    {
        Summary = result,
        EventType = "result",
        FinalResult = result,
    };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly GroupRelayService _relay;
        private RelayCompletionPublisher? _relayCompletions;

        public Harness(string? finalResult = null)
        {
            var workDir = Path.Combine(Path.GetTempPath(), $"fleet-status-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            WorkDir = workDir;

            var agentOptions = Options.Create(new AgentOptions
            {
                Name = "test-agent",
                Role = "test",
                WorkDir = workDir,
                GroupDebounceSeconds = 30,
                ShortName = "test",
            });
            var telegramOptions = Options.Create(new TelegramOptions());
            var rabbitOptions = Options.Create(new RabbitMqOptions { Exchange = "fleet.tasks" });
            Executor = Substitute.For<IAgentExecutor>();
            Executor.IsProcessWarm.Returns(true);
            if (finalResult is not null)
            {
                Executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                        Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
                    .Returns(call => CompleteWith(finalResult, call.ArgAt<CancellationToken>(3)));
            }

            Sink = Substitute.For<IMessageSink>();
            var allowlist = new AllowlistHolder(telegramOptions);
            _relay = BuildCapturingRelay(agentOptions, rabbitOptions, Published);
            TaskManager = new TaskManager(agentOptions, Executor, new SessionManager(),
                NullLogger<TaskManager>.Instance, sink: Sink);
            var prompts = new PromptAssembler(Executor);
            var commands = new CommandDispatcher(TaskManager, Executor, agentOptions,
                NullLogger<CommandDispatcher>.Instance, sink: Sink);
            Behavior = new GroupBehavior(agentOptions, telegramOptions, allowlist, Executor, _relay,
                TaskManager, commands, prompts, NullLogger<GroupBehavior>.Instance, sink: Sink);

        }

        public string WorkDir { get; }
        public IAgentExecutor Executor { get; }
        public IMessageSink Sink { get; }
        public TaskManager TaskManager { get; }
        public GroupBehavior Behavior { get; }
        public PublishedMessages Published { get; } = new();

        /// <summary>
        /// Attaches the production relay-completion owner. Before #277 D-2 this reflected a
        /// private method off AgentTransport; the effect now has its own singleton, which
        /// subscribes in its constructor, so the test exercises the real path instead.
        /// </summary>
        public void AttachRelayCompletionPublisher()
        {
            _relayCompletions = new RelayCompletionPublisher(TaskManager, _relay,
                NullLogger<RelayCompletionPublisher>.Instance);
        }

        public bool HasDebounceTimer(long chatId)
        {
            var field = typeof(GroupBehavior).GetField("_debounceTimers",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timers = (ConcurrentDictionary<long, CancellationTokenSource>)field.GetValue(Behavior)!;
            return timers.ContainsKey(chatId);
        }

        public async Task WaitUntilIdleAsync(long chatId)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (TaskManager.HasRunningTasks(chatId) && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.False(TaskManager.HasRunningTasks(chatId));
        }

        public async ValueTask DisposeAsync()
        {
            _relayCompletions?.Dispose();
            Behavior.CancelAllDebounce();
            await TaskManager.CancelAllAsync();
            await _relay.DisposeAsync();
            try { Directory.Delete(WorkDir, recursive: true); } catch { }
        }
    }

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
            Assert.True(await _available.WaitAsync(TimeSpan.FromSeconds(5)), "relay response was not published");
            Assert.True(Messages.TryDequeue(out var message));
            return message;
        }
    }
}
