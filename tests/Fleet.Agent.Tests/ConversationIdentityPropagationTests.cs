using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC8–AC10 (D3): routing identity must survive queueing, coalescing, injection and continuation.
///
/// These are the tests whose absence let three production drops through review. Each covers a path
/// where a submission is accepted under one identity and answered under another — which, from the
/// client's side, looks like a submission that is acknowledged and then never terminates.
/// </summary>
public class ConversationIdentityPropagationTests
{
    private const string ClientChannel = "example-adapter";

    private sealed class Harness
    {
        public required TaskManager Manager { get; init; }
        public required ConversationEventBus Bus { get; init; }
        public required ConversationRegistry Registry { get; init; }
        public required ConversationEventCounters Counters { get; init; }

        public List<ConversationEvent> Drain()
        {
            var events = new List<ConversationEvent>();
            foreach (var reader in Bus.TerminalReaders.ToList())
                while (reader.TryRead(out var pending))
                    events.Add(pending.Event);
            while (Bus.ProgressReader.TryRead(out var pending))
                events.Add(pending.Event);
            return events;
        }
    }

    private static Harness Build(IAgentExecutor executor)
    {
        var registry = new ConversationRegistry();
        var counters = new ConversationEventCounters();
        var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
        var options = Options.Create(new AgentOptions
        {
            Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude",
        });

        var manager = new TaskManager(options, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, injectionCounter: null, events: bus,
            telegramConfig: null, counters: counters)
        {
            Sink = Substitute.For<IMessageSink>(),
        };

        return new Harness { Manager = manager, Bus = bus, Registry = registry, Counters = counters };
    }

    private static (long key, ConversationIdentity identity) Open(Harness harness, string submissionId)
    {
        var reference = new ConversationRef(ClientChannel, "c_1", "p_owner");
        var key = harness.Registry.Resolve(reference);
        return (key, new ConversationIdentity
        {
            PrincipalId = "p_owner",
            Role = PrincipalRole.Owner,
            ChannelId = ClientChannel,
            ConversationId = "c_1",
            SubmissionId = submissionId,
            Attempt = 1,
        });
    }

    private static async Task WaitUntilIdleAsync(TaskManager manager, long key)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (manager.HasRunningTasks(key))
            await Task.Delay(10, cts.Token);
        await Task.Delay(100, CancellationToken.None);
    }

    /// <summary>An executor that blocks the first turn until released, then answers immediately.</summary>
    private sealed class GatedExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _firstTurnStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _turns;

        public Task FirstTurnStarted => _firstTurnStarted.Task;
        public void Release() => _release.TrySetResult();
        public int TurnCount => _turns;

        public MidTurnInjectionResult InjectionResult { get; set; } =
            new(MidTurnInjectionStatus.Injected, null);

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var turn = Interlocked.Increment(ref _turns);
            if (turn == 1)
            {
                _firstTurnStarted.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            yield return new AgentProgress
            {
                Summary = "r", EventType = "result", FinalResult = $"answer {turn}",
            };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string message, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default) =>
            Task.FromResult(InjectionResult);

        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(true);
        public void RequestRestart() { }
        public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
            string command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Yields a process-exit result on the first turn, then a normal answer on the second.
    /// Models an executor that dies mid-turn after a message was injected into it.
    /// </summary>
    private sealed class ProcessExitThenAnswerExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _firstTurnStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _turns;

        public Task FirstTurnStarted => _firstTurnStarted.Task;
        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var turn = Interlocked.Increment(ref _turns);
            if (turn == 1)
            {
                _firstTurnStarted.TrySetResult();
                await _release.Task.WaitAsync(ct);
                // The executor process died. InjectedMessagesForResume is non-empty by now, so
                // the runtime takes the resume branch rather than completing the turn.
                yield return new AgentProgress
                {
                    Summary = "process exited", EventType = "result", IsProcessExit = true,
                };
                yield break;
            }
            yield return new AgentProgress
            {
                Summary = "r", EventType = "result", FinalResult = "answer after resume",
            };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string message, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default) =>
            Task.FromResult(MidTurnInjectionResult.Injected);

        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(true);
        public void RequestRestart() { }
        public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
            string command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// AC10. A process-exit resume mints a new TurnId, keeps the conversation, and sets
    /// <c>Attempt = 2</c> — and, critically, the resumed turn still answers BOTH submissions.
    ///
    /// The bug this pins: the resume branch used to re-point the identity at the redelivered
    /// (injected) message's identity. That silently dropped the ORIGINAL submission from the turn —
    /// it could never terminate — while duplicating the injected id, which the injection accept
    /// site had already recorded. Both halves are asserted here.
    /// </summary>
    [Fact]
    public async Task AProcessExitResume_MintsANewTurnBumpsAttemptAndTerminatesBothSubmissions()
    {
        var executor = new ProcessExitThenAnswerExecutor();
        var harness = Build(executor);
        var (key, original) = Open(harness, "s_original");
        var injected = original with { SubmissionId = "s_injected" };

        await harness.Manager.StartTask(key, "original", "original", isSessionTask: true, identity: original);
        await executor.FirstTurnStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var outcome = await harness.Manager.StartTask(key, "injected", "injected",
            isSessionTask: true, identity: injected);
        Assert.Equal(TaskDispatchOutcome.Injected, outcome);

        executor.Release();
        await WaitUntilIdleAsync(harness.Manager, key);

        var events = harness.Drain();

        // The resumed turn announced itself with a NEW TurnId and Attempt == 2.
        var starts = events.Where(e => e.Kind == ConversationEventKind.TurnStarted).ToList();
        Assert.Equal(2, starts.Count);
        var resumed = starts.Single(e => e.Identity.Attempt == 2);
        Assert.NotEqual(starts.Single(e => e.Identity.Attempt == 1).Identity.TurnId, resumed.Identity.TurnId);
        Assert.False(string.IsNullOrEmpty(resumed.Identity.TurnId));
        // Same conversation throughout — a resume is not a new conversation.
        Assert.All(events, e => Assert.Equal("c_1", e.Identity.ConversationId));

        // The terminal covers BOTH submissions, each exactly once.
        //
        // mergedSubmissionIds is the COMPLETE list of submissions the turn answered, including the
        // turn's own — so the terminal's identity.SubmissionId is expected to appear in it, and
        // the no-duplicates check is on that list rather than on a union with it.
        var final = Assert.Single(events, e => e.Kind == ConversationEventKind.TurnFinal);
        var merged = final.PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds;

        Assert.Contains("s_original", merged);
        Assert.Contains("s_injected", merged);
        Assert.Equal(merged.Count, merged.Distinct(StringComparer.Ordinal).Count());

        // The turn still belongs to the ORIGINAL submission — re-pointing it at the redelivered
        // message was the defect.
        Assert.Equal("s_original", final.Identity.SubmissionId);
        Assert.Contains(final.Identity.SubmissionId, merged);
        Assert.Equal(2, final.Identity.Attempt);
    }

    /// <summary>
    /// AC9. An injected submission is answered inside the RUNNING turn, so its submission id must
    /// appear in that turn's terminal event. Without it the client sees
    /// submission.accepted{injected} and then silence forever.
    /// </summary>
    [Fact]
    public async Task AnInjectedSubmission_IsListedInTheRunningTurnsTerminalEvent()
    {
        var executor = new GatedExecutor();
        var harness = Build(executor);
        var (key, first) = Open(harness, "s_first");
        var injected = first with { SubmissionId = "s_injected" };

        await harness.Manager.StartTask(key, "first", "first", isSessionTask: true, identity: first);
        await executor.FirstTurnStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var outcome = await harness.Manager.StartTask(key, "injected", "injected",
            isSessionTask: true, identity: injected);
        Assert.Equal(TaskDispatchOutcome.Injected, outcome);

        executor.Release();
        await WaitUntilIdleAsync(harness.Manager, key);

        var final = Assert.Single(harness.Drain(), e => e.Kind == ConversationEventKind.TurnFinal);
        var merged = final.PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds;

        Assert.Contains("s_first", merged);
        Assert.Contains("s_injected", merged);
    }

    /// <summary>
    /// AC8. A coalesced queue entry is ONE turn answering SEVERAL submissions: each part gets its
    /// own submission.accepted{queued}, and the single terminal event lists all of them.
    ///
    /// This is the path where identity was previously dropped entirely — DrainQueue rebuilt a
    /// fresh identity, so the terminal event carried a submission id no client had ever seen.
    /// </summary>
    [Fact]
    public async Task ACoalescedQueueEntry_ProducesOneTerminalEventListingEverySubmission()
    {
        var executor = new GatedExecutor
        {
            // Force the queue path rather than injection.
            InjectionResult = MidTurnInjectionResult.Unsupported,
        };
        var harness = Build(executor);
        var (key, first) = Open(harness, "s_1");

        await harness.Manager.StartTask(key, "first", "first", isSessionTask: true, identity: first);
        await executor.FirstTurnStarted.WaitAsync(TimeSpan.FromSeconds(5));

        // Three further submissions, each with its own identity, coalesced behind the running turn.
        foreach (var id in new[] { "s_2", "s_3", "s_4" })
        {
            await harness.Manager.StartTask(key, $"task {id}", id, isSessionTask: true,
                identity: first with { SubmissionId = id });
        }

        executor.Release();
        await WaitUntilIdleAsync(harness.Manager, key);

        var events = harness.Drain();

        // Each queued part was acknowledged under its OWN submission id.
        var queuedIds = events
            .Where(e => e.Kind == ConversationEventKind.SubmissionAccepted
                        && e.PayloadAs<SubmissionAcceptedPayload>()!.Disposition == SubmissionDisposition.Queued)
            .Select(e => e.Identity.SubmissionId)
            .ToList();
        Assert.Contains("s_2", queuedIds);

        // Every submission id that was accepted also terminates: the union of all terminal events'
        // own ids and their mergedSubmissionIds covers all four.
        var terminated = events
            .Where(e => e.Kind == ConversationEventKind.TurnFinal)
            .SelectMany(e => e.PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds
                .Append(e.Identity.SubmissionId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in new[] { "s_1", "s_2", "s_3", "s_4" })
            Assert.Contains(id, terminated);
    }

    /// <summary>
    /// A queued submission keeps its conversation and its own submission id through the drain.
    /// Before the fix, DrainQueue synthesized a brand-new identity, so the answer to a queued
    /// message was attributed to a submission the client had never made.
    /// </summary>
    [Fact]
    public async Task AQueuedSubmission_KeepsItsConversationAndSubmissionIdThroughTheDrain()
    {
        var executor = new GatedExecutor
        {
            InjectionResult = MidTurnInjectionResult.Unsupported,
        };
        var harness = Build(executor);
        var (key, first) = Open(harness, "s_running");

        await harness.Manager.StartTask(key, "running", "running", isSessionTask: true, identity: first);
        await executor.FirstTurnStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Manager.StartTask(key, "queued", "queued", isSessionTask: true,
            identity: first with { SubmissionId = "s_queued" });

        executor.Release();
        await WaitUntilIdleAsync(harness.Manager, key);

        var events = harness.Drain();

        // No event may carry a conversation other than the one that was opened — a synthesized
        // identity would have used the runtime key as the conversation id instead.
        Assert.All(events, e => Assert.Equal("c_1", e.Identity.ConversationId));
        Assert.All(events, e => Assert.Equal(ClientChannel, e.Identity.ChannelId));
        Assert.All(events, e => Assert.Equal("p_owner", e.Identity.PrincipalId));

        var terminated = events
            .Where(e => ConversationEventKind.Terminal.Contains(e.Kind))
            .SelectMany(e => e.Kind == ConversationEventKind.TurnFinal
                ? e.PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds.Append(e.Identity.SubmissionId)
                : [e.Identity.SubmissionId])
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("s_running", terminated);
        Assert.Contains("s_queued", terminated);
    }

    /// <summary>
    /// Every turn.started must carry a TurnId, and distinct turns must carry distinct ones —
    /// a continuation that reused the previous turn's id would make two answers indistinguishable.
    /// </summary>
    [Fact]
    public async Task EveryTurnStartedCarriesADistinctTurnId()
    {
        var executor = new GatedExecutor
        {
            InjectionResult = MidTurnInjectionResult.Unsupported,
        };
        var harness = Build(executor);
        var (key, first) = Open(harness, "s_1");

        await harness.Manager.StartTask(key, "first", "first", isSessionTask: true, identity: first);
        await executor.FirstTurnStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Manager.StartTask(key, "second", "second", isSessionTask: true,
            identity: first with { SubmissionId = "s_2" });

        executor.Release();
        await WaitUntilIdleAsync(harness.Manager, key);

        var turnIds = harness.Drain()
            .Where(e => e.Kind == ConversationEventKind.TurnStarted)
            .Select(e => e.Identity.TurnId)
            .ToList();

        Assert.NotEmpty(turnIds);
        Assert.All(turnIds, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.Equal(turnIds.Count, turnIds.Distinct(StringComparer.Ordinal).Count());
    }

    // ── production wiring ─────────────────────────────────────────────────────

    /// <summary>
    /// The test that would have caught the counters never being wired.
    ///
    /// <c>Counters</c> used to be a public settable property that nothing in <c>src/</c> ever set,
    /// so <c>conversation_submissions_total</c> and <c>turn_outcome_unknown_total</c> existed and
    /// measured nothing in production. Unit tests set it by hand and looked green — a mirror test.
    /// Resolving the real object graph is the only thing that proves the wiring.
    /// </summary>
    [Fact]
    public async Task TaskManagerResolvedFromDi_ActuallyIncrementsTheCounters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AgentOptions>(o =>
        {
            o.Name = "test"; o.Role = "test"; o.WorkDir = "/tmp"; o.Provider = "claude";
        });
        services.Configure<TelegramOptions>(_ => { });

        var executor = new GatedExecutor();
        services.AddSingleton<IAgentExecutor>(executor);
        services.AddSingleton<SessionManager>();

        // Exactly the registrations Program.cs performs for the seam.
        services.AddSingleton<ConversationEventCounters>();
        services.AddSingleton<ConversationRegistry>();
        services.AddSingleton<IConversationRegistry>(sp => sp.GetRequiredService<ConversationRegistry>());
        services.AddSingleton<ConversationEventBus>();
        services.AddSingleton<IConversationEventPublisher>(sp => sp.GetRequiredService<ConversationEventBus>());
        services.AddSingleton<TaskManager>();

        using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<TaskManager>();
        manager.Sink = Substitute.For<IMessageSink>();
        var counters = provider.GetRequiredService<ConversationEventCounters>();
        var registry = provider.GetRequiredService<ConversationRegistry>();

        var key = registry.Resolve(new ConversationRef(ClientChannel, "c_1", "p_owner"));
        var identity = new ConversationIdentity
        {
            PrincipalId = "p_owner", Role = PrincipalRole.Owner, ChannelId = ClientChannel,
            ConversationId = "c_1", SubmissionId = "s_1", Attempt = 1,
        };

        executor.Release();
        await manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        await WaitUntilIdleAsync(manager, key);

        Assert.Equal(1, counters.SubmissionCount(nameof(TaskDispatchOutcome.Ran)));
    }

    /// <summary>
    /// The production counterpart of AC56: a Telegram turn driven through the real path — with no
    /// registry entry, because nothing registers Telegram conversations — must count as
    /// not_routed and leave the drop counter at zero.
    /// </summary>
    [Fact]
    public async Task ATelegramTurnOnTheProductionPath_CountsNotRoutedAndDropsNothing()
    {
        var executor = new GatedExecutor();
        var harness = Build(executor);

        executor.Release();
        // No identity supplied and no registry entry — exactly how a real Telegram message arrives.
        await harness.Manager.StartTask(12345L, "task", "display", isSessionTask: true);
        await WaitUntilIdleAsync(harness.Manager, 12345L);

        Assert.True(harness.Counters.NotRoutedCount(ChannelIds.Telegram) > 0,
            "a Telegram turn should count as not_routed");
        Assert.Equal(0, harness.Counters.TotalDropped());
    }

    /// <summary>The same, for relay-sourced work, which must also never reach an adapter.</summary>
    [Fact]
    public async Task ARelayTurnOnTheProductionPath_CountsNotRoutedAndDropsNothing()
    {
        var executor = new GatedExecutor();
        var harness = Build(executor);

        executor.Release();
        await harness.Manager.StartTask(0L, "task", "display", isSessionTask: false,
            source: TaskSource.Relay, relaySender: "sender");
        await WaitUntilIdleAsync(harness.Manager, 0L);

        Assert.True(harness.Counters.NotRoutedCount(ChannelIds.Relay) > 0);
        Assert.Equal(0, harness.Counters.TotalDropped());
    }
}
