using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Conversations.Journal;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Comms.Tests;

public sealed partial class JournalReadMcpTests
{
    [Theory]
    [InlineData("claude", MidTurnInjectionStatus.Injected)]
    [InlineData("codex", MidTurnInjectionStatus.NoActiveTurn)]
    [InlineData("gemini", MidTurnInjectionStatus.Unsupported)]
    public async Task MidTurnReply_UsesTheExistingRuntimeBinding_WithoutPublishingAnotherSequence(
        string provider, MidTurnInjectionStatus injection)
    {
        await using var host = await McpHost.StartAsync();
        var t = DateTimeOffset.UnixEpoch;
        host.Reads.Add(Fleet.Protocol.Ulid.NewUlid(), "supergroup", -101, 5, t, "group alpha", [AgentA]);
        host.Reads.Add(Fleet.Protocol.Ulid.NewUlid(), "supergroup", -202, 5, t, "group bravo", [AgentA]);
        using var client = host.CreateClient();
        using var publisher = new TurnBindingPublisher(new JournalHttpClient(client,
            Token(JournalTokens.PurposeIngest, AgentA)), NullLogger<TurnBindingPublisher>.Instance);
        publisher.ObserveChat(-101, 7001, "supergroup");
        publisher.ObserveChat(-202, 7001, "supergroup");
        await publisher.StartAsync(default);
        await using var executor = new ReplyExecutor(injection);
        var manager = new TaskManager(Options.Create(new AgentOptions
        { Name = "agent1", Role = "test", WorkDir = "/tmp", Provider = provider }), executor,
            new SessionManager(), NullLogger<TaskManager>.Instance, turnBindings: publisher);
        try
        {
            Assert.Equal(TaskDispatchOutcome.Ran, await manager.StartTask(-101, "human turn", "human", true));
            await WaitUntil(() => executor.Tasks.Count == 1);
            var token = Token(JournalTokens.PurposeRead, AgentA);
            // Publication is asynchronous: wait on the actual observable read, not a fabricated ack.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ToolResponse initial;
            do
            {
                initial = await host.CallAsync(token, "get_message", new { telegram_message_id = 5 });
                if (initial.IsError) await Task.Delay(10, deadline.Token);
            } while (initial.IsError);
            Assert.Equal("group alpha", initial.Json.GetProperty("text").GetString());
            var before = publisher.Current;
            var disposition = await manager.StartTask(-101, "[reply_to_message_id: 5] reply", "reply", true);
            Assert.Equal(injection == MidTurnInjectionStatus.Injected ? TaskDispatchOutcome.Injected : TaskDispatchOutcome.Queued, disposition);
            Assert.Equal(before, publisher.Current);
            Assert.Equal("group alpha", (await host.CallAsync(token, "get_message", new { telegram_message_id = 5 })).Json.GetProperty("text").GetString());
            Assert.Equal(TaskDispatchOutcome.Queued, await manager.StartTask(-202, "different group", "other", true));
            Assert.Equal(before, publisher.Current);
            Assert.Equal(-202, Assert.Single(manager.GetQueueSnapshot()).ChatId);
        }
        finally
        {
            executor.ReleaseAll();
            await WaitUntil(() => !manager.HasRunningTasks(-101) && !manager.HasRunningTasks(-202));
            await manager.CancelAllAsync();
            await publisher.StopAsync(default);
        }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private sealed class ReplyExecutor(MidTurnInjectionStatus status) : IAgentExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseAll() => _release.TrySetResult();
        public ConcurrentQueue<string> Tasks { get; } = new();
        public string? LastSessionId => "synthetic-session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;
        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(string task,
            IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Tasks.Enqueue(task);
            await _release.Task.WaitAsync(ct);
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task };
        }
        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task,
            IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default) => Task.FromResult(status switch
            {
                MidTurnInjectionStatus.Injected => MidTurnInjectionResult.Injected,
                MidTurnInjectionStatus.NoActiveTurn => MidTurnInjectionResult.NoActiveTurn("synthetic -32600"),
                _ => MidTurnInjectionResult.Unsupported,
            });
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
