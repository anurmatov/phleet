using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for GeminiExecutor event mapping logic (issue #132).
/// Tests cover stream-json parsing, tool call/result handling, error events,
/// and the raw Gemini API candidates format — all without spawning a real process.
/// </summary>
public class GeminiExecutorTests
{
    private static GeminiExecutor CreateExecutor()
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/workspace", Model = "gemini-2.5-flash" });
        var promptBuilder = new PromptBuilder(options, NullLogger<PromptBuilder>.Instance);
        return new GeminiExecutor(options, promptBuilder, NullLogger<GeminiExecutor>.Instance);
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ── Behavioral contracts ──────────────────────────────────────────────────

    [Fact]
    public void IsProcessWarm_AlwaysFalse()
    {
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
    public async Task StopProcessAsync_CompletesImmediately()
    {
        var executor = CreateExecutor();
        // Should not block or throw — it's a no-op for a per-task spawn executor.
        await executor.StopProcessAsync();
    }

    // ── MapEvent: text events ─────────────────────────────────────────────────

    [Fact]
    public void MapEvent_TextEvent_ReturnsAssistantChunk()
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"text\",\"text\":\"Hello world\"}");

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.Equal("assistant", result!.EventType);
        Assert.Equal("Hello world", result.Summary);
        Assert.Equal("Hello world", acc.ToString());
    }

    [Fact]
    public void MapEvent_GenericTextField_ReturnsAssistantChunk()
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        // Any event with a top-level "text" field should be accumulated.
        var ev = Parse("{\"text\":\"chunk\"}");

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.Equal("assistant", result!.EventType);
        Assert.Equal("chunk", result.Summary);
    }

    [Fact]
    public void MapEvent_RawCandidatesFormat_ExtractsPartsText()
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("""
            {
              "candidates": [{
                "content": {
                  "parts": [
                    {"text": "part1"},
                    {"text": "part2"}
                  ]
                }
              }]
            }
            """);

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.Equal("assistant", result!.EventType);
        Assert.Equal("part1part2", result.Summary);
    }

    // ── MapEvent: tool events ─────────────────────────────────────────────────

    [Theory]
    [InlineData("tool_call")]
    [InlineData("toolCall")]
    [InlineData("function_call")]
    public void MapEvent_ToolCallVariants_ReturnsToolUse(string eventType)
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse($"{{\"type\":\"{eventType}\",\"name\":\"memory_get\",\"args\":{{\"id\":\"abc\"}}}}");

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.Equal("tool_use", result!.EventType);
        Assert.Equal("memory_get", result.ToolName);
        Assert.True(result.IsSignificant);
    }

    [Theory]
    [InlineData("tool_result")]
    [InlineData("toolResult")]
    [InlineData("function_result")]
    public void MapEvent_ToolResultVariants_ReturnsToolResult(string eventType)
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse($"{{\"type\":\"{eventType}\",\"content\":\"result text\"}}");

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.Equal("tool_result", result!.EventType);
        Assert.False(result.IsSignificant);
    }

    // ── MapEvent: error event ─────────────────────────────────────────────────

    [Fact]
    public void MapEvent_ErrorEvent_ReturnsErrorResult()
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"error\",\"message\":\"rate limit exceeded\"}");

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.Equal("result", result!.EventType);
        Assert.True(result.IsErrorResult);
        Assert.Equal("rate limit exceeded", result.FinalResult);
    }

    [Fact]
    public void MapEvent_ErrorEventNoMessage_ReturnsUnknownError()
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"error\"}");

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.True(result!.IsErrorResult);
        Assert.Equal("Unknown error from gemini CLI", result.FinalResult);
    }

    // ── MapEvent: no-payload events ───────────────────────────────────────────

    [Fact]
    public void MapEvent_UsageEvent_ReturnsNull()
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"usage\",\"inputTokens\":100,\"outputTokens\":50}");

        var result = executor.MapEvent(ev, acc);

        Assert.Null(result);
        Assert.Equal("", acc.ToString()); // accumulator untouched
    }

    // ── ExtractEventText ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractEventText_TextTypeWithTextField_ReturnsText()
    {
        var ev = Parse("{\"type\":\"text\",\"text\":\"hello\"}");
        var result = GeminiExecutor.ExtractEventText(ev, "text");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ExtractEventText_ContentTypeWithContentField_ReturnsContent()
    {
        var ev = Parse("{\"type\":\"content\",\"content\":\"world\"}");
        var result = GeminiExecutor.ExtractEventText(ev, "content");
        Assert.Equal("world", result);
    }

    [Fact]
    public void ExtractEventText_UnknownTypeWithTextField_ReturnsTextViaFallback()
    {
        var ev = Parse("{\"type\":\"custom\",\"text\":\"fallback\"}");
        var result = GeminiExecutor.ExtractEventText(ev, "custom");
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void ExtractEventText_NullType_StillHandlesTopLevelText()
    {
        var ev = Parse("{\"text\":\"bare\"}");
        var result = GeminiExecutor.ExtractEventText(ev, null);
        Assert.Equal("bare", result);
    }

    [Fact]
    public void ExtractEventText_NoTextField_ReturnsNull()
    {
        var ev = Parse("{\"type\":\"usage\",\"tokens\":100}");
        var result = GeminiExecutor.ExtractEventText(ev, "usage");
        Assert.Null(result);
    }
}
