using System.Text;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// #460: <see cref="ClaudeExecutor"/> appends the harness plan's arguments, exactly as
/// <see cref="HarnessDefaults.Build"/> computed them (AC-X3 parity on the agent side), and restarts
/// once without <c>harnessExtra</c> when the CLI exits on its own before <c>system/init</c> (AC-X4) —
/// never after a kill Fleet asked for.
/// </summary>
public sealed class ClaudeExecutorArgsDefaultsTests
{
    private static readonly string WorkDir = CreateWorkDir();

    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "claude-harness-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static AgentOptions Options(string? baseUrl = null, string? extra = null) => new()
    {
        Name = "agent1",
        Role = "developer",
        WorkDir = WorkDir,
        Provider = "claude",
        Model = baseUrl is null ? "claude-model-x" : "qwen3.8:27b",
        AnthropicBaseUrl = baseUrl,
        HarnessExtra = extra,
    };

    private static ClaudeExecutor Executor(AgentOptions options, ILogger? harnessLog = null,
        ILogger<ClaudeExecutor>? executorLog = null)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        return new ClaudeExecutor(wrapped, executorLog ?? NullLogger<ClaudeExecutor>.Instance,
            new PromptBuilder(wrapped, NullLogger<PromptBuilder>.Instance),
            new AgentHarness(options, _ => null, harnessLog ?? NullLogger.Instance));
    }

    /// <summary>The tail BuildArgs appends after the system prompt file, quoted as the CLI receives it.</summary>
    private static string Tail(ClaudeExecutor executor)
    {
        var args = executor.BuildArgsForTests();
        const string marker = "system-prompt.md\" ";
        return args[(args.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
    }

    private static string Quoted(IReadOnlyList<string> args) =>
        string.Join(' ', args.Select(a => a.StartsWith('-') ? a : $"\"{a}\""));

    [Fact]
    public void Local_AppendsThePlanArgs_Last_WithEachValueQuoted()
    {
        var executor = Executor(Options("http://inference-host:11434"));

        Assert.Equal(
            "--tools \"Agent,Task,ToolSearch,Skill,TodoWrite,TaskStop,SendMessage,Bash,Edit,Glob,Grep,Read,Write\" "
            + "--disallowedTools \"WebSearch\" --exclude-dynamic-system-prompt-sections --strict-mcp-config",
            Tail(executor));
        Assert.Equal(Quoted(executor.HarnessForTests.Plan!.ClaudeArgs!), Tail(executor));
    }

    [Fact]
    public void Extras_ReachTheArgs()
    {
        var executor = Executor(Options(extra: "--tools NotebookEdit\n--disallowedTools WebSearch\n!--exclude-dynamic-system-prompt-sections"));

        Assert.Equal(
            "--tools \"Agent,Task,ToolSearch,Skill,TodoWrite,TaskStop,SendMessage,WebFetch,WebSearch,Bash,Edit,Glob,Grep,Read,Write,NotebookEdit\" "
            + "--disallowedTools \"WebSearch\" --strict-mcp-config",
            Tail(executor));
    }

    [Fact]
    public async Task ExitBeforeInit_WithAnExtra_RestartsOnceWithoutIt()
    {
        var log = new CapturingLogger<AgentHarness>();
        var executor = Executor(Options(extra: "--tools NotebookEdit"), log);
        Assert.Contains("NotebookEdit", Tail(executor));

        // The CLI wrote nothing and its stdout ended: the reader marks an exit before system/init.
        await executor.RunStdoutReaderForTests(new StreamReader(new MemoryStream()));
        Assert.True(executor.ExitedBeforeInitForTests);

        executor.FallBackIfExitedBeforeInitForTests();

        Assert.False(executor.ExitedBeforeInitForTests);
        Assert.DoesNotContain("NotebookEdit", Tail(executor));
        Assert.Equal(HarnessDefaults.ExtraNone, executor.HarnessForTests.Plan!.ExtraState);
        Assert.Contains("harnessExtra", Assert.Single(log.Entries, e => e.Level == LogLevel.Error).Message);
    }

    /// <summary>A real child the executor treats as its claude process.</summary>
    private static System.Diagnostics.Process StartChild(ClaudeExecutor executor, string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") // hygiene-ok: OS stand-in, not the claude CLI
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        var process = System.Diagnostics.Process.Start(psi)!;
        executor.SetProcessForTests(process);
        return process;
    }

    /// <summary>A real child whose stdout the executor's reader follows, as EnsureProcess wires it.</summary>
    private static (System.Diagnostics.Process Process, Task Reader) Attach(ClaudeExecutor executor, string script)
    {
        var process = StartChild(executor, script);
        return (process, executor.RunStdoutReaderForTests(process.StandardOutput));
    }

    /// <summary>
    /// Stdout that reports end of stream only when released and never throws for the cancel — the
    /// case where the reader sees EOF after Fleet's kill instead of observing the cancel. (A plain
    /// StreamReader turns the cancelled token into an exception, which hides the case.)
    /// </summary>
    private sealed class EofAfterKillReader(Task released) : StreamReader(Stream.Null)
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await released;
            return null;
        }
    }

    [Theory]
    [InlineData(false)]   // the reader observes the cancel
    [InlineData(true)]    // the reader sees EOF only after the kill
    public async Task IntentionalKillBeforeInit_KeepsTheExtra(bool eofAfterKill)
    {
        var log = new CapturingLogger<AgentHarness>();
        var executorLog = new CapturingLogger<ClaudeExecutor>();
        var executor = Executor(Options(extra: "--tools NotebookEdit"), log, executorLog);
        var release = new TaskCompletionSource();
        Task reader;
        if (eofAfterKill)
        {
            StartChild(executor, "sleep 30");
            reader = executor.RunStdoutReaderForTests(new EofAfterKillReader(release.Task));
        }
        else
        {
            reader = Attach(executor, "sleep 30").Reader;
        }

        // A cancel or a restart: Fleet kills a live process that has not reached system/init yet.
        await executor.KillProcessForTestsAsync();
        release.SetResult();
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
        // The reader ended on end-of-stream or the cancel, never on an exception.
        Assert.DoesNotContain(executorLog.Entries, e => e.Message.Contains("exited unexpectedly"));
        executor.FallBackIfExitedBeforeInitForTests();

        Assert.False(executor.ExitedBeforeInitForTests);
        Assert.Contains("NotebookEdit", Tail(executor));
        Assert.Equal(HarnessDefaults.ExtraApplied, executor.HarnessForTests.Plan!.ExtraState);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task UnpromptedExitBeforeInit_SurvivesTheCleanupKill_AndFallsBack()
    {
        var executor = Executor(Options(extra: "--tools NotebookEdit"));
        var (process, reader) = Attach(executor, "exit 3");

        await reader.WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync();
        Assert.True(executor.ExitedBeforeInitForTests);

        // ExecuteAsync cleans up a dead process with the same kill before it retries.
        await executor.KillProcessForTestsAsync();
        Assert.True(executor.ExitedBeforeInitForTests);

        executor.FallBackIfExitedBeforeInitForTests();
        Assert.DoesNotContain("NotebookEdit", Tail(executor));
    }

    [Fact]
    public async Task ExitAfterInit_IsNotAStartFailure()
    {
        var executor = Executor(Options(extra: "--tools NotebookEdit"));
        var init = """{"type":"system","subtype":"init","session_id":"s1","tools":["Task"]}""" + "\n";

        await executor.RunStdoutReaderForTests(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(init))));
        executor.FallBackIfExitedBeforeInitForTests();

        Assert.False(executor.ExitedBeforeInitForTests);
        Assert.Contains("NotebookEdit", Tail(executor));
        Assert.Equal(HarnessDefaults.ExtraApplied, executor.HarnessForTests.Plan!.ExtraState);
    }
}
