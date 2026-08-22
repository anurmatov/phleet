using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
