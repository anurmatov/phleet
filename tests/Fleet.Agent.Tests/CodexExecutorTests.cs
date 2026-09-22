using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public class CodexExecutorTests
{
    private static CodexExecutor CreateExecutor(
        string attachmentDir = "/workspace/attachments",
        Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?>? processStarter = null,
        ILogger<CodexExecutor>? logger = null,
        string? model = null,
        string workDir = "/workspace",
        Func<string, string?>? environmentReader = null)
    {
        var agentOptions = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = workDir,
        });
        if (model is not null)
            agentOptions.Value.Model = model;
        var telegramOptions = Options.Create(new TelegramOptions
        {
            AttachmentDir = attachmentDir,
        });
        var promptBuilder = new PromptBuilder(agentOptions, NullLogger<PromptBuilder>.Instance);
        var resolvedLogger = logger ?? NullLogger<CodexExecutor>.Instance;
        return processStarter is null && environmentReader is null
            ? new CodexExecutor(agentOptions, telegramOptions, promptBuilder, resolvedLogger)
            : new CodexExecutor(
                agentOptions, telegramOptions, promptBuilder, resolvedLogger,
                processStarter ?? System.Diagnostics.Process.Start, environmentReader);
    }

    /// <summary>Captures log messages for assertion in tests.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task EnsureProcessReady_StartFailureExhaustsRetryBudget()
    {
        var attempts = 0;
        var executor = CreateExecutor(processStarter: _ =>
        {
            attempts++;
            return null;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.EnsureProcessReadyForTestsAsync());

        Assert.Contains("after 3 attempts", ex.Message);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task StreamTurn_IgnoresStaleTurnNotifications()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-new");

        await channel.Writer.WriteAsync(TurnCompleted("turn-old", "stale"));
        await channel.Writer.WriteAsync(TurnStarted("turn-new"));
        await channel.Writer.WriteAsync(TurnCompleted("turn-new", "fresh"));
        channel.Writer.TryComplete();

        var progress = await CollectAsync(executor.StreamTurnForTests("turn-new"));

        Assert.Equal(2, progress.Count);
        Assert.Equal("system", progress[0].EventType);
        Assert.Equal("result", progress[1].EventType);
        Assert.Equal("fresh", progress[1].FinalResult);
        Assert.Null(executor.ActiveTurnIdForTests);
    }

    [Fact]
    public async Task StreamTurn_ProcessCrashMidTurn_ReturnsErrorAndClearsActiveTurn()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-1");
        channel.Writer.TryComplete();

        var progress = await CollectAsync(executor.StreamTurnForTests("turn-1"));

        var final = Assert.Single(progress);
        Assert.True(final.IsErrorResult);
        Assert.Contains("exited unexpectedly", final.FinalResult);
        Assert.Null(executor.ActiveTurnIdForTests);
    }

    [Fact]
    public async Task StreamTurn_Cancelled_DrainsInterruptedTurnAndClearsActiveTurn()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-1");

        await channel.Writer.WriteAsync(TokenUsage("turn-1", 12, 7));
        await channel.Writer.WriteAsync(TurnCompleted("turn-1", "", "interrupted"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in executor.StreamTurnForTests("turn-1", cts.Token))
            {
            }
        });

        Assert.Null(executor.ActiveTurnIdForTests);
        Assert.False(channel.Reader.TryRead(out _));
    }

    private static async Task<List<AgentProgress>> CollectAsync(IAsyncEnumerable<AgentProgress> progressStream)
    {
        var progress = new List<AgentProgress>();
        await foreach (var item in progressStream)
            progress.Add(item);
        return progress;
    }

    private static JsonObject TurnStarted(string turnId) =>
        new()
        {
            ["method"] = "turn/started",
            ["params"] = new JsonObject
            {
                ["threadId"] = "thread-1",
                ["turn"] = new JsonObject
                {
                    ["id"] = turnId,
                },
            },
        };

    private static JsonObject TurnCompleted(string turnId, string assistantText, string status = "completed") =>
        new()
        {
            ["method"] = "turn/completed",
            ["params"] = new JsonObject
            {
                ["threadId"] = "thread-1",
                ["turn"] = new JsonObject
                {
                    ["id"] = turnId,
                    ["status"] = status,
                    ["durationMs"] = 1,
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "agentMessage",
                            ["text"] = assistantText,
                        },
                    },
                },
            },
        };

    // Real-wire protocol: text arrives via item/completed(agentMessage) before turn/completed.
    // turn.items in turn/completed is always empty on the live codex app-server.
    [Fact]
    public async Task StreamTurn_AgentMessageViaItemCompleted_UsesAccumulatedText()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-1");

        await channel.Writer.WriteAsync(TurnStarted("turn-1"));
        await channel.Writer.WriteAsync(ItemCompleted("turn-1", "agentMessage", "hello from codex"));
        // turn/completed with no items — matches real wire protocol
        await channel.Writer.WriteAsync(TurnCompletedNoItems("turn-1"));
        channel.Writer.TryComplete();

        var progress = await CollectAsync(executor.StreamTurnForTests("turn-1"));

        var final = progress.Last(p => p.FinalResult is not null);
        Assert.Equal("hello from codex", final.FinalResult);
        Assert.False(final.IsErrorResult);
    }

    // Fallback: when no item/completed(agentMessage) was received, use turn.items.
    // This matches the test fixture shape and guards against future protocol changes.
    [Fact]
    public async Task StreamTurn_AgentMessageInTurnItems_FallsBackToExtractAssistantText()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-1");

        // TurnCompleted puts agentMessage in turn.items — the fallback path
        await channel.Writer.WriteAsync(TurnCompleted("turn-1", "fallback text"));
        channel.Writer.TryComplete();

        var progress = await CollectAsync(executor.StreamTurnForTests("turn-1"));

        var final = progress.Single(p => p.FinalResult is not null);
        Assert.Equal("fallback text", final.FinalResult);
        Assert.False(final.IsErrorResult);
    }

    private static JsonObject TokenUsage(string turnId, int inputTokens, int outputTokens) =>
        new()
        {
            ["method"] = "thread/tokenUsage/updated",
            ["params"] = new JsonObject
            {
                ["turnId"] = turnId,
                ["tokenUsage"] = new JsonObject
                {
                    ["last"] = new JsonObject
                    {
                        ["inputTokens"] = inputTokens,
                        ["outputTokens"] = outputTokens,
                    },
                },
            },
        };

    // Matches the real wire protocol: item/completed with the given item type and text.
    private static JsonObject ItemCompleted(string turnId, string itemType, string text) =>
        new()
        {
            ["method"] = "item/completed",
            ["params"] = new JsonObject
            {
                ["turnId"] = turnId,
                ["item"] = new JsonObject
                {
                    ["type"] = itemType,
                    ["text"] = text,
                },
            },
        };

    // turn/completed with no items array — matches the real wire protocol where the
    // assistant text arrives via item/completed(agentMessage) before turn/completed.
    private static JsonObject TurnCompletedNoItems(string turnId, string status = "completed") =>
        new()
        {
            ["method"] = "turn/completed",
            ["params"] = new JsonObject
            {
                ["threadId"] = "thread-1",
                ["turn"] = new JsonObject
                {
                    ["id"] = turnId,
                    ["status"] = status,
                    ["durationMs"] = 1,
                    // Intentionally no "items" key — production codex app-server omits it.
                },
            },
        };

    // Concurrent ExecuteAsync calls must queue rather than throw. We hold _turnLock externally,
    // start an ExecuteAsync iteration in the background (which blocks waiting for the lock),
    // then release — verifying the second caller got the lock and proceeded (failing on process
    // startup as expected, but NOT throwing the old "refused to start a second turn" error).
    [Fact]
    public async Task ExecuteAsync_ConcurrentCalls_AreSerialized()
    {
        var executor = CreateExecutor(processStarter: _ => null);

        // Hold the turn lock externally so the background call has to wait.
        await executor.TurnLockForTests.WaitAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception? caughtEx = null;
        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in executor.ExecuteAsync("task", ct: cts.Token)) { }
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }
        });

        // Give the background task time to start and reach _turnLock.WaitAsync.
        await Task.Delay(50);
        Assert.False(backgroundTask.IsCompleted, "ExecuteAsync should be waiting on _turnLock");

        // Release the lock — the background call should now proceed (and fail on process startup).
        executor.TurnLockForTests.Release();
        await backgroundTask;

        // The failure must be the startup-exhaustion error, not the old single-flight throw.
        Assert.NotNull(caughtEx);
        Assert.IsType<InvalidOperationException>(caughtEx);
        Assert.DoesNotContain("refused to start a second turn", caughtEx.Message);
        Assert.Contains("3 attempts", caughtEx.Message);
    }

    [Theory]
    [InlineData(-32600, "no active turn to steer", true)]
    [InlineData(-32600, "expected active turn id `old` but found `new`", true)]
    [InlineData(-32600, "other invalid request", false)]
    [InlineData(-32000, "no active turn to steer", false)]
    public void IsTurnSteerPreconditionFailure_MatchesOnlyTurnBoundaryErrors(long code, string message, bool expected)
    {
        Assert.Equal(expected, CodexExecutor.IsTurnSteerPreconditionFailureForTests(code, message));
    }

    // BuildItemStartedProgress must emit a [codex tool_use:...] log line for every tool item,
    // mirroring ClaudeExecutor's tool-call logger for observability parity.
    [Fact]
    public void BuildItemStartedProgress_LogsToolUse()
    {
        var capturer = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(logger: capturer);

        var @params = new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["type"] = "commandExecution",
                ["command"] = "Bash",
                ["args"] = new JsonArray { "ls", "-la" },
            },
        };

        var progress = executor.BuildItemStartedProgressForTests(@params);

        Assert.NotNull(progress);
        Assert.Equal("tool_use", progress.EventType);

        Assert.Contains(capturer.Messages, m => m.Contains("[codex tool_use:Bash"));
    }

    private static JsonObject AgentMessageParams(string? phase, string? text = null) =>
        new()
        {
            ["item"] = new JsonObject
            {
                ["type"] = "agentMessage",
                ["phase"] = phase,
                ["text"] = text ?? "hello",
            },
        };

    [Fact]
    public void BuildItemStartedProgress_AgentMessage_FinalAnswerPhase_SetsFlag()
    {
        var executor = CreateExecutor();
        Assert.False(executor.TurnHasFinalAnswerPhaseForTests);

        executor.BuildItemStartedProgressForTests(AgentMessageParams("final_answer"));

        Assert.True(executor.TurnHasFinalAnswerPhaseForTests);
    }

    [Fact]
    public void BuildItemCompletedProgress_AgentMessage_FinalAnswerPhase_SetsFlag()
    {
        var executor = CreateExecutor();

        executor.BuildItemCompletedProgressForTests(AgentMessageParams("final_answer", "done"));

        Assert.True(executor.TurnHasFinalAnswerPhaseForTests);
    }

    [Fact]
    public void BuildItemStartedProgress_AgentMessage_CommentaryPhase_DoesNotSetFlag()
    {
        var executor = CreateExecutor();

        executor.BuildItemStartedProgressForTests(AgentMessageParams("commentary"));

        Assert.False(executor.TurnHasFinalAnswerPhaseForTests);
    }

    [Fact]
    public void BuildItemStartedProgress_AgentMessage_NoPhase_DoesNotSetFlag()
    {
        var executor = CreateExecutor();
        // Build params without the phase key at all.
        var @params = new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["type"] = "agentMessage",
                ["text"] = "thinking…",
            },
        };

        executor.BuildItemStartedProgressForTests(@params);

        Assert.False(executor.TurnHasFinalAnswerPhaseForTests);
    }

    [Fact]
    public async Task TryInjectMessageAsync_WhenFlagSet_ReturnsNoActiveTurn()
    {
        var executor = CreateExecutor();
        // Simulate having an active turn so the process check would pass.
        executor.SetThreadStateForTests("thread-1", "turn-1");
        // Manually trip the flag via BuildItemStartedProgress.
        executor.BuildItemStartedProgressForTests(AgentMessageParams("final_answer"));

        var result = await executor.TryInjectMessageAsync("late message", null, null, CancellationToken.None);

        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
        Assert.Contains("final_answer", result.Error ?? "");
    }

    // ── Local model providers (#325) ──────────────────────────────────────────────────────────

    [Theory]
    // Both built-in local providers, matched case-insensitively.
    [InlineData("ollama/gpt-oss:20b", "ollama", "gpt-oss:20b")]
    [InlineData("OLLAMA/gpt-oss:20b", "ollama", "gpt-oss:20b")]
    [InlineData("lmstudio/qwen3-coder", "lmstudio", "qwen3-coder")]
    // A slash alone is not a provider prefix — these must pass through untouched.
    [InlineData("gpt-5", null, "gpt-5")]
    [InlineData("owl/t-lite", null, "owl/t-lite")]
    [InlineData("/gpt-oss:20b", null, "/gpt-oss:20b")]
    [InlineData("ollama/", null, "ollama/")]
    public void SplitLocalModel_RecognisesOnlyBuiltInProviderPrefixes(
        string configured, string? expectedProvider, string expectedModel)
    {
        var (provider, model) = CodexExecutor.SplitLocalModel(configured);

        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedModel, model);
    }

    [Theory]
    // Prefixed model, nothing to reach it with — the fault the host-start gate exists for.
    [InlineData("ollama/gpt-oss:20b", null, true)]
    [InlineData("ollama/gpt-oss:20b", "", true)]
    [InlineData("ollama/gpt-oss:20b", "   ", true)]
    [InlineData("lmstudio/qwen3-coder", null, true)]
    // Configured, or not opted in at all.
    [InlineData("ollama/gpt-oss:20b", "http://host.docker.internal:11434/v1", false)]
    [InlineData("gpt-5", null, false)]
    [InlineData("owl/t-lite", null, false)]
    public void DescribeLocalModelFault_FlagsOnlyPrefixedModelsWithNoBaseUrl(
        string model, string? ossBaseUrl, bool expectFault)
    {
        var fault = CodexExecutor.DescribeLocalModelFault(model, ossBaseUrl);

        if (!expectFault)
        {
            Assert.Null(fault);
            return;
        }

        Assert.NotNull(fault);
        Assert.Contains("CODEX_OSS_BASE_URL", fault);
        Assert.Contains(model, fault);
    }

    [Fact]
    public async Task EnsureProcessReady_LocalProviderWithoutBaseUrl_FailsFastBeforeSpawningCodex()
    {
        var starts = 0;
        var executor = CreateExecutor(
            model: "ollama/gpt-oss:20b",
            environmentReader: _ => null,
            processStarter: _ => { starts++; return null; });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.EnsureProcessReadyForTestsAsync());

        Assert.Contains("CODEX_OSS_BASE_URL", ex.Message);
        Assert.Contains("ollama/gpt-oss:20b", ex.Message);
        // A configuration fault must not spend the crash-loop budget, and must never leave a
        // codex process running against codex's own localhost default.
        Assert.Equal(0, starts);
        Assert.DoesNotContain("3 attempts", ex.Message);
    }

    [Fact]
    public async Task EnsureProcessReady_LocalProviderWithBlankBaseUrl_FailsFast()
    {
        var executor = CreateExecutor(
            model: "ollama/gpt-oss:20b",
            environmentReader: _ => "   ",
            processStarter: _ => null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.EnsureProcessReadyForTestsAsync());

        Assert.Contains("CODEX_OSS_BASE_URL", ex.Message);
    }

    [Fact]
    public async Task ThreadStart_PrefixedModel_SendsSplitModelAndModelProvider()
    {
        using var capture = new ThreadStartCapture();
        using var workspace = new TempWorkspace();
        var executor = CreateExecutor(
            model: "ollama/gpt-oss:20b",
            workDir: workspace.Path,
            environmentReader: key =>
                key == "CODEX_OSS_BASE_URL" ? "http://host.docker.internal:11434/v1" : null,
            processStarter: capture.Start);

        var startParams = await DriveThreadStartAsync(executor, capture);

        Assert.Equal(
            ["model", "modelProvider", "cwd", "approvalPolicy", "sandbox", "serviceName", "baseInstructions", "ephemeral"],
            startParams.Select(kv => kv.Key));
        Assert.Equal("gpt-oss:20b", (string?)startParams["model"]);
        Assert.Equal("ollama", (string?)startParams["modelProvider"]);
        Assert.Equal(workspace.Path, (string?)startParams["cwd"]);
        Assert.Equal("never", (string?)startParams["approvalPolicy"]);
        Assert.Equal("danger-full-access", (string?)startParams["sandbox"]);
        Assert.Equal("phleet", (string?)startParams["serviceName"]);
        Assert.True((bool?)startParams["ephemeral"]);
    }

    // The regression that matters most: an agent that did not opt in must send the payload it
    // always sent, key for key and in the same order — no modelProvider key at all.
    [Fact]
    public async Task ThreadStart_UnprefixedModel_SendsPayloadUnchanged()
    {
        using var capture = new ThreadStartCapture();
        using var workspace = new TempWorkspace();
        var executor = CreateExecutor(
            model: "gpt-5",
            workDir: workspace.Path,
            environmentReader: _ => null,
            processStarter: capture.Start);

        var startParams = await DriveThreadStartAsync(executor, capture);

        Assert.Equal(
            ["model", "cwd", "approvalPolicy", "sandbox", "serviceName", "baseInstructions", "ephemeral"],
            startParams.Select(kv => kv.Key));
        Assert.Equal("gpt-5", (string?)startParams["model"]);
        Assert.Equal(workspace.Path, (string?)startParams["cwd"]);
        Assert.Equal("never", (string?)startParams["approvalPolicy"]);
        Assert.Equal("danger-full-access", (string?)startParams["sandbox"]);
        Assert.Equal("phleet", (string?)startParams["serviceName"]);
        Assert.True((bool?)startParams["ephemeral"]);
    }

    /// <summary>
    /// Runs the real startup handshake against the stand-in app-server and returns the
    /// <c>thread/start</c> params exactly as they went over the wire.
    /// </summary>
    private static async Task<JsonObject> DriveThreadStartAsync(CodexExecutor executor, ThreadStartCapture capture)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The stand-in writes nothing to stdout, so the responder below is the only thing that can
        // complete a request — no echo can race it.
        var responder = Task.Run(async () =>
        {
            await executor.WaitAndCompleteNextPendingRequestForTests(new JsonObject(), cts.Token);
            await executor.WaitAndCompleteNextPendingRequestForTests(new JsonObject
            {
                ["thread"] = new JsonObject { ["id"] = "thread-1", ["ephemeral"] = true },
            }, cts.Token);
        }, cts.Token);

        await executor.EnsureProcessReadyForTestsAsync(cts.Token);
        await responder;

        return capture.ReadThreadStartParams();
    }

    /// <summary>A throwaway <c>WorkDir</c>, because startup writes <c>system-prompt.md</c> into it.</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace() => Directory.CreateDirectory(Path);

        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-ws-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* teardown only */ }
        }
    }

    /// <summary>
    /// A stand-in codex app-server: <c>cat</c> redirected into a file, so it swallows every frame
    /// the executor writes and answers nothing. The frames stay on disk to be asserted, and the
    /// test — not an echo of its own request — decides each JSON-RPC response.
    /// </summary>
    /// <remarks>
    /// POSIX-only, like <see cref="StandInProcess"/>: the suite already hard-depends on
    /// <c>/bin/cat</c>, and a skipped provider test is an unmeasured provider reported as green.
    /// </remarks>
    private sealed class ThreadStartCapture : IDisposable
    {
        private const string ShellPath = "/bin/sh"; // hygiene-ok: OS stand-in binary, not provider data

        private readonly string _capturePath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-rpc-{Guid.NewGuid():N}.jsonl");

        private System.Diagnostics.Process? _process;

        public System.Diagnostics.Process Start(System.Diagnostics.ProcessStartInfo _)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ShellPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"cat > '{_capturePath}'");

            _process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start the stand-in app-server '{ShellPath}'.");
            return _process;
        }

        public JsonObject ReadThreadStartParams()
        {
            // Closing the executor's stdin lets cat drain and exit, so the capture file is complete.
            _process!.StandardInput.BaseStream.Close();
            _process.WaitForExit(milliseconds: 5000);

            foreach (var line in File.ReadAllLines(_capturePath))
            {
                if (JsonNode.Parse(line) is JsonObject frame &&
                    (string?)frame["method"] == "thread/start" &&
                    frame["params"] is JsonObject startParams)
                {
                    return startParams;
                }
            }

            throw new InvalidOperationException($"No thread/start frame was captured in {_capturePath}.");
        }

        public void Dispose()
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch { /* teardown only */ }

            _process?.Dispose();
            try { File.Delete(_capturePath); } catch { /* teardown only */ }
        }
    }

    [Fact]
    public async Task AfterCommentaryPhase_InjectionStillSucceeds()
    {
        // After a commentary-phase (or absent-phase) agentMessage, the final-answer flag must NOT
        // be set, so TryInjectMessageAsync must proceed to turn/steer and return Injected.
        using var standIn = new StandInProcess();
        var process = standIn.Process;
        try
        {
            var executor = CreateExecutor();
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(process.StandardInput);
            executor.SetThreadStateForTests("thread-1", "turn-1");
            executor.BuildItemStartedProgressForTests(AgentMessageParams("commentary"));
            Assert.False(executor.TurnHasFinalAnswerPhaseForTests);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // WaitAndCompleteNextPendingSteerForTests polls _pendingRequests until the turn/steer
            // RPC is registered, then resolves it with a successful response.
            var resolveTask = executor.WaitAndCompleteNextPendingRequestForTests(
                new JsonObject { ["turnId"] = "turn-1" }, cts.Token);
            var injectTask = executor.TryInjectMessageAsync("mid-turn injection", null, null, cts.Token);
            await Task.WhenAll(resolveTask, injectTask);

            Assert.Equal(MidTurnInjectionStatus.Injected, (await injectTask).Status);
        }
        finally
        {
            process.Kill();
            process.Dispose();
        }
    }
}
