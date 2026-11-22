using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// What each provider's terminal turn becomes in the usage feed: missing stays null, zero stays
/// zero, and each executor produces exactly one row per turn that reached its own result.
/// Synthetic model names and sessions only.
/// </summary>
public class ProviderUsageTests
{
    // ── Claude extraction ────────────────────────────────────────────────────

    [Fact]
    public void Claude_SeveralModels_SumsEveryEntry_AndNamesNoSingleModel()
    {
        var snapshot = ProviderUsageSnapshot.ForClaude(Claude(
                """
                {"model-a":{"inputTokens":10,"outputTokens":4,"cacheReadInputTokens":100,"cacheCreationInputTokens":7,"contextWindow":1000,"costUSD":0.01},
                 "model-b":{"inputTokens":5,"outputTokens":1,"cacheReadInputTokens":0,"cacheCreationInputTokens":3,"contextWindow":2000,"costUSD":0.02}}
                """, totalCost: "0.035", durationMs: 1234),
            numTurns: 2, sessionId: "session-1", configuredModel: "configured-model", processGeneration: 3);

        Assert.Equal("claude", snapshot.Provider);
        Assert.Equal(ProviderUsageSnapshot.MultipleModels, snapshot.Model);
        Assert.Equal("model-a\nmodel-b", snapshot.ModelSet);   // the identity behind the label, ordinal
        Assert.True(snapshot.IsCumulative);
        Assert.Equal((15L, 5L, 100L, 10L), (snapshot.InputTokens, snapshot.OutputTokens, snapshot.CacheReadTokens, snapshot.CacheCreationTokens));
        Assert.Equal(0.035m, snapshot.CostUsd);
        Assert.Null(snapshot.ContextWindow);   // two windows: not attributed to either
        Assert.Equal((2L, 1234L), (snapshot.NumTurns, snapshot.DurationMs));
        Assert.Equal(("session-1", 3L), (snapshot.SessionId, snapshot.ProcessGeneration));
    }

    [Fact]
    public void Claude_ComponentMissingFromOneEntry_IsNull_ReportedZeroIsZero()
    {
        var snapshot = ProviderUsageSnapshot.ForClaude(Claude(
                """
                {"model-a":{"inputTokens":10,"outputTokens":0,"cacheReadInputTokens":0},
                 "model-b":{"inputTokens":5,"outputTokens":0,"cacheReadInputTokens":0,"cacheCreationInputTokens":3}}
                """),
            numTurns: null, sessionId: null, configuredModel: null, processGeneration: 1);

        Assert.Equal(0L, snapshot.OutputTokens);
        Assert.Equal(0L, snapshot.CacheReadTokens);
        Assert.Null(snapshot.CacheCreationTokens);   // model-a never reported it
        Assert.Null(snapshot.CostUsd);               // no total and no costUSD at all
        Assert.Null(snapshot.NumTurns);
        Assert.Null(snapshot.SessionId);
    }

    [Theory]
    [InlineData("""{"m":{"costUSD":0.25},"n":{"costUSD":0.5}}""", "0.75")]
    [InlineData("""{"m":{"costUSD":0.25},"n":{}}""", null)]
    public void Claude_WithoutTotalCost_SumsModelCostsOnlyWhenEveryEntryHasOne(string modelUsage, string? expected)
    {
        var snapshot = ProviderUsageSnapshot.ForClaude(Claude(modelUsage), null, null, null, 1);

        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), snapshot.CostUsd);
    }

    [Theory]
    [InlineData("""{"reported-model":{"inputTokens":1}}""", "configured-model", "reported-model")]
    [InlineData(null, "configured-model", "configured-model")]
    [InlineData(null, "", ProviderUsageSnapshot.UnknownModel)]
    [InlineData(null, null, ProviderUsageSnapshot.UnknownModel)]
    public void Claude_ModelAttribution_ReportedThenConfiguredThenUnknown(string? modelUsage, string? configured, string expected)
    {
        var data = modelUsage is null ? new Dictionary<string, object>() : Claude(modelUsage);

        var snapshot = ProviderUsageSnapshot.ForClaude(data, null, null, configured, 1);

        Assert.Equal(expected, snapshot.Model);
        if (modelUsage is null)
            Assert.Null(snapshot.InputTokens);
    }

    [Fact]
    public async Task ClaudeExecutor_SuccessAndError_EachEmitOneRow_AndTheFinalResultIsUnchanged()
    {
        var reporter = Reporter();
        await using var claude = new ClaudeTurns(reporter);

        var first = await claude.RunAsync(ClaudeResult("ok", isError: false,
            """{"model-a":{"inputTokens":10,"outputTokens":4,"cacheReadInputTokens":0,"cacheCreationInputTokens":2,"contextWindow":1000}}""",
            totalCost: "0.10"));
        var second = await claude.RunAsync(ClaudeResult("failed", isError: true,
            """{"model-a":{"inputTokens":25,"outputTokens":9,"cacheReadInputTokens":3,"cacheCreationInputTokens":2,"contextWindow":1000}}""",
            totalCost: "0.25"));

        Assert.Equal("ok", Assert.Single(first, p => p.FinalResult is not null).FinalResult);
        Assert.True(Assert.Single(second, p => p.FinalResult is not null).IsErrorResult);

        var rows = Drain(reporter);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].GetProperty("delta_reset").GetBoolean());   // first turn of the process
        Assert.Equal(10, rows[0].GetProperty("input_tokens").GetInt64());
        Assert.Equal("model-a", rows[0].GetProperty("model").GetString());
        Assert.Equal(1000, rows[0].GetProperty("context_window").GetInt64());

        Assert.False(rows[1].GetProperty("delta_reset").GetBoolean());
        Assert.Equal(15, rows[1].GetProperty("input_tokens").GetInt64());
        Assert.Equal(5, rows[1].GetProperty("output_tokens").GetInt64());
        Assert.Equal(3, rows[1].GetProperty("cache_read_tokens").GetInt64());
        Assert.Equal(0, rows[1].GetProperty("cache_creation_tokens").GetInt64());
        Assert.Equal(0.15m, rows[1].GetProperty("cost_usd").GetDecimal());
        Assert.Equal(25, rows[1].GetProperty("cum_input_tokens").GetInt64());
        Assert.Equal(0.25m, rows[1].GetProperty("cum_cost_usd").GetDecimal());
        Assert.NotEqual(rows[0].GetProperty("turn_id").GetString(), rows[1].GetProperty("turn_id").GetString());
    }

    [Fact]
    public async Task ClaudeExecutor_UsageOff_QueuesNothing_AndTheTurnStillCompletes()
    {
        var reporter = Reporter(enabled: false);
        await using var claude = new ClaudeTurns(reporter);

        var progress = await claude.RunAsync(ClaudeResult("ok", isError: false, """{"m":{"inputTokens":1}}"""));

        Assert.Equal("ok", Assert.Single(progress, p => p.FinalResult is not null).FinalResult);
        Assert.Empty(Drain(reporter));
    }

    [Fact]
    public async Task ClaudeExecutor_NonCurrentResult_IsNotATurnOfItsOwn()
    {
        var reporter = Reporter();
        await using var claude = new ClaudeTurns(reporter);

        await claude.RunAsync(
            new ClaudeStreamEvent
            {
                Type = "result",
                Result = "background",
                Origin = new ClaudeMessageOrigin { Kind = "task-notification" },
                NumTurns = 1,
            },
            ClaudeResult("ok", isError: false, """{"m":{"inputTokens":3}}"""));

        Assert.Single(Drain(reporter));
    }

    // ── Codex extraction ─────────────────────────────────────────────────────

    [Fact]
    public void Codex_TotalIsMappedAsReported_AbsentCacheWriteIsNull()
    {
        var snapshot = ProviderUsageSnapshot.ForCodex(
            JsonNode.Parse("""{"inputTokens":120,"cachedInputTokens":80,"outputTokens":30,"reasoningOutputTokens":9,"totalTokens":150}""")!.AsObject(),
            contextWindow: 200000, durationMs: 42, threadId: "thread-1", model: "model-c", processGeneration: 1);

        Assert.Equal((120L, 30L, 80L), (snapshot.InputTokens, snapshot.OutputTokens, snapshot.CacheReadTokens));
        Assert.Null(snapshot.CacheCreationTokens);
        Assert.Null(snapshot.CostUsd);
        Assert.Equal((200000L, 42L), (snapshot.ContextWindow, snapshot.DurationMs));
        Assert.Equal(("codex", "model-c", "thread-1"), (snapshot.Provider, snapshot.Model, snapshot.SessionId));
    }

    [Fact]
    public async Task CodexExecutor_DifferencesTotalAcrossTerminals_NotTheLastUpdate()
    {
        var reporter = Reporter();
        var codex = CodexWith(reporter);
        var channel = Channel.CreateUnbounded<JsonObject>();
        codex.SetNotificationChannelForTests(channel);

        // Turn 1: two updates; the latest total counts, and the per-update "last" is never summed.
        codex.SetThreadStateForTests("thread-1", "turn-1");
        codex.SetUsageTurnForTests(reporter.BeginTurn());
        await channel.Writer.WriteAsync(TokenUsage("turn-1", total: Totals(50, 10, 20, cacheWrite: 0), last: Totals(50, 10, 20)));
        await channel.Writer.WriteAsync(TokenUsage("turn-1", total: Totals(90, 25, 40, cacheWrite: 5), last: Totals(40, 15, 20)));
        await channel.Writer.WriteAsync(Completed("turn-1", "completed"));
        await CollectAsync(codex.StreamTurnForTests("turn-1"));

        // Turn 2, same thread.
        codex.SetThreadStateForTests("thread-1", "turn-2");
        codex.SetUsageTurnForTests(reporter.BeginTurn());
        await channel.Writer.WriteAsync(TokenUsage("turn-2", total: Totals(130, 32, 60, cacheWrite: 5), last: Totals(40, 7, 20)));
        await channel.Writer.WriteAsync(Completed("turn-2", "failed"));
        await CollectAsync(codex.StreamTurnForTests("turn-2"));

        var rows = Drain(reporter);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].GetProperty("delta_reset").GetBoolean());
        Assert.Equal(90, rows[0].GetProperty("input_tokens").GetInt64());

        var second = rows[1];
        Assert.False(second.GetProperty("delta_reset").GetBoolean());
        Assert.Equal(40, second.GetProperty("input_tokens").GetInt64());
        Assert.Equal(7, second.GetProperty("output_tokens").GetInt64());
        Assert.Equal(20, second.GetProperty("cache_read_tokens").GetInt64());
        Assert.Equal(0, second.GetProperty("cache_creation_tokens").GetInt64());
        Assert.Equal(130, second.GetProperty("cum_input_tokens").GetInt64());
        Assert.False(second.TryGetProperty("cost_usd", out _));
        Assert.Equal("thread-1", second.GetProperty("session_id").GetString());
        Assert.Equal(7, second.GetProperty("duration_ms").GetInt64());
    }

    [Fact]
    public async Task CodexExecutor_UpdateForAnotherThread_IsIgnored()
    {
        var reporter = Reporter();
        var codex = CodexWith(reporter);
        var channel = Channel.CreateUnbounded<JsonObject>();
        codex.SetNotificationChannelForTests(channel);
        codex.SetThreadStateForTests("thread-1", "turn-1");
        codex.SetUsageTurnForTests(reporter.BeginTurn());

        await channel.Writer.WriteAsync(TokenUsage("turn-1", total: Totals(999, 999, 999), last: Totals(1, 1, 1), threadId: "thread-other"));
        await channel.Writer.WriteAsync(Completed("turn-1", "completed"));
        await CollectAsync(codex.StreamTurnForTests("turn-1"));

        var row = Assert.Single(Drain(reporter));
        Assert.False(row.TryGetProperty("input_tokens", out _));   // nothing reported for this thread
        Assert.False(row.TryGetProperty("cum_input_tokens", out _));
    }

    [Fact]
    public async Task CodexExecutor_AppServerExitBeforeTerminal_EmitsNoRow()
    {
        var reporter = Reporter();
        var codex = CodexWith(reporter);
        var channel = Channel.CreateUnbounded<JsonObject>();
        codex.SetNotificationChannelForTests(channel);
        codex.SetThreadStateForTests("thread-1", "turn-1");
        codex.SetUsageTurnForTests(reporter.BeginTurn());

        await channel.Writer.WriteAsync(TokenUsage("turn-1", total: Totals(10, 1, 0), last: Totals(10, 1, 0)));
        channel.Writer.TryComplete();
        var progress = await CollectAsync(codex.StreamTurnForTests("turn-1"));

        Assert.True(Assert.Single(progress, p => p.FinalResult is not null).IsProcessExit);
        Assert.Empty(Drain(reporter));
    }

    // ── Gemini ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task GeminiExecutor_ARunThatEndsWithoutAResult_IsOneRow_WithNoTokensOrCost(int exitCode)
    {
        var reporter = Reporter();
        var root = Path.Combine(Path.GetTempPath(), $"gemini-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new AgentOptions
            {
                Name = "test", Role = "test", WorkDir = root, Provider = "gemini", Model = "model-g",
            });
            await using var gemini = new GeminiExecutor(
                options, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), NullLogger<GeminiExecutor>.Instance,
                psi => StandInShell(psi, $"echo '{{\"event\":\"init\",\"conversation_id\":\"c-1\",\"init\":{{}}}}'; cat >/dev/null; exit {exitCode}"),
                usage: reporter, geminiHome: Path.Combine(root, ".gemini"));

            var progress = new List<AgentProgress>();
            await foreach (var p in gemini.ExecuteAsync("synthetic task"))
                progress.Add(p);

            // The CLI printed no result: an error whatever the exit code, and a row with what is known.
            Assert.True(Assert.Single(progress, p => p.FinalResult is not null).IsErrorResult);
            var row = Assert.Single(Drain(reporter));
            Assert.Equal(("gemini", "model-g"), (row.GetProperty("provider").GetString(), row.GetProperty("model").GetString()));
            Assert.True(row.GetProperty("duration_ms").GetInt64() >= 0);
            foreach (var measure in new[] { "input_tokens", "output_tokens", "cache_read_tokens", "cache_creation_tokens", "cost_usd", "cum_input_tokens" })
                Assert.False(row.TryGetProperty(measure, out _), measure);
            Assert.False(row.GetProperty("delta_reset").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal static UsageReporter Reporter(bool enabled = true) =>
        new(enabled, "Agent1", NullLogger<UsageReporter>.Instance, () => DateTimeOffset.UtcNow, () => Guid.NewGuid().ToString());

    internal static List<JsonElement> Drain(UsageReporter reporter)
    {
        var rows = new List<JsonElement>();
        while (reporter.Rows.TryRead(out var row))
            rows.Add(JsonDocument.Parse(row.Body).RootElement.Clone());
        return rows;
    }

    private static Dictionary<string, object> Claude(string modelUsage, string? totalCost = null, long? durationMs = null)
    {
        var data = new Dictionary<string, object> { ["modelUsage"] = JsonDocument.Parse(modelUsage).RootElement.Clone() };
        if (totalCost is not null)
            data["total_cost_usd"] = JsonDocument.Parse(totalCost).RootElement.Clone();
        if (durationMs is not null)
            data["duration_ms"] = JsonDocument.Parse(durationMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();
        return data;
    }

    private static ClaudeStreamEvent ClaudeResult(string text, bool isError, string modelUsage, string? totalCost = null) => new()
    {
        Type = "result",
        Result = text,
        IsError = isError,
        NumTurns = 1,
        SessionId = "session-1",
        ExtensionData = Claude(modelUsage, totalCost),
    };

    private static CodexExecutor CodexWith(UsageReporter reporter)
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/workspace", Provider = "codex", Model = "model-c" });
        return new CodexExecutor(
            options, Options.Create(new TelegramOptions { AttachmentDir = "/workspace/attachments" }),
            new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), NullLogger<CodexExecutor>.Instance,
            Process.Start, usage: reporter);
    }

    private static JsonObject Totals(long input, long output, long cached, long? cacheWrite = null)
    {
        var o = new JsonObject { ["inputTokens"] = input, ["cachedInputTokens"] = cached, ["outputTokens"] = output };
        if (cacheWrite is not null) o["cacheWriteInputTokens"] = cacheWrite;
        return o;
    }

    /// <summary>As the app-server's reader hands it over: parsed from text, not built in memory.</summary>
    private static JsonObject Wire(JsonObject frame) => JsonNode.Parse(frame.ToJsonString())!.AsObject();

    private static JsonObject TokenUsage(string turnId, JsonObject total, JsonObject last, string threadId = "thread-1") => Wire(new()
    {
        ["method"] = "thread/tokenUsage/updated",
        ["params"] = new JsonObject
        {
            ["threadId"] = threadId,
            ["turnId"] = turnId,
            ["tokenUsage"] = new JsonObject { ["total"] = total, ["last"] = last },
        },
    });

    private static JsonObject Completed(string turnId, string status) => Wire(new()
    {
        ["method"] = "turn/completed",
        ["params"] = new JsonObject
        {
            ["threadId"] = "thread-1",
            ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = status, ["durationMs"] = 7, ["items"] = new JsonArray() },
        },
    });

    private static async Task<List<AgentProgress>> CollectAsync(IAsyncEnumerable<AgentProgress> stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var list = new List<AgentProgress>();
        await foreach (var p in stream.WithCancellation(cts.Token))
            list.Add(p);
        return list;
    }

    /// <summary>The real start info with a POSIX shell script standing in for the provider CLI.</summary>
    private static Process? StandInShell(ProcessStartInfo original, string script)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = original.RedirectStandardInput,
            RedirectStandardOutput = original.RedirectStandardOutput,
            RedirectStandardError = original.RedirectStandardError,
            UseShellExecute = false,
            WorkingDirectory = original.WorkingDirectory,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        return Process.Start(psi);
    }

    /// <summary>Runs whole Claude turns against a stand-in process and a scripted event channel.</summary>
    private sealed class ClaudeTurns : IAsyncDisposable
    {
        private readonly StandInProcess _process = new();
        private readonly ClaudeExecutor _executor;
        private readonly Channel<ClaudeStreamEvent> _events = Channel.CreateUnbounded<ClaudeStreamEvent>();
        private readonly ProviderFrameReplay.SignalingTextWriter _stdin = new();

        public ClaudeTurns(UsageReporter reporter)
        {
            var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude", Model = "configured-model" });
            _executor = new ClaudeExecutor(
                options, NullLogger<ClaudeExecutor>.Instance, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance),
                usage: reporter);
            _executor.SetProcessForTests(_process.Process);
            _executor.SetStdinForTests(_stdin);
            _executor.SetEventChannelForTests(_events);
        }

        public async Task<IReadOnlyList<AgentProgress>> RunAsync(params ClaudeStreamEvent[] events)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var turn = Task.Run(async () =>
            {
                var progress = new List<AgentProgress>();
                await foreach (var item in _executor.ExecuteAsync("synthetic turn", ct: cts.Token))
                    progress.Add(item);
                return (IReadOnlyList<AgentProgress>)progress;
            }, cts.Token);

            await _stdin.WaitForWriteAsync(cts.Token);
            foreach (var evt in events)
                await _events.Writer.WriteAsync(evt, cts.Token);
            return await turn.WaitAsync(cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _executor.DisposeAsync();
            _process.Dispose();
        }
    }
}
