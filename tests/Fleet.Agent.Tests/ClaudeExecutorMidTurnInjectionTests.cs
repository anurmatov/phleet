using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public class ClaudeExecutorMidTurnInjectionTests
{
    [Fact]
    public async Task BuildUserMessageJsonAsync_TextPayload_HasNoPriorityField()
    {
        var executor = BuildExecutor();

        var json = await executor.BuildUserMessageJsonAsync("hello", null, null, CancellationToken.None);
        var payload = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("user", payload["type"]!.GetValue<string>());
        Assert.False(payload.ContainsKey("priority"));
        Assert.Equal("hello", payload["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task StdinWriters_RacingTurnSendInjectionAndCancel_DoNotInterleaveNdjsonLines()
    {
        var executor = BuildExecutor();
        var inner = new SlowChunkingTextWriter();
        executor.SetStdinForTests(TextWriter.Synchronized(inner));
        var lines = new[]
        {
            """{"type":"user","message":{"content":"turn-send"}}""",
            """{"type":"user","message":{"content":"injection"}}""",
            """{"type":"task_stop","task_id":"cancel"}""",
        };

        await Task.WhenAll(
            executor.WriteStdinLineForTestsAsync(lines[0], useLock: true),
            executor.WriteStdinLineForTestsAsync(lines[1], useLock: true),
            executor.WriteStdinLineForTestsAsync(lines[2], useLock: false));

        var writtenLines = inner.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, writtenLines.Length);
        Assert.All(lines, expected => Assert.Contains(expected, writtenLines));
    }

    // --- TurnCommittedToFinalAnswer flag tests ---

    // An "assistant" event where Message.Content contains only text blocks — Claude's terminal answer.
    private static ClaudeStreamEvent TextOnlyAssistantEvent(string text = "Hello, world!") =>
        new()
        {
            Type = "assistant",
            Message = new ClaudeMessage
            {
                Content =
                [
                    new ClaudeContentBlock { Type = "text", Text = text },
                ],
            },
        };

    // An "assistant" event where Message.Content contains a tool_use block — mid-loop, not terminal.
    private static ClaudeStreamEvent ToolUseAssistantEvent() =>
        new()
        {
            Type = "assistant",
            Message = new ClaudeMessage
            {
                Content =
                [
                    new ClaudeContentBlock { Type = "tool_use", Name = "Bash", Id = "x" },
                ],
            },
        };

    [Fact]
    public void TextOnlyAssistantEvent_SetsCommittedFlag()
    {
        var executor = BuildExecutor();
        executor.OpenTurnForTests();
        Assert.False(executor.TurnCommittedToFinalAnswerForTests);

        executor.ParseProgressForTests(TextOnlyAssistantEvent());

        Assert.True(executor.TurnCommittedToFinalAnswerForTests);
    }

    [Fact]
    public void ToolUseAssistantEvent_DoesNotSetCommittedFlag()
    {
        var executor = BuildExecutor();
        executor.OpenTurnForTests();

        executor.ParseProgressForTests(ToolUseAssistantEvent());

        Assert.False(executor.TurnCommittedToFinalAnswerForTests);
    }

    [Fact]
    public async Task CommittedFlag_BlocksInjection_WithExpectedErrorText()
    {
        var executor = BuildExecutor();
        executor.ParseProgressForTests(TextOnlyAssistantEvent());
        Assert.True(executor.TurnCommittedToFinalAnswerForTests);

        var result = await executor.TryInjectMessageAsync("late message", null, null, CancellationToken.None);

        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
        Assert.Contains("final answer", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC0 (S0), replacing the former <c>CommittedFlag_IsFalseOnFreshExecutor</c> on purpose (#429):
    /// no turn is open before the first turn start, so both flags start closed and nothing is
    /// written. The first turn stays injectable because its turn start opens the flags.
    /// </summary>
    [Fact]
    public async Task FreshExecutor_BothFlagsClosed_InjectionRefusedWithNothingWritten()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var stdin = new StringWriter();
        executor.SetStdinForTests(stdin);

        Assert.True(executor.TurnCommittedToFinalAnswerForTests);
        Assert.True(executor.CurrentTurnResultSeenForTests);

        var result = await executor.TryInjectMessageAsync("too early");

        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
        Assert.Equal("", stdin.ToString());
        Assert.Contains("Mid-turn injection refused: gate_closed", logs.Information);
    }

    [Fact]
    public async Task AfterToolUse_InjectionStillSucceeds()
    {
        // A tool_use assistant event must NOT set the final-answer flag.
        // After seeing one, TryInjectMessageAsync must proceed and return Injected.
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/cat",
            RedirectStandardInput = true,
            UseShellExecute = false,
        })!;
        try
        {
            var executor = BuildExecutor();
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(process.StandardInput);
            executor.OpenTurnForTests();
            executor.ParseProgressForTests(ToolUseAssistantEvent());
            Assert.False(executor.TurnCommittedToFinalAnswerForTests);

            var result = await executor.TryInjectMessageAsync("mid-turn injection", null, null, CancellationToken.None);

            Assert.Equal(MidTurnInjectionStatus.Injected, result.Status);
        }
        finally
        {
            process.Kill();
            process.Dispose();
        }
    }

    [Fact]
    public async Task SendCommandAsync_DrainedAssistantText_DoesNotSurfaceInNextExecuteTurn()
    {
        // Drives SendCommandAsync for real, then a normal ExecuteAsync turn, and asserts
        // at the sink (the yielded events a caller observes) that no recovered_answer
        // surfaces. Removing _preservedDrainedAnswerText = null from SendCommandAsync
        // makes this test fail: ExecuteAsync would then yield recovered_answer.
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/cat",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            var executor = BuildExecutor();
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(process.StandardInput);

            var channel = Channel.CreateUnbounded<ClaudeStreamEvent>();
            executor.SetEventChannelForTests(channel);

            // Simulate the previous turn's final-answer event arriving late into the channel.
            channel.Writer.TryWrite(TextOnlyAssistantEvent("stale text from /run path"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // --- /run turn ---
            var sendTask = Task.Run(async () =>
            {
                var events = new List<AgentProgress>();
                await foreach (var p in executor.SendCommandAsync("/run echo hello", cts.Token))
                    events.Add(p);
                return events;
            });

            // SendCommandAsync synchronously drains the stale event, clears
            // _preservedDrainedAnswerText, writes to stdin, then blocks on ReadAsync.
            // A brief delay is enough — the drain and stdin write are microsecond operations.
            await Task.Delay(100, cts.Token);
            channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "run done" });
            await sendTask;

            // --- normal ExecuteAsync turn ---
            var executeTask = Task.Run(async () =>
            {
                var events = new List<AgentProgress>();
                await foreach (var p in executor.ExecuteAsync("next task", ct: cts.Token))
                    events.Add(p);
                return events;
            });

            // ExecuteAsync drains (empty channel), finds _preservedDrainedAnswerText null,
            // writes to stdin, then blocks on ReadAsync.
            await Task.Delay(100, cts.Token);
            channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "real answer" });
            var executeEvents = await executeTask;

            // Stale text from the /run drain MUST NOT surface in this conversational turn.
            Assert.DoesNotContain(executeEvents, p => p.EventType == "recovered_answer");
            // The real response from the new turn must arrive at the sink.
            Assert.Contains(executeEvents, p => p.FinalResult == "real answer");
        }
        finally
        {
            process.Kill();
            process.Dispose();
        }
    }

    [Fact]
    public void DrainStaleTurnEvents_StaleResultEvent_IsConsumedNotPassedThrough()
    {
        // A background subtask's "result" event arrives between turns and must be consumed
        // by the drain so the new turn's read loop cannot see it and exit prematurely.
        var executor = BuildExecutor();
        executor.OpenTurnForTests();
        var channel = Channel.CreateUnbounded<ClaudeStreamEvent>();
        executor.SetEventChannelForTests(channel);
        channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "background result" });

        executor.DrainStaleTurnEventsForTests();

        Assert.False(channel.Reader.TryRead(out _), "result event must be consumed by drain");
        Assert.False(executor.TurnCommittedToFinalAnswerForTests);
        Assert.Null(executor.PreservedDrainedAnswerTextForTests);
    }

    [Fact]
    public void DrainStaleTurnEvents_TaskNotificationEvent_IsDiscarded()
    {
        // A background subtask's "task_notification" event must be consumed by the drain
        // and not reach the new turn's read loop.
        var executor = BuildExecutor();
        var channel = Channel.CreateUnbounded<ClaudeStreamEvent>();
        executor.SetEventChannelForTests(channel);
        channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "task_notification" });

        executor.DrainStaleTurnEventsForTests();

        Assert.False(channel.Reader.TryRead(out _), "task_notification must be consumed by drain");
        Assert.Null(executor.PreservedDrainedAnswerTextForTests);
    }

    [Fact]
    public void DrainStaleTurnEvents_AssistantTextEvent_PreservesTextWithoutSettingFlag()
    {
        // Arrange: an assistant text event in the channel (the previous turn's lost answer).
        var executor = BuildExecutor();
        executor.OpenTurnForTests();
        var channel = Channel.CreateUnbounded<ClaudeStreamEvent>();
        executor.SetEventChannelForTests(channel);
        channel.Writer.TryWrite(TextOnlyAssistantEvent("stale answer from prior turn"));
        channel.Writer.TryComplete();

        // Act: drain — must NOT call ParseAssistantEvent.
        executor.DrainStaleTurnEventsForTests();

        // _turnCommittedToFinalAnswer must remain false so the injection gate is not
        // tripped for the new turn that is about to start.
        Assert.False(executor.TurnCommittedToFinalAnswerForTests);
        // The answer text must be preserved for out-of-band delivery.
        Assert.Equal("stale answer from prior turn", executor.PreservedDrainedAnswerTextForTests);
    }

    [Fact]
    public async Task ExecuteAsync_PreservedAnswerText_IsDeliveredOnFirstTurnNotSecond()
    {
        // Pin: _preservedDrainedAnswerText = null; at ~line 157 inside ExecuteAsync.
        //
        // If that line is removed, _preservedDrainedAnswerText retains the stale text
        // after turn 1 delivers it.  Turn 2's DrainStaleTurnEvents finds nothing new but
        // the field is still non-null, so the if-block fires again and yields
        // recovered_answer a second time.  The test catches that re-delivery.
        //
        // It asserts at the sink (the IAsyncEnumerable a caller iterates) — no private
        // field inspection.  Uses /bin/cat as the live process so stdin writes succeed.
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/cat",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            var executor = BuildExecutor();
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(process.StandardInput);

            var channel = Channel.CreateUnbounded<ClaudeStreamEvent>();
            executor.SetEventChannelForTests(channel);

            // Plant a stale answer from a previous turn.  DrainStaleTurnEvents (called at
            // the top of ExecuteAsync's turn loop) will consume it and store the text in
            // _preservedDrainedAnswerText without calling ParseAssistantEvent, so the
            // injection gate is not tripped.
            channel.Writer.TryWrite(TextOnlyAssistantEvent("recovered stale answer"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // ── Turn 1 ──────────────────────────────────────────────────────────────
            var turn1Events = new List<AgentProgress>();
            var turn1 = Task.Run(async () =>
            {
                await foreach (var p in executor.ExecuteAsync("task 1", ct: cts.Token))
                    turn1Events.Add(p);
            });

            // Drain + recovered_answer yield + stdin write are sub-millisecond; 150 ms
            // is ample before we inject the result that terminates turn 1.
            await Task.Delay(150, cts.Token);
            channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "turn 1 answer" });
            await turn1;

            // The recovered answer must surface exactly once in turn 1.
            var recoveredInTurn1 = turn1Events.Where(p => p.EventType == "recovered_answer").ToList();
            Assert.Single(recoveredInTurn1);
            Assert.Equal("recovered stale answer", recoveredInTurn1[0].Summary);
            Assert.Contains(turn1Events, p => p.FinalResult == "turn 1 answer");

            // ── Turn 2 ──────────────────────────────────────────────────────────────
            var turn2Events = new List<AgentProgress>();
            var turn2 = Task.Run(async () =>
            {
                await foreach (var p in executor.ExecuteAsync("task 2", ct: cts.Token))
                    turn2Events.Add(p);
            });

            await Task.Delay(150, cts.Token);
            channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "turn 2 answer" });
            await turn2;

            // _preservedDrainedAnswerText must have been cleared in turn 1 (line ~157).
            // Turn 2's drain finds nothing; the if-block must not fire.
            Assert.DoesNotContain(turn2Events, p => p.EventType == "recovered_answer");
            Assert.Contains(turn2Events, p => p.FinalResult == "turn 2 answer");
        }
        finally
        {
            process.Kill();
            process.Dispose();
        }
    }

    // --- #429: the final-answer gate during foreground tool calls ---

    private static ClaudeStreamEvent Narration(string text = "Running the next call now.", string? parent = null, string? origin = null) =>
        new()
        {
            Type = "assistant",
            ParentToolUseId = parent,
            Origin = origin is null ? null : new ClaudeMessageOrigin { Kind = origin },
            Message = new ClaudeMessage { Content = [new ClaudeContentBlock { Type = "text", Text = text }] },
        };

    private static ClaudeStreamEvent ToolCall(string? parent = null, string? origin = null) =>
        new()
        {
            Type = "assistant",
            ParentToolUseId = parent,
            Origin = origin is null ? null : new ClaudeMessageOrigin { Kind = origin },
            Message = new ClaudeMessage { Content = [new ClaudeContentBlock { Type = "tool_use", Name = "Bash", Id = "x" }] },
        };

    private static ClaudeStreamEvent NarrationWithToolCall() =>
        new()
        {
            Type = "assistant",
            Message = new ClaudeMessage
            {
                Content =
                [
                    new ClaudeContentBlock { Type = "text", Text = "Running the next call now." },
                    new ClaudeContentBlock { Type = "tool_use", Name = "Bash", Id = "x" },
                ],
            },
        };

    private static ClaudeStreamEvent CurrentResult(string? result = "done") =>
        new() { Type = "result", Result = result };

    /// <summary>A live /bin/cat process with a <see cref="StringWriter"/> standing in for its stdin.</summary>
    private sealed class LiveExecutor : IDisposable
    {
        private readonly System.Diagnostics.Process _process;

        public ClaudeExecutor Executor { get; }
        public StringWriter Stdin { get; } = new();
        public ClaudeExecutorLogRecorder Logs { get; } = new();

        public LiveExecutor()
        {
            _process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/cat",
                RedirectStandardInput = true,
                UseShellExecute = false,
            })!;
            Executor = BuildExecutor(Logs);
            Executor.SetProcessForTests(_process);
            Executor.SetStdinForTests(Stdin);
        }

        public string[] Lines => Stdin.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        public void Dispose()
        {
            try { _process.Kill(); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }

    /// <summary>AC1 (S1/S4): narration, then its tool call, reopens the gate once.</summary>
    [Fact]
    public async Task Narration_ThenToolUse_ReopensGate_InjectionSucceeds()
    {
        using var live = new LiveExecutor();
        live.Executor.OpenTurnForTests();

        live.Executor.ParseProgressForTests(Narration());
        Assert.True(live.Executor.TurnCommittedToFinalAnswerForTests);
        live.Executor.ParseProgressForTests(ToolCall());

        Assert.False(live.Executor.TurnCommittedToFinalAnswerForTests);
        Assert.Equal(1, live.Executor.GateReopenCountForTests);
        var result = await live.Executor.TryInjectMessageAsync("steer");
        Assert.Equal(MidTurnInjectionStatus.Injected, result.Status);
        Assert.Single(live.Lines);
    }

    /// <summary>AC2 (S2): the E1 shape, 40 narrated foreground calls.</summary>
    [Fact]
    public async Task FortyNarratedToolCalls_InjectableDuringEachTool_RefusedInEachGap_OneReopenLine()
    {
        using var live = new LiveExecutor();
        live.Executor.OpenTurnForTests();

        for (var call = 1; call <= 40; call++)
        {
            live.Executor.ParseProgressForTests(Narration($"Running call {call} now."));
            var inGap = await live.Executor.TryInjectMessageAsync("steer");
            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, inGap.Status);

            live.Executor.ParseProgressForTests(ToolCall());
            var duringTool = await live.Executor.TryInjectMessageAsync("steer");
            Assert.Equal(MidTurnInjectionStatus.Injected, duringTool.Status);
        }

        Assert.Equal(40, live.Executor.GateReopenCountForTests);
        live.Executor.ParseProgressForTests(Narration("All 40 calls finished."));
        live.Executor.ParseProgressForTests(CurrentResult());

        Assert.Single(live.Logs.Information, line => line == "Final-answer gate reopened 40 time(s) this turn");
    }

    /// <summary>AC2 (S3): text and tool_use in one event never set the gate and count no reopen.</summary>
    [Fact]
    public void FortyCombinedTextAndToolEvents_GateNeverSet_NoReopenLine()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        executor.OpenTurnForTests();

        for (var call = 1; call <= 40; call++)
        {
            executor.ParseProgressForTests(NarrationWithToolCall());
            Assert.False(executor.TurnCommittedToFinalAnswerForTests);
        }

        Assert.Equal(0, executor.GateReopenCountForTests);
        executor.ParseProgressForTests(CurrentResult());
        Assert.DoesNotContain(logs.Information, line => line.StartsWith("Final-answer gate reopened", StringComparison.Ordinal));
    }

    /// <summary>AC3 (S5/S7): true final text, then the current result — the #236 gate holds.</summary>
    [Fact]
    public async Task FinalTextThenResult_GateStaysClosed_LaterToolUseDoesNotReopen()
    {
        using var live = new LiveExecutor();
        live.Executor.OpenTurnForTests();
        live.Executor.ParseProgressForTests(ToolCall());
        live.Executor.ParseProgressForTests(Narration("Here is the answer."));

        // S7: final text, no result yet.
        var beforeResult = await live.Executor.TryInjectMessageAsync("late");
        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, beforeResult.Status);

        live.Executor.ParseProgressForTests(CurrentResult());
        live.Executor.ParseProgressForTests(ToolCall());

        Assert.True(live.Executor.TurnCommittedToFinalAnswerForTests);
        Assert.True(live.Executor.CurrentTurnResultSeenForTests);
        var result = await live.Executor.TryInjectMessageAsync("late");
        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
        Assert.Contains("final answer", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mid-turn injection refused: gate_closed", live.Logs.Information);
        Assert.Empty(live.Lines);
    }

    /// <summary>A turn that ends with a result but no final text still closes (max turns, error).</summary>
    [Fact]
    public async Task ResultWithoutFinalText_ClosesBothFlags()
    {
        using var live = new LiveExecutor();
        live.Executor.OpenTurnForTests();
        live.Executor.ParseProgressForTests(ToolCall());

        live.Executor.ParseProgressForTests(new ClaudeStreamEvent { Type = "result", Subtype = "error_max_turns", IsError = true });

        Assert.True(live.Executor.TurnCommittedToFinalAnswerForTests);
        Assert.True(live.Executor.CurrentTurnResultSeenForTests);
        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, (await live.Executor.TryInjectMessageAsync("late")).Status);
        Assert.Empty(live.Lines);
    }

    /// <summary>AC5 (S8): nested events neither set nor clear the gate.</summary>
    [Fact]
    public void NestedEvents_NeverSetOrClearTheGate()
    {
        var executor = BuildExecutor();
        executor.OpenTurnForTests();

        executor.ParseProgressForTests(Narration("subagent text", parent: "toolu_parent"));
        Assert.False(executor.TurnCommittedToFinalAnswerForTests);

        executor.ParseProgressForTests(Narration("Here is the answer."));
        executor.ParseProgressForTests(ToolCall(parent: "toolu_parent"));

        Assert.True(executor.TurnCommittedToFinalAnswerForTests);
        Assert.Equal(0, executor.GateReopenCountForTests);
    }

    /// <summary>AC6 (S9): background-origin events do not reopen the gate or close the turn.</summary>
    [Fact]
    public void NonCurrentOriginEvents_DoNotReopenOrMarkTheResultSeen()
    {
        var executor = BuildExecutor();
        executor.OpenTurnForTests();
        executor.ParseProgressForTests(Narration("Here is the answer."));

        executor.ParseProgressForTests(ToolCall(origin: "task-notification"));
        Assert.True(executor.TurnCommittedToFinalAnswerForTests);
        Assert.Equal(0, executor.GateReopenCountForTests);

        executor.ParseProgressForTests(new ClaudeStreamEvent
        {
            Type = "result",
            Result = "background",
            Origin = new ClaudeMessageOrigin { Kind = "task-notification" },
        });
        Assert.False(executor.CurrentTurnResultSeenForTests);
    }

    /// <summary>AC9 (S12): final text parsed while the injection waits for the stdin lock.</summary>
    [Fact]
    public async Task FinalTextWhileWaitingForTheLock_RefusedUnderLock_NothingWritten()
    {
        using var live = new LiveExecutor();
        live.Executor.OpenTurnForTests();
        live.Executor.ParseProgressForTests(ToolCall());

        await live.Executor.StdinWriteLockForTests.WaitAsync();
        var injection = live.Executor.TryInjectMessageAsync("steer");
        live.Executor.ParseProgressForTests(Narration("Here is the answer."));
        live.Executor.StdinWriteLockForTests.Release();
        var result = await injection;

        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
        Assert.Contains("Mid-turn injection refused: gate_closed_under_lock", live.Logs.Information);
        Assert.Empty(live.Lines);
        Assert.Equal(1, live.Executor.StdinWriteLockForTests.CurrentCount);
    }

    /// <summary>AC9, second case: the lock is released without final text, so one line is written.</summary>
    [Fact]
    public async Task LockReleasedWithoutFinalText_Injected_OneLineWritten()
    {
        using var live = new LiveExecutor();
        live.Executor.OpenTurnForTests();
        live.Executor.ParseProgressForTests(ToolCall());

        await live.Executor.StdinWriteLockForTests.WaitAsync();
        var injection = live.Executor.TryInjectMessageAsync("steer");
        live.Executor.StdinWriteLockForTests.Release();
        var result = await injection;

        Assert.Equal(MidTurnInjectionStatus.Injected, result.Status);
        Assert.Single(live.Lines);
        Assert.Equal(1, live.Executor.StdinWriteLockForTests.CurrentCount);
    }

    private static ClaudeExecutor BuildExecutor(ILogger<ClaudeExecutor>? logger = null)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
        });
        var promptBuilder = new PromptBuilder(options, NullLogger<PromptBuilder>.Instance);
        return new ClaudeExecutor(options, logger ?? NullLogger<ClaudeExecutor>.Instance, promptBuilder);
    }

    private sealed class SlowChunkingTextWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => _buffer.Append(value);

        public override void Write(string? value) => _buffer.Append(value);

        public override void WriteLine(string? value)
        {
            _buffer.Append(value);
            _buffer.AppendLine();
        }

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            await WriteAsync(buffer, cancellationToken);
            await WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
        }

        public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            var text = buffer.ToString();
            foreach (var ch in text)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _buffer.Append(ch);
                await Task.Yield();
            }
        }

        public override string ToString() => _buffer.ToString();
    }
}

/// <summary>Records rendered <see cref="ClaudeExecutor"/> log lines by level (#429 reason and reopen logs).</summary>
internal sealed class ClaudeExecutorLogRecorder : ILogger<ClaudeExecutor>
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();
    public IReadOnlyList<string> Information => Of(LogLevel.Information);
    public IReadOnlyList<string> Warnings => Of(LogLevel.Warning);

    private IReadOnlyList<string> Of(LogLevel level) =>
        _entries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}
