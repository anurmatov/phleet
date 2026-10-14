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
/// once without <c>harnessExtra</c> when the CLI exits before <c>system/init</c> (AC-X4).
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

    private static ClaudeExecutor Executor(AgentOptions options, ILogger? harnessLog = null)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        return new ClaudeExecutor(wrapped, NullLogger<ClaudeExecutor>.Instance,
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
