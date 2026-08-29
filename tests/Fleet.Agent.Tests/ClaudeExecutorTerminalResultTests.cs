using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        private readonly Process _process;
        private readonly ClaudeExecutor _executor;
        private readonly Channel<ClaudeStreamEvent> _events = Channel.CreateUnbounded<ClaudeStreamEvent>();
        private readonly SignalingTextWriter _stdin = new();

        public ExecutorHarness(int maxTurns = 100)
        {
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/cat",
                RedirectStandardInput = true,
                UseShellExecute = false,
            })!;
            _executor = BuildExecutor(maxTurns);
            _executor.SetProcessForTests(_process);
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
            _process.Dispose();
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

    private static ClaudeExecutor BuildExecutor(int maxTurns)
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
        return new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance, promptBuilder);
    }
}
