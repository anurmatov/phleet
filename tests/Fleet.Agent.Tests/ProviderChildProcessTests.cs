using System.Diagnostics;
using System.Text;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

/// <summary>
/// The child-process mechanics the three provider executors share
/// (<see cref="ProviderChildProcess"/>): what the start info redirects, when a line pump reports
/// that the stream ended on its own, and what a kill reports.
/// </summary>
public sealed class ProviderChildProcessTests
{
    private const string Shell = "/bin/sh"; // hygiene-ok: OS stand-in, not a provider CLI

    private static StreamReader Lines(string text) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    private static Process StartShell(string script)
    {
        var psi = ProviderChildProcess.CreateStartInfo(Shell, Path.GetTempPath());
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        return ProviderChildProcess.Start(Process.Start, psi, "Failed to start the stand-in shell");
    }

    /// <summary>A reader whose read fails, as a pipe does when its process object is disposed under it.</summary>
    private sealed class FailingReader : StreamReader
    {
        public FailingReader() : base(Stream.Null) { }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            throw new IOException("the pipe is gone");
    }

    /// <summary>A reader that only ends when its token is cancelled.</summary>
    private sealed class BlockedReader : StreamReader
    {
        public BlockedReader() : base(Stream.Null) { }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartInfo_RedirectsAllThreeStreams_WithoutAShell(bool createNoWindow)
    {
        var psi = ProviderChildProcess.CreateStartInfo("provider-cli", "/workspace/example", createNoWindow);

        Assert.Equal("provider-cli", psi.FileName);
        Assert.Equal("/workspace/example", psi.WorkingDirectory);
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(createNoWindow, psi.CreateNoWindow);
        Assert.Equal("", psi.Arguments);
        Assert.Empty(psi.ArgumentList);
    }

    [Fact]
    public void Start_ThrowsTheGivenFailure_WhenTheStarterReturnsNoProcess()
    {
        var psi = ProviderChildProcess.CreateStartInfo("provider-cli", "/workspace/example");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProviderChildProcess.Start(_ => null, psi, "Failed to start provider-cli"));

        Assert.Equal("Failed to start provider-cli", ex.Message);
    }

    [Fact]
    public async Task ReadLines_HandsOverNonBlankLines_AndReportsTheStreamEndedOnItsOwn()
    {
        var seen = new List<string>();

        var endedOnItsOwn = await ProviderChildProcess.ReadLinesAsync(
            Lines("one\n\n   \ntwo\n"),
            line => { seen.Add(line); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.True(endedOnItsOwn);
        Assert.Equal(["one", "two"], seen);
    }

    [Fact]
    public async Task ReadLines_ACancelledRead_IsNotAnEndOfStream_AndDoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var pump = ProviderChildProcess.ReadLinesAsync(
            new BlockedReader(), _ => ValueTask.CompletedTask, cts.Token);

        await cts.CancelAsync();

        Assert.False(await pump.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ReadLines_AnAlreadyCancelledToken_ReadsNothing()
    {
        var seen = new List<string>();

        var endedOnItsOwn = await ProviderChildProcess.ReadLinesAsync(
            Lines("one\n"),
            line => { seen.Add(line); return ValueTask.CompletedTask; },
            new CancellationToken(canceled: true));

        Assert.False(endedOnItsOwn);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task ReadLines_AFailureInTheHandlerOrTheRead_ReachesTheCaller()
    {
        await Assert.ThrowsAsync<FormatException>(() => ProviderChildProcess.ReadLinesAsync(
            Lines("one\n"), _ => throw new FormatException("bad line"), CancellationToken.None));

        await Assert.ThrowsAsync<IOException>(() => ProviderChildProcess.ReadLinesAsync(
            new FailingReader(), _ => ValueTask.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task PumpStderr_HandsOverEveryLine_BlankOnesIncluded()
    {
        var seen = new List<string>();

        await ProviderChildProcess.PumpStderr(Lines("warn\n\nagain\n"), seen.Add)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["warn", "", "again"], seen);
    }

    [Fact]
    public async Task PumpStderr_EndsQuietly_OnAReadFailure_AndOnCancel()
    {
        await ProviderChildProcess.PumpStderr(new FailingReader(), _ => { })
            .WaitAsync(TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource();
        var pump = ProviderChildProcess.PumpStderr(new BlockedReader(), _ => { }, cts.Token);
        await cts.CancelAsync();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Kill_StopsARunningProcess_AndSaysItWasRunning()
    {
        using var process = StartShell("sleep 30");
        var announced = 0;

        var wasRunning = await ProviderChildProcess.KillAsync(process, () => announced++)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(wasRunning);
        Assert.Equal(1, announced);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Kill_OfAProcessThatAlreadyExited_KillsNothing_AndSaysSo()
    {
        using var process = StartShell("exit 0");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var announced = 0;

        var wasRunning = await ProviderChildProcess.KillAsync(process, () => announced++)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(wasRunning);
        Assert.Equal(0, announced);
    }

    [Fact]
    public async Task Kill_OfADisposedProcess_ThrowsForTheExecutorToDecide()
    {
        var process = StartShell("exit 0");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        process.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ProviderChildProcess.KillAsync(process));
    }

    [Fact]
    public async Task KillAndConfirmExit_ConfirmsARunningProcessIsGone_AndNeverThrows()
    {
        using var process = StartShell("sleep 30");

        Assert.True(await ProviderChildProcess.KillAndConfirmExitAsync(process, TimeSpan.FromSeconds(10)));
        Assert.True(process.HasExited);

        var disposed = StartShell("exit 0");
        await disposed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        disposed.Dispose();

        Assert.False(await ProviderChildProcess.KillAndConfirmExitAsync(disposed, TimeSpan.FromSeconds(1)));
    }
}
