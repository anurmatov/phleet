using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Services.HostedProviders;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Tests;

/// <summary>
/// A Codex agent's thread is saved and named in the session file, and the next app-server resumes
/// it while the instructions and the model provider are the same. These tests drive the real
/// startup against the stand-in app-server: the frames the executor wrote, in order, and the
/// session file it left are what is asserted. No codex is started.
/// </summary>
public partial class CodexExecutorTests
{
    // ── the start rule ───────────────────────────────────────────────────────

    [Fact]
    public async Task Session_ANewThread_IsSaved_AndTheFileRecordsItsIdAndTheInstructionsHash()
    {
        using var workspace = new TempWorkspace();
        using var capture = new ThreadStartCapture();
        var logger = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start, logger: logger);

        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));

        Assert.Equal("new", executor.SessionOrigin);
        Assert.Contains(logger.Messages, m => m == "Codex session new: no file (thread thread-1)");
        Assert.DoesNotContain(logger.Messages, m => m.Contains("unexpected persistence state", StringComparison.Ordinal));

        var frames = capture.ReadFrames();
        Assert.Equal(["initialize", "initialized", "thread/start"], frames.Select(f => f.Method));
        var start = frames[2].Params!;
        Assert.False((bool?)start["ephemeral"]);

        var file = ReadSessionFile(workspace);
        Assert.Equal("codex", (string?)file["provider"]);
        Assert.Equal("thread-1", (string?)file["sessionId"]);
        Assert.Equal("cloud", (string?)file["runsOn"]);
        // The hash is of the baseInstructions string codex was given.
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((string)start["baseInstructions"]!))).ToLowerInvariant(),
            (string?)file["instructionsSha256"]);
        Assert.Equal(["session.json"], Directory.GetFiles(Path.Combine(workspace.Path, ".fleet")).Select(Path.GetFileName));
    }

    /// <summary>A thread codex did not save cannot be resumed, so it is reported and not recorded.</summary>
    [Fact]
    public async Task Session_AThreadCodexDidNotSave_IsWarnedAbout_AndNotRecorded()
    {
        using var workspace = new TempWorkspace();
        using var capture = new ThreadStartCapture();
        var logger = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start, logger: logger);

        await StartAsync(executor, Ok(), Ok(new JsonObject
        {
            ["thread"] = new JsonObject { ["id"] = "thread-1", ["ephemeral"] = true },
        }));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("unexpected persistence state (ephemeral=True", StringComparison.Ordinal));
        Assert.False(File.Exists(SessionFilePath(workspace)));
    }

    [Fact]
    public async Task Session_TheThreadOneContainerStarted_IsResumedByTheNext()
    {
        using var workspace = new TempWorkspace();
        await StartFirstContainerAsync(workspace);
        var before = File.ReadAllText(SessionFilePath(workspace));

        using var capture = new ThreadStartCapture();
        var logger = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start, logger: logger);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));

        Assert.Equal("resumed", executor.SessionOrigin);
        Assert.Equal("thread-1", executor.LastSessionId);
        Assert.Contains(logger.Messages, m => m == "Codex session resumed: thread-1");
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("Codex session new", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(SessionFilePath(workspace)));

        var frames = capture.ReadFrames();
        Assert.Equal(["initialize", "initialized", "thread/resume"], frames.Select(f => f.Method));
        Assert.Equal("thread-1", (string?)frames[2].Params!["threadId"]);
    }

    [Theory]
    [InlineData("other provider", false)]
    [InlineData("instructions changed", true)]
    [InlineData("unreadable file", false)]
    [InlineData("model runs elsewhere", true)]
    public async Task Session_AnythingElseInTheFile_IsANewThread_AndTheThreadItReplacesIsDeleted(string reason, bool deletesOld)
    {
        using var workspace = new TempWorkspace();
        await StartFirstContainerAsync(workspace);
        var hash = (string)ReadSessionFile(workspace)["instructionsSha256"]!;
        switch (reason)
        {
            case "other provider": SeedSessionFile(workspace, "session-old", hash, provider: "claude"); break;
            case "instructions changed": SeedSessionFile(workspace, "thread-old", new string('0', 64)); break;
            case "unreadable file": File.WriteAllText(SessionFilePath(workspace), "{ this is not a session"); break;
            case "model runs elsewhere": SeedSessionFile(workspace, "thread-old", hash, runsOn: "ollama"); break;
        }

        using var capture = new ThreadStartCapture();
        var logger = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start, logger: logger);
        var answers = new List<Rpc> { Ok(), Ok(ThreadAnswer("thread-2")) };
        if (deletesOld) answers.Add(Ok());
        await StartAsync(executor, [.. answers]);

        Assert.Equal("new", executor.SessionOrigin);
        Assert.Contains(logger.Messages, m => m == $"Codex session new: {reason} (thread thread-2)");

        var frames = capture.ReadFrames();
        Assert.Equal(
            deletesOld
                ? ["initialize", "initialized", "thread/start", "thread/delete"]
                : new[] { "initialize", "initialized", "thread/start" },
            frames.Select(f => f.Method));
        if (deletesOld)
            Assert.Equal("""{"threadId":"thread-old"}""", frames[3].Params!.ToJsonString());

        var file = ReadSessionFile(workspace);
        Assert.Equal("codex", (string?)file["provider"]);
        Assert.Equal("thread-2", (string?)file["sessionId"]);
        Assert.Equal(hash, (string?)file["instructionsSha256"]);
        Assert.Equal("cloud", (string?)file["runsOn"]);
    }

    // ── what a resume carries ────────────────────────────────────────────────

    public enum Mode { Cloud, Local, Hosted }

    /// <summary>
    /// A thread is resumed in a new app-server, which knows nothing the first one was told. So the
    /// resume carries everything the start carries; the two differ only in the fields the protocol
    /// defines for one of them.
    /// </summary>
    [Theory]
    [InlineData(Mode.Cloud)]
    [InlineData(Mode.Local)]
    [InlineData(Mode.Hosted)]
    public async Task Session_ResumeParams_EqualStartParams_ApartFromTheFieldsOnlyOneOfThemHas(Mode mode)
    {
        using var workspace = new TempWorkspace();
        await using var adapter = mode == Mode.Hosted ? await StartAdapterHostAsync() : null;
        var echoed = mode switch { Mode.Hosted => "phleet_zai", Mode.Local => "ollama", _ => null };

        CodexExecutor NewExecutor(ThreadStartCapture capture) => mode switch
        {
            Mode.Local => CreateExecutor(
                model: "ollama/qwen3.8:27b", workDir: workspace.Path, environmentReader: _ => null,
                processStarter: capture.Start, codexOssBaseUrl: ConfigOssBaseUrl, contextWindow: 32768),
            Mode.Hosted => CreateExecutor(
                model: "zai/glm-5.3", workDir: workspace.Path, environmentReader: _ => null,
                processStarter: capture.Start, adapterHost: adapter),
            _ => CreateExecutor(
                model: "gpt-5", workDir: workspace.Path, environmentReader: _ => null,
                processStarter: capture.Start),
        };

        using var startCapture = new ThreadStartCapture();
        await StartAsync(NewExecutor(startCapture), Ok(), Ok(ThreadAnswer("thread-1", echoed)));
        var start = startCapture.ReadParams("thread/start");

        using var resumeCapture = new ThreadStartCapture();
        await StartAsync(NewExecutor(resumeCapture), Ok(), Ok(ThreadAnswer("thread-1", echoed)));
        var resume = resumeCapture.ReadParams("thread/resume");

        Assert.Equal("thread-1", (string?)resume["threadId"]);
        Assert.True((bool?)resume["excludeTurns"]);
        Assert.False((bool?)start["ephemeral"]);
        Assert.Equal("phleet", (string?)start["serviceName"]);

        // Every field of the pinned ThreadResumeParams the start also sends, and nothing else.
        Assert.Equal(
            start.Select(kv => kv.Key).Except(["serviceName", "ephemeral"]),
            resume.Select(kv => kv.Key).Except(["threadId", "excludeTurns"]));
        foreach (var key in new[] { "model", "modelProvider", "config", "cwd", "approvalPolicy", "sandbox", "baseInstructions" })
            Assert.Equal(start[key]?.ToJsonString(), resume[key]?.ToJsonString());

        Assert.NotNull(resume["model"]);
        Assert.NotNull(resume["config"]);
        Assert.NotNull(resume["baseInstructions"]);
        Assert.Equal(mode != Mode.Cloud, resume.ContainsKey("modelProvider"));
        if (mode == Mode.Local)
            Assert.Equal(32768, (int?)resume["config"]!["model_context_window"]);
    }

    /// <summary>
    /// The hosted provider's loopback address and token are made anew by every agent start. A
    /// resumed thread is given this start's, not the ones it was created with.
    /// </summary>
    [Fact]
    public async Task Session_AResumedHostedThread_IsGivenThisStartsForwarderAddressAndToken()
    {
        using var workspace = new TempWorkspace();
        await using (var firstAdapter = await StartAdapterHostAsync())
        {
            using var firstCapture = new ThreadStartCapture();
            var first = CreateExecutor(model: "zai/glm-5.3", workDir: workspace.Path, environmentReader: _ => null,
                processStarter: firstCapture.Start, adapterHost: firstAdapter);
            await StartAsync(first, Ok(), Ok(ThreadAnswer("thread-1", "phleet_zai")));
        }

        await using var adapter = await StartAdapterHostAsync();
        var endpoint = await adapter.Endpoint;
        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(model: "zai/glm-5.3", workDir: workspace.Path, environmentReader: _ => null,
            processStarter: capture.Start, adapterHost: adapter);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1", "phleet_zai")));

        var resume = capture.ReadParams("thread/resume");
        Assert.Equal("phleet_zai", (string?)resume["modelProvider"]);
        Assert.Equal($"http://127.0.0.1:{endpoint.BaseAddress.Port}/zai",
            (string?)resume["config"]!["model_providers.phleet_zai.base_url"]);
        Assert.Equal(endpoint.Token,
            (string?)resume["config"]!["model_providers.phleet_zai.http_headers"]![HostedProviderForwarder.TokenHeader]);
        Assert.Equal("resumed", executor.SessionOrigin);
    }

    /// <summary>
    /// The echo check of thread/start holds on resume: a hosted thread that came back on another
    /// provider is not used. It counts as a failed resume, and the new thread has its own check.
    /// </summary>
    [Fact]
    public async Task Session_AResumedHostedThreadOnAnotherProvider_IsNotUsed_AndANewThreadIsStarted()
    {
        using var workspace = new TempWorkspace();
        await using var adapter = await StartAdapterHostAsync();
        using (var firstCapture = new ThreadStartCapture())
        {
            var first = CreateExecutor(model: "zai/glm-5.3", workDir: workspace.Path, environmentReader: _ => null,
                processStarter: firstCapture.Start, adapterHost: adapter);
            await StartAsync(first, Ok(), Ok(ThreadAnswer("thread-1", "phleet_zai")));
        }

        using var capture = new ThreadStartCapture();
        var logger = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(model: "zai/glm-5.3", workDir: workspace.Path, environmentReader: _ => null,
            processStarter: capture.Start, adapterHost: adapter, logger: logger);
        await StartAsync(executor,
            Ok(), Ok(ThreadAnswer("thread-1", "openai")), Ok(ThreadAnswer("thread-2", "phleet_zai")), Ok());

        Assert.Equal("thread-2", executor.LastSessionId);
        Assert.Equal("new", executor.SessionOrigin);
        Assert.Contains(logger.Messages, m => m.StartsWith(
            "Codex session new: resume failed: the thread came back on model provider 'openai', not 'phleet_zai'",
            StringComparison.Ordinal));
        Assert.Equal(
            ["initialize", "initialized", "thread/resume", "thread/start", "thread/delete"],
            capture.ReadFrames().Select(f => f.Method));
        Assert.Equal("thread-2", (string?)ReadSessionFile(workspace)["sessionId"]);
    }

    // ── a resume that fails ──────────────────────────────────────────────────

    [Fact]
    public async Task Session_AFailedResume_StartsANewThreadInTheSameStart_AndIsNotTriedAgain()
    {
        using var workspace = new TempWorkspace();
        await StartFirstContainerAsync(workspace);

        using var capture = new ThreadStartCapture();
        var logger = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start, logger: logger);
        await StartAsync(executor, Ok(), Fail("no rollout found for thread id thread-1"), Ok(ThreadAnswer("thread-2")), Ok());

        Assert.Equal("thread-2", executor.LastSessionId);
        Assert.Equal("new", executor.SessionOrigin);
        Assert.Contains(logger.Messages, m => m ==
            "Codex session new: resume failed: thread/resume: no rollout found for thread id thread-1 (thread thread-2)");
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("Codex session resumed", StringComparison.Ordinal));

        var frames = capture.ReadFrames();
        Assert.Equal(
            ["initialize", "initialized", "thread/resume", "thread/start", "thread/delete"],
            frames.Select(f => f.Method));
        Assert.Equal("""{"threadId":"thread-1"}""", frames[4].Params!.ToJsonString());
        Assert.Equal("thread-2", (string?)ReadSessionFile(workspace)["sessionId"]);
    }

    /// <summary>
    /// When the app-server itself dies during the resume there is nothing left to start a thread
    /// in. The file is removed before the start is retried, so the retry begins a new thread and
    /// the resume is not asked for a second time.
    /// </summary>
    [Fact]
    public async Task Session_AnAppServerThatDiesDuringTheResume_IsNotAskedToResumeAgain()
    {
        using var workspace = new TempWorkspace();
        await StartFirstContainerAsync(workspace);

        using var capture = new ThreadStartCapture();
        var logger = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start, logger: logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var responder = Task.Run(async () =>
        {
            await executor.WaitAndCompleteNextPendingRequestForTests(new JsonObject(), cts.Token);
            // thread/resume is waiting for its answer: the app-server dies instead of giving one.
            await capture.WaitForMethodAsync("thread/resume", cts.Token);
            var resumeRequest = Assert.Single(executor.PendingRequestTasksForTests);
            capture.KillCurrent();
            // Not answered by this test: it ends when the executor notices the app-server is gone.
            await Task.WhenAny(resumeRequest).WaitAsync(cts.Token);
            // The retry: a new app-server, asked to initialize and to start a thread.
            await executor.WaitAndCompleteNextPendingRequestForTests(new JsonObject(), cts.Token);
            await executor.WaitAndCompleteNextPendingRequestForTests(ThreadAnswer("thread-2"), cts.Token);
        }, cts.Token);
        await executor.EnsureProcessReadyForTestsAsync(cts.Token);
        await responder;

        Assert.Equal("thread-2", executor.LastSessionId);
        Assert.Contains(logger.Messages, m => m.StartsWith(
            "Codex session new: resume failed: the app-server failed during thread/resume", StringComparison.Ordinal));
        Assert.Equal("thread-2", (string?)ReadSessionFile(workspace)["sessionId"]);
        Assert.Equal(
            ["initialize", "initialized", "thread/resume", "initialize", "initialized", "thread/start"],
            capture.ReadFrames().Select(f => f.Method));
    }

    /// <summary>An answer that names no thread is not a resumed thread, and is not asked for twice either.</summary>
    [Fact]
    public async Task Session_AResumeAnsweredWithoutAThread_IsAFailedResume()
    {
        using var workspace = new TempWorkspace();
        await StartFirstContainerAsync(workspace);

        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start);
        await StartAsync(executor, Ok(), Ok(new JsonObject()), Ok(ThreadAnswer("thread-2")), Ok());

        Assert.Equal("thread-2", executor.LastSessionId);
        Assert.Equal(
            ["initialize", "initialized", "thread/resume", "thread/start", "thread/delete"],
            capture.ReadFrames().Select(f => f.Method));
    }

    /// <summary>Deleting the replaced thread is best-effort: a refusal changes nothing else.</summary>
    [Fact]
    public async Task Session_ARefusedThreadDelete_DoesNotFailTheStart()
    {
        using var workspace = new TempWorkspace();
        await StartFirstContainerAsync(workspace);
        SeedSessionFile(workspace, "thread-old", new string('0', 64));

        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-2")), Fail("thread not found: thread-old"));

        Assert.Equal("thread-2", executor.LastSessionId);
        Assert.Equal("thread-2", (string?)ReadSessionFile(workspace)["sessionId"]);
        Assert.False(executor.RestartRequestedForTests);
    }

    // ── /reset, /stop, a credential restart, a lost thread ───────────────────

    [Fact]
    public async Task Session_StopProcess_RemovesTheFile_AndTheThreadItLeftIsDeletedByTheNextStart()
    {
        using var workspace = new TempWorkspace();
        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));
        Assert.True(File.Exists(SessionFilePath(workspace)));

        await executor.StopProcessAsync();

        Assert.False(File.Exists(SessionFilePath(workspace)));
        Assert.Null(executor.SessionOrigin);

        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-2")), Ok());
        Assert.Equal("new", executor.SessionOrigin);
        Assert.Equal(
            ["initialize", "initialized", "thread/start", "initialize", "initialized", "thread/start", "thread/delete"],
            capture.ReadFrames().Select(f => f.Method));
    }

    /// <summary>A token broadcast stops the app-server too, and the next one resumes the thread.</summary>
    [Fact]
    public async Task Session_ACredentialRestart_KeepsTheFile_AndTheNextStartResumes()
    {
        using var workspace = new TempWorkspace();
        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));

        Assert.True(await executor.TryStopProcessAsync());
        Assert.True(File.Exists(SessionFilePath(workspace)));

        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));
        Assert.Equal(
            ["initialize", "initialized", "thread/start", "initialize", "initialized", "thread/resume"],
            capture.ReadFrames().Select(f => f.Method));
    }

    /// <summary>
    /// codex saying mid-run that it does not know the thread already meant "restart with a fresh
    /// thread". With a session file that needs the file gone, or the restart would resume it.
    /// </summary>
    [Fact]
    public async Task Session_AThreadCodexNoLongerKnows_IsNotGoneBackTo()
    {
        using var workspace = new TempWorkspace();
        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var responder = executor.WaitAndFailNextPendingRequestForTests(
            new JsonObject { ["code"] = -32600L, ["message"] = "thread not found: thread-1" }, cts.Token);
        var events = new List<AgentProgress>();
        await foreach (var progress in executor.ExecuteAsync("hello", ct: cts.Token))
            events.Add(progress);
        await responder;

        Assert.True(Assert.Single(events).IsErrorResult);
        Assert.True(executor.RestartRequestedForTests);
        Assert.False(File.Exists(SessionFilePath(workspace)));
    }

    // ── the new-session line ─────────────────────────────────────────────────

    [Fact]
    public async Task Session_ANewThread_CarriesTheNewSessionLine_OnItsFirstTurnOnly()
    {
        using var workspace = new TempWorkspace();
        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));

        await RunTurnAsync(executor, "first task", "turn-1");
        await RunTurnAsync(executor, "second task", "turn-2");

        Assert.Equal(
            [PromptAssembler.WithNewSessionLine("first task"), "second task"],
            TurnTexts(capture));
    }

    [Fact]
    public async Task Session_AResumedThread_CarriesNothingExtra()
    {
        using var workspace = new TempWorkspace();
        await StartFirstContainerAsync(workspace);

        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));
        Assert.Equal("resumed", executor.SessionOrigin);

        await RunTurnAsync(executor, "first task", "turn-1");

        Assert.Equal(["first task"], TurnTexts(capture));
    }

    // ── rig ──────────────────────────────────────────────────────────────────

    /// <summary>One JSON-RPC answer the test gives the executor: a result, or an error.</summary>
    private sealed record Rpc(JsonObject? Result, JsonObject? Error);

    private static Rpc Ok(JsonObject? result = null) => new(result ?? new JsonObject(), null);

    private static Rpc Fail(string message) => new(null, new JsonObject { ["code"] = -32600L, ["message"] = message });

    /// <summary>What codex answers to thread/start and thread/resume for a saved thread.</summary>
    private static JsonObject ThreadAnswer(string threadId, string? modelProvider = null)
    {
        var answer = new JsonObject { ["thread"] = SavedThread(threadId) };
        if (modelProvider is not null)
            answer["modelProvider"] = modelProvider;
        return answer;
    }

    /// <summary>
    /// Starts the app-server and its thread, giving the executor's requests these answers in the
    /// order it makes them. One answer too many or too few fails the test instead of passing it:
    /// the responder and the start both have to finish.
    /// </summary>
    private static async Task StartAsync(CodexExecutor executor, params Rpc[] answers)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var responder = Task.Run(async () =>
        {
            foreach (var answer in answers)
            {
                if (answer.Error is null)
                    await executor.WaitAndCompleteNextPendingRequestForTests(answer.Result!, cts.Token);
                else
                    await executor.WaitAndFailNextPendingRequestForTests(answer.Error, cts.Token);
            }
        }, cts.Token);

        await executor.EnsureProcessReadyForTestsAsync(cts.Token);
        await responder;
        Assert.Empty(executor.PendingRequestTasksForTests);
    }

    /// <summary>A first container's life: a new thread <c>thread-1</c>, saved and recorded in the file.</summary>
    private static async Task StartFirstContainerAsync(TempWorkspace workspace)
    {
        using var capture = new ThreadStartCapture();
        var executor = CreateExecutor(workDir: workspace.Path, environmentReader: _ => null, processStarter: capture.Start);
        await StartAsync(executor, Ok(), Ok(ThreadAnswer("thread-1")));
        Assert.Equal("thread-1", (string?)ReadSessionFile(workspace)["sessionId"]);
    }

    /// <summary>One whole turn: turn/start answered, then the turn's completion delivered.</summary>
    private static async Task RunTurnAsync(CodexExecutor executor, string task, string turnId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var notifications = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(notifications);

        var responder = Task.Run(async () =>
        {
            await executor.WaitAndCompleteNextPendingRequestForTests(
                new JsonObject { ["turn"] = new JsonObject { ["id"] = turnId } }, cts.Token);
            await notifications.Writer.WriteAsync(new JsonObject
            {
                ["method"] = "turn/completed",
                ["params"] = new JsonObject { ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = "completed" } },
            }, cts.Token);
        }, cts.Token);

        await foreach (var _ in executor.ExecuteAsync(task, ct: cts.Token)) { }
        await responder;
    }

    /// <summary>The text of every turn/start the executor wrote, in order.</summary>
    private static List<string> TurnTexts(ThreadStartCapture capture) =>
        capture.ReadFrames()
            .Where(f => f.Method == "turn/start")
            .Select(f => (string)f.Params!["input"]!.AsArray().Single(i => (string?)i!["type"] == "text")!["text"]!)
            .ToList();

    private static string SessionFilePath(TempWorkspace workspace) =>
        Path.Combine(workspace.Path, ".fleet", "session.json");

    private static JsonObject ReadSessionFile(TempWorkspace workspace) =>
        JsonNode.Parse(File.ReadAllText(SessionFilePath(workspace)))!.AsObject();

    private static void SeedSessionFile(
        TempWorkspace workspace, string sessionId, string hash, string provider = "codex", string runsOn = "cloud") =>
        File.WriteAllText(SessionFilePath(workspace), new JsonObject
        {
            ["provider"] = provider,
            ["sessionId"] = sessionId,
            ["instructionsSha256"] = hash,
            ["runsOn"] = runsOn,
        }.ToJsonString());
}
