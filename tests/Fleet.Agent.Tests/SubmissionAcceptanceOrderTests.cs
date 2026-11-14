using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// A conversation client is told a submission was accepted before it sees anything that follows
/// from the acceptance: <c>turn.started</c>, <c>submission.accepted</c>, then the turn's own events
/// for a message that runs at once; <c>submission.accepted</c> before the turn that later answers it
/// for a message that was queued, merged or injected.
/// </summary>
/// <remarks>
/// Each test gives the event that must come later every chance to overtake: while the acceptance is
/// being published, the publisher lets the turn go and waits for the later event to appear. With
/// the order held by construction the wait simply runs out; where the acceptance is published
/// after the fact, the later event gets there first and the test fails.
/// </remarks>
public class SubmissionAcceptanceOrderTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Overtake = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task ATurnThatRunsAtOnce_IsAcceptedBeforeItsFirstOwnEvent()
    {
        var events = new OrderedEvents();
        var executor = new ScriptedTurns();
        executor.ReleaseAllTurns();
        var manager = BuildManager(executor, events);
        events.BeforeAcceptance = (submission, _) =>
        {
            if (submission == "s1") events.WaitFor(e => e is { Submission: "s1", Kind: ConversationEventKind.TurnProgress }, Overtake);
        };

        Assert.Equal(TaskDispatchOutcome.Ran,
            await manager.StartTask(1, "one", "one", isSessionTask: true, identity: Identity("s1", chat: 1)));
        await events.WaitUntilAsync(e => e is { Submission: "s1", Kind: ConversationEventKind.TurnFinal });

        var mine = events.Of("s1");
        Assert.Equal(ConversationEventKind.TurnStarted, mine[0]);
        Assert.Equal("submission.accepted ran", mine[1]);
        Assert.Contains(ConversationEventKind.TurnProgress, mine.Skip(2));
        Assert.Equal(ConversationEventKind.TurnFinal, mine[^1]);
    }

    [Fact]
    public async Task AQueuedMessage_IsAcceptedAsQueuedBeforeItsTurnStarts_AndAsRunBeforeThatTurnEmits()
    {
        var events = new OrderedEvents();
        var executor = new ScriptedTurns();
        var manager = BuildManager(executor, events);
        events.BeforeAcceptance = (submission, disposition) =>
        {
            if (submission != "s2") return;
            if (disposition == SubmissionDisposition.Queued)
            {
                // Let the running turn end, so the queue is drained while this is still being published.
                executor.ReleaseAllTurns();
                events.WaitFor(e => e is { Submission: "s2", Kind: ConversationEventKind.TurnStarted }, Overtake);
            }
            else
            {
                events.WaitFor(e => e is { Submission: "s2", Kind: ConversationEventKind.TurnProgress }, Overtake);
            }
        };

        _ = manager.StartTask(1, "one", "one", isSessionTask: true, identity: Identity("s1", chat: 1));
        await executor.WaitForExecuteCountAsync(1);
        Assert.Equal(TaskDispatchOutcome.Queued,
            await manager.StartTask(2, "two", "two", isSessionTask: true, identity: Identity("s2", chat: 2)));
        await events.WaitUntilAsync(e => e is { Submission: "s2", Kind: ConversationEventKind.TurnFinal });

        var mine = events.Of("s2");
        Assert.Equal("submission.accepted queued", mine[0]);
        Assert.Equal(ConversationEventKind.TurnStarted, mine[1]);
        Assert.Equal("submission.accepted ran", mine[2]);
        Assert.Contains(ConversationEventKind.TurnProgress, mine.Skip(3));
        Assert.Equal(ConversationEventKind.TurnFinal, mine[^1]);
    }

    [Fact]
    public async Task AMessageMergedIntoAQueuedEntry_IsAcceptedBeforeThatEntrysTurnStarts()
    {
        var events = new OrderedEvents();
        var executor = new ScriptedTurns();
        var manager = BuildManager(executor, events);
        events.BeforeAcceptance = (submission, _) =>
        {
            if (submission != "s3") return;
            executor.ReleaseAllTurns();
            events.WaitFor(e => e is { Submission: "s2", Kind: ConversationEventKind.TurnStarted }, Overtake);
        };

        _ = manager.StartTask(1, "one", "one", isSessionTask: true, identity: Identity("s1", chat: 1));
        await executor.WaitForExecuteCountAsync(1);
        Assert.Equal(TaskDispatchOutcome.Queued,
            await manager.StartTask(2, "two", "two", isSessionTask: true, identity: Identity("s2", chat: 2)));
        Assert.Equal(TaskDispatchOutcome.Queued,
            await manager.StartTask(2, "three", "three", isSessionTask: true, identity: Identity("s3", chat: 2)));
        await events.WaitUntilAsync(e => e is { Submission: "s2", Kind: ConversationEventKind.TurnFinal });

        // One turn answers both, under the first submission's identity; the merged one is accepted first.
        var order = events.All();
        Assert.True(
            order.IndexOf(("s3", "submission.accepted queued")) < order.IndexOf(("s2", ConversationEventKind.TurnStarted)),
            string.Join(" | ", order));
        Assert.Contains("three", executor.ExecutedTasks[1]);
    }

    [Fact]
    public async Task AnInjectedMessage_IsAcceptedBeforeTheTurnItJoinedFinishes()
    {
        var events = new OrderedEvents();
        var executor = new ScriptedTurns { InjectionResult = MidTurnInjectionResult.Injected };
        var manager = BuildManager(executor, events);
        events.BeforeAcceptance = (submission, _) =>
        {
            if (submission != "s2") return;
            executor.ReleaseAllTurns();
            events.WaitFor(e => e is { Submission: "s1", Kind: ConversationEventKind.TurnFinal }, Overtake);
        };

        _ = manager.StartTask(1, "one", "one", isSessionTask: true, identity: Identity("s1", chat: 1));
        await executor.WaitForExecuteCountAsync(1);
        Assert.Equal(TaskDispatchOutcome.Injected,
            await manager.StartTask(1, "two", "two", isSessionTask: true, identity: Identity("s2", chat: 1)));
        await events.WaitUntilAsync(e => e is { Submission: "s1", Kind: ConversationEventKind.TurnFinal });

        var order = events.All();
        Assert.True(
            order.IndexOf(("s2", "submission.accepted injected")) < order.IndexOf(("s1", ConversationEventKind.TurnFinal)),
            string.Join(" | ", order));
    }

    [Fact]
    public async Task AMessageThatCannotBeInjected_IsAcceptedBeforeItsContinuationTurnStarts()
    {
        var events = new OrderedEvents();
        var executor = new ScriptedTurns { InjectionResult = MidTurnInjectionResult.Unsupported };
        var manager = BuildManager(executor, events);
        events.BeforeAcceptance = (submission, _) =>
        {
            if (submission != "s2") return;
            executor.ReleaseAllTurns();
            events.WaitFor(e => e is { Submission: "s2", Kind: ConversationEventKind.TurnStarted }, Overtake);
        };

        _ = manager.StartTask(1, "one", "one", isSessionTask: true, identity: Identity("s1", chat: 1));
        await executor.WaitForExecuteCountAsync(1);
        Assert.Equal(TaskDispatchOutcome.Queued,
            await manager.StartTask(1, "two", "two", isSessionTask: true, identity: Identity("s2", chat: 1)));
        await events.WaitUntilAsync(e => e is { Submission: "s2", Kind: ConversationEventKind.TurnFinal });

        var mine = events.Of("s2");
        Assert.Equal("submission.accepted queued", mine[0]);
        Assert.Equal(ConversationEventKind.TurnStarted, mine[1]);
    }

    private static ConversationIdentity Identity(string submission, long chat) => new()
    {
        PrincipalId = "p",
        Role = PrincipalRole.Owner,
        ChannelId = ChannelIds.Telegram,
        ConversationId = chat.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SubmissionId = submission,
        Attempt = 1,
    };

    private static TaskManager BuildManager(IAgentExecutor executor, IConversationEventPublisher events)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
        });
        return new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            events: events, sink: Substitute.For<IMessageSink>());
    }

    /// <summary>Events in publication order, keyed by the submission they were published for.</summary>
    private sealed class OrderedEvents : IConversationEventPublisher
    {
        private readonly object _gate = new();
        private readonly List<(string Submission, string Kind)> _events = [];

        /// <summary>Runs on the publishing thread, before a <c>submission.accepted</c> is recorded.</summary>
        public Action<string, SubmissionDisposition>? BeforeAcceptance { get; set; }

        public void Publish(ConversationEvent evt) => Record(evt.Identity.SubmissionId, evt.Kind);

        public bool Publish<TPayload>(long chatId, string kind, ConversationIdentity identity, TPayload? payload)
            where TPayload : class
        {
            if (payload is SubmissionAcceptedPayload accepted)
            {
                BeforeAcceptance?.Invoke(identity.SubmissionId, accepted.Disposition);
                Record(identity.SubmissionId, $"{kind} {accepted.Disposition.ToString().ToLowerInvariant()}");
            }
            else
            {
                Record(identity.SubmissionId, kind);
            }
            return true;
        }

        private void Record(string submission, string kind)
        {
            lock (_gate)
            {
                _events.Add((submission, kind));
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Waits, on the calling thread, until a matching event has been published or the time is up.</summary>
        public bool WaitFor(Func<(string Submission, string Kind), bool> match, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (_gate)
            {
                while (!_events.Any(match))
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(_gate, left);
                }
                return true;
            }
        }

        public async Task WaitUntilAsync(Func<(string Submission, string Kind), bool> match)
        {
            using var cts = new CancellationTokenSource(Wait);
            while (true)
            {
                lock (_gate)
                    if (_events.Any(match)) return;
                await Task.Delay(10, cts.Token);
            }
        }

        public List<(string Submission, string Kind)> All()
        {
            lock (_gate) return [.. _events];
        }

        /// <summary>One submission's events, without the typing ticks that run on their own timer.</summary>
        public List<string> Of(string submission)
        {
            lock (_gate) return _events.Where(e => e.Submission == submission).Select(e => e.Kind).ToList();
        }
    }

    /// <summary>Every turn waits for <see cref="ReleaseAllTurns"/>, reports one tool, then answers.</summary>
    private sealed class ScriptedTurns : IAgentExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _executed = new();

        public MidTurnInjectionResult InjectionResult { get; init; } = MidTurnInjectionResult.Unsupported;
        public IReadOnlyList<string> ExecutedTasks => _executed.ToList();
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executed.Enqueue(task);
            await _release.Task.WaitAsync(ct);
            yield return new AgentProgress { IsSignificant = true, ToolName = "Read", Summary = "Using Read", EventType = "tool_use" };
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default) => Task.FromResult(InjectionResult);

        public void ReleaseAllTurns() => _release.TrySetResult();
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
            ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForExecuteCountAsync(int expected)
        {
            using var cts = new CancellationTokenSource(Wait);
            while (_executed.Count < expected)
                await Task.Delay(10, cts.Token);
        }
    }
}
