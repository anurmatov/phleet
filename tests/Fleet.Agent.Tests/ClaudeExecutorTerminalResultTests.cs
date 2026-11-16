using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

public class ClaudeExecutorTerminalResultTests
{
    [Fact]
    public async Task ExecuteAsync_SuccessfulTurnFixture_EmitsOneTerminalFinalResult()
    {
        var events = await RunFixtureAsync("claude-successful-turn.ndjson");

        var final = Assert.Single(events, progress => progress.FinalResult is not null);
        Assert.Equal("result", final.EventType);
        Assert.Equal("RELAY-PROBE-OK", final.FinalResult);
    }

    [Fact]
    public async Task ExecuteAsync_NestedBackgroundFixture_EmitsOnlyParentTerminalResult()
    {
        var events = await RunFixtureAsync("claude-nested-background-turn.ndjson");

        var final = Assert.Single(events, progress => progress.FinalResult is not null);
        Assert.Equal("result", final.EventType);
        Assert.Equal("PARENT-FINAL-OK", final.FinalResult);
        Assert.DoesNotContain(events, progress =>
            progress.FinalResult?.Contains("BACKGROUND-PROGRESS", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ExecuteAsync_NestedAssistant_DoesNotSeedEmptyParentFallback()
    {
        var events = await RunEventsAsync(
            TextAssistant("NESTED-FALLBACK-MUST-NOT-RELAY", parentToolUseId: "toolu-nested"),
            new ClaudeStreamEvent
            {
                Type = "result",
                Result = "NESTED-FALLBACK-MUST-NOT-RELAY",
                Origin = new ClaudeMessageOrigin { Kind = "task-notification" },
                NumTurns = 1,
            },
            new ClaudeStreamEvent { Type = "result", Result = "", NumTurns = 1 });

        Assert.DoesNotContain(events, progress => progress.FinalResult is not null);
    }

    [Fact]
    public async Task ExecuteAsync_SkippedResult_LogsSafeProvenanceWarning()
    {
        var logger = Substitute.For<ILogger<ClaudeExecutor>>();
        using var harness = new ExecutorHarness(logger: logger);

        await harness.RunTurnAsync(
            new ClaudeStreamEvent
            {
                Type = "result",
                Subtype = "success",
                Result = "RESULT-TEXT-MUST-NOT-BE-LOGGED",
                SessionId = "SESSION-ID-MUST-NOT-BE-LOGGED",
                Origin = new ClaudeMessageOrigin { Kind = "unexpected-origin" },
            },
            new ClaudeStreamEvent { Type = "result", Result = "PARENT-FINAL", NumTurns = 1 });

        var warning = Assert.Single(logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && call.GetArguments()[0] is LogLevel.Warning);
        var message = warning.GetArguments()[2]?.ToString() ?? string.Empty;
        Assert.Contains("unexpected-origin", message, StringComparison.Ordinal);
        Assert.Contains("success", message, StringComparison.Ordinal);
        Assert.DoesNotContain("RESULT-TEXT-MUST-NOT-BE-LOGGED", message, StringComparison.Ordinal);
        Assert.DoesNotContain("SESSION-ID-MUST-NOT-BE-LOGGED", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_TerminalError_PreservesErrorSessionAndStructuredOutput()
    {
        using var structured = JsonDocument.Parse("""{"status":"error"}""");
        using var modelUsage = JsonDocument.Parse(
            """{"synthetic-model":{"inputTokens":12,"outputTokens":3,"contextWindow":1000,"costUSD":0.01}}""");
        var events = await RunEventsAsync(
            TextAssistant("SYNTHETIC-ERROR"),
            new ClaudeStreamEvent
            {
                Type = "result",
                Result = "SYNTHETIC-ERROR",
                IsError = true,
                NumTurns = 1,
                SessionId = "fixture-error-session",
                StructuredOutput = structured.RootElement.Clone(),
                ExtensionData = new Dictionary<string, object>
                {
                    ["modelUsage"] = modelUsage.RootElement.Clone(),
                },
            });

        var final = Assert.Single(events, progress => progress.FinalResult is not null);
        Assert.Equal("result", final.EventType);
        Assert.Equal("SYNTHETIC-ERROR", final.FinalResult);
        Assert.True(final.IsErrorResult);
        Assert.Equal("fixture-error-session", final.SessionId);
        Assert.Equal("""{"status":"error"}""", final.StructuredOutput);

        var stats = Assert.Single(events, progress => progress.Stats is not null).Stats!;
        Assert.Equal(12, stats.InputTokens);
        Assert.Equal(3, stats.OutputTokens);
        Assert.Equal(1, stats.NumTurns);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTerminalResult_PromotesTopLevelAssistantTextOnce()
    {
        var events = await RunEventsAsync(
            TextAssistant("EMPTY-RESULT-FALLBACK"),
            new ClaudeStreamEvent
            {
                Type = "result",
                Result = "",
                IsError = false,
                NumTurns = 1,
                SessionId = "fixture-empty-session",
            });

        var final = Assert.Single(events, progress => progress.FinalResult is not null);
        Assert.Equal("result", final.EventType);
        Assert.Equal("EMPTY-RESULT-FALLBACK", final.FinalResult);
    }

    [Fact]
    public async Task ExecuteAsync_MaxTurnResult_PreservesStructuredOutput()
    {
        using var structured = JsonDocument.Parse("""{"blockers":["synthetic blocker"]}""");
        using var harness = new ExecutorHarness(maxTurns: 1);

        var events = await harness.RunTurnAsync(
            TextAssistant("MAX-TURN-ANSWER"),
            new ClaudeStreamEvent
            {
                Type = "result",
                Result = "MAX-TURN-ANSWER",
                IsError = false,
                NumTurns = 1,
                StructuredOutput = structured.RootElement.Clone(),
            });

        var final = Assert.Single(events, progress => progress.FinalResult is not null);
        Assert.True(final.IsErrorResult);
        Assert.Equal("""{"blockers":["synthetic blocker"]}""", final.StructuredOutput);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTerminalResult_DoesNotReusePriorTurnAssistantText()
    {
        using var harness = new ExecutorHarness();

        var first = await harness.RunTurnAsync(
            TextAssistant("FIRST-TURN-FALLBACK"),
            new ClaudeStreamEvent { Type = "result", Result = "", NumTurns = 1 });
        var second = await harness.RunTurnAsync(
            new ClaudeStreamEvent { Type = "result", Result = "", NumTurns = 1 });

        Assert.Equal("FIRST-TURN-FALLBACK", Assert.Single(first, p => p.FinalResult is not null).FinalResult);
        Assert.DoesNotContain(second, progress => progress.FinalResult is not null);
    }

    /// <summary>
    /// #254 under #429 S1/S2: narration before foreground tool calls reopens injection, but the
    /// terminal result still owns the one final answer, and nested or background events still
    /// cannot supply it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NarratedToolCalls_ResultStillOwnsTheOneFinalAnswer()
    {
        var events = await RunEventsAsync(
            TextAssistant("NARRATION-ONE"),
            ToolUseAssistant(),
            TextAssistant("NESTED-MUST-NOT-RELAY", parentToolUseId: "toolu-nested"),
            new ClaudeStreamEvent
            {
                Type = "result",
                Result = "BACKGROUND-MUST-NOT-RELAY",
                Origin = new ClaudeMessageOrigin { Kind = "task-notification" },
                NumTurns = 1,
            },
            TextAssistant("NARRATION-TWO"),
            ToolUseAssistant(),
            TextAssistant("FINAL-ANSWER"),
            new ClaudeStreamEvent { Type = "result", Result = "", NumTurns = 1 });

        var final = Assert.Single(events, progress => progress.FinalResult is not null);
        Assert.Equal("result", final.EventType);
        Assert.Equal("FINAL-ANSWER", final.FinalResult);
    }

    // ── mid-turn messages (#286) ─────────────────────────────────────────────

    /// <summary>
    /// Text is a mid-turn message only once a tool call has followed it, and it leaves on that
    /// tool call's event. The last text, which nothing follows but the result, is the answer and
    /// is never sent as narration, so no text reaches a client twice.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TextFollowedByAToolCall_LeavesAsNarrationOnThatCall_AndTheAnswerDoesNot()
    {
        var events = await RunEventsAsync(
            TextAssistant("I will read the file first."),
            ToolUseAssistant(),
            TextAssistant("Now the tests."),
            ToolUseAssistant(),
            TextAssistant("FINAL-ANSWER"),
            new ClaudeStreamEvent { Type = "result", Result = "FINAL-ANSWER", NumTurns = 1 });

        Assert.Equal(
            ["I will read the file first.", "Now the tests."],
            events.Where(e => e.Narration is not null).Select(e => e.Narration));
        Assert.All(events.Where(e => e.Narration is not null), e => Assert.Equal("Bash", e.ToolName));
        Assert.DoesNotContain(events, e => e.Narration == "FINAL-ANSWER");
        Assert.Equal("FINAL-ANSWER", Assert.Single(events, e => e.FinalResult is not null).FinalResult);
    }

    [Fact]
    public async Task ExecuteAsync_TextInTheSameMessageAsTheToolCall_IsNarrationToo_AfterAnyHeldText()
    {
        var events = await RunEventsAsync(
            TextAssistant("Held from the message before."),
            new ClaudeStreamEvent
            {
                Type = "assistant",
                Message = new ClaudeMessage
                {
                    Content =
                    [
                        new ClaudeContentBlock { Type = "text", Text = "Written beside the call." },
                        new ClaudeContentBlock { Type = "tool_use", Name = "Bash", Id = "toolu-call" },
                    ],
                },
            },
            ToolUseAssistant(),
            new ClaudeStreamEvent { Type = "result", Result = "done", NumTurns = 1 });

        Assert.Equal(
            ["Held from the message before.\n\nWritten beside the call."],
            events.Where(e => e.Narration is not null).Select(e => e.Narration));
    }

    // ── every tool call of a message ─────────────────────────────────────────

    /// <summary>
    /// One assistant event may hold several <c>tool_use</c> blocks (tool calls made in parallel).
    /// Each is a tool call of its own, reported in the order of the blocks. Only the first block
    /// used to be reported, so the chat, the tool list and a client saw one call where there were three.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SeveralToolCallsInOneMessage_AreEachReported_InOrder()
    {
        var events = await RunEventsAsync(
            TextAssistant("Held from the message before."),
            Frame("""
                {"type":"assistant","message":{"content":[
                  {"type":"text","text":"Reading both at once."},
                  {"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/workspace/example/a.txt"}},
                  {"type":"tool_use","id":"toolu_2","name":"Grep","input":{"pattern":"synthetic-marker"}},
                  {"type":"tool_use","id":"toolu_3","name":"mcp__files__read_file","input":{"path":"/workspace/example/b.txt"}}
                ]},"parent_tool_use_id":null}
                """),
            new ClaudeStreamEvent { Type = "result", Result = "done", NumTurns = 1 });

        var calls = events.Where(e => e.ToolName is not null).ToList();
        Assert.Equal(["Read", "Grep", "mcp__files__read_file"], calls.Select(c => c.ToolName));
        Assert.Equal(
            ["Reading /workspace/example/a.txt", "Searching for \"synthetic-marker\"", "Using read_file"],
            calls.Select(c => c.Summary));
        Assert.Equal(
            ["{\"file_path\":\"/workspace/example/a.txt\"}", "{\"pattern\":\"synthetic-marker\"}", "{\"path\":\"/workspace/example/b.txt\"}"],
            calls.Select(c => c.ToolArgs));
        Assert.All(calls, c => Assert.True(c.IsSignificant));

        // The text before the calls belongs to the first of them and to no other.
        Assert.Equal("Held from the message before.\n\nReading both at once.", calls[0].Narration);
        Assert.All(calls.Skip(1), c => Assert.Null(c.Narration));
    }

    /// <summary>The nested rule is per event, whatever the number of calls: none of them carries narration.</summary>
    [Fact]
    public async Task ExecuteAsync_SeveralNestedToolCallsInOneMessage_AreEachReported_AndCarryNoNarration()
    {
        var events = await RunEventsAsync(
            TextAssistant("PARENT-TEXT"),
            Frame("""
                {"type":"assistant","message":{"content":[
                  {"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/workspace/example/a.txt"}},
                  {"type":"tool_use","id":"toolu_2","name":"Read","input":{"file_path":"/workspace/example/b.txt"}}
                ]},"parent_tool_use_id":"toolu_nested"}
                """),
            ToolUseAssistant(),
            new ClaudeStreamEvent { Type = "result", Result = "done", NumTurns = 1 });

        var calls = events.Where(e => e.ToolName is not null).ToList();
        Assert.Equal(["Read", "Read", "Bash"], calls.Select(c => c.ToolName));
        Assert.Null(calls[0].Narration);
        Assert.Null(calls[1].Narration);
        Assert.Equal("PARENT-TEXT", calls[2].Narration);
    }

    /// <summary>
    /// A Bash command is described whole, on one line. It was cut at 80 characters, and because
    /// the chat shows this summary and not the arguments, those 80 were all anyone saw of it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ABashCommand_IsDescribedWhole_OnOneLine()
    {
        const string command = "cd /workspace/example &&\n  dotnet test tests/Example.Tests --filter \"FullyQualifiedName~Synthetic\" --logger \"console;verbosity=detailed\" | tail -40";
        var input = new JsonObject { ["command"] = command, ["description"] = "Run the synthetic tests" };

        var events = await RunEventsAsync(
            Frame($$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{{input.ToJsonString()}}}]},"parent_tool_use_id":null}"""),
            new ClaudeStreamEvent { Type = "result", Result = "done", NumTurns = 1 });

        var call = Assert.Single(events, e => e.ToolName == "Bash");
        Assert.Equal("Running: " + command.Replace("\n", " "), call.Summary);
        Assert.True(call.Summary.Length > 100);
    }

    /// <summary>
    /// The arguments leave the executor in full. They were cut at 500 characters here, ahead of the
    /// one setting that decides how much of them is shown (<c>AgentOptions.ToolArgsTruncateLength</c>).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ToolArguments_AreNotCutByTheExecutor()
    {
        var input = new JsonObject { ["path"] = "/workspace/example/notes.txt", ["text"] = new string('n', 2000) };

        var events = await RunEventsAsync(
            Frame($$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"mcp__files__write_file","input":{{input.ToJsonString()}}}]},"parent_tool_use_id":null}"""),
            new ClaudeStreamEvent { Type = "result", Result = "done", NumTurns = 1 });

        var call = Assert.Single(events, e => e.ToolName is not null);
        Assert.Equal(input.ToJsonString(), call.ToolArgs);
    }

    private static ClaudeStreamEvent Frame(string json) =>
        JsonSerializer.Deserialize<ClaudeStreamEvent>(json) ?? throw new InvalidOperationException("The frame deserialized to null.");

    /// <summary>
    /// A nested (subagent) tool call or text says nothing about the parent turn's text: it neither
    /// carries narration out nor becomes narration.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NestedEvents_NeitherTakeNorBecomeNarration()
    {
        var nestedCall = ToolUseAssistant();
        nestedCall.ParentToolUseId = "toolu-nested";

        var events = await RunEventsAsync(
            TextAssistant("PARENT-TEXT"),
            nestedCall,
            TextAssistant("NESTED-TEXT", parentToolUseId: "toolu-nested"),
            ToolUseAssistant(),
            new ClaudeStreamEvent { Type = "result", Result = "done", NumTurns = 1 });

        var narrated = Assert.Single(events, e => e.Narration is not null);
        Assert.Equal("PARENT-TEXT", narrated.Narration);
    }

    /// <summary>Text that was the last thing before the result does not leak into the next turn as narration.</summary>
    [Fact]
    public async Task ExecuteAsync_TheAnswerOfOneTurn_IsNotNarrationInTheNext()
    {
        using var harness = new ExecutorHarness();
        await harness.RunTurnAsync(
            TextAssistant("FIRST-TURN-ANSWER"),
            new ClaudeStreamEvent { Type = "result", Result = "FIRST-TURN-ANSWER", NumTurns = 1 });
        var second = await harness.RunTurnAsync(
            ToolUseAssistant(),
            new ClaudeStreamEvent { Type = "result", Result = "second", NumTurns = 1 });

        Assert.DoesNotContain(second, e => e.Narration is not null);
    }

    private static ClaudeStreamEvent ToolUseAssistant() =>
        new()
        {
            Type = "assistant",
            Message = new ClaudeMessage
            {
                Content = [new ClaudeContentBlock { Type = "tool_use", Name = "Bash", Id = "toolu-call" }],
            },
        };

    private static ClaudeStreamEvent TextAssistant(string text, string? parentToolUseId = null) =>
        new()
        {
            Type = "assistant",
            ParentToolUseId = parentToolUseId,
            Message = new ClaudeMessage
            {
                Content = [new ClaudeContentBlock { Type = "text", Text = text }],
            },
        };

    private static async Task<IReadOnlyList<AgentProgress>> RunFixtureAsync(string name) =>
        await RunEventsAsync(ReadFixture(name).ToArray());

    private static async Task<IReadOnlyList<AgentProgress>> RunEventsAsync(params ClaudeStreamEvent[] events)
    {
        using var harness = new ExecutorHarness();
        return await harness.RunTurnAsync(events);
    }

    private static IEnumerable<ClaudeStreamEvent> ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            yield return JsonSerializer.Deserialize<ClaudeStreamEvent>(line)
                ?? throw new InvalidOperationException($"Fixture line in {name} deserialized to null.");
        }
    }

    private sealed class ExecutorHarness : IDisposable
    {
        private readonly StandInProcess _standIn;
        private readonly ClaudeExecutor _executor;
        private readonly Channel<ClaudeStreamEvent> _events = Channel.CreateUnbounded<ClaudeStreamEvent>();
        private readonly SignalingTextWriter _stdin = new();

        public ExecutorHarness(int maxTurns = 100, ILogger<ClaudeExecutor>? logger = null)
        {
            _standIn = new StandInProcess();
            _executor = BuildExecutor(maxTurns, logger ?? NullLogger<ClaudeExecutor>.Instance);
            _executor.SetProcessForTests(_standIn.Process);
            _executor.SetStdinForTests(_stdin);
            _executor.SetEventChannelForTests(_events);
        }

        public async Task<IReadOnlyList<AgentProgress>> RunTurnAsync(params ClaudeStreamEvent[] events)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var turn = Task.Run(async () =>
            {
                var progress = new List<AgentProgress>();
                await foreach (var item in _executor.ExecuteAsync("synthetic fixture turn", ct: cts.Token))
                    progress.Add(item);
                return (IReadOnlyList<AgentProgress>)progress;
            }, cts.Token);

            await _stdin.WaitForWriteAsync(cts.Token);
            foreach (var evt in events)
                await _events.Writer.WriteAsync(evt, cts.Token);

            return await turn.WaitAsync(cts.Token);
        }

        public void Dispose()
        {
            _executor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _standIn.Dispose();
        }
    }

    private sealed class SignalingTextWriter : TextWriter
    {
        private readonly Channel<bool> _writes = Channel.CreateUnbounded<bool>();

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            _writes.Writer.TryWrite(true);
            return Task.CompletedTask;
        }

        public Task WaitForWriteAsync(CancellationToken cancellationToken) =>
            _writes.Reader.ReadAsync(cancellationToken).AsTask();
    }

    private static ClaudeExecutor BuildExecutor(int maxTurns, ILogger<ClaudeExecutor> logger)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
            MaxTurns = maxTurns,
        });
        var promptBuilder = new PromptBuilder(options, NullLogger<PromptBuilder>.Instance);
        return new ClaudeExecutor(options, logger, promptBuilder);
    }
}
