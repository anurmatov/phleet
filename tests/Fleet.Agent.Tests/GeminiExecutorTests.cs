using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Services.Credentials;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// GeminiExecutor against the Antigravity CLI's print mode (<c>agy</c> 1.5.14). The event shapes
/// are the ones <c>tests/antigravity-cli/run.sh</c> records from the real pinned binary; here a
/// shell script stands in for it, so the arguments, what is written to its input, the session
/// rule and the mapping are checked without the binary.
/// </summary>
public sealed class GeminiExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gemini-exec-{Guid.NewGuid():N}");
    private readonly string _workDir;
    private readonly string _geminiHome;

    public GeminiExecutorTests()
    {
        _workDir = Path.Combine(_root, "workspace");
        _geminiHome = Path.Combine(_root, "home", ".gemini");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A step_update event around the step object <paramref name="step"/>.</summary>
    private static string Step(string step) => "{\"event\":\"step_update\",\"step_update\":" + step + "}";

    // ── arguments ────────────────────────────────────────────────────────────

    [Fact]
    public void Args_AreTheMeasuredFlags_WithTheModelVerbatim_AndTheConversationToResume()
    {
        Assert.Equal(
            [
                "--output-format", "stream-json",
                "--input-format", "stream-json",
                "--disable-slash-commands",
                "--dangerously-skip-permissions",
                "--settings", """{"modelProvider":"gemini","enableTerminalSandbox":false}""",
                "--model", "gemini-4-argon-medium",
                "--conversation", "c-1",
            ],
            GeminiExecutor.BuildArgs("gemini-4-argon-medium", "c-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Args_WithoutAModel_HaveNoModelFlag_AndANewSessionHasNoConversationFlag(string? model)
    {
        var args = GeminiExecutor.BuildArgs(model, resumeId: null);

        Assert.DoesNotContain("--model", args);
        Assert.DoesNotContain("--conversation", args);
    }

    [Fact]
    public void Args_NeverCarryAnEffort_APrompt_OrASandbox()
    {
        var args = GeminiExecutor.BuildArgs("gemini-4-neon-xhigh", "c-1");

        // The slug carries the effort and the CLI refuses --effort beside it; the prompt goes to
        // the input, never to an argument; --sandbox makes every shell command fail in a container.
        Assert.DoesNotContain("--effort", args);
        Assert.DoesNotContain("-p", args);
        Assert.DoesNotContain("--print", args);
        Assert.DoesNotContain("--sandbox", args);
        Assert.DoesNotContain("--continue", args);
    }

    [Fact]
    public void StartInfo_RunsAgyInTheWorkDir_WithItsSelfUpdateOff_AndNothingOfTheOldCli()
    {
        var turns = new StandIn(this);
        var psi = turns.Executor().BuildStartInfo(resumeId: null);

        Assert.Equal("agy", psi.FileName);
        Assert.Equal(_workDir, psi.WorkingDirectory);
        Assert.Equal("true", psi.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
        Assert.False(psi.Environment.ContainsKey("GEMINI_SYSTEM_MD"));
        Assert.False(psi.Environment.ContainsKey("GEMINI_CLI_TRUST_WORKSPACE"));
        Assert.True(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError);
    }

    [Fact]
    public void UserMessageLine_IsOneLineOfJson_ThatCarriesTheTextExactly()
    {
        const string text = "first line\nsecond \"quoted\" line\n/help is not a command — привет 🙂";

        var line = GeminiExecutor.UserMessageLine(text);

        Assert.EndsWith("\n", line);
        Assert.Equal(1, line.Count(c => c == '\n'));
        var parsed = Parse(line);
        Assert.Equal("user", parsed.GetProperty("event").GetString());
        Assert.Equal(text, parsed.GetProperty("message").GetProperty("content").GetString());
    }

    // ── attachments ──────────────────────────────────────────────────────────

    [Fact]
    public void AnAttachment_IsAPathHintInTheText_AddedOnce_AndOnlyWhenItWasPersisted()
    {
        var images = new[]
        {
            new MessageImage([1], "image/png") { FilePath = "/attachments/a.png" },
            new MessageImage([2], "image/png"),
        };
        var documents = new[] { new MessageDocument("file-1", "application/pdf", 3, "b.pdf") { FilePath = "/attachments/b.pdf" } };

        // A chat image arrives without a hint; the Telegram intake has put its own in already.
        Assert.Equal(
            "look at this\n[image attachment: /attachments/a.png]\n[document attachment: /attachments/b.pdf]",
            GeminiExecutor.WithAttachmentHints("look at this", images, documents));
        const string hinted = "look at this\n[image attachment: /attachments/a.png]\n[document attachment: /attachments/b.pdf]";
        Assert.Equal(hinted, GeminiExecutor.WithAttachmentHints(hinted, images, documents));
        Assert.Equal("no attachments", GeminiExecutor.WithAttachmentHints("no attachments", null, null));
    }

    // ── events ───────────────────────────────────────────────────────────────

    [Fact]
    public void Text_IsNotSignificant_AndIsKeptForTheResult()
    {
        var turn = new GeminiExecutor.TurnState();

        var first = GeminiExecutor.MapStep(Parse(Step("""{"step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"Scripted rep"}""")), turn);
        var second = GeminiExecutor.MapStep(Parse(Step("""{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"ly 1.\n","usage":{"input_tokens":800}}""")), turn);

        var chunk = Assert.Single(first);
        Assert.Equal(("assistant", "Scripted rep", false), (chunk.EventType, chunk.Summary, chunk.IsSignificant));
        Assert.Null(chunk.FinalResult);
        Assert.Equal("ly 1.\n", Assert.Single(second).Summary);
        Assert.Equal("Scripted reply 1.\n", turn.Text.ToString());
    }

    [Fact]
    public void AToolStep_IsAnnouncedOnce_Significant_WithItsNameAndArguments_AndItsEndIsAToolResult()
    {
        var turn = new GeminiExecutor.TurnState();
        const string call = """{"step_index":2,"step_type":"tool","tool_name":"run_command","state":"ACTIVE","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo synthetic"}}}""";
        const string done = """{"step_index":2,"step_type":"tool","tool_name":"run_command","state":"DONE","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo synthetic"},"output":"Command exited with code 0.\n\nCommand output:\nsynthetic\r\n"}}""";

        var started = GeminiExecutor.MapStep(Parse(Step(call)), turn);
        var ended = GeminiExecutor.MapStep(Parse(Step(done)), turn);

        var use = Assert.Single(started);
        Assert.Equal(("tool_use", "run_command", true), (use.EventType, use.ToolName, use.IsSignificant));
        Assert.Equal("""{"CommandLine":"echo synthetic"}""", use.ToolArgs);
        Assert.Equal("Using run_command", use.Summary);
        var result = Assert.Single(ended);
        Assert.Equal(("tool_result", false), (result.EventType, result.IsSignificant));
        Assert.Contains("synthetic", result.Summary);
    }

    [Fact]
    public void AToolStepThatFails_IsAToolResultWithTheCliErrorText()
    {
        var turn = new GeminiExecutor.TurnState();
        const string failed = """{"step_index":2,"state":"ERROR","step_type":"tool","tool_name":"write_file","tool_info":{"name":"write_file","parameters":{"Path":"/work/a.txt"},"error":{"type":"TOOL_ERROR","message":"permission check failed for write_file"}}}""";

        var mapped = GeminiExecutor.MapStep(Parse(Step(failed)), turn);

        // No ACTIVE event came first: the step is still announced before its result.
        Assert.Equal(["tool_use", "tool_result"], mapped.Select(p => p.EventType));
        Assert.Equal("write_file", mapped[0].ToolName);
        Assert.Equal("permission check failed for write_file", mapped[1].Summary);
        Assert.False(mapped[1].IsErrorResult);
    }

    [Fact]
    public void AnMcpTool_IsShownUnderTheNameOtherProvidersGiveIt_WithItsOwnArguments()
    {
        var turn = new GeminiExecutor.TurnState();
        const string call = """{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"call_mcp_tool","tool_info":{"name":"call_mcp_tool","parameters":{"Arguments":{"id":7},"ServerName":"recorder","ToolName":"get_message"}}}""";

        var use = Assert.Single(GeminiExecutor.MapStep(Parse(Step(call)), turn));

        Assert.Equal("mcp__recorder__get_message", use.ToolName);
        Assert.Equal("""{"id":7}""", use.ToolArgs);
    }

    [Theory]
    [InlineData("""{"event":"init","conversation_id":"c-1","init":{"cwd":"/work","tools":["read_file"],"permission_mode":"always-proceed"}}""")]
    [InlineData("""{"event":"step_update","step_update":{"conversation_id":"c-1","step_index":0,"state":"DONE","step_type":"user_input"}}""")]
    [InlineData("""{"event":"step_update","step_update":{"conversation_id":"c-1","step_index":3,"state":"DONE","step_type":"system_message","duration_seconds":0.0003}}""")]
    [InlineData("""{"event":"step_update","step_update":{"conversation_id":"c-1","step_index":1,"state":"DONE","step_type":"error_message"}}""")]
    [InlineData("""{"event":"step_update","step_update":{"conversation_id":"c-1","step_index":4,"state":"DONE","step_type":"checkpoint"}}""")]
    [InlineData("""{"event":"step_update","step_update":{"conversation_id":"c-1","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":0.004}}""")]
    [InlineData("""{"event":"something_new","payload":{"text":"ignored"}}""")]
    public void EverythingElse_MapsToNothing(string json)
    {
        var turn = new GeminiExecutor.TurnState();

        Assert.Empty(GeminiExecutor.MapStep(Parse(json), turn));
        Assert.Null(GeminiExecutor.MapResult(Parse(json), turn, DateTimeOffset.UtcNow));
        Assert.Equal(0, turn.Text.Length);
    }

    [Fact]
    public void ASuccessResult_IsTheTerminal_WithTheCliResponse()
    {
        var turn = new GeminiExecutor.TurnState();
        turn.Text.Append("text that came as deltas");

        var final = GeminiExecutor.MapResult(Parse(
            """{"event":"result","result":{"conversation_id":"c-1","status":"SUCCESS","response":"Scripted reply 1.\n","duration_seconds":0.16,"num_turns":3,"usage":{"input_tokens":2400,"output_tokens":111,"thinking_tokens":21,"cache_read_tokens":1200,"total_tokens":2511}}}"""),
            turn, DateTimeOffset.UtcNow)!;

        Assert.Equal(("result", "Scripted reply 1.\n", false, true), (final.EventType, final.FinalResult, final.IsErrorResult, final.IsSignificant));
        Assert.Equal("c-1", final.SessionId);
        Assert.Equal(3, turn.NumTurns);
        Assert.Equal(2400, turn.Usage!.Value.GetProperty("input_tokens").GetInt64());
    }

    [Fact]
    public void ASuccessResultWithoutAResponse_FallsBackToTheTextThatWasStreamed()
    {
        var turn = new GeminiExecutor.TurnState();
        turn.Text.Append("streamed text");

        var final = GeminiExecutor.MapResult(Parse("""{"event":"result","result":{"conversation_id":"c-1","status":"SUCCESS","response":""}}"""), turn, DateTimeOffset.UtcNow)!;

        Assert.Equal("streamed text", final.FinalResult);
        Assert.False(final.IsErrorResult);
        Assert.Null(turn.Usage);
        Assert.Null(turn.NumTurns);
    }

    [Fact]
    public void AnErrorResult_IsAnErrorTerminal_WithTheCliErrorText()
    {
        var final = GeminiExecutor.MapResult(Parse(
            """{"event":"result","result":{"conversation_id":"c-1","status":"ERROR","response":"","error":"agent executor error: generating and executing: Error 500, Message: scripted failure, Status: INTERNAL, Details: []","num_turns":1,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"finished_reason":"error"}}"""),
            new GeminiExecutor.TurnState(), DateTimeOffset.UtcNow)!;

        Assert.True(final.IsErrorResult);
        Assert.True(final.IsSignificant);
        Assert.Contains("Error 500", final.FinalResult);
    }

    // ── the session ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewSession_HasNoConversationFlag_ItsFirstMessageStartsWithTheNewSessionLine_AndTheFileIsWritten()
    {
        var turns = new StandIn(this);
        var executor = turns.Executor();
        Assert.True(executor.KeepsConversation);
        Assert.False(executor.IsProcessWarm);
        Assert.Null(executor.SessionOrigin);

        var progress = await turns.RunAsync(executor, "synthetic task", Turn("c-1", "Scripted reply 1."));

        Assert.Equal("Scripted reply 1.", Assert.Single(progress, p => p.FinalResult is not null).FinalResult);
        Assert.DoesNotContain("--conversation", turns.Starts[0].Args);
        Assert.Equal(PromptAssembler.NewSessionLine + "\nsynthetic task", turns.Starts[0].Message);
        Assert.Equal(("gemini", "c-1", "cloud"), SessionFile());
        Assert.Equal(ProviderSessionFile.HashInstructions(turns.Instructions), SessionFileHash());
        Assert.Equal("new", executor.SessionOrigin);
        Assert.Equal("c-1", executor.LastSessionId);
        Assert.Contains(turns.Log.Entries, e => e.Message == "Gemini session new: no file");
    }

    [Fact]
    public async Task TheNextTurn_ResumesTheConversation_WithoutTheLine()
    {
        var turns = new StandIn(this);
        var executor = turns.Executor();
        await turns.RunAsync(executor, "first", Turn("c-1", "one"));

        await turns.RunAsync(executor, "second", Turn("c-1", "two"));

        Assert.Equal(["--conversation", "c-1"], turns.Starts[1].Args.TakeLast(2));
        Assert.Equal("second", turns.Starts[1].Message);
        Assert.Equal("new", executor.SessionOrigin);
        Assert.Single(turns.Log.Entries, e => e.Message.StartsWith("Gemini session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AfterARestart_TheFileIsResumed_WhenProviderAndInstructionsMatch()
    {
        var turns = new StandIn(this);
        await turns.RunAsync(turns.Executor(), "first", Turn("c-1", "one"));

        // A new executor is a new agent process, in this container or the next one.
        var restarted = turns.Executor();
        await turns.RunAsync(restarted, "after the restart", Turn("c-1", "two"));

        Assert.Equal(["--conversation", "c-1"], turns.Starts[1].Args.TakeLast(2));
        Assert.Equal("after the restart", turns.Starts[1].Message);
        Assert.Equal("resumed", restarted.SessionOrigin);
        Assert.Contains(turns.Log.Entries, e => e.Message == "Gemini session resumed: c-1");
    }

    [Fact]
    public async Task ChangedInstructions_StartANewSession_AndReplaceTheFile()
    {
        var turns = new StandIn(this);
        await turns.RunAsync(turns.Executor(), "first", Turn("c-1", "one"));
        var oldHash = SessionFileHash();

        turns.ChangeInstructions();
        var restarted = turns.Executor();
        await turns.RunAsync(restarted, "with new instructions", Turn("c-2", "two"));

        Assert.DoesNotContain("--conversation", turns.Starts[1].Args);
        Assert.Equal(PromptAssembler.NewSessionLine + "\nwith new instructions", turns.Starts[1].Message);
        Assert.Equal(("gemini", "c-2", "cloud"), SessionFile());
        Assert.NotEqual(oldHash, SessionFileHash());
        Assert.Contains(turns.Log.Entries, e => e.Message == "Gemini session new: instructions changed");

        // The same holds without a restart: the executor compares its own session too.
        turns.ChangeInstructions();
        await turns.RunAsync(restarted, "changed again", Turn("c-3", "three"));
        Assert.DoesNotContain("--conversation", turns.Starts[2].Args);
        Assert.StartsWith(PromptAssembler.NewSessionLine, turns.Starts[2].Message);
        Assert.Equal(("gemini", "c-3", "cloud"), SessionFile());
    }

    [Fact]
    public async Task ASessionOfAnotherProvider_IsNotResumed()
    {
        var turns = new StandIn(this);
        Directory.CreateDirectory(Path.Combine(_workDir, ".fleet"));
        File.WriteAllText(
            Path.Combine(_workDir, AgentSessionFile.RelativePath),
            $$"""{"provider":"claude","sessionId":"claude-session","instructionsSha256":"{{ProviderSessionFile.HashInstructions(turns.Instructions)}}","runsOn":"cloud"}""");

        await turns.RunAsync(turns.Executor(), "task", Turn("c-1", "one"));

        Assert.DoesNotContain("--conversation", turns.Starts[0].Args);
        Assert.Contains(turns.Log.Entries, e => e.Message == "Gemini session new: other provider");
        Assert.Equal(("gemini", "c-1", "cloud"), SessionFile());
    }

    [Fact]
    public async Task AConversationTheCliDoesNotFind_IsAFailedResume_KnownBeforeTheMessageIsWritten()
    {
        var turns = new StandIn(this);
        await turns.RunAsync(turns.Executor(), "first", Turn("c-1", "one"));

        // The CLI does not fail on an unknown conversation: it starts another and says so in init.
        var restarted = turns.Executor();
        var progress = await turns.RunAsync(restarted, "after the conversation was lost", Turn("c-other", "two"));

        Assert.Equal(2, turns.Starts.Count); // one process for this turn: nothing is killed or retried
        Assert.Equal(["--conversation", "c-1"], turns.Starts[1].Args.TakeLast(2));
        Assert.Equal(PromptAssembler.NewSessionLine + "\nafter the conversation was lost", turns.Starts[1].Message);
        Assert.Equal("two", Assert.Single(progress, p => p.FinalResult is not null).FinalResult);
        Assert.Equal(("gemini", "c-other", "cloud"), SessionFile());
        Assert.Equal("new", restarted.SessionOrigin);
        Assert.Contains(turns.Log.Entries, e => e.Message.StartsWith("Gemini session new: resume failed: ", StringComparison.Ordinal));
        Assert.DoesNotContain(turns.Log.Entries, e => e.Message.StartsWith("Gemini session resumed", StringComparison.Ordinal));

        // And the turn after it resumes the conversation the CLI started, without the line.
        await turns.RunAsync(restarted, "next", Turn("c-other", "three"));
        Assert.Equal(["--conversation", "c-other"], turns.Starts[2].Args.TakeLast(2));
        Assert.Equal("next", turns.Starts[2].Message);
    }

    [Fact]
    public async Task AResumeThatEndsBeforeInit_RemovesTheFile_AndBecomesANewSessionInTheSameTurn_Once()
    {
        var turns = new StandIn(this);
        await turns.RunAsync(turns.Executor(), "first", Turn("c-1", "one"));

        var restarted = turns.Executor();
        var progress = await turns.RunAsync(
            restarted, "task",
            "echo 'conversation store is damaged' >&2; exit 1",
            Turn("c-2", "two"));

        Assert.Equal(3, turns.Starts.Count);
        Assert.Contains("--conversation", turns.Starts[1].Args);
        Assert.DoesNotContain("--conversation", turns.Starts[2].Args);
        Assert.Equal(PromptAssembler.NewSessionLine + "\ntask", turns.Starts[2].Message);
        var final = Assert.Single(progress, p => p.FinalResult is not null);
        Assert.Equal(("two", false), (final.FinalResult, final.IsErrorResult));
        Assert.Equal(("gemini", "c-2", "cloud"), SessionFile());
        Assert.Contains(turns.Log.Entries, e => e.Message == "Gemini session new: resume failed: the CLI ended before its init");
    }

    [Fact]
    public async Task AStartThatEndsBeforeInit_IsNotRetried_WhenItWasNotAResume_AndNeverAThirdTime()
    {
        var turns = new StandIn(this);
        const string broken = """printf '%s\n' '{"event":"result","result":{"conversation_id":"","status":"ERROR","response":"","error":"invalid model selection (--model \"no-such-model\")","num_turns":0}}'; exit 1""";

        var fresh = await turns.RunAsync(turns.Executor(), "task", broken);

        Assert.Single(turns.Starts);
        var final = Assert.Single(fresh, p => p.FinalResult is not null);
        Assert.True(final.IsErrorResult);
        Assert.Contains("invalid model selection", final.FinalResult);
        Assert.False(File.Exists(Path.Combine(_workDir, AgentSessionFile.RelativePath)));

        // With a session to resume: the resume and one new start, both failing, and then the error.
        await turns.RunAsync(turns.Executor(), "first", Turn("c-1", "one"));
        var resumed = await turns.RunAsync(turns.Executor(), "task", broken, broken, Turn("c-9", "never reached"));

        Assert.Equal(4, turns.Starts.Count);
        Assert.True(Assert.Single(resumed, p => p.FinalResult is not null).IsErrorResult);
        Assert.False(File.Exists(Path.Combine(_workDir, AgentSessionFile.RelativePath)));
    }

    [Fact]
    public async Task AStreamThatEndsWithoutAResult_IsAnErrorWithWhatTheCliSaid()
    {
        var turns = new StandIn(this);

        var progress = await turns.RunAsync(
            turns.Executor(), "task",
            """printf '%s\n' '{"event":"init","conversation_id":"c-1","init":{}}'; cat > /dev/null; echo 'error: interrupted' >&2; exit 3""");

        var final = Assert.Single(progress, p => p.FinalResult is not null);
        Assert.True(final.IsErrorResult);
        Assert.Equal("error: interrupted", final.FinalResult);
    }

    [Fact]
    public async Task ACliThatStaysAfterItsResult_DoesNotHoldTheTurn_AndIsKilled()
    {
        var turns = new StandIn(this);
        var pidFile = Path.Combine(_root, "stays.pid");
        var stays = Turn("c-1", "the answer") + $"\necho $$ > '{pidFile}'\nexec sleep 600\n";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var progress = await turns.RunAsync(turns.Executor(exitWait: TimeSpan.FromMilliseconds(300)), "task", stays);

        var final = Assert.Single(progress, p => p.FinalResult is not null);
        Assert.False(final.IsErrorResult);
        Assert.Equal("the answer", final.FinalResult);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"the turn took {clock.Elapsed}");
        Assert.Contains(turns.Log.Entries, e => e.Message.Contains("did not exit within"));

        // The enumeration has ended, so the kill has been done and confirmed.
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.ThrowsAny<Exception>(() =>
        {
            using var still = System.Diagnostics.Process.GetProcessById(pid);
            if (still.HasExited) throw new InvalidOperationException("exited");
        });
    }

    [Fact]
    public async Task ACliThatAnswersBeforeInitAndWaitsForItsInput_GetsTheEndOfInput()
    {
        var turns = new StandIn(this);
        const string waits = """printf '%s\n' '{"event":"result","result":{"conversation_id":"","status":"ERROR","response":"","error":"synthetic early error","num_turns":0}}'; cat > /dev/null; exit 1""";

        var progress = await turns.RunAsync(turns.Executor(exitWait: TimeSpan.FromSeconds(20)), "task", waits);

        var final = Assert.Single(progress, p => p.FinalResult is not null);
        Assert.True(final.IsErrorResult);
        Assert.Equal("synthetic early error", final.FinalResult);
        Assert.DoesNotContain(turns.Log.Entries, e => e.Message.Contains("did not exit within"));
    }

    [Fact]
    public async Task ResetAndStop_RemoveTheFile_SoTheNextTurnIsANewSession()
    {
        var turns = new StandIn(this);
        var executor = turns.Executor();
        await turns.RunAsync(executor, "first", Turn("c-1", "one"));
        Assert.True(File.Exists(Path.Combine(_workDir, AgentSessionFile.RelativePath)));

        await executor.StopProcessAsync();

        Assert.False(File.Exists(Path.Combine(_workDir, AgentSessionFile.RelativePath)));
        Assert.Null(executor.SessionOrigin);
        Assert.Null(executor.LastSessionId);
        await turns.RunAsync(executor, "after the reset", Turn("c-2", "two"));
        Assert.DoesNotContain("--conversation", turns.Starts[1].Args);
        Assert.StartsWith(PromptAssembler.NewSessionLine, turns.Starts[1].Message);
    }

    [Fact]
    public async Task ACredentialChange_KeepsTheSession()
    {
        var turns = new StandIn(this);
        var executor = turns.Executor();
        await turns.RunAsync(executor, "first", Turn("c-1", "one"));

        Assert.False(await executor.TryStopProcessAsync());
        executor.RequestRestart();

        Assert.True(File.Exists(Path.Combine(_workDir, AgentSessionFile.RelativePath)));
        await turns.RunAsync(executor, "second", Turn("c-1", "two"));
        Assert.Contains("--conversation", turns.Starts[1].Args);
    }

    [Fact]
    public async Task AGeminiTokenBroadcast_ChangesNothing_AndRestartsNothing()
    {
        var options = new AgentOptions { Name = "test", Role = "test", WorkDir = _workDir, Provider = "gemini" };
        var executor = Substitute.For<IAgentExecutor>();

        await new ProviderTokenUpdater(options, executor, NullLogger.Instance).ApplyAsync(
            """{"provider":"gemini","accessToken":"synthetic-access-token","expiresAt":4102444800000}""");

        await executor.DidNotReceive().TryStopProcessAsync();
        executor.DidNotReceive().RequestRestart();
    }

    // ── what is never cut ────────────────────────────────────────────────────

    [Fact]
    public async Task AMessageOverTheLimit_IsRefused_WithTheLimitAndTheSize_BeforeAnyProcess()
    {
        var turns = new StandIn(this);
        var reporter = ProviderUsageTests.Reporter();
        var tooLong = new string('x', GeminiExecutor.MaxPromptBytes - Encoding.UTF8.GetByteCount(PromptAssembler.NewSessionLine));

        var progress = await turns.RunAsync(turns.Executor(reporter), tooLong);

        var final = Assert.Single(progress);
        Assert.True(final.IsErrorResult);
        Assert.Equal(
            $"The message is {GeminiExecutor.MaxPromptBytes + 1} bytes; the Antigravity CLI takes at most {GeminiExecutor.MaxPromptBytes} bytes in one message. Nothing was sent.",
            final.FinalResult);
        Assert.Empty(turns.Starts);
        Assert.Empty(ProviderUsageTests.Drain(reporter));
        Assert.False(File.Exists(Path.Combine(_workDir, AgentSessionFile.RelativePath)));
    }

    [Fact]
    public async Task AMessageAtTheLimit_IsSentWhole_AndTheLimitCountsBytesNotCharacters()
    {
        var turns = new StandIn(this);
        var line = Encoding.UTF8.GetByteCount(PromptAssembler.NewSessionLine) + 1;
        // Two-byte characters: half as many characters fit.
        var atLimit = new string('я', (GeminiExecutor.MaxPromptBytes - line) / 2) + ((GeminiExecutor.MaxPromptBytes - line) % 2 == 1 ? "x" : "");

        var progress = await turns.RunAsync(turns.Executor(), atLimit, Turn("c-1", "one"));

        Assert.False(Assert.Single(progress, p => p.FinalResult is not null).IsErrorResult);
        Assert.Equal(GeminiExecutor.MaxPromptBytes, Encoding.UTF8.GetByteCount(turns.Starts[0].Message));
        Assert.EndsWith(atLimit, turns.Starts[0].Message);
    }

    [Fact]
    public async Task InstructionsTheCliWouldCut_RefuseTheTurn_BeforeAnyProcess()
    {
        var turns = new StandIn(this);
        turns.SetInstructions(string.Concat(Enumerable.Repeat("A synthetic rule that is repeated to fill the budget.\n", 1200)));

        var progress = await turns.RunAsync(turns.Executor(), "task");

        var final = Assert.Single(progress);
        Assert.True(final.IsErrorResult);
        Assert.Contains($"the Antigravity CLI delivers at most {GeminiInstructionFiles.BudgetBytes} bytes of instructions", final.FinalResult);
        Assert.DoesNotContain("synthetic rule", final.FinalResult);
        Assert.Empty(turns.Starts);
        Assert.DoesNotContain(turns.Log.Entries, e => e.Message.Contains("synthetic rule"));
    }

    [Fact]
    public async Task TheInstructions_AreWrittenAsRuleFiles_BeforeTheProcessStarts()
    {
        var turns = new StandIn(this);

        await turns.RunAsync(turns.Executor(), "task", Turn("c-1", "one"));

        var file = Path.Combine(GeminiInstructionFiles.RulesDirectory(_geminiHome), "fleet-instructions-001.md");
        Assert.Equal(GeminiInstructionFiles.FrontMatter + turns.Instructions, File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(GeminiInstructionFiles.RulesDirectory(_geminiHome)));
    }

    // ── usage ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheUsageRow_CarriesWhatTheResultReports_PerConversation_AndANullStaysNull()
    {
        var turns = new StandIn(this);
        var reporter = ProviderUsageTests.Reporter();
        var executor = turns.Executor(reporter);

        await turns.RunAsync(executor, "first", Turn("c-1", "one", """{"input_tokens":800,"output_tokens":37,"thinking_tokens":7,"cache_read_tokens":400,"total_tokens":837}""", numTurns: 1));
        await turns.RunAsync(executor, "second", Turn("c-1", "two", """{"input_tokens":1700,"output_tokens":80,"total_tokens":1780}""", numTurns: 2));

        var rows = ProviderUsageTests.Drain(reporter);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("gemini", "gemini-4-argon-medium", "c-1"), (rows[0].GetProperty("provider").GetString(), rows[0].GetProperty("model").GetString(), rows[0].GetProperty("session_id").GetString()));
        Assert.Equal((800, 37, 400), (rows[0].GetProperty("cum_input_tokens").GetInt64(), rows[0].GetProperty("cum_output_tokens").GetInt64(), rows[0].GetProperty("cum_cache_read_tokens").GetInt64()));
        // The CLI counts the conversation, so the second row is the difference.
        Assert.Equal((900, 43), (rows[1].GetProperty("input_tokens").GetInt64(), rows[1].GetProperty("output_tokens").GetInt64()));
        Assert.Equal(1700, rows[1].GetProperty("cum_input_tokens").GetInt64());
        // Not reported by the CLI this time: absent, never zero.
        Assert.False(rows[1].TryGetProperty("cache_read_tokens", out _));
        Assert.False(rows[1].TryGetProperty("cum_cache_read_tokens", out _));
        Assert.False(rows[0].TryGetProperty("cost_usd", out _));
        Assert.False(rows[0].TryGetProperty("cache_creation_tokens", out _));
    }

    // ── the key gate ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AGeminiAgentWithoutAKey_DoesNotStart_AndTheFaultNamesTheVariable(string? value)
    {
        var agent = new AgentOptions { Name = "test", Role = "test", WorkDir = _workDir, Provider = "gemini" };

        var fault = AgentHostRegistration.DescribeGeminiKeyFault(agent, name => name == "GEMINI_API_KEY" ? value : "other-value");

        Assert.NotNull(fault);
        Assert.StartsWith("GEMINI_API_KEY is not set", fault);
    }

    [Theory]
    [InlineData("gemini", "synthetic-key-value", null)]
    [InlineData("gemini", "fleet_placeholder_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ", null)]
    [InlineData("claude", null, null)]
    [InlineData("codex", "", null)]
    public void TheKeyGate_PassesAGeminiAgentWithAKey_AndEveryOtherProvider(string provider, string? value, string? expected)
    {
        var agent = new AgentOptions { Name = "test", Role = "test", WorkDir = _workDir, Provider = provider };

        var fault = AgentHostRegistration.DescribeGeminiKeyFault(agent, _ => value);

        Assert.Equal(expected, fault);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A stand-in script for one start: init, read the input, the result, exit 0.</summary>
    private static string Turn(string conversationId, string response, string? usage = null, int numTurns = 1) =>
        $$$"""
        printf '%s\n' '{"event":"init","conversation_id":"{{{conversationId}}}","init":{"cwd":"/work","tools":[],"permission_mode":"always-proceed"}}'
        cat > "$CAPTURE"
        printf '%s\n' '{"event":"step_update","step_update":{"conversation_id":"{{{conversationId}}}","step_index":0,"state":"DONE","step_type":"user_input"}}'
        printf '%s\n' '{"event":"result","result":{"conversation_id":"{{{conversationId}}}","status":"SUCCESS","response":"{{{response}}}","num_turns":{{{numTurns}}}{{{(usage is null ? "" : ",\"usage\":" + usage)}}}}}'
        """;

    private (string Provider, string SessionId, string RunsOn) SessionFile()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_workDir, AgentSessionFile.RelativePath)));
        return (doc.RootElement.GetProperty("provider").GetString()!, doc.RootElement.GetProperty("sessionId").GetString()!, doc.RootElement.GetProperty("runsOn").GetString()!);
    }

    private string SessionFileHash()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_workDir, AgentSessionFile.RelativePath)));
        return doc.RootElement.GetProperty("instructionsSha256").GetString()!;
    }

    /// <summary>Starts shell scripts in place of the CLI and records what each start was given.</summary>
    private sealed class StandIn(GeminiExecutorTests test)
    {
        private readonly string _contentRoot = Path.Combine(test._root, "content");
        private readonly Queue<string> _scripts = new();
        private int _instructionsVersion;

        public sealed record Start(IReadOnlyList<string> Args, string CapturePath)
        {
            /// <summary>The text of the one user message written to the input ("" when none was).</summary>
            public string Message
            {
                get
                {
                    if (!File.Exists(CapturePath)) return "";
                    var text = File.ReadAllText(CapturePath);
                    if (text.Length == 0) return "";
                    Assert.EndsWith("\n", text);
                    Assert.Equal(1, text.Count(c => c == '\n'));
                    using var doc = JsonDocument.Parse(text);
                    Assert.Equal("user", doc.RootElement.GetProperty("event").GetString());
                    return doc.RootElement.GetProperty("message").GetProperty("content").GetString()!;
                }
            }
        }

        public List<Start> Starts { get; } = [];
        public CapturingLogger<GeminiExecutor> Log { get; } = new();
        /// <summary>The system prompt as the executor builds it now.</summary>
        public string Instructions => Prompt(AgentOptions()).BuildSystemPrompt();

        private IOptions<AgentOptions> AgentOptions() => Options.Create(new AgentOptions
        {
            Name = "test", Role = "role-a", WorkDir = test._workDir, Provider = "gemini", Model = "gemini-4-argon-medium",
        });

        private PromptBuilder Prompt(IOptions<AgentOptions> options) =>
            new(options, NullLogger<PromptBuilder>.Instance) { ContentRoot = _contentRoot };

        public void ChangeInstructions() => SetInstructions($"Synthetic instructions, version {++_instructionsVersion}.\nSecond line.\n");

        public void SetInstructions(string text)
        {
            var path = Path.Combine(_contentRoot, "roles", "role-a", "system.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public GeminiExecutor Executor(UsageReporter? usage = null, TimeSpan? exitWait = null)
        {
            if (!Directory.Exists(_contentRoot))
                ChangeInstructions();
            var options = AgentOptions();
            return new GeminiExecutor(
                options, Prompt(options), Log, StartScript, usage: usage, geminiHome: test._geminiHome, exitWait: exitWait);
        }

        public async Task<List<AgentProgress>> RunAsync(GeminiExecutor executor, string task, params string[] scripts)
        {
            foreach (var script in scripts)
                _scripts.Enqueue(script);
            var progress = new List<AgentProgress>();
            await foreach (var p in executor.ExecuteAsync(task))
                progress.Add(p);
            return progress;
        }

        private Process? StartScript(ProcessStartInfo original)
        {
            Assert.Equal("agy", original.FileName);
            var capture = Path.Combine(test._root, $"input-{Starts.Count}.ndjson");
            Starts.Add(new Start([.. original.ArgumentList], capture));
            var psi = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = original.WorkingDirectory,
            };
            psi.Environment["CAPTURE"] = capture;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(_scripts.Count > 0 ? _scripts.Dequeue() : "echo 'no script for this start' >&2; exit 97");
            return Process.Start(psi);
        }
    }
}
