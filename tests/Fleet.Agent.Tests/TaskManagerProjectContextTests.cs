using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Fleet.Agent.Tests.ProjectContextTestSupport;

namespace Fleet.Agent.Tests;

/// <summary>
/// #347 delivery and ledger: every path where text reaches the executor renders the routed
/// attachment exactly once, marks it only on acceptance, and never lets it reach a user-visible
/// surface. Shape follows <see cref="TaskManagerInboxCoalescingTests"/>: a per-turn releasable
/// fake executor, released one turn at a time.
/// </summary>
public sealed class TaskManagerProjectContextTests : IDisposable
{
    private const string FullA = "A-FULL-CONTEXT-BODY";
    private const string FullB = "B-FULL-CONTEXT-BODY";

    private readonly ProjectContextRoot _root = new ProjectContextRoot()
        .WithFull(ProjectA, FullA + "\n")
        .WithFull(ProjectB, FullB + "\n");
    private readonly LedgerTestExecutor _executor = new();
    private readonly ConcurrentCapturingLogger<ProjectContextAttacher> _log = new();
    private readonly ProjectContextAttacher _attacher;
    private readonly IMessageSink _sink = Substitute.For<IMessageSink>();
    private readonly InjectionOutcomeCounter _counter = new();

    public TaskManagerProjectContextTests() =>
        _attacher = new ProjectContextAttacher(_executor, _log) { ContentRoot = _root.Path };

    public void Dispose() => _root.Dispose();

    private static IReadOnlyList<ContextAttachmentRequest> RoutedA => [Request(ProjectA)];

    private TaskManager Build(IConversationEventPublisher? events = null, SessionManager? sessions = null, bool withAttacher = true) =>
        new(Options.Create(new AgentOptions { Name = "agent-a", Role = "test", WorkDir = "/tmp", Provider = "claude" }),
            _executor, sessions ?? new SessionManager(), NullLogger<TaskManager>.Instance, _counter, events,
            sink: _sink, contextAttacher: withAttacher ? _attacher : null);

    private static Task WaitIdleAsync(TaskManager manager, long chatId) =>
        WaitUntilAsync(() => !manager.HasRunningTasks(chatId));

    private async Task RunTurnAsync(TaskManager manager, long chatId, string text,
        IReadOnlyList<ContextAttachmentRequest>? requests, int executeIndex)
    {
        _ = manager.StartTask(chatId, text, text, isSessionTask: true, contextRequests: requests);
        await _executor.WaitForExecuteCountAsync(executeIndex + 1);
        _executor.ReleaseNextTurn();
        await WaitIdleAsync(manager, chatId);
    }

    private IEnumerable<string> AttachLines => _log.At(LogLevel.Information).Where(m => m.StartsWith("ProjectContextAttach path=", StringComparison.Ordinal));

    // ── new turn + ledger ────────────────────────────────────────────────────

    [Fact]
    public async Task TwoWarmRoutedTurns_RenderOnce()
    {
        var manager = Build();

        await RunTurnAsync(manager, 1, "first", RoutedA, 0);
        await RunTurnAsync(manager, 1, "second", RoutedA, 1);

        Assert.StartsWith(BlockHeader(ProjectA), _executor.ExecutedTasks[0], StringComparison.Ordinal);
        Assert.Contains(FullA, _executor.ExecutedTasks[0], StringComparison.Ordinal);
        Assert.EndsWith("first", _executor.ExecutedTasks[0], StringComparison.Ordinal);
        Assert.Equal("second", _executor.ExecutedTasks[1]);
        Assert.Equal(
            ["ProjectContextAttach path=turn rendered=project-a suppressed= accepted=true",
             "ProjectContextAttach path=turn rendered= suppressed=project-a:already_attached accepted=true"],
            AttachLines);
    }

    [Theory]
    [InlineData("cold")]
    [InlineData("session")]
    [InlineData("epoch")]
    public async Task ColdStart_SessionChange_OrCompaction_RenderAgain(string reset)
    {
        var manager = Build();
        await RunTurnAsync(manager, 1, "first", RoutedA, 0);

        switch (reset)
        {
            case "cold": _executor.Warm = false; break;
            case "session": _executor.SessionId = "session-2"; break;
            case "epoch": Interlocked.Increment(ref _executor.Epoch); break;
        }
        await RunTurnAsync(manager, 1, "second", RoutedA, 1);

        Assert.StartsWith(BlockHeader(ProjectA), _executor.ExecutedTasks[1], StringComparison.Ordinal);
        Assert.EndsWith("second", _executor.ExecutedTasks[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeverWarmExecutor_EveryRoutedDeliveryRenders()
    {
        // Gemini's shape: a fresh process per task, IsProcessWarm always false.
        _executor.Warm = false;
        var manager = Build();

        await RunTurnAsync(manager, 1, "first", RoutedA, 0);
        await RunTurnAsync(manager, 1, "second", RoutedA, 1);
        await RunTurnAsync(manager, 1, "third", RoutedA, 2);

        Assert.All(_executor.ExecutedTasks, t => Assert.StartsWith(BlockHeader(ProjectA), t, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendFailureBeforePromptAccepted_LeavesItUnmarked_AndTheNextRoutedDeliveryRenders()
    {
        _executor.FailBeforeAccept.Enqueue(true);
        var manager = Build();

        _ = manager.StartTask(1, "first", "first", isSessionTask: true, contextRequests: RoutedA);
        await _executor.WaitForExecuteCountAsync(1);
        await WaitIdleAsync(manager, 1);

        Assert.Empty(_attacher.LedgerSnapshot());
        Assert.Equal(1, _attacher.PromptAcceptedMissingCount);
        Assert.Contains("ProjectContextAttach path=turn rendered=project-a suppressed= accepted=false", AttachLines);

        await RunTurnAsync(manager, 1, "second", RoutedA, 1);
        Assert.StartsWith(BlockHeader(ProjectA), _executor.ExecutedTasks[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptAcceptedThenCancel_KeepsTheMark_AndTheNextWarmRoutedDeliverySuppresses()
    {
        var manager = Build();

        _ = manager.StartTask(1, "first", "first", isSessionTask: true, contextRequests: RoutedA);
        await WaitUntilAsync(() => _attacher.LedgerSnapshot().Count == 1);
        await manager.HandleCancel(1, "all");
        await WaitIdleAsync(manager, 1);

        await RunTurnAsync(manager, 1, "second", RoutedA, 1);

        Assert.Equal("second", _executor.ExecutedTasks[1]);
        Assert.Contains("ProjectContextAttach path=turn rendered=project-a suppressed= accepted=true", AttachLines);
    }

    // ── mid-turn injection ──────────────────────────────────────────────────

    [Fact]
    public async Task Injection_Injected_CarriesThePrefixInTheSameFrame_AndMarks()
    {
        var manager = Build();
        _ = manager.StartTask(1, "first", "first", isSessionTask: true);
        await _executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(1, "second", "second", isSessionTask: true, contextRequests: RoutedA);
        await WaitUntilAsync(() => _executor.InjectedTasks.Count == 1);

        var frame = _executor.InjectedTasks[0];
        Assert.StartsWith(BlockHeader(ProjectA), frame, StringComparison.Ordinal);
        var end = frame.IndexOf("[end project context: project-a]", StringComparison.Ordinal);
        var header = frame.IndexOf("[NEW MESSAGE", StringComparison.Ordinal);
        Assert.True(end > 0 && header > end, "the prefix must precede the injection header in one frame");
        Assert.EndsWith("second", frame, StringComparison.Ordinal);
        Assert.Equal([new ProjectContextKey(ProjectA, 3)], _attacher.LedgerSnapshot());

        _executor.ReleaseNextTurn();
        await WaitIdleAsync(manager, 1);
        await RunTurnAsync(manager, 1, "third", RoutedA, 1);

        Assert.Equal("third", _executor.ExecutedTasks[1]);
        Assert.Contains("ProjectContextAttach path=inject rendered=project-a suppressed= accepted=true", AttachLines);
    }

    [Fact]
    public async Task Injection_NoActiveTurn_LeavesItUnmarked_AndTheQueuedMessageRendersAtDrain()
    {
        _executor.InjectionResult = MidTurnInjectionResult.NoActiveTurn("turn ended");
        var manager = Build();
        _ = manager.StartTask(1, "first", "first", isSessionTask: true);
        await _executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(1, "second", "second", isSessionTask: true, contextRequests: RoutedA);
        await WaitUntilAsync(() => _counter.GetCount("claude", InjectionOutcomeCounter.DegradedToQueue) == 1);
        Assert.Empty(_attacher.LedgerSnapshot());

        _executor.ReleaseNextTurn();
        await _executor.WaitForExecuteCountAsync(2);
        _executor.ReleaseNextTurn();
        await WaitIdleAsync(manager, 1);

        Assert.StartsWith(BlockHeader(ProjectA), _executor.ExecutedTasks[1], StringComparison.Ordinal);
        Assert.Contains("second", _executor.ExecutedTasks[1], StringComparison.Ordinal);
        Assert.Equal(
            ["ProjectContextAttach path=inject rendered=project-a suppressed= accepted=false",
             "ProjectContextAttach path=inbox rendered=project-a suppressed= accepted=true"],
            AttachLines);
        // An injection is acknowledged by Injected, not prompt_accepted: its refusal is not a
        // missing acceptance event.
        Assert.Equal(0, _attacher.PromptAcceptedMissingCount);
    }

    // ── merged deliveries ───────────────────────────────────────────────────

    [Fact]
    public async Task ThreeCoalescedQueueParts_RoutedToOneProject_RenderExactlyOneBlock()
    {
        var manager = Build();
        _ = manager.StartTask(1, "running", "running", isSessionTask: true);
        await _executor.WaitForExecuteCountAsync(1);

        for (var i = 1; i <= 3; i++)
            _ = manager.StartTask(2, $"part-{i}", $"part-{i}", isSessionTask: true, contextRequests: RoutedA);
        var queued = Assert.Single(manager.GetQueueSnapshot());
        Assert.Equal(3, queued.PartCount);

        _executor.ReleaseNextTurn();
        await _executor.WaitForExecuteCountAsync(2);
        _executor.ReleaseNextTurn();
        await WaitIdleAsync(manager, 2);

        var merged = _executor.ExecutedTasks[1];
        Assert.Equal(1, CountOccurrences(merged, BlockHeader(ProjectA)));
        Assert.Equal(1, CountOccurrences(merged, FullA));
        Assert.StartsWith(BlockHeader(ProjectA), merged, StringComparison.Ordinal);
        for (var i = 1; i <= 3; i++)
            Assert.Contains($"part-{i}", merged, StringComparison.Ordinal);
        Assert.Contains("ProjectContextAttach path=queue rendered=project-a suppressed= accepted=true", AttachLines);
    }

    [Fact]
    public async Task InboxContinuation_MergingTwoRoutedMessages_RendersEachProjectOnce()
    {
        _executor.InjectionResult = MidTurnInjectionResult.Unsupported;
        var manager = Build();
        _ = manager.StartTask(1, "first", "first", isSessionTask: true);
        await _executor.WaitForExecuteCountAsync(1);

        _ = manager.StartTask(1, "second", "second", isSessionTask: true, contextRequests: RoutedA);
        _ = manager.StartTask(1, "third", "third", isSessionTask: true, contextRequests: [Request(ProjectB), Request(ProjectA)]);
        await WaitUntilAsync(() => _counter.GetCount("claude", InjectionOutcomeCounter.DegradedToQueue) == 2);

        _executor.ReleaseNextTurn();
        await _executor.WaitForExecuteCountAsync(2);
        _executor.ReleaseNextTurn();
        await WaitIdleAsync(manager, 1);

        var continuation = _executor.ExecutedTasks[1];
        Assert.Equal(2, _executor.ExecutedTasks.Count);
        Assert.Equal(1, CountOccurrences(continuation, BlockHeader(ProjectA)));
        Assert.Equal(1, CountOccurrences(continuation, BlockHeader(ProjectB)));
        Assert.True(continuation.IndexOf("second", StringComparison.Ordinal) < continuation.IndexOf("third", StringComparison.Ordinal));
        Assert.Contains("ProjectContextAttach path=inbox rendered=project-a,project-b suppressed= accepted=true", AttachLines);
    }

    [Fact]
    public async Task ProcessExitResume_ReRendersFromTheStoredOriginalMessage()
    {
        var manager = Build();
        _ = manager.StartTask(1, "first", "first", isSessionTask: true);
        await _executor.WaitForExecuteCountAsync(1);
        _ = manager.StartTask(1, "second", "second", isSessionTask: true, contextRequests: RoutedA);
        await WaitUntilAsync(() => _executor.InjectedTasks.Count == 1);

        // The process dies after the injection was accepted; the executor goes cold.
        _executor.ReleaseNextTurn(LedgerTestExecutor.TurnEnd.ProcessExit);
        await _executor.WaitForExecuteCountAsync(2);
        _executor.ReleaseNextTurn();
        await WaitIdleAsync(manager, 1);

        // InjectedMessagesForResume held the ORIGINAL text: one fresh block, no stale injection
        // header, no second copy of a prefix.
        var resumed = _executor.ExecutedTasks[1];
        Assert.StartsWith(BlockHeader(ProjectA), resumed, StringComparison.Ordinal);
        Assert.EndsWith("second", resumed, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(resumed, BlockHeader(ProjectA)));
        Assert.DoesNotContain("[NEW MESSAGE", resumed, StringComparison.Ordinal);
        Assert.Contains("ProjectContextAttach path=resume rendered=project-a suppressed= accepted=true", AttachLines);
    }

    // ── nothing user-visible ────────────────────────────────────────────────

    [Fact]
    public async Task PromptAccepted_NeverReachesTheSink_TheConversationEvents_OrTheSession()
    {
        // Make the acceptance event look as forwardable as possible: significant, with a tool name
        // and a session id. TaskManager must consume it before any forwarding branch sees it.
        _executor.PromptAcceptedEvent = () => new AgentProgress
        {
            EventType = AgentProgress.PromptAcceptedEventType,
            Summary = "PROMPT_ACCEPTED_PROBE",
            IsSignificant = true,
            ToolName = "probe_tool",
            ToolArgs = "{}",
            SessionId = "probe-session",
        };
        var events = Substitute.For<IConversationEventPublisher>();
        var sessions = new SessionManager();
        var manager = Build(events, sessions);
        var toolUses = 0;
        manager.OnToolUse += (_, _, _) => Interlocked.Increment(ref toolUses);

        await RunTurnAsync(manager, 1, "first", RoutedA, 0);

        Assert.Equal(0, toolUses);
        Assert.Null(sessions.GetSession(1));
        Assert.DoesNotContain(_sink.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<string>(),
            s => s.Contains("PROMPT_ACCEPTED_PROBE", StringComparison.Ordinal) || s.Contains("probe_tool", StringComparison.Ordinal));
        var payloads = events.ReceivedCalls().SelectMany(c => c.GetArguments()).ToList();
        Assert.Contains(payloads, p => p is TurnFinalPayload);
        Assert.DoesNotContain(payloads, p => p is TurnProgressPayload { ToolName: "probe_tool" });
        Assert.DoesNotContain(payloads, p => p is TurnNoticePayload);
        Assert.DoesNotContain(payloads, p => p is not null && JsonSerializer.Serialize(p).Contains("PROBE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAttachmentNeverReachesDisplayText_QueueEntries_TheSink_OrEvents()
    {
        var events = Substitute.For<IConversationEventPublisher>();
        var manager = Build(events);

        _ = manager.StartTask(1, "task body", "display text", isSessionTask: true, contextRequests: RoutedA);
        await _executor.WaitForExecuteCountAsync(1);
        Assert.Equal("display text", manager.GetOrchestratorStatus().CurrentTask);

        // Queued while busy: the entry stores the original text — the attachment is rendered at
        // delivery, never at intake.
        _ = manager.StartTask(2, "queued body", "queued display", isSessionTask: true, contextRequests: [Request(ProjectB)]);
        var queued = Assert.Single(manager.GetQueueSnapshot());
        Assert.Equal("queued body", queued.Task);
        Assert.Equal("queued display", queued.DisplayText);

        _executor.ReleaseNextTurn();
        await _executor.WaitForExecuteCountAsync(2);
        Assert.Equal("queued display", manager.GetOrchestratorStatus().CurrentTask);
        _executor.ReleaseNextTurn();
        await WaitIdleAsync(manager, 2);

        Assert.Contains(FullA, _executor.ExecutedTasks[0], StringComparison.Ordinal);
        Assert.Contains(FullB, _executor.ExecutedTasks[1], StringComparison.Ordinal);
        var sinkTexts = _sink.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<string>().ToList();
        Assert.NotEmpty(sinkTexts);
        Assert.DoesNotContain(sinkTexts, s => s.Contains("[project context", StringComparison.Ordinal)
                                              || s.Contains(FullA, StringComparison.Ordinal)
                                              || s.Contains(FullB, StringComparison.Ordinal));
        var serializedEvents = events.ReceivedCalls().SelectMany(c => c.GetArguments())
            .Where(a => a is not null).Select(a => JsonSerializer.Serialize(a)).ToList();
        Assert.NotEmpty(serializedEvents);
        Assert.DoesNotContain(serializedEvents, s => s.Contains("project context", StringComparison.Ordinal)
                                                     || s.Contains("FULL-CONTEXT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutRequests_OrWithoutAnAttacher_TheExecutorInputIsUnchanged()
    {
        var manager = Build();
        await RunTurnAsync(manager, 1, "plain", null, 0);
        await RunTurnAsync(manager, 1, "empty", [], 1);

        var bare = Build(withAttacher: false);
        await RunTurnAsync(bare, 1, "no attacher", RoutedA, 2);

        Assert.Equal(["plain", "empty", "no attacher"], _executor.ExecutedTasks);
        Assert.Empty(_log.Entries);
    }
}
