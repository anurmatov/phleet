using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// #347 provider sources: the compaction events that move <c>CompactionEpoch</c> (claude
/// <c>compact_boundary</c> but not <c>microcompact_boundary</c>; codex <c>thread/compacted</c> and a
/// <c>contextCompaction</c> item), and <c>prompt_accepted</c> exactly once per <c>ExecuteAsync</c>
/// for each executor.
///
/// <para>Fixture provenance: <c>claude/compact-boundary.ndjson</c> is constructed from the SDK
/// output schema in the pinned <c>@anthropic-ai/claude-code@2.1.280</c> bundle (a live compaction
/// was not captured — it needs a context-window's worth of real traffic);
/// <c>codex/context-compaction.jsonl</c> is constructed from the pinned
/// <c>protocols/codex-app-server-v2/</c> schema. Both use placeholders only.</para>
/// </summary>
public class ProjectContextProviderTests
{
    private static int CountAccepted(IEnumerable<AgentProgress> progress) =>
        progress.Count(p => p.EventType == AgentProgress.PromptAcceptedEventType);

    // ── claude ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Claude_CompactBoundaryIncrementsTheEpoch_MicrocompactBoundaryDoesNot()
    {
        await using var replay = new ProviderFrameReplay.ClaudeReplay();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(0, replay.Executor.CompactionEpoch);

        var progress = await replay.RunTurnAsync(ProviderFrameReplay.ReadClaudeFrames("compact-boundary.ndjson"), cts.Token);

        Assert.Equal(1, replay.Executor.CompactionEpoch);
        Assert.Equal("Synthetic answer after compaction.", progress.Single(p => p.FinalResult is not null).FinalResult);
        // Compaction is bookkeeping, never something a user is shown.
        Assert.DoesNotContain(progress, p => p.EventType == "system" && p.IsSignificant);
    }

    [Fact]
    public void Claude_OneCompactFrameParsedByBothReaders_CountsOnce()
    {
        // The background stdout reader and the turn read loop both run ParseProgress over the same
        // system frame; that is one compaction, not two.
        using var replay = new ClaudeParseOnly();
        var frames = ProviderFrameReplay.ReadClaudeFrames("compact-boundary.ndjson");
        var compact = frames.Single(f => f.Subtype == "compact_boundary");
        var micro = frames.Single(f => f.Subtype == "microcompact_boundary");

        replay.Executor.ParseProgressForTests(compact);
        replay.Executor.ParseProgressForTests(compact);
        replay.Executor.ParseProgressForTests(micro);
        Assert.Equal(1, replay.Executor.CompactionEpoch);

        // A second compaction is a second frame.
        replay.Executor.ParseProgressForTests(ProviderFrameReplay.ReadClaudeFrames("compact-boundary.ndjson")
            .Single(f => f.Subtype == "compact_boundary"));
        Assert.Equal(2, replay.Executor.CompactionEpoch);
    }

    [Fact]
    public async Task Claude_EmitsPromptAcceptedOnce_FirstAfterTheFrameWrite()
    {
        await using var replay = new ProviderFrameReplay.ClaudeReplay();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var progress = await replay.RunTurnAsync(ProviderFrameReplay.ReadClaudeFrames("ordinary-turn.ndjson"), cts.Token);

        Assert.Equal(1, CountAccepted(progress));
        Assert.Equal(AgentProgress.PromptAcceptedEventType, progress[0].EventType);
        Assert.False(progress[0].IsSignificant);
    }

    /// <summary>A claude executor for parse-only tests: no process, no turn.</summary>
    private sealed class ClaudeParseOnly : IDisposable
    {
        public ClaudeParseOnly()
        {
            var options = Options.Create(new AgentOptions { Name = "agent-a", Role = "test", WorkDir = "/tmp" });
            Executor = new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance,
                new PromptBuilder(options, NullLogger<PromptBuilder>.Instance));
        }

        public ClaudeExecutor Executor { get; }

        public void Dispose() => Executor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ── codex ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Codex_ThreadCompactedAndAContextCompactionItem_BothIncrementTheEpoch()
    {
        var (epoch, progress) = await StreamCodexAsync(ProviderFrameReplay.ReadCodexFrames("context-compaction.jsonl"));

        // item/completed(contextCompaction) + thread/compacted. item/started is not a compaction yet.
        Assert.Equal(2, epoch);
        Assert.Equal("Synthetic answer after compaction.", progress.Single(p => p.FinalResult is not null).FinalResult);
    }

    public static TheoryData<string, string, int> CodexSources() => new()
    {
        { "notification", """{"method":"thread/compacted","params":{"threadId":"t_thread_1","turnId":"t_1"}}""", 1 },
        { "item completed", """{"method":"item/completed","params":{"threadId":"t_thread_1","turnId":"t_1","item":{"type":"contextCompaction","id":"i_1"}}}""", 1 },
        { "item started", """{"method":"item/started","params":{"threadId":"t_thread_1","turnId":"t_1","item":{"type":"contextCompaction","id":"i_1"}}}""", 0 },
        { "another thread", """{"method":"thread/compacted","params":{"threadId":"t_thread_2","turnId":"t_9"}}""", 0 },
        { "other item", """{"method":"item/completed","params":{"threadId":"t_thread_1","turnId":"t_1","item":{"type":"agentMessage","text":"x"}}}""", 0 },
    };

    [Theory]
    [MemberData(nameof(CodexSources))]
    public async Task Codex_EachCompactionSourceCountsOnItsOwn(string _, string frame, int expected)
    {
        var frames = new List<JsonObject>
        {
            (JsonObject)JsonNode.Parse(frame)!,
            (JsonObject)JsonNode.Parse("""{"method":"turn/completed","params":{"turnId":"t_1","turn":{"id":"t_1","status":"completed"}}}""")!,
        };

        var (epoch, _) = await StreamCodexAsync(frames);

        Assert.Equal(expected, epoch);
    }

    private static async Task<(int Epoch, List<AgentProgress> Progress)> StreamCodexAsync(IReadOnlyList<JsonObject> frames)
    {
        await using var executor = ProviderFrameReplay.CreateCodexExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("t_thread_1", "t_1");
        foreach (var frame in frames)
            await channel.Writer.WriteAsync(frame);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var progress = new List<AgentProgress>();
        await foreach (var item in executor.StreamTurnForTests("t_1", cts.Token))
            progress.Add(item);
        return (executor.CompactionEpoch, progress);
    }

    [Fact]
    public async Task Codex_EmitsPromptAcceptedOnce_AfterTurnStartReturnedATurnId()
    {
        using var workspace = new TempDirectory();
        await using var executor = CreateCodex(workspace.Path, CatStandIn);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var notifications = Channel.CreateUnbounded<JsonObject>();

        var turn = Task.Run(async () =>
        {
            var seen = new List<AgentProgress>();
            await foreach (var p in executor.ExecuteAsync("hello", ct: cts.Token))
                seen.Add(p);
            return seen;
        }, cts.Token);

        await executor.WaitAndCompleteNextPendingRequestForTests(new JsonObject(), cts.Token); // initialize
        await executor.WaitAndCompleteNextPendingRequestForTests(new JsonObject
        {
            ["thread"] = new JsonObject { ["id"] = "t_thread_1", ["ephemeral"] = true },
        }, cts.Token); // thread/start
        // The stand-in answers nothing on stdout; the turn's notifications come from the test.
        // StreamTurnAsync reads this field only after turn/start returns, which is below.
        executor.SetNotificationChannelForTests(notifications);
        Assert.False(turn.IsCompleted);
        await executor.WaitAndCompleteNextPendingRequestForTests(new JsonObject
        {
            ["turn"] = new JsonObject { ["id"] = "t_1" },
        }, cts.Token); // turn/start
        foreach (var frame in ProviderFrameReplay.ReadCodexFrames("ordinary-turn.jsonl"))
            await notifications.Writer.WriteAsync(frame, cts.Token);

        var progress = await turn;

        Assert.Equal(1, CountAccepted(progress));
        Assert.Equal(AgentProgress.PromptAcceptedEventType, progress[0].EventType);
        Assert.Equal("Synthetic answer A.", progress.Single(p => p.FinalResult is not null).FinalResult);
    }

    [Fact]
    public async Task Codex_AStartupFailure_NeverEmitsPromptAccepted()
    {
        using var workspace = new TempDirectory();
        await using var executor = CreateCodex(workspace.Path, _ => null);
        var seen = new List<AgentProgress>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var p in executor.ExecuteAsync("hello"))
                seen.Add(p);
        });

        Assert.Equal(0, CountAccepted(seen));
    }

    private static CodexExecutor CreateCodex(string workDir, Func<ProcessStartInfo, Process?> starter)
    {
        var options = Options.Create(new AgentOptions { Name = "agent-a", Role = "test", WorkDir = workDir, Provider = "codex" });
        return new CodexExecutor(options, Options.Create(new TelegramOptions { AttachmentDir = workDir }),
            new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), NullLogger<CodexExecutor>.Instance, starter);
    }

    // ── gemini ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gemini_EmitsPromptAcceptedOnce_AfterTheProcessStartedWithThePrompt()
    {
        var executor = CreateGemini(GeminiStandIn);

        var progress = new List<AgentProgress>();
        await foreach (var p in executor.ExecuteAsync("hello"))
            progress.Add(p);

        Assert.Equal(1, CountAccepted(progress));
        Assert.Equal(AgentProgress.PromptAcceptedEventType, progress[0].EventType);
        Assert.Equal("Synthetic answer.", progress.Single(p => p.FinalResult is not null).FinalResult);
        // Never warm and never compacted: the ledger clears before every render.
        Assert.False(executor.IsProcessWarm);
        Assert.Equal(0, executor.CompactionEpoch);
    }

    [Fact]
    public async Task Gemini_ACommandIsNotATurn_AndAStartFailureNeverAccepts()
    {
        var command = new List<AgentProgress>();
        await foreach (var p in CreateGemini(GeminiStandIn).SendCommandAsync("/compact"))
            command.Add(p);
        Assert.Equal(0, CountAccepted(command));

        var failed = new List<AgentProgress>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var p in CreateGemini(_ => null).ExecuteAsync("hello"))
                failed.Add(p);
        });
        Assert.Equal(0, CountAccepted(failed));
    }

    private static GeminiExecutor CreateGemini(Func<ProcessStartInfo, Process?> starter)
    {
        var options = Options.Create(new AgentOptions { Name = "agent-a", Role = "test", WorkDir = "/tmp", Provider = "gemini", Model = "gemini-2.5-flash" });
        return new GeminiExecutor(options, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance),
            NullLogger<GeminiExecutor>.Instance, starter);
    }

    // ── stand-ins ────────────────────────────────────────────────────────────

    private const string Shell = "/bin/sh";

    /// <summary>A codex app-server stand-in that swallows every frame and answers nothing.</summary>
    private static Process CatStandIn(ProcessStartInfo _) => StartShell("cat > /dev/null");

    /// <summary>A gemini CLI stand-in: drains the prompt, then prints one stream-json assistant message.</summary>
    private static Process GeminiStandIn(ProcessStartInfo executorStartInfo)
    {
        var line = JsonSerializer.Serialize(new { type = "message", role = "assistant", content = "Synthetic answer.", delta = true });
        return StartShell($"cat > /dev/null; printf '%s\\n' '{line}'", executorStartInfo.WorkingDirectory);
    }

    private static Process StartShell(string script, string? workingDirectory = null)
    {
        Assert.True(File.Exists(Shell), $"The stand-in shell '{Shell}' is required; a skipped provider is an unmeasured provider.");
        var psi = new ProcessStartInfo
        {
            FileName = Shell,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the stand-in shell.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pcp-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* teardown only */ }
        }
    }
}
