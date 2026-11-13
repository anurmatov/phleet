using System.Diagnostics;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// Each claude process generation owns its stdout stream and its event channel. A kill does not
/// wait for the old process's stdout reader, and a process that died on its own is replaced without
/// cancelling it, so that reader can finish after the next process has started. When it does, it
/// may only complete its own channel and may only speak for its own start.
/// </summary>
public sealed class ClaudeExecutorReaderGenerationTests
{
    private const string Init = """{"type":"system","subtype":"init","session_id":"s1","tools":["Task"]}""";
    private const string Answer = """{"type":"assistant","message":{"content":[{"type":"text","text":"answer"}]}}""";

    private static ClaudeExecutor Executor()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions
        {
            Name = "agent1",
            Role = "developer",
            WorkDir = Path.GetTempPath(),
            Provider = "claude",
            Model = "claude-model-x",
        });
        return new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance,
            new PromptBuilder(options, NullLogger<PromptBuilder>.Instance),
            new AgentHarness(options.Value, _ => null, NullLogger.Instance));
    }

    private static Channel<ClaudeStreamEvent> NewChannel() =>
        Channel.CreateUnbounded<ClaudeStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>
    /// A controllable stdout: hands out <paramref name="lines"/>, then stays open until
    /// <see cref="End"/>, whatever happens to the token, then hands out the last lines and reports
    /// end of stream. <see cref="Reading"/> completes once the reader is waiting, so a test can act
    /// while an old reader is still alive.
    /// </summary>
    private sealed class HeldOpenStdout(params string[] lines) : StreamReader(Stream.Null)
    {
        private readonly Queue<string> _lines = new(lines);
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string[]> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _released;

        public Task Reading => _reading.Task;

        public void End(params string[] lastLines) => _release.TrySetResult(lastLines);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_lines.TryDequeue(out var line))
                return line;
            if (_released)
                return null;

            _reading.TrySetResult();
            foreach (var last in await _release.Task)
                _lines.Enqueue(last);
            _released = true;
            return _lines.TryDequeue(out line) ? line : null;
        }
    }

    /// <summary>A real child the executor treats as its claude process.</summary>
    private static Process StartChild(ClaudeExecutor executor)
    {
        var psi = new ProcessStartInfo("/bin/sh") // hygiene-ok: OS stand-in, not the claude CLI
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 30");
        var process = Process.Start(psi)!;
        executor.SetProcessForTests(process);
        return process;
    }

    /// <summary>
    /// Takes every buffered event out of the channel and checks what was there and whether the
    /// channel is closed. A channel's completion is only visible once it is empty, so the two are
    /// checked together.
    /// </summary>
    private static void AssertDrained(Channel<ClaudeStreamEvent> channel, bool closed, params string[] expected)
    {
        var types = new List<string>();
        while (channel.Reader.TryRead(out var evt))
            types.Add(evt.Subtype is null ? evt.Type : $"{evt.Type}/{evt.Subtype}");

        Assert.Equal(expected, types);
        Assert.Equal(closed, channel.Reader.Completion.IsCompleted);
    }

    /// <summary>Starts a reader for one generation, as EnsureProcess does: its own channel, its own stdout.</summary>
    private static async Task<(Channel<ClaudeStreamEvent> Channel, Task Reader)> StartGenerationAsync(
        ClaudeExecutor executor, HeldOpenStdout stdout, CancellationToken ct = default)
    {
        var channel = NewChannel();
        executor.SetEventChannelForTests(channel);
        var reader = executor.RunStdoutReaderForTests(stdout, ct);
        await stdout.Reading.WaitAsync(TimeSpan.FromSeconds(10));
        return (channel, reader);
    }

    [Fact]
    public async Task AKilledProcesssReader_FinishingAfterTheRestart_LeavesTheNewProcesssChannelOpen()
    {
        var executor = Executor();
        using var oldProcess = StartChild(executor);
        var oldStdout = new HeldOpenStdout();
        var (oldChannel, oldReader) = await StartGenerationAsync(executor, oldStdout);

        // The kill cancels the old reader and moves on without waiting for it to stop.
        await executor.KillProcessForTestsAsync();
        Assert.False(oldReader.IsCompleted);

        // The restart: a new process, a new channel, a new reader.
        using var newProcess = StartChild(executor);
        var newStdout = new HeldOpenStdout(Init);
        var (newChannel, newReader) = await StartGenerationAsync(executor, newStdout);

        // Only now does the old process's reader see the end of its stdout.
        oldStdout.End();
        await oldReader.WaitAsync(TimeSpan.FromSeconds(10));

        AssertDrained(oldChannel, closed: true);
        AssertDrained(newChannel, closed: false, "system/init");
        Assert.False(executor.ExitedBeforeInitForTests);

        // The new process is still readable: what it says next arrives, and only its own end closes it.
        newStdout.End(Answer);
        await newReader.WaitAsync(TimeSpan.FromSeconds(10));
        AssertDrained(newChannel, closed: true, "assistant");

        await executor.KillProcessForTestsAsync();
    }

    [Fact]
    public async Task AProcessThatDiedOnItsOwn_ItsReaderFinishingAfterTheRestart_LeavesTheNewProcesssChannelOpen()
    {
        var executor = Executor();
        var oldStdout = new HeldOpenStdout(Init);
        var (oldChannel, oldReader) = await StartGenerationAsync(executor, oldStdout);

        // The process died on its own, so nothing cancelled its reader before the restart.
        var newStdout = new HeldOpenStdout();
        var (newChannel, newReader) = await StartGenerationAsync(executor, newStdout);

        oldStdout.End();
        await oldReader.WaitAsync(TimeSpan.FromSeconds(10));

        AssertDrained(oldChannel, closed: true, "system/init");
        AssertDrained(newChannel, closed: false);
        // The old process reached its init, so its end is not a failure at start.
        Assert.False(executor.ExitedBeforeInitForTests);

        newStdout.End(Init, Answer);
        await newReader.WaitAsync(TimeSpan.FromSeconds(10));
        AssertDrained(newChannel, closed: true, "system/init", "assistant");
    }

    [Fact]
    public async Task ExitBeforeInit_IsJudgedPerProcess_NotByAnEarlierProcesssInit()
    {
        var executor = Executor();

        // The first process reaches its init and later ends.
        var first = new HeldOpenStdout(Init);
        var (_, firstReader) = await StartGenerationAsync(executor, first);
        first.End();
        await firstReader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(executor.ExitedBeforeInitForTests);

        // The second process ends before its own init: that is the failure at start.
        var second = new HeldOpenStdout();
        var (_, secondReader) = await StartGenerationAsync(executor, second);
        second.End();
        await secondReader.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(executor.ExitedBeforeInitForTests);
    }

    [Fact]
    public async Task EventsOfOneProcess_NeverLandInAnotherProcesssChannel()
    {
        var executor = Executor();
        var oldStdout = new HeldOpenStdout();
        var (oldChannel, oldReader) = await StartGenerationAsync(executor, oldStdout);

        var newStdout = new HeldOpenStdout();
        var (newChannel, newReader) = await StartGenerationAsync(executor, newStdout);

        // The old process's last buffered line is read only after the new process has started.
        oldStdout.End(Answer);
        await oldReader.WaitAsync(TimeSpan.FromSeconds(10));

        AssertDrained(oldChannel, closed: true, "assistant");
        AssertDrained(newChannel, closed: false);

        newStdout.End();
        await newReader.WaitAsync(TimeSpan.FromSeconds(10));
        AssertDrained(newChannel, closed: true);
    }
}
