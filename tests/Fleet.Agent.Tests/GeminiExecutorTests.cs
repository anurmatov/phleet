using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for GeminiExecutor event mapping logic (issue #132).
/// Event shapes verified against gemini CLI v0.40.1 live output (canary run 2026-05-04).
/// Tests cover stream-json parsing, tool call/result handling, error events,
/// and legacy fallback paths — all without spawning a real process.
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

    // ── MapEvent: v0.40.1 primary message format ──────────────────────────────

    [Fact]
    public void MapEvent_MessageAssistant_ReturnsAssistantChunk()
    {
        // Verified format from gemini CLI v0.40.1 live output.
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"Hello world\",\"delta\":true}");

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.Equal("assistant", result!.EventType);
        Assert.Equal("Hello world", result.Summary);
        Assert.Equal("Hello world", acc.ToString());
        Assert.False(result.IsSignificant); // intermediate chunk — PR #129 pattern
    }

    [Fact]
    public void MapEvent_MessageUser_ReturnsNull()
    {
        // User echo events should be skipped — they are just the input reflected back.
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"message\",\"role\":\"user\",\"content\":\"what is 2+2?\"}");

        var result = executor.MapEvent(ev, acc);

        Assert.Null(result);
        Assert.Equal("", acc.ToString()); // accumulator untouched
    }

    [Fact]
    public void MapEvent_InitEvent_ReturnsNull()
    {
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"init\",\"session_id\":\"abc\",\"model\":\"gemini-2.5-flash\"}");

        var result = executor.MapEvent(ev, acc);

        Assert.Null(result);
    }

    // ── MapEvent: v0.40.1 result/error format ────────────────────────────────

    [Fact]
    public void MapEvent_ResultStatusError_ReturnsErrorResult()
    {
        // Verified format: {"type":"result","status":"error","error":{"type":"unknown","message":"..."}}
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"result\",\"status\":\"error\",\"error\":{\"type\":\"unknown\",\"message\":\"rate limit exceeded\"}}");

        var result = executor.MapEvent(ev, acc);

        Assert.NotNull(result);
        Assert.Equal("result", result!.EventType);
        Assert.True(result.IsErrorResult);
        Assert.Equal("rate limit exceeded", result.FinalResult);
        Assert.True(result.IsSignificant);
    }

    [Fact]
    public void MapEvent_ResultStatusSuccess_ReturnsNull()
    {
        // Success end-of-stream event — RunCliAsync handles the final result via exit code 0.
        var executor = CreateExecutor();
        var acc = new StringBuilder();
        var ev = Parse("{\"type\":\"result\",\"status\":\"success\",\"stats\":{\"total_tokens\":100}}");

        var result = executor.MapEvent(ev, acc);

        Assert.Null(result); // handled by exit code path, not MapEvent
    }

    // ── MapEvent: legacy text events (fallback paths) ─────────────────────────

    [Fact]
    public void MapEvent_LegacyTextEvent_ReturnsAssistantChunk()
    {
        // Legacy/hypothetical format — handled by ExtractEventText fallback.
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
        // Any event with a top-level "text" field is accumulated via defensive fallback.
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
        // Tool-call events ARE significant — TaskManager gates them via the user-side
        // SuppressToolMessages flag and a 1-in-5 sampling counter (matches ClaudeExecutor).
        // Streaming text deltas remain non-significant; only FinalResult triggers the
        // assistant Telegram message.
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
