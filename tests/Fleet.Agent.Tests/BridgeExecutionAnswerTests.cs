using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// A workflow reuses a task id when it runs a step again, so an answer that names only its task id
/// cannot say which attempt it is from, and a late answer from one attempt could be taken for the
/// next attempt's. The agent therefore puts the execution id it keeps with a task on every answer
/// it sends for that task. The temporal bridge compares it.
///
/// <para>
/// Real <see cref="TaskManager"/>, real <see cref="RelayCompletionPublisher"/>, real
/// <see cref="GroupRelayService"/> over a channel that records what is published. A task that has
/// no execution id is answered with the bytes it always was.
/// </para>
/// </summary>
public sealed class BridgeExecutionAnswerTests
{
    private const string TaskId = "wf-1/review";
    private const long Chat = 2;
    private static readonly string[] FieldsBefore =
        ["ChatId", "Sender", "Text", "Timestamp", "Type", "CorrelationId", "TaskId", "WorkflowId", "SignalName"];

    private sealed class Turns : IAgentExecutor
    {
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _turns = new();

        /// <summary>Lets the oldest running turn finish; <paramref name="truncated"/> makes it a partial answer.</summary>
        public async Task FinishNextAsync(bool truncated = false)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            TaskCompletionSource<bool>? turn;
            while (!_turns.TryDequeue(out turn)) await Task.Delay(10, cts.Token);
            turn.SetResult(truncated);
        }

        public string? LastSessionId => null;
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _turns.Enqueue(turn);
            var truncated = false;
            try { truncated = await turn.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { }
            yield return new AgentProgress { EventType = "result", Summary = "answer to: " + task, FinalResult = "answer to: " + task, IsErrorResult = truncated };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.NoActiveTurn());
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Harness : IDisposable
    {
        private readonly SemaphoreSlim _available = new(0);
        private readonly ConcurrentQueue<(string RoutingKey, JsonElement Message)> _published = new();
        private readonly RelayCompletionPublisher _publisher;

        public Turns Turns { get; } = new();
        public TaskManager Manager { get; }

        public Harness()
        {
            var agent = Options.Create(new AgentOptions
            {
                Name = "test-agent", Role = "test", WorkDir = Path.GetTempPath(), Provider = "claude", ShortName = "test",
            });
            Manager = new TaskManager(agent, Turns, new SessionManager(), NullLogger<TaskManager>.Instance,
                sink: Substitute.For<IMessageSink>());

            var relay = new GroupRelayService(agent, Options.Create(new RabbitMqOptions { Host = "localhost" }),
                NullLogger<GroupRelayService>.Instance);
            var channel = Substitute.For<IChannel>();
            channel.BasicPublishAsync<BasicProperties>(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                    Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.CompletedTask)
                .AndDoes(call =>
                {
                    using var document = JsonDocument.Parse(Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span));
                    _published.Enqueue((call.ArgAt<string>(1), document.RootElement.Clone()));
                    _available.Release();
                });
            Set(relay, "_publishChannel", channel);
            Set(relay, "_initialized", true);

            _publisher = new RelayCompletionPublisher(Manager, relay, NullLogger<RelayCompletionPublisher>.Instance);
        }

        private static void Set(object instance, string field, object value) =>
            instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

        public Task<TaskDispatchOutcome> DirectiveAsync(string instruction, string? executionId, string taskId = TaskId) =>
            Manager.StartTask(Chat, instruction, instruction, isSessionTask: true,
                source: TaskSource.Relay, relaySender: "temporal-bridge", taskId: taskId, executionId: executionId);

        public async Task<JsonElement> NextAnswerAsync()
        {
            Assert.True(await _available.WaitAsync(TimeSpan.FromSeconds(5)), "no answer was published");
            Assert.True(_published.TryDequeue(out var published));
            Assert.Equal("temporal-bridge", published.RoutingKey);
            return published.Message;
        }

        public async Task UntilIdleAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Manager.HasRunningTasks(Chat) || Manager.ActiveTaskIdCountForTest > 0) await Task.Delay(10, cts.Token);
        }

        public void Dispose() => _publisher.Dispose();
    }

    private static string? ExecutionIdOf(JsonElement answer) =>
        answer.TryGetProperty("ExecutionId", out var id) ? id.GetString() : null;

    /// <summary>
    /// Two attempts of one step, one after the other, under the same task id. Each answer names
    /// the execution it came from, which is the only thing that tells them apart.
    /// </summary>
    [Fact]
    public async Task EachAttemptsAnswer_NamesItsOwnExecution()
    {
        using var h = new Harness();

        await h.DirectiveAsync("first attempt", "execution-1");
        await h.Turns.FinishNextAsync();
        var first = await h.NextAnswerAsync();
        await h.UntilIdleAsync();

        await h.DirectiveAsync("second attempt", "execution-2");
        await h.Turns.FinishNextAsync();
        var second = await h.NextAnswerAsync();

        Assert.Equal((TaskId, "execution-1"), (first.GetProperty("TaskId").GetString(), ExecutionIdOf(first)));
        Assert.Contains("first attempt", first.GetProperty("Text").GetString());
        Assert.Equal((TaskId, "execution-2"), (second.GetProperty("TaskId").GetString(), ExecutionIdOf(second)));
        Assert.Contains("second attempt", second.GetProperty("Text").GetString());
        Assert.Equal([.. FieldsBefore, "ExecutionId"], first.EnumerateObject().Select(p => p.Name).ToArray());
    }

    /// <summary>A partial answer (the turn ran out before it finished) and a cancelled turn's failure name it too.</summary>
    [Fact]
    public async Task APartialAnswer_AndACancelledTurnsFailure_NameTheExecution()
    {
        using var h = new Harness();

        await h.DirectiveAsync("work", "execution-1");
        await h.Turns.FinishNextAsync(truncated: true);
        var partial = await h.NextAnswerAsync();
        Assert.Equal(RelayMessageType.PartialResponse, partial.GetProperty("Type").GetString());
        Assert.StartsWith("[status: incomplete]", partial.GetProperty("Text").GetString());
        Assert.Equal("execution-1", ExecutionIdOf(partial));
        await h.UntilIdleAsync();

        await h.DirectiveAsync("work", "execution-2");
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!h.Manager.HasRunningTasks(Chat)) await Task.Delay(10, cts.Token);
        Assert.Equal(BridgeCancelOutcome.Cancelled, await h.Manager.CancelBridgeExecutionAsync(TaskId, "execution-2"));
        var failed = await h.NextAnswerAsync();
        Assert.StartsWith("[status: failed]", failed.GetProperty("Text").GetString());
        Assert.Equal("execution-2", ExecutionIdOf(failed));
    }

    /// <summary>
    /// Every kind of completion goes out through the one publisher, which reads the id while the
    /// task still holds its task id. Raised here for a task that is waiting in the queue, so each
    /// kind can be raised without staging a turn that ends that way.
    /// </summary>
    [Theory]
    [InlineData(CompletionKind.Completed, false, "the answer")]
    [InlineData(CompletionKind.Incomplete, true, "[status: incomplete]")]
    [InlineData(CompletionKind.Failed, true, "[status: failed]")]
    [InlineData(CompletionKind.Idle, false, "[status: idle]")]
    public async Task EveryKindOfAnswer_NamesTheExecution(CompletionKind kind, bool partial, string startsWith)
    {
        using var h = new Harness();
        _ = h.Manager.StartTask(1, "something else", "something else", isSessionTask: false);
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DirectiveAsync("queued work", "execution-1"));

        h.Manager.RaiseTaskCompletedForTest(Chat, "the answer", relaySender: "temporal-bridge",
            source: TaskSource.Relay, isPartial: partial, taskId: TaskId, kind: kind);

        var answer = await h.NextAnswerAsync();
        Assert.Equal("execution-1", ExecutionIdOf(answer));
        Assert.Contains(startsWith, answer.GetProperty("Text").GetString());
        await h.Turns.FinishNextAsync();
    }

    /// <summary>
    /// A task whose directive carried no execution id (the in-process delegation path, a bridge
    /// from before the id) is answered with exactly the fields it always was.
    /// </summary>
    [Fact]
    public async Task ATaskWithoutAnExecutionId_IsAnsweredAsBefore()
    {
        using var h = new Harness();

        await h.DirectiveAsync("work", executionId: null);
        await h.Turns.FinishNextAsync();
        var answer = await h.NextAnswerAsync();

        Assert.Equal(FieldsBefore, answer.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Null(ExecutionIdOf(answer));
    }
}
