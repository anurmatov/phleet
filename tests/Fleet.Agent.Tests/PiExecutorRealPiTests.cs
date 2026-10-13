using System.Diagnostics;
using System.Text.Json.Nodes;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// The real <see cref="PiExecutor"/> running the real pinned <c>pi</c> binary (#386 §15) against the
/// Fleet extension and its two test stubs. Excluded from the default run by its trait; CI's pi job
/// runs it with <c>--filter Category=RealPi</c>.
/// </summary>
/// <remarks>
/// <para><b>Rows covered here:</b> A28 (A9 files, A8 arguments, <c>get_state</c>, a text, a
/// <c>bash</c> and an MCP turn, a steer during a tool, a fast-path threshold compaction it survives),
/// A31 (the first request's <c>tools</c>), A12 (abort on cancel, the same process or the same
/// session afterwards), A13 (reset deletes the session; a crash resumes it), A14 case 1 (the next
/// prompt waits for a compaction's settle, never "already processing"), A14b (c) (the watchdog ends
/// a hung server call), A23 (strace: connections only to the stubs), A32 (no credential file, the
/// placeholder key), A33 (bash edits to the agent dir do not survive <c>/reset</c>; no request while
/// idle). A10, A11, A14 case 2 and A14b (a)/(b) need a Pi that misbehaves on demand and run against
/// the stand-in in <see cref="PiExecutorTests"/>.</para>
/// <para><b>Inputs.</b> <c>FLEET_PI_BIN</c> (default <c>pi</c> on PATH) and
/// <c>FLEET_PI_EXTENSION_DIR</c> (default <c>src/fleet-pi-extension</c>). A missing <c>pi</c>,
/// extension or stub is a HARD FAILURE, never a skip: a skipped real-process row is an unmeasured
/// row reported as green. A23 needs <c>strace</c>; without it the row fails when
/// <c>FLEET_PI_REALPI_STRICT=1</c> (CI) and otherwise returns unmeasured — the one documented
/// exception, because strace is a host tool the developer machine may not have.</para>
/// <para><b>Stub contract</b> (owned by the extension): <c>node stubs/anthropic.ts --record-dir D
/// --script S</c> and <c>node stubs/mcp.ts --record-dir D --tools T</c>, each printing
/// <c>LISTENING &lt;url&gt;</c> as its first stdout line and recording every request as
/// <c>D/NNNN.json</c>.</para>
/// </remarks>
[Trait("Category", "RealPi")]
public sealed class PiExecutorRealPiTests
{
    private const string Tag = "qwen3.8-flash-next";
    private const string McpTool = "mcp__fleet-memory__memory_get";
    private const string McpToolText = "remembered: synthetic memory value";

    private static string PiBinary =>
        Environment.GetEnvironmentVariable("FLEET_PI_BIN") is { Length: > 0 } bin ? bin : PiExecutor.PiBin;

    private static string ExtensionDir =>
        Environment.GetEnvironmentVariable("FLEET_PI_EXTENSION_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(RepoPaths.Root, "src", "fleet-pi-extension");

    // ── A28 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A28_TextBashMcpAndSteerTurns_ThroughThePublicInterface()
    {
        await using var rig = await RealRig.StartAsync(new JsonObject
        {
            ["main"] = new JsonArray
            {
                Text("Hello from the stub."),
                ToolUse("bash", new JsonObject { ["command"] = "echo fleet-bash-marker" }),
                Text("Ran it."),
                ToolUse(McpTool, new JsonObject { ["key"] = "k" }),
                Text("Got it."),
                ToolUse("bash", new JsonObject { ["command"] = "sleep 3" }),
                Text("Steered answer."),
            },
        });

        // Text turn: the files, the arguments and get_state.
        Assert.Equal("Hello from the stub.", Final(await rig.RunTurnAsync("say hello")));
        rig.AssertRuntimeFiles();
        Assert.Equal(
        [
            "--mode", "rpc", "--offline", "--no-approve",
            "--provider", "fleet-local", "--model", Tag,
            "--thinking", "medium",
            "--system-prompt", Path.Combine(rig.WorkDir, "system-prompt.md"),
            "-nc", "-ns", "-np", "-ne", "--no-mcp",
            "-e", ExtensionDir,
            "--tools", $"read,bash,edit,write,{McpTool}",
        ], rig.Starts[0].ArgumentList);
        Assert.Contains(rig.Info, m => m.StartsWith($"pi start: version {PiLocalModel.PinnedVersion} model {Tag}", StringComparison.Ordinal));

        // A31: exactly the four built-ins and the granted name, byte-equal.
        Assert.Equal(["read", "bash", "edit", "write", McpTool], ToolNames(rig.Requests()[0]));

        // bash turn.
        var bash = await rig.RunTurnAsync("run the marker");
        Assert.Contains(bash, p => p.EventType == "tool_use" && p.ToolName == "bash");
        Assert.Contains(bash, p => p.EventType == "tool_result" && p.Summary.Contains("fleet-bash-marker", StringComparison.Ordinal));
        Assert.Equal("Ran it.", Final(bash));

        // MCP turn, through the extension's bridge under the exact granted name.
        var mcp = await rig.RunTurnAsync("read the memory");
        Assert.Contains(mcp, p => p.EventType == "tool_use" && p.ToolName == McpTool);
        Assert.Contains(mcp, p => p.EventType == "tool_result" && p.Summary.Contains(McpToolText, StringComparison.Ordinal));
        Assert.Equal("Got it.", Final(mcp));
        Assert.NotEmpty(rig.McpRequests());

        // A steer while the tool runs reaches the next model call.
        MidTurnInjectionResult? steer = null;
        var steered = new List<AgentProgress>();
        await foreach (var progress in rig.Executor.ExecuteAsync("slow work"))
        {
            steered.Add(progress);
            if (progress.EventType == "tool_use")
                steer = await rig.Executor.TryInjectMessageAsync("steer-marker: also report the date");
        }
        Assert.Equal(MidTurnInjectionStatus.Injected, steer!.Status);
        Assert.Equal("Steered answer.", Final(steered));
        Assert.Contains("steer-marker", rig.Requests()[^1].ToJsonString());
    }

    [Fact]
    public async Task A28_A14_AFastPathThresholdCompaction_IsSurvived_AndTheNextPromptWaitsForItsSettle()
    {
        // W = 65536 compacts at 49152 tokens; long messages cross it in a few turns. The summary
        // answer is delayed 3 s, so the turn after the crossing one meets a compaction in progress.
        await using var rig = await RealRig.StartAsync(
            new JsonObject
            {
                ["summary"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["delay_ms"] = 3000,
                        ["text"] = "Goal: synthetic. Constraints: none. Progress: long turns. Key decisions: none. "
                                   + "Next steps: continue. Critical context: anchor-7Q2.",
                    },
                },
            },
            contextWindow: PiLocalModel.MinContextWindow);

        var filler = string.Join(' ', Enumerable.Repeat("synthetic filler sentence for compaction growth.", 1200));
        var compacted = false;
        for (var turn = 1; turn <= 10 && !compacted; turn++)
        {
            var result = Assert.Single(await rig.RunTurnAsync($"turn {turn}: {filler}"), p => p.EventType == "result");
            Assert.False(result.IsErrorResult, result.FinalResult);
            compacted = rig.Info.Any(m => m.StartsWith("pi compaction threshold path=prefix", StringComparison.Ordinal));
        }

        // The prompt after the crossing turn: step 1 waits for the settle, so Pi never refuses it.
        var after = Assert.Single(await rig.RunTurnAsync("after compaction"), p => p.EventType == "result");
        compacted = compacted || rig.Info.Any(m => m.StartsWith("pi compaction threshold path=prefix", StringComparison.Ordinal));

        Assert.True(compacted, "no fast-path threshold compaction was logged: "
            + string.Join(" | ", rig.Logger.Entries.Where(e => e.Message.StartsWith("pi ", StringComparison.Ordinal)).Select(e => e.Message)));
        Assert.False(after.IsErrorResult, after.FinalResult);
        Assert.DoesNotContain(rig.Warnings, m => m.Contains("fast-path fallback", StringComparison.Ordinal));
        Assert.Single(rig.Starts);
        Assert.Contains(rig.Requests(), r => LastUserText(r).StartsWith("Do not call tools.", StringComparison.Ordinal));
    }

    // ── A12, A13 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A12_CancelDuringATool_AbortsAndTheNextTurnIsAnswered()
    {
        await using var rig = await RealRig.StartAsync(new JsonObject
        {
            ["main"] = new JsonArray { ToolUse("bash", new JsonObject { ["command"] = "sleep 60" }) },
        });
        using var cts = new CancellationTokenSource();
        var cancelled = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var progress in rig.Executor.ExecuteAsync("long tool", ct: cts.Token))
            {
                if (progress.EventType == "tool_use")
                {
                    cancelled.Restart();
                    await cts.CancelAsync();
                }
            }
        });

        // Abort settles a real Pi well inside 5 s; past that the process is killed instead.
        Assert.True(cancelled.Elapsed < TimeSpan.FromSeconds(6.5), $"cancellation took {cancelled.Elapsed}");
        Assert.Equal("OK", Final(await rig.RunTurnAsync("next")));
        if (rig.Starts.Count > 1)
            Assert.Contains("--session", rig.Starts[1].ArgumentList);
    }

    [Fact]
    public async Task A13_ResetDeletesTheSession_ACrashResumesIt()
    {
        await using var rig = await RealRig.StartAsync(new JsonObject());

        Assert.Equal("OK", Final(await rig.RunTurnAsync("first")));
        var firstSession = rig.Executor.SessionFileForTests;
        Assert.NotNull(firstSession);
        Assert.True(File.Exists(firstSession), "Pi did not write its session file");

        await rig.Executor.StopProcessAsync();
        Assert.False(File.Exists(firstSession));
        Assert.Equal("OK", Final(await rig.RunTurnAsync("after reset")));
        Assert.DoesNotContain("--session", rig.Starts[1].ArgumentList);
        var secondSession = rig.Executor.SessionFileForTests;
        Assert.NotNull(secondSession);

        // A crash: the next turn resumes the same file.
        Process.GetProcessById(rig.Executor.ProcessIdForTests!.Value).Kill(entireProcessTree: true);
        await Task.Delay(500);
        Assert.Equal("OK", Final(await rig.RunTurnAsync("after crash")));
        var resumed = rig.Starts[2].ArgumentList.ToList();
        Assert.Equal(Path.GetFullPath(secondSession!), resumed[resumed.IndexOf("--session") + 1]);
        Assert.Contains(rig.Info, m => m.EndsWith("session resumed", StringComparison.Ordinal));
    }

    // ── A14b (c): the watchdog ──────────────────────────────────────────────

    [Fact]
    public async Task A14b_c_AHungServerCall_IsEndedByTheWatchdog()
    {
        await using var rig = await RealRig.StartAsync(
            new JsonObject { ["main"] = new JsonArray { new JsonObject { ["delay_ms"] = 15000, ["text"] = "late" } } },
            timings: new PiExecutorTimings { Watchdog = TimeSpan.FromSeconds(3) });

        var result = Assert.Single(await rig.RunTurnAsync("hang"), p => p.EventType == "result");

        Assert.True(result.IsErrorResult);
        Assert.Contains("local model stopped responding", result.FinalResult);
        Assert.Equal("OK", Final(await rig.RunTurnAsync("again")));
    }

    // ── A23 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A23_ThePiChildConnectsOnlyToTheStubs()
    {
        var strace = FindOnPath("strace");
        if (strace is null)
        {
            Assert.False(Environment.GetEnvironmentVariable("FLEET_PI_REALPI_STRICT") == "1",
                "A23 needs strace, and FLEET_PI_REALPI_STRICT=1 makes its absence a failure.");
            return; // Unmeasured on this machine; CI runs it strict.
        }

        using var traceDir = new PiTempDirectory("pi-strace");
        var trace = Path.Combine(traceDir.Path, "connect.trace");
        var wrapper = Path.Combine(traceDir.Path, "pi-traced");
        File.WriteAllText(wrapper,
            $"#!/bin/sh\nexec {strace} -f -qq -e trace=connect -o '{trace}' '{ResolvePi()}' \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using (var rig = await RealRig.StartAsync(new JsonObject
        {
            ["main"] = new JsonArray
            {
                Text("one"),
                ToolUse(McpTool, new JsonObject { ["key"] = "k" }), Text("two"),
                Text("three"),
            },
        }, piBinary: wrapper))
        {
            Assert.Equal("one", Final(await rig.RunTurnAsync("1")));
            Assert.Equal("two", Final(await rig.RunTurnAsync("2")));
            Assert.Equal("three", Final(await rig.RunTurnAsync("3")));

            var allowed = new[] { new Uri(rig.Origin).Port, new Uri(rig.McpUrl).Port };
            var connects = File.ReadAllLines(trace).Where(l => l.Contains("connect(", StringComparison.Ordinal)).ToList();
            var inet = connects.Where(l => l.Contains("AF_INET", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(inet);
            foreach (var line in inet)
            {
                Assert.Contains("127.0.0.1", line);
                Assert.Contains(allowed, port => line.Contains($"htons({port})", StringComparison.Ordinal));
            }
        }
    }

    // ── A32, A33 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A32_NoCredentialFileIsReachable_AndModelsJsonHoldsThePlaceholderKey()
    {
        const string find =
            "find / -xdev \\( -name auth.json -o -name .credentials.json -o -name oauth_creds.json -o -name mcp-auth.json \\) 2>/dev/null; echo find-done";
        await using var rig = await RealRig.StartAsync(new JsonObject
        {
            ["main"] = new JsonArray { ToolUse("bash", new JsonObject { ["command"] = find }), Text("searched") },
        });

        var progress = await rig.RunTurnAsync("look for credentials");

        var output = Assert.Single(progress, p => p.EventType == "tool_result").Summary;
        Assert.Equal("find-done", output.Trim());
        var models = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.AgentDir, "models.json")))!;
        Assert.Equal(ClaudeLocalModel.PlaceholderAuthToken, (string?)models["providers"]!["fleet-local"]!["apiKey"]);
    }

    [Fact]
    public async Task A33_BashEditsToTheAgentDirDoNotSurviveReset_AndNothingCallsTheServerWhileIdle()
    {
        var idle = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("FLEET_PI_A33_IDLE_SECONDS"), out var seconds) ? seconds : 120);
        await using var rig = await RealRig.StartAsync(r => new JsonObject
        {
            ["main"] = new JsonArray
            {
                ToolUse("bash", new JsonObject
                {
                    ["command"] =
                        $"mkdir -p '{r.AgentDir}/extensions/extra' && "
                        + $"printf '{{\"mcpServers\":{{\"extra\":{{\"type\":\"http\",\"url\":\"{r.McpUrl}\"}}}}}}' > '{r.AgentDir}/mcp.json' && "
                        + $"printf 'export default function (pi) {{ pi.registerTool({{ name: \"extra_tool\" }}) }}' > '{r.AgentDir}/extensions/extra/index.ts' && "
                        + $"printf '{{\"cacheWarming\":\"streaming\"}}' > '{r.AgentDir}/settings.json'",
                }),
                Text("tampered"),
                Text("fresh"),
            },
        });

        Assert.Equal("tampered", Final(await rig.RunTurnAsync("tamper")));
        await rig.Executor.StopProcessAsync();
        Assert.Equal("fresh", Final(await rig.RunTurnAsync("after reset")));

        rig.AssertRuntimeFiles();
        Assert.Equal(["read", "bash", "edit", "write", McpTool], ToolNames(rig.Requests()[^1]));

        var before = rig.Requests().Count;
        await Task.Delay(idle);
        Assert.Equal(before, rig.Requests().Count);
    }

    // ── Rig ─────────────────────────────────────────────────────────────────

    private static JsonObject Text(string text) => new() { ["text"] = text };

    private static JsonObject ToolUse(string name, JsonObject input) =>
        new() { ["tool_use"] = new JsonObject { ["name"] = name, ["input"] = input } };

    private static string Final(IEnumerable<AgentProgress> progress)
    {
        var result = Assert.Single(progress, p => p.EventType == "result");
        Assert.False(result.IsErrorResult, result.FinalResult);
        return result.FinalResult!;
    }

    private static List<string> ToolNames(JsonObject request) =>
        (request["body"]?["tools"] as JsonArray ?? []).Select(t => (string)t!["name"]!).ToList();

    private static string LastUserText(JsonObject request)
    {
        var messages = request["body"]?["messages"] as JsonArray ?? [];
        var user = messages.LastOrDefault(m => (string?)m?["role"] == "user");
        return user?["content"] switch
        {
            JsonValue value => (string?)value ?? "",
            JsonArray blocks => blocks.Where(b => (string?)b?["type"] == "text").Select(b => (string?)b!["text"]).LastOrDefault() ?? "",
            _ => "",
        };
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir, name))
        .FirstOrDefault(File.Exists);

    /// <summary>The pinned binary, or a hard failure naming what is missing.</summary>
    private static string ResolvePi()
    {
        var bin = PiBinary;
        var path = bin.Contains('/') ? (File.Exists(bin) ? bin : null) : FindOnPath(bin);
        return path ?? throw new InvalidOperationException(
            $"The pinned pi binary '{bin}' was not found (set FLEET_PI_BIN or put pi on PATH). This is a hard "
            + "failure by design: a skipped real-process row is an unmeasured row reported as green.");
    }

    private sealed class RealRig : IAsyncDisposable
    {
        private readonly PiTempDirectory _temp = new("pi-realpi");
        private readonly List<Process> _stubs = [];
        private readonly List<ProcessStartInfo> _starts = [];

        private RealRig()
        {
            WorkDir = _temp.Sub("work");
            AgentDir = Path.Combine(_temp.Path, "agent");
            RecordDir = Path.Combine(_temp.Path, "anthropic-records");
            McpRecordDir = Path.Combine(_temp.Path, "mcp-records");
        }

        public string WorkDir { get; }
        public string AgentDir { get; }
        public string RecordDir { get; }
        public string McpRecordDir { get; }
        public string Origin { get; private set; } = "";
        public string McpUrl { get; private set; } = "";
        public string McpConfigPath => Path.Combine(WorkDir, ".mcp.json");
        public PiExecutor Executor { get; private set; } = null!;
        public PiTestLogger Logger { get; } = new();

        public IReadOnlyList<ProcessStartInfo> Starts
        {
            get { lock (_starts) return _starts.ToList(); }
        }

        public IReadOnlyList<string> Info => Logger.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();
        public IReadOnlyList<string> Warnings => Logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

        public int ContextWindow { get; private set; }

        public static Task<RealRig> StartAsync(
            JsonObject script, int contextWindow = PiStandIn.ContextWindow, PiExecutorTimings? timings = null,
            string? piBinary = null) =>
            StartAsync(_ => script, contextWindow, timings, piBinary);

        /// <summary>The script is built once the directories and the MCP stub's URL are known.</summary>
        public static async Task<RealRig> StartAsync(
            Func<RealRig, JsonObject> script, int contextWindow = PiStandIn.ContextWindow,
            PiExecutorTimings? timings = null, string? piBinary = null)
        {
            var pi = piBinary ?? ResolvePi();
            if (!Directory.Exists(ExtensionDir))
                throw new InvalidOperationException(
                    $"The Fleet Pi extension directory '{ExtensionDir}' does not exist (set FLEET_PI_EXTENSION_DIR). "
                    + "This is a hard failure by design.");

            var rig = new RealRig { ContextWindow = contextWindow };
            try
            {
                var tools = Path.Combine(rig._temp.Path, "mcp-tools.json");
                File.WriteAllText(tools, new JsonArray
                {
                    new JsonObject { ["name"] = "memory_get", ["text"] = McpToolText },
                }.ToJsonString());
                rig.McpUrl = await rig.StartStubAsync("mcp.ts", "--record-dir", rig.McpRecordDir, "--tools", tools);
                File.WriteAllText(rig.McpConfigPath, new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        ["fleet-memory"] = new JsonObject
                        {
                            ["type"] = "http",
                            ["url"] = rig.McpUrl,
                            ["headers"] = new JsonObject { ["X-Fleet-Test"] = "realpi" },
                        },
                    },
                }.ToJsonString());

                var scriptPath = Path.Combine(rig._temp.Path, "script.json");
                File.WriteAllText(scriptPath, script(rig).ToJsonString());
                rig.Origin = await rig.StartStubAsync("anthropic.ts", "--record-dir", rig.RecordDir, "--script", scriptPath);

                var options = Options.Create(new AgentOptions
                {
                    Name = "agent-a",
                    Role = "test",
                    WorkDir = rig.WorkDir,
                    Provider = "pi",
                    Model = Tag,
                    PiBaseUrl = rig.Origin,
                    ContextWindow = contextWindow,
                    Effort = "",
                    AllowedTools = [McpTool],
                });
                rig.Executor = new PiExecutor(options, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), rig.Logger,
                    new PiExecutorSettings
                    {
                        AgentDir = rig.AgentDir,
                        ExtensionDir = ExtensionDir,
                        McpConfigPath = rig.McpConfigPath,
                        PiBinary = pi,
                        Timings = timings ?? new PiExecutorTimings(),
                        ProcessStarter = psi =>
                        {
                            lock (rig._starts)
                                rig._starts.Add(psi);
                            return OsPiProcess.Start(psi);
                        },
                    });
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        private async Task<string> StartStubAsync(string stub, params string[] args)
        {
            var path = Path.Combine(ExtensionDir, "test", "stubs", stub);
            if (!File.Exists(path))
                throw new InvalidOperationException($"The extension stub '{path}' does not exist. This is a hard failure by design.");

            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(path);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            var process = Process.Start(psi) ?? throw new InvalidOperationException($"node did not start {stub}");
            _stubs.Add(process);
            _ = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var first = await process.StandardOutput.ReadLineAsync(cts.Token);
            _ = process.StandardOutput.ReadToEndAsync();
            if (first is null || !first.StartsWith("LISTENING ", StringComparison.Ordinal))
                throw new InvalidOperationException($"{stub} did not print 'LISTENING <url>' first (got '{first}').");
            return first["LISTENING ".Length..].Trim();
        }

        public async Task<List<AgentProgress>> RunTurnAsync(string task)
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var progress = new List<AgentProgress>();
            await foreach (var item in Executor.ExecuteAsync(task, ct: guard.Token))
                progress.Add(item);
            return progress;
        }

        public List<JsonObject> Requests() => ReadRecords(RecordDir);

        public List<JsonObject> McpRequests() => ReadRecords(McpRecordDir);

        private static List<JsonObject> ReadRecords(string dir) =>
            Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal)
                    .Select(f => JsonNode.Parse(File.ReadAllText(f))!.AsObject()).ToList()
                : [];

        public void AssertRuntimeFiles()
        {
            string Read(string name) => File.ReadAllText(Path.Combine(AgentDir, name));
            Assert.Equal(PiRuntimeFiles.BuildModelsJson(Origin, Tag, ContextWindow), Read("models.json"));
            Assert.Equal(PiRuntimeFiles.BuildSettingsJson(), Read("settings.json"));
            Assert.Equal(PiRuntimeFiles.BuildExtensionConfigJson([McpTool], McpConfigPath), Read("fleet-pi.json"));
        }

        public async ValueTask DisposeAsync()
        {
            if (Executor is not null)
                await Executor.DisposeAsync();
            foreach (var stub in _stubs)
            {
                try
                {
                    stub.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                stub.Dispose();
            }
            _temp.Dispose();
        }
    }
}
