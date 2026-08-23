using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
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
        Assert.False(executor.TurnCommittedToFinalAnswerForTests);

        executor.ParseProgressForTests(TextOnlyAssistantEvent());

        Assert.True(executor.TurnCommittedToFinalAnswerForTests);
    }

    [Fact]
    public void ToolUseAssistantEvent_DoesNotSetCommittedFlag()
    {
        var executor = BuildExecutor();

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

    [Fact]
    public void CommittedFlag_IsFalseOnFreshExecutor()
    {
        // The flag must start cleared so the first turn is always injectable.
        var executor = BuildExecutor();
        Assert.False(executor.TurnCommittedToFinalAnswerForTests);
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

    private static ClaudeExecutor BuildExecutor()
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
        });
        var promptBuilder = new PromptBuilder(options, NullLogger<PromptBuilder>.Instance);
        return new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance, promptBuilder);
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
