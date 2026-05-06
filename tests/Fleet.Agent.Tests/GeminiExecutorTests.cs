using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for GeminiExecutor bridge event mapping logic (issue #145).
/// Tests cover the JSONL bridge protocol (mirrors codex-bridge) — ack, turn lifecycle,
/// text streaming, tool use/result, error handling — without spawning a real bridge process.
/// </summary>
public class GeminiExecutorTests
{
    private static GeminiExecutor CreateExecutor()
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/workspace",
            Model = "gemini-2.5-flash",
        });
        var promptBuilder = new PromptBuilder(options, NullLogger<PromptBuilder>.Instance);
        return new GeminiExecutor(options, promptBuilder, NullLogger<GeminiExecutor>.Instance);
    }

    private static GeminiExecutor.BridgeEvent ParseEvent(string json)
    {
        var ev = System.Text.Json.JsonSerializer.Deserialize<GeminiExecutor.BridgeEvent>(
            json, GeminiExecutor.BridgeEvent.JsonOptions);
        return ev ?? throw new InvalidOperationException("Deserialized null");
    }

    // ── Behavioral contracts ──────────────────────────────────────────────────

    [Fact]
    public void IsProcessWarm_FalseBeforeStart()
    {
        // Bridge process has not been started yet — warm = false.
        var executor = CreateExecutor();
        Assert.False(executor.IsProcessWarm);
    }

    [Fact]
    public void GetActiveBackgroundTasks_AlwaysEmpty()
    {
        var executor = CreateExecutor();
        Assert.Empty(executor.GetActiveBackgroundTasks());
    }

    [Fact]
    public async Task StopProcessAsync_NoProcessRunning_CompletesImmediately()
    {
        var executor = CreateExecutor();
        // StopProcessAsync on a never-started executor must not throw.
        await executor.StopProcessAsync();
    }

    [Fact]
    public async Task TryStopProcessAsync_NoProcessRunning_ReturnsFalse()
    {
        var executor = CreateExecutor();
        var stopped = await executor.TryStopProcessAsync();
        Assert.False(stopped);
    }

    // ── MapEvent: ack ─────────────────────────────────────────────────────────

    [Fact]
    public void MapEvent_Ack_ReturnsSystemConnected()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"ack\"}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("system", result!.EventType);
        Assert.Equal("Connected", result.Summary);
        Assert.False(result.IsSignificant);
    }

    // ── MapEvent: turn lifecycle ──────────────────────────────────────────────

    [Fact]
    public void MapEvent_TurnStarted_ReturnsSystemProcessing()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"turn.started\"}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("system", result!.EventType);
        Assert.Equal("Processing...", result.Summary);
        Assert.False(result.IsSignificant);
    }

    [Fact]
    public void MapEvent_TurnCompleted_ReturnsSignificantResult()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"turn.completed\",\"text\":\"Hello world\",\"durationMs\":500," +
                            "\"usage\":{\"inputTokens\":100,\"outputTokens\":50}}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("result", result!.EventType);
        Assert.Equal("Hello world", result.FinalResult);
        Assert.Equal("Hello world", result.Summary);
        Assert.True(result.IsSignificant);
        Assert.Equal(100, result.Stats?.InputTokens);
        Assert.Equal(50,  result.Stats?.OutputTokens);
        Assert.Equal(500, result.Stats?.DurationMs);
    }

    [Fact]
    public void MapEvent_TurnCompleted_NullText_ReturnsFinalResultEmptyString()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"turn.completed\",\"durationMs\":0}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("", result!.FinalResult);
        Assert.True(result.IsSignificant);
    }

    // ── MapEvent: streaming text ──────────────────────────────────────────────

    [Fact]
    public void MapEvent_ItemStartedMessage_ReturnsAssistantChunk_NotSignificant()
    {
        // Text chunks must NOT be significant — only turn.completed triggers Telegram routing
        // (PR #129 pattern, same as codex-bridge chunks).
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"item.started\",\"itemType\":\"message\",\"text\":\"Hello\"}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("assistant", result!.EventType);
        Assert.Equal("Hello", result.Summary);
        Assert.Null(result.FinalResult);  // intermediate — no final result
        Assert.False(result.IsSignificant);
    }

    // ── MapEvent: tool use / result ───────────────────────────────────────────

    [Fact]
    public void MapEvent_ItemStartedToolUse_ReturnsToolUseSignificant()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"item.started\",\"itemType\":\"tool_use\"," +
                            "\"toolName\":\"memory_get\",\"toolArgs\":\"{\\\"id\\\":\\\"abc\\\"}\"}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("tool_use", result!.EventType);
        Assert.Equal("memory_get", result.ToolName);
        Assert.Equal("{\"id\":\"abc\"}", result.ToolArgs);
        Assert.True(result.IsSignificant);
    }

    [Fact]
    public void MapEvent_ItemCompletedToolResult_ReturnsToolResult_NotSignificant()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"item.completed\",\"itemType\":\"tool_result\",\"text\":\"result text\"}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("tool_result", result!.EventType);
        Assert.Equal("result text", result.Summary);
        Assert.False(result.IsSignificant);
    }

    // ── MapEvent: errors ──────────────────────────────────────────────────────

    [Fact]
    public void MapEvent_TurnFailed_ReturnsErrorResult()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"turn.failed\",\"error\":\"rate limit exceeded\"}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("result", result!.EventType);
        Assert.Equal("rate limit exceeded", result.FinalResult);
        Assert.True(result.IsErrorResult);
        Assert.True(result.IsSignificant);
    }

    [Fact]
    public void MapEvent_Error_ReturnsErrorResult()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"error\",\"message\":\"Invalid JSON on stdin\"}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("result", result!.EventType);
        Assert.Equal("Invalid JSON on stdin", result.FinalResult);
        Assert.True(result.IsErrorResult);
    }

    [Fact]
    public void MapEvent_TurnFailed_NullError_FallsBackToDefaultMessage()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"turn.failed\"}");

        var result = executor.MapEvent(ev);

        Assert.NotNull(result);
        Assert.Equal("Turn failed", result!.FinalResult);
        Assert.True(result.IsErrorResult);
    }

    // ── MapEvent: unknown event types ─────────────────────────────────────────

    [Fact]
    public void MapEvent_UnknownType_ReturnsNull()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"heartbeat\",\"ts\":1234567890}");

        var result = executor.MapEvent(ev);

        Assert.Null(result);
    }

    [Fact]
    public void MapEvent_ItemStartedUnknownItemType_ReturnsNull()
    {
        var executor = CreateExecutor();
        var ev = ParseEvent("{\"type\":\"item.started\",\"itemType\":\"unknown_kind\",\"text\":\"x\"}");

        var result = executor.MapEvent(ev);

        Assert.Null(result);
    }
}
