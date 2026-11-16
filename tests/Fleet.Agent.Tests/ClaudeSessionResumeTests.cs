using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// A Claude agent keeps its session across container restarts while its instructions are the same,
/// and starts a new one when they are not. Every test drives the real <see cref="ClaudeExecutor"/>
/// start path against a stand-in child (<c>/bin/sh</c>) that speaks just enough stream-json: the
/// arguments the executor built, the lines it wrote and the session file it left are what is
/// asserted. No claude CLI is started.
/// </summary>
public sealed class ClaudeSessionResumeTests : IAsyncLifetime
{
    private const string Shell = "/bin/sh"; // hygiene-ok: OS stand-in binary, not the claude CLI
    private const string Instructions = "You are a synthetic agent.\n";

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"fleet-claude-session-{Guid.NewGuid():N}");
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"fleet-claude-roles-{Guid.NewGuid():N}");
    private readonly List<ClaudeExecutor> _executors = [];
    private readonly List<Process> _children = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workDir);
        WriteInstructions(Instructions);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var executor in _executors)
            await executor.DisposeAsync();
        foreach (var child in _children)
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { /* teardown only */ }
        }
        try { Directory.Delete(_workDir, recursive: true); } catch { /* teardown only */ }
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* teardown only */ }
    }

    // ── the start rule ───────────────────────────────────────────────────────

    [Fact]
    public async Task WithNoFile_TheSessionIsNew_AndTheFileIsWrittenWithItsIdAndTheInstructionsHash()
    {
        var agent = Agent();

        var answer = await agent.TurnAsync("first task");

        Assert.Equal("answer from sid-1", answer.FinalResult);
        Assert.DoesNotContain("--resume", Assert.Single(agent.Starts));
        Assert.Equal("new", agent.Executor.SessionOrigin);
        Assert.Contains(agent.Log(), line => line == "Claude session new: no file");

        var file = ReadSessionFile();
        Assert.Equal("claude", (string?)file["provider"]);
        Assert.Equal("sid-1", (string?)file["sessionId"]);
        // The hash is of the bytes claude was given through --append-system-prompt-file.
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_workDir, "system-prompt.md")))).ToLowerInvariant(),
            (string?)file["instructionsSha256"]);
        Assert.Equal(["session.json"], Directory.GetFiles(Path.Combine(_workDir, ".fleet")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task WithTheSameInstructions_TheSessionIsResumed_AndTheFileStaysAsItWas()
    {
        SeedSessionFile("sid-old", HashOf(Instructions));
        var before = File.ReadAllText(SessionFilePath);
        var agent = Agent();

        var answer = await agent.TurnAsync("first task");

        Assert.Equal("answer from sid-old", answer.FinalResult);
        Assert.Contains("--resume \"sid-old\"", Assert.Single(agent.Starts));
        Assert.Equal("resumed", agent.Executor.SessionOrigin);
        Assert.Contains(agent.Log(), line => line == "Claude session resumed: sid-old");
        Assert.DoesNotContain(agent.Log(), line => line.StartsWith("Claude session new", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(SessionFilePath));
    }

    /// <summary>The whole round: a session started in one container is the one the next container resumes.</summary>
    [Fact]
    public async Task TheSessionOneContainerStarted_IsResumedByTheNext()
    {
        var first = Agent();
        await first.TurnAsync("first task");
        await first.Executor.DisposeAsync();

        var second = Agent();
        await second.TurnAsync("second task");

        Assert.Contains("--resume \"sid-1\"", Assert.Single(second.Starts));
        Assert.Equal("resumed", second.Executor.SessionOrigin);
    }

    [Theory]
    [InlineData("other provider")]
    [InlineData("instructions changed")]
    [InlineData("unreadable file")]
    [InlineData("model runs elsewhere")]
    public async Task AnythingElseInTheFile_IsANewSession_AndTheFileIsReplaced(string reason)
    {
        var hash = HashOf(Instructions);
        switch (reason)
        {
            case "other provider": SeedSessionFile("thread-old", hash, provider: "codex"); break;
            case "instructions changed": SeedSessionFile("sid-old", HashOf(Instructions + "An older rule.\n")); break;
            case "unreadable file": WriteSessionFile("{ this is not a session"); break;
            case "model runs elsewhere": SeedSessionFile("sid-old", hash, runsOn: "local"); break;
        }
        var agent = Agent();

        await agent.TurnAsync("first task");

        Assert.DoesNotContain("--resume", Assert.Single(agent.Starts));
        Assert.Contains(agent.Log(), line => line == $"Claude session new: {reason}");
        Assert.Equal("new", agent.Executor.SessionOrigin);
        var file = ReadSessionFile();
        Assert.Equal("claude", (string?)file["provider"]);
        Assert.Equal("sid-1", (string?)file["sessionId"]);
        Assert.Equal(hash, (string?)file["instructionsSha256"]);
        Assert.Equal("cloud", (string?)file["runsOn"]);
    }

    /// <summary>
    /// The instructions the hash is taken of are the ones THIS start uses: an edit between two
    /// containers is seen by the second one, which then does not resume.
    /// </summary>
    [Fact]
    public async Task EditedInstructions_MakeTheNextContainerStartANewSession()
    {
        var first = Agent();
        await first.TurnAsync("first task");
        await first.Executor.DisposeAsync();
        var firstHash = (string?)ReadSessionFile()["instructionsSha256"];

        WriteInstructions(Instructions + "A new rule.\n");
        var second = Agent();
        await second.TurnAsync("second task");

        Assert.DoesNotContain("--resume", Assert.Single(second.Starts));
        Assert.Contains(second.Log(), line => line == "Claude session new: instructions changed");
        Assert.NotEqual(firstHash, (string?)ReadSessionFile()["instructionsSha256"]);
        Assert.Equal(HashOf(Instructions + "A new rule.\n"), (string?)ReadSessionFile()["instructionsSha256"]);
    }

    // ── a resume that fails ──────────────────────────────────────────────────

    /// <summary>How the stand-in refuses the session it is asked to resume.</summary>
    public enum Refusal { ExitsBeforeInit, ErrorsBeforeInit }

    [Theory]
    [InlineData(Refusal.ExitsBeforeInit)]
    [InlineData(Refusal.ErrorsBeforeInit)]
    public async Task AFailedResume_FallsBackToANewSession_InsideTheSameTurn_AndIsNotTriedAgain(Refusal refusal)
    {
        SeedSessionFile("sid-gone", HashOf(Instructions));
        var agent = Agent(resumeBehaviour: refusal);

        var events = await agent.TurnEventsAsync("first task");

        // One answer, from the new session; the refusal is not passed on as the turn's result.
        var result = Assert.Single(events, e => e.EventType == "result");
        Assert.Equal("answer from sid-1", result.FinalResult);
        Assert.False(result.IsErrorResult);
        Assert.DoesNotContain(events, e => e.IsProcessExit);

        Assert.Equal(2, agent.Starts.Count);
        Assert.Contains("--resume \"sid-gone\"", agent.Starts[0]);
        Assert.DoesNotContain("--resume", agent.Starts[1]);
        Assert.Contains(agent.Log(), line => line.StartsWith("Claude session new: resume failed: ", StringComparison.Ordinal));
        Assert.DoesNotContain(agent.Log(), line => line.StartsWith("Claude session resumed", StringComparison.Ordinal));
        Assert.Equal("new", agent.Executor.SessionOrigin);

        // The file names the new session, and the message that reached it says it is new.
        Assert.Equal("sid-1", (string?)ReadSessionFile()["sessionId"]);
        Assert.Equal(PromptAssembler.WithNewSessionLine("first task"), Assert.Single(agent.MessagesTo(1)));

        // Nothing goes back to the session that failed: the next turn uses the running process.
        await agent.TurnAsync("second task");
        Assert.Equal(2, agent.Starts.Count);
    }

    /// <summary>
    /// The fallback happens once. When the new session cannot start either, the turn ends the way
    /// a dead process always ended it; the session that failed to resume is not asked for again.
    /// </summary>
    [Fact]
    public async Task AFailedResume_IsNeverRetried_EvenWhenTheNewSessionFailsToo()
    {
        SeedSessionFile("sid-gone", HashOf(Instructions));
        var agent = Agent(resumeBehaviour: Refusal.ExitsBeforeInit, newSessionExits: true);

        var events = await agent.TurnEventsAsync("first task");

        Assert.Contains(events, e => e.IsProcessExit);
        Assert.Single(agent.Starts, args => args.Contains("--resume", StringComparison.Ordinal));
        Assert.Equal(3, agent.Starts.Count);
        Assert.False(File.Exists(SessionFilePath));
    }

    /// <summary>
    /// What stayed as it was: a process that dies in the middle of a turn is started again with
    /// --resume, in the same container, and the turn's message is sent to it.
    /// </summary>
    [Fact]
    public async Task AProcessThatDiesMidTurn_IsRestartedWithResume()
    {
        var agent = Agent(firstProcessDiesAfterInit: true);

        var answer = await agent.TurnAsync("first task");

        Assert.Equal(2, agent.Starts.Count);
        Assert.DoesNotContain("--resume", agent.Starts[0]);
        Assert.Contains("--resume \"sid-1\"", agent.Starts[1]);
        Assert.Equal("answer from sid-1", answer.FinalResult);
        Assert.Equal("new", agent.Executor.SessionOrigin);
    }

    // ── /reset, /stop and a credential restart ───────────────────────────────

    [Fact]
    public async Task StopProcess_RemovesTheFile_AndTheNextSessionIsNew()
    {
        var agent = Agent();
        await agent.TurnAsync("first task");
        Assert.True(File.Exists(SessionFilePath));

        await agent.Executor.StopProcessAsync();

        Assert.False(File.Exists(SessionFilePath));
        Assert.Null(agent.Executor.SessionOrigin);

        await agent.TurnAsync("second task");
        Assert.Equal(2, agent.Starts.Count);
        Assert.DoesNotContain("--resume", agent.Starts[1]);
        Assert.Equal(PromptAssembler.WithNewSessionLine("second task"), Assert.Single(agent.MessagesTo(1)));
    }

    /// <summary>
    /// A token broadcast stops the process too (<c>TryStopProcessAsync</c>), but it is not a wish
    /// for a new session: the next process resumes, and says nothing about a new one.
    /// </summary>
    [Fact]
    public async Task ACredentialRestart_KeepsTheSession()
    {
        var agent = Agent();
        await agent.TurnAsync("first task");

        Assert.True(await agent.Executor.TryStopProcessAsync());

        Assert.True(File.Exists(SessionFilePath));
        await agent.TurnAsync("second task");
        Assert.Equal(2, agent.Starts.Count);
        Assert.Contains("--resume \"sid-1\"", agent.Starts[1]);
        Assert.Equal("second task", Assert.Single(agent.MessagesTo(1)));
    }

    [Theory]
    [InlineData("/reset")]
    [InlineData("/stop")]
    public async Task TheResetAndStopCommands_RemoveTheFile(string command)
    {
        SeedSessionFile("sid-old", HashOf(Instructions));
        var agent = Agent();
        var manager = new TaskManager(
            agent.Options, agent.Executor, new SessionManager(), NullLogger<TaskManager>.Instance);

        if (command == "/reset")
            await manager.HandleReset(chatId: 1);
        else
            await manager.HandleStop(chatId: 1);

        Assert.False(File.Exists(SessionFilePath));
    }

    // ── the new-session line ─────────────────────────────────────────────────

    [Fact]
    public async Task ANewSession_CarriesTheNewSessionLine_OnItsFirstTurnOnly()
    {
        var agent = Agent();

        await agent.TurnAsync("first task");
        await agent.TurnAsync("second task");
        await agent.TurnAsync("third task");

        Assert.Equal(
            [PromptAssembler.WithNewSessionLine("first task"), "second task", "third task"],
            agent.MessagesTo(0));
        Assert.StartsWith(PromptAssembler.NewSessionLine + "\n", agent.MessagesTo(0)[0], StringComparison.Ordinal);
        Assert.DoesNotContain('\n', PromptAssembler.NewSessionLine);
    }

    [Fact]
    public async Task AResumedSession_CarriesNothingExtra()
    {
        SeedSessionFile("sid-old", HashOf(Instructions));
        var agent = Agent();

        await agent.TurnAsync("first task");
        await agent.TurnAsync("second task");

        Assert.Equal(["first task", "second task"], agent.MessagesTo(0));
    }

    // ── rig ──────────────────────────────────────────────────────────────────

    private string SessionFilePath => Path.Combine(_workDir, ".fleet", "session.json");

    private JsonObject ReadSessionFile() => JsonNode.Parse(File.ReadAllText(SessionFilePath))!.AsObject();

    private void WriteSessionFile(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath)!);
        File.WriteAllText(SessionFilePath, content);
    }

    private void SeedSessionFile(string sessionId, string hash, string provider = "claude", string runsOn = "cloud") =>
        WriteSessionFile(new JsonObject
        {
            ["provider"] = provider,
            ["sessionId"] = sessionId,
            ["instructionsSha256"] = hash,
            ["runsOn"] = runsOn,
        }.ToJsonString());

    private void WriteInstructions(string text)
    {
        var dir = Path.Combine(_contentRoot, "roles", PromptBuilder.BaseDirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.md"), text);
    }

    /// <summary>The hash of the system prompt an agent with these instructions is started with.</summary>
    private string HashOf(string instructions)
    {
        var current = File.ReadAllText(Path.Combine(_contentRoot, "roles", PromptBuilder.BaseDirectoryName, "system.md"));
        WriteInstructions(instructions);
        try
        {
            return ProviderSessionFile.HashInstructions(NewPromptBuilder(NewOptions()).BuildSystemPrompt());
        }
        finally
        {
            WriteInstructions(current);
        }
    }

    private IOptions<AgentOptions> NewOptions() => Options.Create(new AgentOptions
    {
        Name = "agent1",
        Role = "developer",
        WorkDir = _workDir,
        Provider = "claude",
        Model = "claude-model-x",
        InstructionOrder = [PromptBuilder.BaseDirectoryName],
    });

    private PromptBuilder NewPromptBuilder(IOptions<AgentOptions> options) =>
        new(options, NullLogger<PromptBuilder>.Instance) { ContentRoot = _contentRoot };

    private StandInAgent Agent(
        Refusal? resumeBehaviour = null, bool newSessionExits = false, bool firstProcessDiesAfterInit = false)
    {
        var options = NewOptions();
        var logger = new RecordingLogger<ClaudeExecutor>();
        var executor = new ClaudeExecutor(options, logger, NewPromptBuilder(options),
            new AgentHarness(options.Value, _ => null, NullLogger.Instance));
        _executors.Add(executor);

        var agent = new StandInAgent(options, executor, logger);
        var newSessions = 0;
        executor.ProcessStarter = startInfo =>
        {
            var index = agent.Starts.Count;
            agent.Starts.Add(startInfo.Arguments);
            var capture = agent.CapturePath(index);

            string script;
            var resumed = System.Text.RegularExpressions.Regex.Match(startInfo.Arguments, "--resume \"([^\"]+)\"");
            if (resumed.Success && resumeBehaviour == Refusal.ExitsBeforeInit)
            {
                script = "echo 'No conversation found with that session ID' >&2; exit 1";
            }
            else if (resumed.Success && resumeBehaviour == Refusal.ErrorsBeforeInit)
            {
                script = "IFS= read -r line; "
                    + "printf '%s\\n' '{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,"
                    + "\"result\":\"No conversation found\",\"session_id\":\"sid-unrelated\"}'; exit 1";
            }
            else if (!resumed.Success && newSessionExits)
            {
                // After the message is read, so the executor's write cannot race the exit.
                script = "IFS= read -r line; exit 1";
            }
            else
            {
                var sessionId = resumed.Success ? resumed.Groups[1].Value : $"sid-{++newSessions}";
                var init = $"{{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{sessionId}\"}}";
                var result = $"{{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,"
                    + $"\"result\":\"answer from {sessionId}\",\"session_id\":\"{sessionId}\"}}";
                script = firstProcessDiesAfterInit && index == 0
                    ? $"IFS= read -r line; printf '%s\\n' \"$line\" >> '{capture}'; printf '%s\\n' '{init}'; exit 1"
                    : $"while IFS= read -r line; do printf '%s\\n' \"$line\" >> '{capture}'; "
                      + $"printf '%s\\n' '{init}'; printf '%s\\n' '{result}'; done";
            }

            var standIn = new ProcessStartInfo(Shell)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            standIn.ArgumentList.Add("-c");
            standIn.ArgumentList.Add(script);
            var child = Process.Start(standIn)
                ?? throw new InvalidOperationException($"Failed to start the stand-in process '{Shell}'.");
            _children.Add(child);
            return child;
        };
        return agent;
    }

    private sealed class StandInAgent(
        IOptions<AgentOptions> options, ClaudeExecutor executor, RecordingLogger<ClaudeExecutor> logger)
    {
        private readonly string _captureBase =
            Path.Combine(options.Value.WorkDir, $"stand-in-stdin-{Guid.NewGuid():N}");

        public IOptions<AgentOptions> Options { get; } = options;
        public ClaudeExecutor Executor { get; } = executor;

        /// <summary>The arguments of every claude start, in order.</summary>
        public List<string> Starts { get; } = [];

        public string CapturePath(int startIndex) => $"{_captureBase}-{startIndex}.jsonl";

        public IReadOnlyList<string> Log()
        {
            lock (logger.Entries) return logger.Entries.Select(e => e.Message).ToList();
        }

        /// <summary>The text of every user message the given start was sent, in order.</summary>
        public IReadOnlyList<string> MessagesTo(int startIndex) =>
            File.Exists(CapturePath(startIndex))
                ? File.ReadAllLines(CapturePath(startIndex))
                    .Select(line => (string)JsonNode.Parse(line)!["message"]!["content"]!)
                    .ToList()
                : [];

        public async Task<List<AgentProgress>> TurnEventsAsync(string task)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var events = new List<AgentProgress>();
            await foreach (var progress in Executor.ExecuteAsync(task, ct: timeout.Token))
                events.Add(progress);
            return events;
        }

        public async Task<AgentProgress> TurnAsync(string task) =>
            Assert.Single(await TurnEventsAsync(task), e => e.EventType == "result");
    }
}
