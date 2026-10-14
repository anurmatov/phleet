using System.Diagnostics;
using System.Text.Json.Nodes;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using static Fleet.Agent.Tests.Harness.PiEvents;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="PiExecutor"/> (#386 §4.3) against the in-memory <see cref="PiStandIn"/>: every
/// member, the ack timer, the <c>get_state</c> checks, and the rows A7b, A8, A10–A14b that a
/// scripted Pi can drive. The real binary runs in <c>PiExecutorRealPiTests</c>.
/// </summary>
/// <remarks>
/// Deadlines are injected short (<see cref="PiStandIn.FastTimings"/>) except where a row names a
/// duration that the assertion depends on: the 2 s steer (A11) and the 5 s abort settle (A12, A14).
/// </remarks>
public sealed class PiExecutorTests
{
    private const string ExtensionDir = "/opt/fleet-pi-extension";

    /// <summary>One executor, its stand-in and its temporary directories.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly PiTempDirectory _temp = new("pi-executor");

        private Rig()
        {
            WorkDir = _temp.Sub("work");
            AgentDir = Path.Combine(_temp.Path, "agent");
        }

        public PiStandIn Pi { get; } = new();
        public PiTestLogger Logger { get; } = new();
        public PiExecutor Executor { get; private set; } = null!;
        public string WorkDir { get; }
        public string AgentDir { get; }
        public string TempPath => _temp.Path;

        public static Rig Create(
            PiExecutorTimings? timings = null,
            IReadOnlyDictionary<string, string>? environment = null,
            string? effort = "",
            Action<PiStandIn>? arrange = null,
            bool imageInput = false,
            FakePiLocalTools? tools = null,
            TimeSpan? commandDeadline = null)
        {
            var rig = new Rig { Tools = tools ?? new FakePiLocalTools() };
            arrange?.Invoke(rig.Pi);
            rig.Executor = rig.Pi.CreateExecutor(rig.WorkDir, rig.AgentDir, timings, environment, effort, logger: rig.Logger,
                imageInput: imageInput, localTools: rig.Tools, commandDeadline: commandDeadline);
            return rig;
        }

        /// <summary>The fake ffmpeg and poppler the executor's attachment processing calls (#455).</summary>
        public FakePiLocalTools Tools { get; private init; } = null!;

        /// <summary>A real session file on disk, reported by the stand-in.</summary>
        public string CreateSessionFile()
        {
            var file = Path.Combine(_temp.Sub("sessions"), "s-1.jsonl");
            File.WriteAllText(file, "{\"type\":\"session\",\"version\":3,\"id\":\"s-1\"}\n");
            Pi.SessionFile = file;
            return file;
        }

        public async Task<List<AgentProgress>> RunTurnAsync(
            string task = "hello", IReadOnlyList<MessageImage>? images = null, CancellationToken ct = default,
            IReadOnlyList<MessageDocument>? documents = null)
        {
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
            guard.CancelAfter(TimeSpan.FromSeconds(60));
            var progress = new List<AgentProgress>();
            await foreach (var item in Executor.ExecuteAsync(task, images, documents, guard.Token))
                progress.Add(item);
            return progress;
        }

        public IReadOnlyList<string> Messages(LogLevel level) =>
            Logger.Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            _temp.Dispose();
        }
    }

    private static AgentProgress Terminal(IEnumerable<AgentProgress> progress) =>
        Assert.Single(progress, p => p.EventType == "result");

    private static bool HasSession(System.Diagnostics.ProcessStartInfo psi, out string? file)
    {
        var args = psi.ArgumentList.ToList();
        var index = args.IndexOf("--session");
        file = index >= 0 ? args[index + 1] : null;
        return index >= 0;
    }

    // ── A8: arguments and environment ───────────────────────────────────────

    [Fact]
    public async Task A8_TheArgumentListIsExactlySection42()
    {
        await using var rig = Rig.Create();

        await rig.RunTurnAsync();

        var psi = rig.Pi.Starts[0];
        Assert.Equal(PiExecutor.PiBin, psi.FileName);
        Assert.Equal(rig.WorkDir, psi.WorkingDirectory);
        Assert.Equal(
        [
            "--mode", "rpc", "--offline", "--no-approve",
            "--provider", "fleet-local", "--model", PiStandIn.Tag,
            "--thinking", "medium",
            "--system-prompt", Path.Combine(rig.WorkDir, "system-prompt.md"),
            "-nc", "-ns", "-np", "-ne", "--no-mcp",
            "-e", ExtensionDir,
            "--tools", "read,bash,edit,write,mcp__fleet-memory__memory_get,mcp__fleet-temporal__notify_cto",
        ], psi.ArgumentList);
        Assert.True(File.Exists(Path.Combine(rig.WorkDir, "system-prompt.md")));
    }

    [Theory]
    [InlineData("off", "off")]
    [InlineData("on", "medium")]
    [InlineData("", "medium")]
    [InlineData(null, "medium")]
    public async Task A8_EffortSelectsTheThinkingArgument(string? effort, string thinking)
    {
        await using var rig = Rig.Create(effort: effort, arrange: pi => pi.ThinkingLevel = thinking);

        var result = Terminal(await rig.RunTurnAsync());

        Assert.False(result.IsErrorResult);
        var args = rig.Pi.Starts[0].ArgumentList.ToList();
        Assert.Equal(thinking, args[args.IndexOf("--thinking") + 1]);
    }

    [Fact]
    public async Task A8_NoDenylistedNameReachesTheChild_GhAndGithubDo()
    {
        var extras = new[]
        {
            "ANTHROPIC_X", "CLAUDE_CODE_X", "OPENAI_X", "CODEX_X", "GEMINI_X", "AWS_SESSION_TOKEN", "GOOGLE_CLOUD_PROJECT",
        };
        var seeded = PiLocalModel.CredentialEnvNames.Concat(extras).ToDictionary(n => n, _ => "seeded-marker-value");
        seeded["GH_TOKEN"] = "gh-marker";
        seeded["GITHUB_TOKEN"] = "github-marker";
        seeded["GIT_AUTHOR_NAME"] = "fleet";
        seeded["PATH"] = "/usr/bin:/bin";
        await using var rig = Rig.Create(environment: seeded);

        await rig.RunTurnAsync();

        var environment = rig.Pi.Starts[0].Environment;
        Assert.Equal(61, PiLocalModel.CredentialEnvNames.Count);
        foreach (var name in PiLocalModel.CredentialEnvNames.Concat(extras))
            Assert.False(environment.ContainsKey(name), $"{name} reached the Pi child");
        Assert.DoesNotContain(environment.Values, v => v == "seeded-marker-value");

        Assert.Equal("gh-marker", environment["GH_TOKEN"]);
        Assert.Equal("github-marker", environment["GITHUB_TOKEN"]);
        Assert.Equal("fleet", environment["GIT_AUTHOR_NAME"]);
        Assert.Equal("1", environment["PI_OFFLINE"]);
        Assert.Equal("0", environment["PI_TELEMETRY"]);
        Assert.Equal(rig.AgentDir, environment["PI_CODING_AGENT_DIR"]);
        Assert.False(environment.ContainsKey("PI_SKIP_VERSION_CHECK"));
    }

    [Fact]
    public void A8_TheProductionAgentDirIsTheContainerLocalPiDir()
    {
        Assert.Equal("/root/.pi/agent", new PiExecutorSettings().AgentDir);
        Assert.Equal(ExtensionDir, new PiExecutorSettings().ExtensionDir);

        var environment = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "x" };
        PiRuntimeFiles.ApplyChildEnvironment(environment);
        Assert.Equal("/root/.pi/agent", environment["PI_CODING_AGENT_DIR"]);
        Assert.False(environment.ContainsKey("ANTHROPIC_API_KEY"));
    }

    // ── A9 on every start, immutability ─────────────────────────────────────

    [Fact]
    public async Task EveryStart_RewritesTheThreeFiles()
    {
        await using var rig = Rig.Create();
        await rig.RunTurnAsync();

        string Read(string name) => File.ReadAllText(Path.Combine(rig.AgentDir, name));
        Assert.Equal(PiRuntimeFiles.BuildModelsJson(PiStandIn.BaseUrl, PiStandIn.Tag, PiStandIn.ContextWindow),
            Read("models.json"));
        Assert.Equal(PiRuntimeFiles.BuildSettingsJson(), Read("settings.json"));
        Assert.Equal(PiRuntimeFiles.BuildExtensionConfigJson(
            ["mcp__fleet-memory__memory_get", "mcp__fleet-temporal__notify_cto"]), Read("fleet-pi.json"));

        // The model's bash rewrites a file mid-session; the next start puts it back.
        File.WriteAllText(Path.Combine(rig.AgentDir, "settings.json"), "{\"cacheWarming\":\"streaming\"}");
        rig.Executor.RequestRestart();
        await rig.RunTurnAsync();

        Assert.Equal(2, rig.Pi.Starts.Count);
        Assert.Equal(PiRuntimeFiles.BuildSettingsJson(), Read("settings.json"));
    }

    [Fact]
    public async Task NoSetCommandIsEverSent()
    {
        await using var rig = Rig.Create();

        await rig.RunTurnAsync();
        await rig.RunTurnAsync();

        Assert.DoesNotContain(rig.Pi.Current.Frames, f => PiExecutor.Str(f, "type")!.StartsWith("set_", StringComparison.Ordinal));
    }

    // ── A7b: the get_state check ────────────────────────────────────────────

    [Theory]
    [InlineData("autoCompactionEnabled")]
    [InlineData("model.id")]
    [InlineData("model.provider")]
    [InlineData("thinkingLevel")]
    [InlineData("steeringMode")]
    public async Task A7b_AMismatchIsKilledRetriedThreeTimes_ThenNamesTheFieldNeverTheValue(string field)
    {
        await using var rig = Rig.Create(arrange: pi => pi.StateFor = _ =>
        {
            var state = pi.DefaultState();
            switch (field)
            {
                case "autoCompactionEnabled": state["autoCompactionEnabled"] = false; break;
                case "model.id": state["model"]!["id"] = "another-model-marker"; break;
                case "model.provider": state["model"]!["provider"] = "anthropic"; break;
                case "thinkingLevel": state["thinkingLevel"] = "high"; break;
                case "steeringMode": state["steeringMode"] = "one-at-a-time"; break;
            }
            return state;
        });

        var progress = await rig.RunTurnAsync();

        var result = Terminal(progress);
        Assert.True(result.IsErrorResult);
        Assert.Equal($"Pi failed to start: {field}", result.FinalResult);
        Assert.Equal(PiExecutor.StartAttempts, rig.Pi.Processes.Count);
        Assert.All(rig.Pi.Processes, p => Assert.Equal(1, p.Kills));
        Assert.All(rig.Pi.Processes, p => Assert.Empty(p.FramesOfType("prompt")));
        Assert.Contains(rig.Messages(LogLevel.Error), m => m.StartsWith($"Pi failed to start: {field}", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Message.Contains("another-model-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A7b_ACorrectAnswerStartsTheProcessOnce()
    {
        await using var rig = Rig.Create();

        var result = Terminal(await rig.RunTurnAsync());

        Assert.False(result.IsErrorResult);
        Assert.Equal("OK", result.FinalResult);
        var process = Assert.Single(rig.Pi.Processes);
        Assert.Equal(0, process.Kills);
        Assert.Contains(rig.Messages(LogLevel.Information), m =>
            m == $"pi start: version 1.0.4 model {PiStandIn.Tag} server {PiStandIn.BaseUrl} context 131072 "
               + $"reserve 16384 tail 8192 summaryMax 1280 thinking on session new images off poppler {PiStandIn.PopplerVersion}");
    }

    [Fact]
    public async Task A32_AStartedPiWhoseAuthFileHoldsACredential_IsKilledRetriedAndNamesTheFile()
    {
        string? agentDir = null;
        await using var rig = Rig.Create(arrange: pi => pi.StateFor = _ =>
        {
            // What a started Pi would leave behind if something had put a credential in its store.
            File.WriteAllText(Path.Combine(agentDir!, "auth.json"), "{\"anthropic\":{\"key\":\"marker-value\"}}");
            return pi.DefaultState();
        });
        agentDir = rig.AgentDir;

        var result = Terminal(await rig.RunTurnAsync());

        Assert.True(result.IsErrorResult);
        Assert.Equal("Pi failed to start: auth.json", result.FinalResult);
        Assert.Equal(PiExecutor.StartAttempts, rig.Pi.Processes.Count);
        Assert.All(rig.Pi.Processes, p => Assert.Empty(p.FramesOfType("prompt")));
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Message.Contains("marker-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A32_PisOwnEmptyAuthFile_Starts()
    {
        string? agentDir = null;
        await using var rig = Rig.Create(arrange: pi => pi.StateFor = _ =>
        {
            File.WriteAllText(Path.Combine(agentDir!, "auth.json"), "{}");
            return pi.DefaultState();
        });
        agentDir = rig.AgentDir;

        var result = Terminal(await rig.RunTurnAsync());

        Assert.False(result.IsErrorResult);
        Assert.Single(rig.Pi.Processes);
    }

    [Fact]
    public async Task A7b_NoGetStateAnswer_FailsAfterThreeAttemptsNamingGetState()
    {
        await using var rig = Rig.Create(
            timings: PiStandIn.FastTimings with { StartupState = TimeSpan.FromMilliseconds(300) },
            arrange: pi => pi.AnswerGetState = false);

        var result = Terminal(await rig.RunTurnAsync());

        Assert.Equal("Pi failed to start: get_state", result.FinalResult);
        Assert.Equal(3, rig.Pi.Processes.Count);
        Assert.All(rig.Pi.Processes, p => Assert.Equal(1, p.Kills));
    }

    [Fact]
    public async Task A7b_TheStartFailureLogCarriesTheStderrTail()
    {
        await using var rig = Rig.Create(
            timings: PiStandIn.FastTimings with { StartupState = TimeSpan.FromMilliseconds(300) },
            arrange: pi => pi.AnswerGetState = false);
        var turn = rig.RunTurnAsync();
        for (var n = 1; n <= 3; n++)
            await (await rig.Pi.WaitForStartAsync(n)).EmitStderrAsync($"node: startup diagnostic {n}");

        await turn;

        var error = Assert.Single(rig.Messages(LogLevel.Error), m => m.StartsWith("Pi failed to start", StringComparison.Ordinal));
        Assert.Contains("startup diagnostic 3", error);
    }

    // ── ExecuteAsync, FinalResult, Stats ────────────────────────────────────

    [Fact]
    public async Task ATextTurn_YieldsDeltasThenTheFinalResultWithStatsAndSession()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), TextDelta("Hel"), TextDelta("lo"),
                FinalText("Hello", Usage(input: 100, output: 7, cacheRead: 900)), TurnEnd(), AgentEnd(), AgentSettled());
        };

        var progress = await rig.RunTurnAsync("say hello");

        Assert.Equal(["Hel", "lo"], progress.Where(p => p.EventType == "assistant").Select(p => p.Summary));
        var result = Terminal(progress);
        Assert.Equal("Hello", result.FinalResult);
        Assert.False(result.IsErrorResult);
        Assert.Equal("s-1", result.SessionId);
        Assert.Equal(100, result.Stats!.InputTokens);
        Assert.Equal(900, result.Stats.CacheReadTokens);
        Assert.Equal(7, result.Stats.OutputTokens);
        Assert.Equal("say hello", PiExecutor.Str(rig.Pi.Current.FramesOfType("prompt")[0], "message"));
        Assert.Equal("s-1", rig.Executor.LastSessionId);
        Assert.True(rig.Executor.IsProcessWarm);
        Assert.True(rig.Executor.LastActivity > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task FinalResult_IsTheFirstStopMessageWithoutToolCalls_AndStatsSumEveryCall()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(),
                ToolCallMessage("c1", "bash", new JsonObject { ["command"] = "ls" }),
                ToolStart("c1", "bash", new JsonObject { ["command"] = "ls" }),
                ToolEnd("c1", "bash", "a.txt"),
                TurnEnd(), TurnStart(), FinalText("listed", Usage(input: 30, output: 5, cacheRead: 200)),
                TurnEnd(), AgentEnd(), AgentSettled());
        };

        var result = Terminal(await rig.RunTurnAsync());

        Assert.Equal("listed", result.FinalResult);
        Assert.Equal(150, result.Stats!.InputTokens);
        Assert.Equal(13, result.Stats.OutputTokens);
        Assert.Equal(200, result.Stats.CacheReadTokens);
    }

    [Fact]
    public async Task AToolResultThatIsNotAnObject_IsIgnored_AndTheRunCompletes()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            var end = ToolEnd("c1", "bash", "");
            end["result"] = "a bare string, not an object";
            await p.EmitAsync(AgentStart(), TurnStart(),
                ToolCallMessage("c1", "bash", new JsonObject { ["command"] = "ls" }),
                ToolStart("c1", "bash", new JsonObject { ["command"] = "ls" }), end,
                TurnEnd(), TurnStart(), FinalText("listed"), TurnEnd(), AgentEnd(), AgentSettled());
        };

        var progress = await rig.RunTurnAsync();

        Assert.Equal("", Assert.Single(progress, p => p.EventType == "tool_result").Summary);
        Assert.Equal("listed", Terminal(progress).FinalResult);
    }

    [Fact]
    public async Task IsProcessWarm_IsFalseBeforeTheFirstTurnAndAfterReset()
    {
        await using var rig = Rig.Create();
        Assert.False(rig.Executor.IsProcessWarm);

        await rig.RunTurnAsync();
        Assert.True(rig.Executor.IsProcessWarm);

        await rig.Executor.StopProcessAsync();
        Assert.False(rig.Executor.IsProcessWarm);
    }

    [Fact]
    public async Task IsProcessWarm_IsTrueAtOnceForAResumedSessionWithMessages()
    {
        await using var rig = Rig.Create(arrange: pi => pi.MessageCount = 4);

        // Any command starts the process; the session already holds turns.
        await foreach (var _ in rig.Executor.SendCommandAsync("true")) { }

        Assert.True(rig.Executor.IsProcessWarm);
    }

    // ── Ack dispositions (§4.3 step 2) ──────────────────────────────────────

    [Fact]
    public async Task Ack_Handled_YieldsTheCommandTextAndNoRun()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = (p, f) => p.AckAsync(f, "handled");

        var result = Terminal(await rig.RunTurnAsync("/fleet-status"));

        Assert.Equal("Pi handled this message as a command; nothing was sent to the model", result.FinalResult);
        Assert.False(result.IsErrorResult);

        // No run means nothing to settle: the next prompt goes out at once.
        rig.Pi.OnPrompt = null;
        var stopwatch = Stopwatch.StartNew();
        Assert.Equal("OK", Terminal(await rig.RunTurnAsync()).FinalResult);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Ack_Queued_ProceedsWithAWarning()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f, "queued");
            await p.EmitAsync(TextRun("queued answer"));
        };

        var result = Terminal(await rig.RunTurnAsync());

        Assert.Equal("queued answer", result.FinalResult);
        Assert.Contains(rig.Messages(LogLevel.Warning), m => m.Contains("queued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ack_Rejected_IsAnErrorResult()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = (p, f) => p.RespondAsync(f, success: false,
            error: "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.");

        var result = Terminal(await rig.RunTurnAsync());

        Assert.True(result.IsErrorResult);
        Assert.StartsWith("Pi refused the message: Agent is already processing", result.FinalResult);
    }

    // ── A14b: the ack timer ─────────────────────────────────────────────────

    [Fact]
    public async Task A14b_a_ACompactionLongerThanTheAckTimerPausesIt()
    {
        // Scaled from the row: ack 10 s → 1 s, compaction 25 s → 2.5 s, ack 2 s after → 0.2 s.
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.EmitAsync(CompactionStart("threshold"));
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            await p.EmitAsync(CompactionEnd("threshold"));
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            await p.AckAsync(f);
            await p.EmitAsync(TextRun("after compaction"));
        };

        var result = Terminal(await rig.RunTurnAsync());

        Assert.False(result.IsErrorResult);
        Assert.Equal("after compaction", result.FinalResult);
        Assert.Equal(0, rig.Pi.Current.Kills);
        Assert.Contains(rig.Messages(LogLevel.Information), m =>
            m.StartsWith("pi compaction threshold path=prefix tokensBefore=115000 tail=4096 splitTurn=false summary=390 cached=114000 ms=21000",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task A14b_b_NoAckWithinTheTimer_KillsAndTheNextTurnResumes()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = (_, _) => Task.CompletedTask;

        var result = Terminal(await rig.RunTurnAsync());

        Assert.True(result.IsErrorResult);
        Assert.Equal(
            "The local model did not accept the message within 10 s; it was not processed. Send it again.",
            result.FinalResult);
        Assert.Equal(1, rig.Pi.Processes[0].Kills);
        Assert.Contains(rig.Messages(LogLevel.Error), m => m.Contains("ack timeout", StringComparison.Ordinal));

        rig.Pi.OnPrompt = null;
        Assert.Equal("OK", Terminal(await rig.RunTurnAsync()).FinalResult);
        Assert.True(HasSession(rig.Pi.Starts[1], out var file));
        Assert.Equal(rig.Pi.SessionFile, file);
    }

    [Fact]
    public async Task A14b_c_AnUnendedCompactionIsEndedByTheWatchdogNotTheAckTimer()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = (p, _) => p.EmitAsync(CompactionStart("threshold"));
        var stopwatch = Stopwatch.StartNew();

        var result = Terminal(await rig.RunTurnAsync());

        Assert.True(result.IsErrorResult);
        Assert.Contains("local model stopped responding", result.FinalResult);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(2.9), $"ended after {stopwatch.Elapsed}");
        Assert.Equal(1, rig.Pi.Current.Kills);
        Assert.DoesNotContain(rig.Messages(LogLevel.Error), m => m.Contains("ack timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watchdog_ARunWithNoEvent_AbortsAndYieldsTheStoppedRespondingResult()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart());
        };
        rig.Pi.OnAbort = async (p, f) =>
        {
            await p.EmitAsync(ErrorMessage("Request was aborted", "aborted"), AgentEnd(), AgentSettled());
            await p.RespondAsync(f);
        };

        var result = Terminal(await rig.RunTurnAsync());

        Assert.Equal(PiExecutor.WatchdogText, result.FinalResult);
        Assert.True(result.IsErrorResult);
        Assert.Single(rig.Pi.Current.FramesOfType("abort"));
        Assert.Equal(0, rig.Pi.Current.Kills);
    }

    // ── A11: steering ───────────────────────────────────────────────────────

    private static Func<PiStandInProcess, JsonObject, Task> ToolThenFinal(string answer) => async (p, f) =>
    {
        await p.AckAsync(f);
        await p.EmitAsync(AgentStart(), TurnStart(),
            ToolCallMessage("c1", "bash", new JsonObject { ["command"] = "sleep 1" }),
            ToolStart("c1", "bash", new JsonObject { ["command"] = "sleep 1" }));
        await p.WaitForFrameAsync("steer", timeout: TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        await p.EmitAsync(ToolEnd("c1", "bash", ""), TurnEnd(), TurnStart(), FinalText(answer), TurnEnd(), AgentEnd(), AgentSettled());
    };

    [Fact]
    public async Task A11_ASteerDuringAToolIsInjected()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = ToolThenFinal("steered answer");
        MidTurnInjectionResult? injection = null;

        await foreach (var progress in rig.Executor.ExecuteAsync("work"))
        {
            if (progress.EventType == "tool_use")
                injection = await rig.Executor.TryInjectMessageAsync("also check b.txt");
        }

        Assert.Equal(MidTurnInjectionStatus.Injected, injection!.Status);
        var steer = Assert.Single(rig.Pi.Current.FramesOfType("steer"));
        Assert.Equal("also check b.txt", PiExecutor.Str(steer, "message"));
    }

    [Fact]
    public async Task A11_AfterTheFinalAnswer_NoActiveTurnAndNoFrame()
    {
        await using var rig = Rig.Create();
        var release = new TaskCompletionSource();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), FinalText("done"));
            await release.Task;
            await p.EmitAsync(TurnEnd(), AgentEnd(), AgentSettled());
        };

        var turn = rig.RunTurnAsync();
        var waited = Stopwatch.StartNew();
        while (!rig.Executor.FinalSeenForTests && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);

        var afterFinal = await rig.Executor.TryInjectMessageAsync("too late");
        release.SetResult();
        await turn;
        var afterTurn = await rig.Executor.TryInjectMessageAsync("no run");

        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, afterFinal.Status);
        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, afterTurn.Status);
        Assert.Empty(rig.Pi.Current.FramesOfType("steer"));
    }

    [Fact]
    public async Task A11_NoSteerResponseWithinTwoSeconds_Failed()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = ToolThenFinal("answer");
        rig.Pi.OnSteer = (_, _) => Task.CompletedTask;
        MidTurnInjectionResult? injection = null;
        var elapsed = TimeSpan.Zero;

        await foreach (var progress in rig.Executor.ExecuteAsync("work"))
        {
            if (progress.EventType != "tool_use")
                continue;
            var stopwatch = Stopwatch.StartNew();
            injection = await rig.Executor.TryInjectMessageAsync("steer");
            elapsed = stopwatch.Elapsed;
        }

        Assert.Equal(MidTurnInjectionStatus.Failed, injection!.Status);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A11_ASecondFinalInTheSameRun_ComesThroughReadInjectedTurnAnswers()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), FinalText("first answer"), TurnEnd(),
                TurnStart(), FinalText("late steer answer"), TurnEnd(), AgentEnd(), AgentSettled());
        };

        var result = Terminal(await rig.RunTurnAsync());
        var extra = new List<AgentProgress>();
        await foreach (var answer in rig.Executor.ReadInjectedTurnAnswersAsync(1))
            extra.Add(answer);

        Assert.Equal("first answer", result.FinalResult);
        Assert.Equal("late steer answer", Assert.Single(extra).FinalResult);
    }

    [Fact]
    public async Task A11_AFreshRunForALateSteer_IsReadBeforeTheSettle()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), FinalText("first answer"), TurnEnd(), AgentEnd());
            await Task.Delay(200);
            await p.EmitAsync(AgentStart(), TurnStart(), FinalText("late run answer"), TurnEnd(), AgentEnd(), AgentSettled());
        };

        var result = Terminal(await rig.RunTurnAsync());
        var extra = new List<AgentProgress>();
        await foreach (var answer in rig.Executor.ReadInjectedTurnAnswersAsync(1))
            extra.Add(answer);

        Assert.Equal("first answer", result.FinalResult);
        Assert.Equal("late run answer", Assert.Single(extra).FinalResult);

        // The settle was consumed: the next turn does not wait for it.
        var stopwatch = Stopwatch.StartNew();
        rig.Pi.OnPrompt = null;
        Assert.Equal("OK", Terminal(await rig.RunTurnAsync()).FinalResult);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReadInjectedTurnAnswers_WithNothingPending_ReturnsAfterTheShortBound()
    {
        await using var rig = Rig.Create();
        await rig.RunTurnAsync();

        var extra = new List<AgentProgress>();
        await foreach (var answer in rig.Executor.ReadInjectedTurnAnswersAsync(2))
            extra.Add(answer);

        Assert.Empty(extra);
    }

    // ── A12: cancellation ───────────────────────────────────────────────────

    [Fact]
    public async Task A12_AbortWithin100ms_NoSettleWithin5s_KilledAndTheNextTurnResumesTheSameFile()
    {
        await using var rig = Rig.Create(timings: PiStandIn.FastTimings with { AbortSettle = TimeSpan.FromSeconds(5) });
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(),
                ToolCallMessage("c1", "bash", new JsonObject { ["command"] = "sleep 600" }),
                ToolStart("c1", "bash", new JsonObject { ["command"] = "sleep 600" }));
        };
        using var cts = new CancellationTokenSource();
        var toolSeen = new TaskCompletionSource();
        var turn = Task.Run(async () =>
        {
            await foreach (var progress in rig.Executor.ExecuteAsync("long work", ct: cts.Token))
            {
                if (progress.EventType == "tool_use")
                    toolSeen.TrySetResult();
            }
        });
        await toolSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = rig.Pi.Current;

        var cancelledAt = Stopwatch.GetTimestamp();
        cts.Cancel();
        var abort = await first.WaitForFrameAsync("abort", timeout: TimeSpan.FromSeconds(2));
        var abortAfter = Stopwatch.GetElapsedTime(cancelledAt, first.ReceivedAt(abort));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        var cancelTook = Stopwatch.GetElapsedTime(cancelledAt);

        Assert.True(abortAfter < TimeSpan.FromMilliseconds(100), $"abort reached Pi {abortAfter.TotalMilliseconds} ms after cancel");
        Assert.True(cancelTook >= TimeSpan.FromSeconds(4.9), $"killed after {cancelTook}");
        Assert.Equal(1, first.Kills);

        rig.Pi.OnPrompt = null;
        Assert.Equal("OK", Terminal(await rig.RunTurnAsync()).FinalResult);
        Assert.True(HasSession(rig.Pi.Starts[1], out var file));
        Assert.Equal(rig.Pi.SessionFile, file);
    }

    [Fact]
    public async Task A12_ASettleAfterAbortKeepsTheProcess()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), ToolStart("c1", "bash", new JsonObject()));
        };
        rig.Pi.OnAbort = async (p, f) =>
        {
            await p.EmitAsync(ToolEnd("c1", "bash", "aborted", isError: true), ErrorMessage("aborted", "aborted"),
                AgentEnd(), AgentSettled());
            await p.RespondAsync(f);
        };
        using var cts = new CancellationTokenSource();
        var turn = Task.Run(async () =>
        {
            await foreach (var progress in rig.Executor.ExecuteAsync("work", ct: cts.Token))
            {
                if (progress.EventType == "tool_use")
                    await cts.CancelAsync();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);

        Assert.Equal(0, rig.Pi.Current.Kills);
        rig.Pi.OnPrompt = null;
        Assert.Equal("OK", Terminal(await rig.RunTurnAsync()).FinalResult);
        Assert.Single(rig.Pi.Processes);
    }

    // ── A13: /reset, /stop, crash ───────────────────────────────────────────

    [Fact]
    public async Task A13_ResetKillsAndDeletesTheSessionFile_TheNextTurnHasNoSession()
    {
        await using var rig = Rig.Create();
        var sessionFile = rig.CreateSessionFile();
        await rig.RunTurnAsync();

        await rig.Executor.StopProcessAsync();

        Assert.Equal(1, rig.Pi.Processes[0].Kills);
        Assert.False(File.Exists(sessionFile));
        Assert.Null(rig.Executor.SessionFileForTests);
        Assert.Null(rig.Executor.LastSessionId);

        await rig.RunTurnAsync();
        Assert.False(HasSession(rig.Pi.Starts[1], out _));
        Assert.Contains(rig.Messages(LogLevel.Information), m => m.Contains("session new images off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A13_TryStop_StopsARunningProcessAndReportsFalseWhenNoneRuns()
    {
        await using var rig = Rig.Create();
        Assert.False(await rig.Executor.TryStopProcessAsync());

        var sessionFile = rig.CreateSessionFile();
        await rig.RunTurnAsync();

        Assert.True(await rig.Executor.TryStopProcessAsync());
        Assert.False(File.Exists(sessionFile));
        Assert.False(await rig.Executor.TryStopProcessAsync());
    }

    [Fact]
    public async Task A13_AfterACrashTheNextTurnResumesWithTheSessionPath()
    {
        await using var rig = Rig.Create();
        var sessionFile = rig.CreateSessionFile();
        await rig.RunTurnAsync();

        rig.Pi.Current.Exit();
        await Task.Delay(50);
        Assert.False(rig.Executor.IsProcessWarm);
        await rig.RunTurnAsync();

        Assert.Equal(2, rig.Pi.Starts.Count);
        Assert.True(HasSession(rig.Pi.Starts[1], out var file));
        Assert.Equal(sessionFile, file);
        Assert.True(File.Exists(sessionFile));
        Assert.Contains(rig.Messages(LogLevel.Information), m => m.Contains("session resumed images off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestRestart_OnlySetsAFlag_TheNextTurnRestartsWithTheSession()
    {
        await using var rig = Rig.Create();
        await rig.RunTurnAsync();

        rig.Executor.RequestRestart();
        Assert.Equal(0, rig.Pi.Processes[0].Kills);

        await rig.RunTurnAsync();
        Assert.Equal(1, rig.Pi.Processes[0].Kills);
        Assert.True(HasSession(rig.Pi.Starts[1], out var file));
        Assert.Equal(rig.Pi.SessionFile, file);
    }

    [Fact]
    public async Task ACorruptSessionFileStartsANewSessionWithAWarning()
    {
        await using var rig = Rig.Create();
        var sessionFile = rig.CreateSessionFile();
        await rig.RunTurnAsync();

        File.WriteAllText(sessionFile, "not a session\n");
        rig.Executor.RequestRestart();
        await rig.RunTurnAsync();

        Assert.False(HasSession(rig.Pi.Starts[1], out _));
        Assert.Contains(rig.Messages(LogLevel.Warning), m => m.Contains("unreadable or corrupt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSessionComesFromGetStateAfterEverySettle()
    {
        await using var rig = Rig.Create();
        await rig.RunTurnAsync();
        Assert.Equal("s-1", rig.Executor.LastSessionId);

        rig.Pi.SessionId = "s-2";
        rig.Pi.SessionFile = "/workspace/example/.pi/sessions/s-2.jsonl"; // hygiene-ok: corpus path
        await rig.RunTurnAsync();

        var waited = Stopwatch.StartNew();
        while (rig.Executor.LastSessionId != "s-2" && waited.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.Equal("s-2", rig.Executor.LastSessionId);
        Assert.Equal("/workspace/example/.pi/sessions/s-2.jsonl", rig.Executor.SessionFileForTests); // hygiene-ok: corpus path
        Assert.True(rig.Pi.Current.FramesOfType("get_state").Count >= 2);
    }

    // ── A14: waiting for the previous settle ────────────────────────────────

    [Fact]
    public async Task A14_APostRunCompaction_DelaysTheNextPromptUntilSettle_NeverAlreadyProcessing()
    {
        await using var rig = Rig.Create(timings: PiStandIn.FastTimings with { PreviousSettle = TimeSpan.FromSeconds(10) });
        var busy = false;
        long settledAt = 0;
        rig.Pi.OnPrompt = async (p, f) =>
        {
            if (Volatile.Read(ref busy))
            {
                await p.RespondAsync(f, success: false,
                    error: "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.");
                return;
            }

            Volatile.Write(ref busy, true);
            await p.AckAsync(f);
            await p.EmitAsync(TextRun(PiExecutor.Str(f, "message")!, settle: false));
            await p.EmitAsync(CompactionStart("threshold"));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await p.EmitAsync(CompactionEnd("threshold"));
            Volatile.Write(ref busy, false);
            Interlocked.Exchange(ref settledAt, Stopwatch.GetTimestamp());
            await p.EmitAsync(AgentSettled());
        };

        var first = Terminal(await rig.RunTurnAsync("first"));
        var second = Terminal(await rig.RunTurnAsync("second"));

        Assert.Equal("first", first.FinalResult);
        Assert.Equal("second", second.FinalResult);
        Assert.False(second.IsErrorResult);
        var secondPrompt = rig.Pi.Current.FramesOfType("prompt")[1];
        Assert.True(rig.Pi.Current.ReceivedAt(secondPrompt) > Interlocked.Read(ref settledAt));
        Assert.Single(rig.Pi.Processes);
    }

    [Fact]
    public async Task A14_APreviousRunThatNeverSettles_IsAbortedKilledAndTheTurnAnsweredOnTheSameSession()
    {
        await using var rig = Rig.Create(timings: PiStandIn.FastTimings with
        {
            PreviousSettle = TimeSpan.FromSeconds(1),
            AbortSettle = TimeSpan.FromSeconds(5),
        });
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            if (p.Index == 0)
                await p.EmitAsync(TextRun("first", settle: false));
            else
                await p.EmitAsync(TextRun("second"));
        };

        Assert.Equal("first", Terminal(await rig.RunTurnAsync("first")).FinalResult);
        var stopwatch = Stopwatch.StartNew();
        var second = Terminal(await rig.RunTurnAsync("second"));

        Assert.Equal("second", second.FinalResult);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(5.9), $"answered after {stopwatch.Elapsed}");
        Assert.Single(rig.Pi.Processes[0].FramesOfType("abort"));
        Assert.Single(rig.Pi.Processes[0].FramesOfType("prompt"));
        Assert.Equal(1, rig.Pi.Processes[0].Kills);
        Assert.True(HasSession(rig.Pi.Starts[1], out var file));
        Assert.Equal(rig.Pi.SessionFile, file);
        Assert.Contains(rig.Messages(LogLevel.Warning), m => m.Contains("did not settle", StringComparison.Ordinal));
    }

    // ── Events (§4.3) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Events_ToolUseCapsArgsAt200_ToolResultAndAutoRetryAreNotSignificant()
    {
        await using var rig = Rig.Create();
        var longArgs = new JsonObject { ["command"] = new string('x', 500) };
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), ToolCallMessage("c1", "bash", longArgs.DeepClone().AsObject()),
                ToolStart("c1", "bash", longArgs.DeepClone().AsObject()), ToolEnd("c1", "bash", "out"), TurnEnd(),
                AutoRetryStart(), TurnStart(), FinalText("ok"), TurnEnd(), AgentEnd(), AgentSettled());
        };

        var progress = await rig.RunTurnAsync();

        var toolUse = Assert.Single(progress, p => p.EventType == "tool_use");
        Assert.Equal("bash", toolUse.ToolName);
        Assert.True(toolUse.IsSignificant);
        Assert.Equal(200, toolUse.ToolArgs!.Length);
        var toolResult = Assert.Single(progress, p => p.EventType == "tool_result");
        Assert.Equal("out", toolResult.Summary);
        Assert.False(toolResult.IsSignificant);
        var warning = Assert.Single(progress, p => p.EventType == "warning");
        Assert.False(warning.IsSignificant);
        Assert.Contains(rig.Messages(LogLevel.Warning), m => m.StartsWith("pi auto_retry: attempt 1/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Events_AnErrorEnd_IsAnErrorResultNamingTheTag()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(),
                ErrorMessage("404 {\"error\":\"model 'qwen3.8-flash-next' not found\"}"), TurnEnd(), AgentEnd(), AgentSettled());
        };

        var result = Terminal(await rig.RunTurnAsync());

        Assert.True(result.IsErrorResult);
        Assert.False(result.IsProcessExit);
        Assert.StartsWith($"The local model returned an error for model {PiStandIn.Tag}: 404", result.FinalResult);
    }

    [Fact]
    public async Task Events_AnUnreachableServer_NamesTheOrigin()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), ErrorMessage("Connection error."), TurnEnd(), AgentEnd(), AgentSettled());
        };

        var result = Terminal(await rig.RunTurnAsync());

        Assert.Equal($"local model server unreachable ({PiStandIn.BaseUrl})", result.FinalResult);
    }

    [Fact]
    public async Task Events_TheServerTextIsBoundedTo300Characters()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), ErrorMessage("500 " + new string('e', 1000)), AgentEnd(), AgentSettled());
        };

        var result = Terminal(await rig.RunTurnAsync());

        var prefix = $"The local model returned an error for model {PiStandIn.Tag}: ";
        Assert.Equal(prefix.Length + 300, result.FinalResult!.Length);
    }

    [Fact]
    public async Task Events_StdoutClosedMidRun_IsAProcessExit()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(), TextDelta("partial"));
            p.Exit();
        };

        var result = Terminal(await rig.RunTurnAsync());

        Assert.True(result.IsErrorResult);
        Assert.True(result.IsProcessExit);
    }

    [Fact]
    public async Task Logs_CallLineCarriesHostTtftAndCountsOnly_AndTheBudgetWarnings()
    {
        await using var rig = Rig.Create(timings: PiStandIn.FastTimings with
        {
            PostCompactionTtftWarning = TimeSpan.FromMilliseconds(100),
            CompactionWarning = TimeSpan.FromMilliseconds(100),
        });
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), CompactionStart("threshold"));
            await Task.Delay(200);
            await p.EmitAsync(CompactionEnd("threshold", tailTokens: 12000), TurnStart());
            await Task.Delay(250);
            await p.EmitAsync(TextDelta("secret answer text"),
                FinalText("secret answer text", Usage(input: 500, output: 9, cacheRead: 21504)),
                TurnEnd(), AgentEnd(), AgentSettled());
        };

        await rig.RunTurnAsync("secret prompt text");

        var call = Assert.Single(rig.Messages(LogLevel.Information), m => m.StartsWith("pi call:", StringComparison.Ordinal));
        Assert.Matches(@"^pi call: prompt=22004 cached=21504 output=9 ttft_ms=\d+ total_ms=\d+ stop=stop$", call);
        var ttft = long.Parse(System.Text.RegularExpressions.Regex.Match(call, @"ttft_ms=(\d+)").Groups[1].Value);
        Assert.InRange(ttft, 200, 5000);

        var warnings = rig.Messages(LogLevel.Warning);
        Assert.Contains(warnings, m => m.Contains("kept tail of 12000 tokens is over the 10240 budget", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.StartsWith("pi compaction threshold took", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.StartsWith("pi: post-compaction TTFT", StringComparison.Ordinal));

        // §12 / §20: counts and names only.
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stderr_ExtensionLinesAreForwardedAtTheirLevel()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.EmitStderrAsync("WARN fast-path fallback reason=kept_unresolved");
            await p.EmitStderrAsync("INFO mcp tools registered=2");
            await p.EmitStderrAsync("ERROR /workspace/.mcp.json is malformed");
            await p.EmitStderrAsync("Debugger listening");
            await Task.Delay(100);
            await p.AckAsync(f);
            await p.EmitAsync(TextRun("OK"));
        };

        await rig.RunTurnAsync();

        Assert.Contains("[pi] fast-path fallback reason=kept_unresolved", rig.Messages(LogLevel.Warning));
        Assert.Contains("[pi] mcp tools registered=2", rig.Messages(LogLevel.Information));
        Assert.Contains("[pi] /workspace/.mcp.json is malformed", rig.Messages(LogLevel.Error));
        Assert.Contains("[pi stderr] Debugger listening", rig.Messages(LogLevel.Debug));
    }

    [Fact]
    public async Task AnExtensionDialogIsCancelledAtOnce()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.EmitAsync(new JsonObject
            {
                ["type"] = "extension_ui_request",
                ["id"] = "ui-1",
                ["method"] = "confirm",
                ["title"] = "Allow?",
            });
            var response = await p.WaitForFrameAsync("extension_ui_response");
            Assert.True(PiExecutor.Bool(response, "cancelled"));
            await p.AckAsync(f);
            await p.EmitAsync(TextRun("OK"));
        };

        Assert.Equal("OK", Terminal(await rig.RunTurnAsync()).FinalResult);
    }

    // ── Media fallback, /run, background tasks ──────────────────────────────

    [Fact]
    public async Task Images_AreNotSent_TheMessageSaysHowManyWereOmitted()
    {
        await using var rig = Rig.Create();
        var images = new List<MessageImage> { new([1, 2, 3], "image/png"), new([4], "image/jpeg") };

        await rig.RunTurnAsync("what is this?", images);

        var prompt = Assert.Single(rig.Pi.Current.FramesOfType("prompt"));
        Assert.Equal("what is this?\n\n[2 image attachment(s) omitted: this local model is text-only]",
            PiExecutor.Str(prompt, "message"));
        Assert.Null(prompt["images"]);
    }

    // ── #455: attachments on every path ─────────────────────────────────────

    private MessageDocument TextPdf(Rig rig, string name = "report.pdf")
    {
        var path = Path.Combine(rig.TempPath, name);
        File.WriteAllText(path, "%PDF-1.4\nsynthetic\n");
        return new MessageDocument("file-id", "application/pdf", 20, name) { FilePath = path };
    }

    private static string ImagesJson(JsonObject frame) => frame["images"]?.ToJsonString() ?? "";

    [Fact]
    public async Task ImageInputOn_ThePromptCarriesImageBlocks_WithTheSniffedTypeAndExactBytes()
    {
        await using var rig = Rig.Create(imageInput: true);
        var png = TestImages.Png(64, 48);

        await rig.RunTurnAsync("what is this?", [new MessageImage(png, "image/jpeg")]);

        var prompt = Assert.Single(rig.Pi.Current.FramesOfType("prompt"));
        Assert.Equal("what is this?", PiExecutor.Str(prompt, "message"));
        var image = Assert.Single(prompt["images"]!.AsArray())!.AsObject();
        Assert.Equal("image", PiExecutor.Str(image, "type"));
        Assert.Equal("image/png", PiExecutor.Str(image, "mimeType"));
        Assert.Equal(png, Convert.FromBase64String(PiExecutor.Str(image, "data")!));
        Assert.Contains(rig.Messages(LogLevel.Information), m => m.Contains("images on"));
    }

    [Fact]
    public async Task A6_PromptAndSteer_CarryIdenticalImagesAndTheSamePdfText_ExtractedOnce()
    {
        await using var rig = Rig.Create(imageInput: true);
        rig.Pi.OnPrompt = ToolThenFinal("answer");
        var images = new List<MessageImage> { new(TestImages.Png(64, 48), "image/png") };
        var documents = new List<MessageDocument> { TextPdf(rig) };
        MidTurnInjectionResult? injection = null;

        await foreach (var progress in rig.Executor.ExecuteAsync("look", images, documents))
        {
            if (progress.EventType == "tool_use")
                injection = await rig.Executor.TryInjectMessageAsync("look", images, documents);
        }

        Assert.Equal(MidTurnInjectionStatus.Injected, injection!.Status);
        var prompt = Assert.Single(rig.Pi.Current.FramesOfType("prompt"));
        var steer = Assert.Single(rig.Pi.Current.FramesOfType("steer"));
        Assert.Equal(ImagesJson(prompt), ImagesJson(steer));
        Assert.Equal(PiExecutor.Str(prompt, "message"), PiExecutor.Str(steer, "message"));
        Assert.Contains("[PDF text: report.pdf, pages 1–3 of 3;", PiExecutor.Str(steer, "message"));
        Assert.Equal(1, rig.Tools.Count(PiPdfExtractor.PdfToText));
    }

    [Fact]
    public async Task A6_ARefusedSteer_IsResentAsAPrompt_WithNoSecondExtraction()
    {
        await using var rig = Rig.Create(imageInput: true);
        rig.Pi.OnPrompt = ToolThenFinal("answer");
        rig.Pi.OnSteer = (p, f) => p.RespondAsync(f, success: false, error: "busy");
        var documents = new List<MessageDocument> { TextPdf(rig) };
        MidTurnInjectionResult? injection = null;

        await foreach (var progress in rig.Executor.ExecuteAsync("work"))
        {
            if (progress.EventType == "tool_use")
                injection = await rig.Executor.TryInjectMessageAsync("read this", documents: documents);
        }
        rig.Pi.OnPrompt = null;
        await rig.RunTurnAsync("read this", documents: documents);

        Assert.Equal(MidTurnInjectionStatus.Failed, injection!.Status);
        Assert.Contains("[PDF text: report.pdf", PiExecutor.Str(rig.Pi.Current.FramesOfType("prompt")[^1], "message"));
        Assert.Equal(1, rig.Tools.Count(PiPdfExtractor.PdfToText));
    }

    [Fact]
    public async Task A6_AQueuedBatchOfThreeImages_IsOnePromptWithTwoImagesAndTheNote()
    {
        await using var rig = Rig.Create(imageInput: true);
        var images = Enumerable.Range(1, 3)
            .Select(i => new MessageImage(TestImages.Png(10 * i, 10), "image/jpeg") { FilePath = $"/workspace/example/{i}.jpg" })
            .ToList();

        await rig.RunTurnAsync("three parts", images);

        var prompt = Assert.Single(rig.Pi.Current.FramesOfType("prompt"));
        Assert.Equal(2, prompt["images"]!.AsArray().Count);
        Assert.Equal(
            "three parts\n\n[1 image attachment(s) omitted: 1 not inline: over the 2-image limit — open the path with read]",
            PiExecutor.Str(prompt, "message"));
    }

    [Fact]
    public async Task A6b_TheRunEndsWhileTheSteerIsExtracting_NoSteerIsWritten_AndThePromptUsesTheCache()
    {
        var gate = new TaskCompletionSource();
        var tools = new FakePiLocalTools();
        tools.Handler = async (tool, _, _, ct) =>
        {
            if (tool == PiPdfExtractor.PdfToText)
                await gate.Task.WaitAsync(ct);
            return null;
        };
        await using var rig = Rig.Create(imageInput: true, tools: tools);
        var endRun = new TaskCompletionSource();
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(AgentStart(), TurnStart(),
                ToolCallMessage("c1", "bash", new JsonObject { ["command"] = "true" }),
                ToolStart("c1", "bash", new JsonObject { ["command"] = "true" }));
            await endRun.Task;
            await p.EmitAsync(ToolEnd("c1", "bash", ""), TurnEnd(), TurnStart(), FinalText("first"), TurnEnd(), AgentEnd(), AgentSettled());
        };
        var documents = new List<MessageDocument> { TextPdf(rig) };
        Task<MidTurnInjectionResult>? steer = null;

        await foreach (var progress in rig.Executor.ExecuteAsync("work"))
        {
            if (progress.EventType != "tool_use")
                continue;
            steer = rig.Executor.TryInjectMessageAsync("read this", documents: documents);
            while (tools.Count(PiPdfExtractor.PdfToText) == 0)
                await Task.Delay(10);
            endRun.SetResult();
        }
        gate.SetResult();
        var injection = await steer!;

        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, injection.Status);
        Assert.Empty(rig.Pi.Current.FramesOfType("steer"));

        rig.Pi.OnPrompt = null;
        await rig.RunTurnAsync("read this", documents: documents);
        Assert.Contains("[PDF text: report.pdf", PiExecutor.Str(rig.Pi.Current.FramesOfType("prompt")[^1], "message"));
        Assert.Equal(1, tools.Count(PiPdfExtractor.PdfToText));
    }

    [Fact]
    public async Task A7_CancelledDuringProcessing_NoFrameIsWritten()
    {
        var tools = new FakePiLocalTools();
        tools.Handler = async (_, _, budget, ct) => await tools.BlockAsync(budget, ct);
        await using var rig = Rig.Create(tools: tools);
        using var cts = new CancellationTokenSource();
        var documents = new List<MessageDocument> { TextPdf(rig) };

        var turn = rig.RunTurnAsync("read this", ct: cts.Token, documents: documents);
        while (tools.Calls.Count == 0)
            await Task.Delay(10);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        Assert.All(rig.Pi.Processes, p => Assert.Empty(p.FramesOfType("prompt")));
    }

    [Fact]
    public async Task A7_CancelledDuringABlockedWrite_TheWholeFrameStillArrives()
    {
        await using var rig = Rig.Create();
        await rig.RunTurnAsync("first");
        var gate = new TaskCompletionSource();
        rig.Pi.StdinGate = gate;
        using var cts = new CancellationTokenSource();

        var turn = rig.RunTurnAsync("second", ct: cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await Task.Delay(200);
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        // Every byte on stdin is a complete JSON line; the cancelled prompt went out whole.
        var stdin = rig.Pi.Processes[0].StdinText;
        Assert.EndsWith("\n", stdin);
        var frames = stdin.TrimEnd('\n').Split('\n').Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        var prompts = frames.Where(f => PiExecutor.Str(f, "type") == "prompt").ToList();
        Assert.Equal(2, prompts.Count);
        Assert.Equal("second", PiExecutor.Str(prompts[1], "message"));
    }

    [Fact]
    public async Task A7_AWriteBlockedPastTheDeadline_KillsTheProcess_AndEndsWithProcessExitText()
    {
        await using var rig = Rig.Create(timings: PiStandIn.FastTimings with { StdinWrite = TimeSpan.FromSeconds(1) });
        await rig.RunTurnAsync("first");
        var gate = new TaskCompletionSource();
        rig.Pi.StdinGate = gate;
        try
        {
            var clock = Stopwatch.StartNew();
            var result = Terminal(await rig.RunTurnAsync("second"));

            Assert.Equal(PiExecutor.ProcessExitText, result.FinalResult);
            Assert.True(result.IsErrorResult);
            Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10));
            Assert.Equal(1, rig.Pi.Processes[0].Kills);
            Assert.Contains(rig.Messages(LogLevel.Error), m => m.StartsWith("pi: a stdin frame was not written within 1 s"));
        }
        finally
        {
            rig.Pi.StdinGate = null;
            gate.SetResult();
        }
    }

    [Fact]
    public async Task A13_PiEchoingFewerImagesThanSent_LogsOneDroppedWarning()
    {
        await using var rig = Rig.Create(imageInput: true);
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "look" },
                        new JsonObject { ["type"] = "image", ["data"] = "AAAA", ["mimeType"] = "image/png" },
                    },
                },
            });
            await p.EmitAsync(TextRun("OK"));
        };

        await rig.RunTurnAsync("look", [new MessageImage(TestImages.Png(10, 10), "image/png"), new MessageImage(TestImages.Png(20, 20), "image/png")]);

        Assert.Single(rig.Messages(LogLevel.Warning), m => m == "pi images: Pi dropped 1 of 2 image(s) on the prompt path");
    }

    [Fact]
    public async Task A13_ASessionFileOver256MiB_LogsOneWarningPerCrossing()
    {
        await using var rig = Rig.Create();
        var session = rig.CreateSessionFile();
        using (var stream = new FileStream(session, FileMode.Open))
            stream.SetLength(PiLocalModel.SessionFileWarningBytes + 1024 * 1024); // sparse

        await rig.RunTurnAsync("one");
        await rig.RunTurnAsync("two");

        Assert.Single(rig.Messages(LogLevel.Warning), m => m == "pi session file is 257 MiB; /reset starts a new one");
    }

    [Fact]
    public async Task AnEchoLineOf24MiB_IsHandled_AndNeverLogged()
    {
        await using var rig = Rig.Create();
        var big = new string('q', 24 * 1024 * 1024);
        rig.Pi.OnPrompt = async (p, f) =>
        {
            await p.AckAsync(f);
            await p.EmitAsync(new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = big } },
                },
            });
            await p.EmitAsync(TextRun("OK"));
        };

        Assert.Equal("OK", Terminal(await rig.RunTurnAsync()).FinalResult);
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Message.Contains("qqqqqqqq"));
    }

    [Fact]
    public async Task Run_IsAnRpcBashExcludedFromContext()
    {
        await using var rig = Rig.Create();
        rig.Pi.OnBash = (p, f) => p.RespondAsync(f, new JsonObject
        {
            ["output"] = "total 0\n",
            ["exitCode"] = 2,
            ["cancelled"] = false,
            ["truncated"] = false,
        });

        var progress = new List<AgentProgress>();
        await foreach (var item in rig.Executor.SendCommandAsync("ls -la"))
            progress.Add(item);

        var bash = Assert.Single(rig.Pi.Current.FramesOfType("bash"));
        Assert.Equal("ls -la", PiExecutor.Str(bash, "command"));
        Assert.True(PiExecutor.Bool(bash, "excludeFromContext"));
        Assert.Equal("total 0\n\n[exit code 2]", Assert.Single(progress).FinalResult);
    }

    [Fact]
    public async Task BackgroundTasks_AreAlwaysEmpty()
    {
        await using var rig = Rig.Create();

        Assert.Empty(rig.Executor.GetActiveBackgroundTasks());
        Assert.False(await rig.Executor.CancelBackgroundTaskAsync("any"));
    }

    // ── A10: recorded-shape fixtures through ProviderFrameReplay ────────────

    [Fact]
    public async Task A10_OrdinaryTurn_ReplaysToTheFinalResult()
    {
        var progress = await ProviderFrameReplay.ReplayPiAsync("ordinary-turn.ndjson");

        var result = Terminal(progress);
        Assert.Equal("The build passed.", result.FinalResult);
        Assert.False(result.IsErrorResult);
        Assert.Equal("The build passed.", string.Concat(progress.Where(p => p.EventType == "assistant").Select(p => p.Summary)));
    }

    [Fact]
    public async Task A10_ToolTurn_ReplaysToolUseAndToolResultProgress()
    {
        var progress = (await ProviderFrameReplay.ReplayPiAsync("tool-turn.ndjson")).ToList();

        var toolUse = Assert.Single(progress, p => p.EventType == "tool_use");
        Assert.Equal("bash", toolUse.ToolName);
        Assert.Contains("ls /workspace/example", toolUse.ToolArgs); // hygiene-ok: corpus path
        var toolResult = Assert.Single(progress, p => p.EventType == "tool_result");
        Assert.Equal("notes.txt\n", toolResult.Summary);
        Assert.Equal("One file: notes.txt.", Terminal(progress).FinalResult);
        Assert.True(progress.IndexOf(toolUse) < progress.IndexOf(toolResult));
    }

    [Fact]
    public async Task A10_ErrorTurn_ReplaysToAnErrorResult()
    {
        var result = Terminal(await ProviderFrameReplay.ReplayPiAsync("error-turn.ndjson"));

        Assert.True(result.IsErrorResult);
        Assert.False(result.IsProcessExit);
    }

    [Fact]
    public async Task A10_ProcessExit_ReplaysToIsProcessExit()
    {
        var result = Terminal(await ProviderFrameReplay.ReplayPiAsync("process-exit.ndjson", exitAfterReplay: true));

        Assert.True(result.IsErrorResult);
        Assert.True(result.IsProcessExit);
    }

    [Fact]
    public async Task A10_FailedOverflowRecovery_ReplaysToTheResetText()
    {
        var result = Terminal(await ProviderFrameReplay.ReplayPiAsync("overflow-recovery-failed.ndjson"));

        Assert.True(result.IsErrorResult);
        Assert.Equal("context overflowed and automatic recovery failed — send /reset", result.FinalResult);
    }

    [Fact]
    public async Task A10_FailedOverflowRecoveryWithoutItsEvent_StillReplaysToTheResetText()
    {
        var result = Terminal(await ProviderFrameReplay.ReplayPiAsync("overflow-recovery-silent.ndjson"));

        Assert.True(result.IsErrorResult);
        Assert.Equal("context overflowed and automatic recovery failed — send /reset", result.FinalResult);
    }

    [Fact]
    public async Task A10_APreviousRunsLateFailedRecovery_DoesNotFailTheNextRun()
    {
        var result = Terminal(await ProviderFrameReplay.ReplayPiAsync("stray-recovery-before-run.ndjson"));
        var ordinary = Terminal(await ProviderFrameReplay.ReplayPiAsync("ordinary-turn.ndjson"));

        Assert.False(result.IsErrorResult);
        Assert.Equal(ordinary.FinalResult, result.FinalResult);
    }
}
