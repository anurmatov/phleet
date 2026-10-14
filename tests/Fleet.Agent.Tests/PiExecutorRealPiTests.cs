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
using Xunit.Abstractions;

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
public sealed class PiExecutorRealPiTests(ITestOutputHelper output)
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
        // W = 65536 compacts at 49152 tokens. The stub reports server usage, so the second answer's
        // usage (50000 input) crosses the threshold the way a real server's would. The summary
        // answer is delayed 3 s, so the turn after the crossing one meets a compaction in progress.
        await using var rig = await RealRig.StartAsync(
            new JsonObject
            {
                ["main"] = new JsonArray
                {
                    new JsonObject { ["text"] = "Turn one done.", ["usage"] = new JsonObject { ["input"] = 30000 } },
                    new JsonObject { ["text"] = "Turn two done.", ["usage"] = new JsonObject { ["input"] = 50000 } },
                },
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
        Assert.Contains(rig.Info, m => m.Contains("session resumed images off", StringComparison.Ordinal));
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

        // One trace file per invocation: the executor also runs `pi --version` through the wrapper.
        using var traceDir = new PiTempDirectory("pi-strace");
        var wrapper = Path.Combine(traceDir.Path, "pi-traced");
        File.WriteAllText(wrapper,
            $"#!/bin/sh\nexec {strace} -f -qq -e trace=connect -o '{traceDir.Path}/connect.'$$ '{ResolvePi()}' \"$@\"\n");
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
            var connects = Directory.GetFiles(traceDir.Path, "connect.*")
                .SelectMany(File.ReadAllLines)
                .Where(l => l.Contains("connect(", StringComparison.Ordinal))
                .ToList();
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

        // A32 as amended in PR #452 (pending human design approval): Pi 1.0.4 writes "{}" to
        // $PI_CODING_AGENT_DIR/auth.json on every start (auth-storage.ts:63-65) and has no flag to skip
        // it, so exactly that one file, holding exactly "{}", is the only match allowed. Anything
        // else, a second match or any other content, fails.
        var output = Assert.Single(progress, p => p.EventType == "tool_result").Summary;
        var authPath = Path.Combine(rig.AgentDir, PiRuntimeFiles.AuthFileName);
        Assert.Equal(new[] { authPath, "find-done" }, output.Trim().Split('\n', StringSplitOptions.TrimEntries));
        Assert.Equal(PiRuntimeFiles.EmptyAuthContent, File.ReadAllText(authPath));
        Assert.Null(PiRuntimeFiles.DescribeCredentialFileFault(rig.AgentDir));
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

    // ── #455: attachments through the real Pi (A8–A12) ──────────────────────

    private static MessageDocument Document(AttachmentFixtures fixtures, string name, string mime = "application/pdf")
    {
        var path = fixtures.PathOf(name);
        return new MessageDocument("file-id", mime, new FileInfo(path).Length, Path.GetFileName(path)) { FilePath = path };
    }

    private static MessageImage Photo(AttachmentFixtures fixtures, string name) =>
        new(fixtures.Bytes(name), "image/jpeg") { FilePath = fixtures.PathOf(name) };

    /// <summary>Every text in the request's user messages, tool results included.</summary>
    private static string UserText(JsonObject request) =>
        string.Join("\n", (request["body"]?["messages"] as JsonArray ?? [])
            .Where(m => (string?)m?["role"] == "user")
            .SelectMany(m => m!["content"] switch
            {
                JsonValue value => [(string?)value ?? ""],
                JsonArray blocks => blocks.SelectMany(TextsOf),
                _ => [],
            }));

    private static IEnumerable<string> TextsOf(JsonNode? block) =>
        (string?)block?["type"] switch
        {
            "text" => [(string?)block!["text"] ?? ""],
            "tool_result" => block!["content"] switch
            {
                JsonValue value => [(string?)value ?? ""],
                JsonArray inner => inner.SelectMany(TextsOf),
                _ => [],
            },
            _ => [],
        };

    /// <summary>The image blocks of the request's user messages (direct, or inside a tool result).</summary>
    private static List<(string MediaType, byte[] Bytes, bool InToolResult)> Images(JsonObject request)
    {
        var found = new List<(string, byte[], bool)>();
        void Visit(JsonNode? block, bool inTool)
        {
            switch ((string?)block?["type"])
            {
                case "image":
                    found.Add(((string)block!["source"]!["media_type"]!, Convert.FromBase64String((string)block["source"]!["data"]!), inTool));
                    break;
                case "tool_result" when block!["content"] is JsonArray inner:
                    foreach (var b in inner)
                        Visit(b, true);
                    break;
            }
        }
        foreach (var message in (request["body"]?["messages"] as JsonArray ?? []).Where(m => (string?)m?["role"] == "user"))
        {
            if (message!["content"] is JsonArray blocks)
                foreach (var block in blocks)
                    Visit(block, false);
        }
        return found;
    }

    /// <summary>The image blocks of the request's last user message only (not the history).</summary>
    private static List<(string MediaType, byte[] Bytes, bool InToolResult)> LastUserImages(JsonObject request)
    {
        var last = (request["body"]?["messages"] as JsonArray ?? []).Last(m => (string?)m?["role"] == "user")!;
        var copy = new JsonObject { ["body"] = new JsonObject { ["messages"] = new JsonArray { last.DeepClone() } } };
        return Images(copy);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    [Fact]
    public async Task A9_TheToolRoutes_ReadEveryBaselineType_WithImageInputOff()
    {
        using var fixtures = AttachmentFixtures.Generate();
        const string ooxml =
            "python3 -c 'import zipfile,re,sys; z=zipfile.ZipFile(sys.argv[1]); "
            + "print(\" \".join(re.sub(\"<[^>]+>\",\" \",z.read(n).decode()) for n in z.namelist() if n.endswith(\".xml\")))' ";
        var frame = Path.Combine(fixtures.Directory, "frame.png");
        await using var rig = await RealRig.StartAsync(new JsonObject
        {
            ["main"] = new JsonArray
            {
                ToolUse("read", new JsonObject { ["path"] = fixtures.PathOf("txt") }),
                ToolUse("read", new JsonObject { ["path"] = fixtures.PathOf("py") }),
                ToolUse("read", new JsonObject { ["path"] = fixtures.PathOf("csv") }),
                ToolUse("read", new JsonObject { ["path"] = fixtures.PathOf("json") }),
                ToolUse("bash", new JsonObject { ["command"] = ooxml + fixtures.PathOf("docx") }),
                ToolUse("bash", new JsonObject { ["command"] = ooxml + fixtures.PathOf("xlsx") }),
                ToolUse("bash", new JsonObject { ["command"] = ooxml + fixtures.PathOf("pptx") }),
                ToolUse("bash", new JsonObject
                {
                    ["command"] = $"ffmpeg -nostdin -v error -i '{fixtures.PathOf("mp4")}' -frames:v 1 '{frame}' && echo frame-ok",
                }),
                Text("read them all"),
            },
        });

        var progress = await rig.RunTurnAsync("read the attachments");

        var results = progress.Where(p => p.EventType == "tool_result").Select(p => p.Summary).ToList();
        Assert.Equal(8, results.Count);
        var names = new[] { "txt", "py", "csv", "json", "docx", "xlsx", "pptx" };
        for (var i = 0; i < names.Length; i++)
            Assert.Contains(fixtures.Code(names[i]), results[i]);
        Assert.Contains("frame-ok", results[7]);
        Assert.True(File.Exists(frame));
        Assert.Equal("read them all", Final(progress));
    }

    [Fact]
    public async Task A10_A8_ImagesAndPdfs_ReachTheServer_OnPromptSteerAndQueuedPaths()
    {
        using var fixtures = AttachmentFixtures.Generate();
        var leftoversBefore = Directory.GetDirectories(Path.GetTempPath(), "fleet-pi-*").Order().ToList();
        await using var rig = await RealRig.StartAsync(new JsonObject
        {
            ["main"] = new JsonArray { ToolUse("bash", new JsonObject { ["command"] = "sleep 2" }), Text("steered") },
        }, imageInput: true);
        var png = Photo(fixtures, "png");
        var jpeg = Photo(fixtures, "jpeg");
        var text = Document(fixtures, "pdf-text");
        var commands = 0;

        // Steer: two images and the text PDF while a tool runs.
        MidTurnInjectionResult? steer = null;
        await foreach (var progress in rig.Executor.ExecuteAsync("start work"))
        {
            if (progress.EventType == "tool_use")
            {
                steer = await rig.Executor.TryInjectMessageAsync("look at these", [png, jpeg], [text]);
                commands++;
            }
        }
        Assert.Equal(MidTurnInjectionStatus.Injected, steer!.Status);
        var steered = rig.Requests()[^1];
        Assert.Contains(fixtures.Code("pdf-text"), UserText(steered));
        var steeredImages = Images(steered).Where(i => !i.InToolResult).ToList();
        Assert.Contains(steeredImages, i => i.MediaType == "image/png" && Sha(i.Bytes) == Sha(png.Bytes));
        Assert.Contains(steeredImages, i => i.MediaType == "image/jpeg" && Sha(i.Bytes) == Sha(jpeg.Bytes));

        // Prompt: the same, then measure the ack-inclusive time to the answer.
        var clock = Stopwatch.StartNew();
        Assert.Equal("OK", Final(await rig.RunTurnAsync("look", [png, jpeg], [text])));
        commands++;
        output.WriteLine($"A10d prompt with 2 images + 1 PDF answered in {clock.ElapsedMilliseconds} ms (stub server)");
        var prompted = rig.Requests()[^1];
        Assert.Contains(fixtures.Code("pdf-text"), UserText(prompted));
        Assert.Equal([Sha(png.Bytes), Sha(jpeg.Bytes)], Images(prompted).Where(i => !i.InToolResult).TakeLast(2).Select(i => Sha(i.Bytes)));

        // A 2560 px photo arrives as Fleet's own downscale output, byte for byte.
        var photo = Photo(fixtures, "photo-2560");
        var tools = new PiLocalTools();
        var expected = (await new PiAttachments(tools, new PiPdfExtractor(tools, rig.Logger), rig.Logger)
            .PrepareImagesAsync([photo], new PiProcessingBudget(TimeSpan.FromSeconds(60)), CancellationToken.None)).Blocks.Single();
        Assert.Equal("OK", Final(await rig.RunTurnAsync("a phone photo", [photo])));
        commands++;
        var downscaled = Images(rig.Requests()[^1]).Last(i => !i.InToolResult);
        Assert.Equal(Sha(expected.Bytes), Sha(downscaled.Bytes));
        Assert.Equal((2000, 1500), PiAttachments.ReadDimensions(downscaled.Bytes, downscaled.MediaType));

        // Queued: a merged batch is one prompt.
        Assert.Equal("OK", Final(await rig.RunTurnAsync("two queued parts", null, [text, Document(fixtures, "pdf-owner-only")])));
        commands++;
        Assert.Contains(fixtures.Code("pdf-owner-only"), UserText(rig.Requests()[^1]));

        // (b) A scanned PDF: one JPEG page per free slot.
        Assert.Equal("OK", Final(await rig.RunTurnAsync("a scan", null, [Document(fixtures, "pdf-scanned")])));
        commands++;
        var pages = LastUserImages(rig.Requests()[^1]).Where(i => !i.InToolResult && i.MediaType == "image/jpeg").ToList();
        Assert.InRange(pages.Count, 1, 2);

        // (e) The named failures, and the owner-only PDF extracted.
        Assert.Equal("OK", Final(await rig.RunTurnAsync("broken ones", null,
            [Document(fixtures, "pdf-user-password"), Document(fixtures, "pdf-truncated"), Document(fixtures, "pdf-not-pdf")])));
        commands++;
        var failures = UserText(rig.Requests()[^1]);
        Assert.Contains("could not be read: password-protected]", failures);
        Assert.Contains("could not be read: damaged]", failures);
        Assert.Contains("not-a.pdf: not a valid PDF]", failures);

        // A8: counts only, one line per command, nothing left in the temporary directory.
        var codes = new[] { "png", "jpeg", "pdf-text", "pdf-owner-only", "photo-2560" }
            .Select(n => fixtures.Code(n)).ToList();
        var secrets = codes.Concat([Sha(png.Bytes), Sha(jpeg.Bytes), Convert.ToBase64String(png.Bytes)[..40], "data:image",
            "text.pdf", "owner-only.pdf", fixtures.Directory, "Synthetic fixture"]).ToList();
        foreach (var entry in rig.Logger.Entries)
            Assert.DoesNotContain(secrets, secret => entry.Message.Contains(secret, StringComparison.Ordinal));
        Assert.Equal(commands, rig.Logger.Entries.Count(e => e.Message.StartsWith("pi attachments:", StringComparison.Ordinal)));
        Assert.Equal(leftoversBefore, Directory.GetDirectories(Path.GetTempPath(), "fleet-pi-*").Order().ToList());
    }

    [Fact]
    public async Task A10_ImageInputOff_ExtractsPdfText_ButSendsNoImage()
    {
        using var fixtures = AttachmentFixtures.Generate();
        await using var rig = await RealRig.StartAsync(new JsonObject());

        Assert.Equal("OK", Final(await rig.RunTurnAsync("documents and a photo", [Photo(fixtures, "png")],
            [Document(fixtures, "pdf-text"), Document(fixtures, "pdf-scanned"), Document(fixtures, "pdf-copy-restricted")])));

        var request = rig.Requests()[^1];
        var text = UserText(request);
        Assert.Contains(fixtures.Code("pdf-text"), text);
        Assert.Contains("[PDF scanned.pdf has no text layer and this model is text-only: not readable]", text);
        Assert.Contains("[PDF copy-restricted.pdf text copying restricted and this model is text-only: not readable]", text);
        Assert.DoesNotContain(fixtures.Code("pdf-copy-restricted"), text);
        Assert.Contains("[1 image attachment(s) omitted: this local model is text-only]", text);
        Assert.Empty(Images(request));
        rig.AssertRuntimeFiles();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A11_ReadingAnImageFile_ReturnsAnImageBlockOnlyWithImageInput(bool imageInput)
    {
        using var fixtures = AttachmentFixtures.Generate();
        var frame = Path.Combine(fixtures.Directory, "frame-a11.png");
        await using var rig = await RealRig.StartAsync(new JsonObject
        {
            ["main"] = new JsonArray
            {
                ToolUse("read", new JsonObject { ["path"] = fixtures.PathOf("png") }),
                ToolUse("bash", new JsonObject
                {
                    ["command"] = $"ffmpeg -nostdin -v error -i '{fixtures.PathOf("mp4")}' -frames:v 1 '{frame}' && echo ok",
                }),
                ToolUse("read", new JsonObject { ["path"] = frame }),
                Text("seen"),
            },
        }, imageInput: imageInput);

        Assert.Equal("seen", Final(await rig.RunTurnAsync("open the image and a video frame")));

        var last = rig.Requests()[^1];
        var toolImages = Images(last).Where(i => i.InToolResult).ToList();
        if (imageInput)
        {
            Assert.Equal(2, toolImages.Count);
        }
        else
        {
            Assert.Empty(toolImages);
            Assert.Contains("(tool image omitted: model does not support images)", UserText(last));
        }
        rig.AssertRuntimeFiles();
    }

    [Fact]
    public async Task A12_ASessionHoldingAnImageAndPdfText_ResumesAfterAKill_CompactsAndResets()
    {
        using var fixtures = AttachmentFixtures.Generate();
        await using var rig = await RealRig.StartAsync(
            new JsonObject
            {
                ["main"] = new JsonArray(
                    new JsonNode[] { Text("saw it"), Text("resumed") }
                        .Concat(Enumerable.Range(0, 10).Select(_ =>
                            (JsonNode)new JsonObject { ["text"] = "big turn", ["usage"] = new JsonObject { ["input"] = 50000 } }))
                        .ToArray()),
                ["summary"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["text"] = "Goal: synthetic. Constraints: none. Progress: an image and a PDF were read. "
                                   + "Key decisions: none. Next steps: continue. Critical context: anchor-455.",
                    },
                },
            },
            contextWindow: PiLocalModel.MinContextWindow, imageInput: true);

        Assert.Equal("saw it", Final(await rig.RunTurnAsync("look", [Photo(fixtures, "png")], [Document(fixtures, "pdf-text")])));
        var session = rig.Executor.SessionFileForTests!;
        Assert.Contains(fixtures.Code("pdf-text"), File.ReadAllText(session));

        Process.GetProcessById(rig.Executor.ProcessIdForTests!.Value).Kill(entireProcessTree: true);
        await Task.Delay(500);
        Assert.Equal("resumed", Final(await rig.RunTurnAsync("still there?")));
        Assert.Contains("--session", rig.Starts[^1].ArgumentList);
        Assert.Contains(Images(rig.Requests()[^1]), i => i.MediaType == "image/png");

        // 50000-token answers cross 65536 − 16384 once the history holds more than the kept tail
        // (as in A28); the next prompt waits for the compaction's settle.
        var filler = string.Join(' ', Enumerable.Repeat("synthetic filler sentence for compaction growth.", 1200));
        static bool Compacted(RealRig r) => r.Logger.Entries.Any(e =>
            e.Message.StartsWith("pi compaction threshold", StringComparison.Ordinal)
            || e.Message.Contains("fallback", StringComparison.Ordinal));
        for (var turn = 1; turn <= 8 && !Compacted(rig); turn++)
            Assert.Equal("big turn", Final(await rig.RunTurnAsync($"grow {turn}: {filler}")));
        var after = Assert.Single(await rig.RunTurnAsync("after compaction"), p => p.EventType == "result");
        Assert.False(after.IsErrorResult, after.FinalResult);
        Assert.True(Compacted(rig), "no threshold compaction or named fallback was logged");

        await rig.Executor.StopProcessAsync();
        Assert.False(File.Exists(session));
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

        public bool ImageInput { get; private set; }

        public static Task<RealRig> StartAsync(
            JsonObject script, int contextWindow = PiStandIn.ContextWindow, PiExecutorTimings? timings = null,
            string? piBinary = null, bool imageInput = false) =>
            StartAsync(_ => script, contextWindow, timings, piBinary, imageInput);

        /// <summary>The script is built once the directories and the MCP stub's URL are known.</summary>
        public static async Task<RealRig> StartAsync(
            Func<RealRig, JsonObject> script, int contextWindow = PiStandIn.ContextWindow,
            PiExecutorTimings? timings = null, string? piBinary = null, bool imageInput = false)
        {
            var pi = piBinary ?? ResolvePi();
            if (!Directory.Exists(ExtensionDir))
                throw new InvalidOperationException(
                    $"The Fleet Pi extension directory '{ExtensionDir}' does not exist (set FLEET_PI_EXTENSION_DIR). "
                    + "This is a hard failure by design.");

            var rig = new RealRig { ContextWindow = contextWindow, ImageInput = imageInput };
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
                    PiImageInput = imageInput,
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

        public async Task<List<AgentProgress>> RunTurnAsync(
            string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null)
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var progress = new List<AgentProgress>();
            await foreach (var item in Executor.ExecuteAsync(task, images, documents, guard.Token))
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
            Assert.Equal(PiRuntimeFiles.BuildModelsJson(Origin, Tag, ContextWindow, imageInput: ImageInput), Read("models.json"));
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
