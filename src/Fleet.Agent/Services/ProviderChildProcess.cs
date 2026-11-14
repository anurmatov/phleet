using System.Diagnostics;

namespace Fleet.Agent.Services;

/// <summary>
/// The child-process mechanics the three provider executors share: the redirected start, the
/// stderr pump, the stdout line pump and the kill.
/// </summary>
/// <remarks>
/// <para>What a provider says on its streams, how a turn is cancelled and what a session is stay in
/// each executor; they differ by provider. Only the plumbing lives here.</para>
/// <para>Nothing here reads, copies or writes <see cref="ProcessStartInfo.Environment"/>. The child's
/// environment is each executor's own decision (the claude child's <c>ANTHROPIC_*</c>, the codex
/// child's <c>CODEX_OSS_BASE_URL</c> and stripped key names), and an executor that sets nothing must
/// keep handing over a start info whose environment was never touched.</para>
/// <para>The pumps take the stream they read as an argument and hold no executor state, so a pump
/// started for one process generation can only ever act on that generation's stream.</para>
/// </remarks>
internal static class ProviderChildProcess
{
    /// <summary>A start info with stdin, stdout and stderr redirected and no shell.</summary>
    internal static ProcessStartInfo CreateStartInfo(
        string fileName, string workingDirectory, bool createNoWindow = true) => new()
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = createNoWindow,
    };

    /// <summary>Starts the child, or throws <paramref name="failure"/> when the starter returns none.</summary>
    internal static Process Start(
        Func<ProcessStartInfo, Process?> starter, ProcessStartInfo startInfo, string failure) =>
        starter(startInfo) ?? throw new InvalidOperationException(failure);

    /// <summary>
    /// Hands every non-blank line of <paramref name="reader"/> to <paramref name="onLine"/> until the
    /// stream ends or <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <returns>
    /// True when the stream ended on its own (the process closed it); false when the read was
    /// cancelled. A cancelled read never throws; any other read failure, and anything
    /// <paramref name="onLine"/> throws, reaches the caller.
    /// </returns>
    internal static async Task<bool> ReadLinesAsync(
        StreamReader reader, Func<string, ValueTask> onLine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (line is null) return true;
            if (string.IsNullOrWhiteSpace(line)) continue;

            await onLine(line);
        }

        return false;
    }

    /// <summary>
    /// Reads <paramref name="stderr"/> on a background task and hands each line, blank ones
    /// included, to <paramref name="onLine"/>. Stderr is diagnostics only: the task ends quietly on
    /// end of stream, on cancel and on any read failure.
    /// </summary>
    internal static Task PumpStderr(StreamReader stderr, Action<string> onLine, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await stderr.ReadLineAsync(ct);
                    if (line is null) break;
                    onLine(line);
                }
            }
            catch
            {
                // Diagnostics only.
            }
        }, CancellationToken.None);

    /// <summary>
    /// Kills the process tree when the process is still running, then waits for the exit.
    /// </summary>
    /// <returns>Whether the process was still running, that is, whether this call killed it.</returns>
    /// <remarks>
    /// Throws what <see cref="Process"/> throws (a disposed or never-started process, a tree that
    /// could not be terminated); each executor keeps its own rule for those.
    /// </remarks>
    internal static async Task<bool> KillAsync(Process process, Action? beforeKill = null)
    {
        var wasRunning = !process.HasExited;
        if (wasRunning)
        {
            beforeKill?.Invoke();
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
        return wasRunning;
    }

    /// <summary>
    /// Kills the process tree and waits at most <paramref name="exitWait"/> for the exit. Never
    /// throws. True only when the exit is confirmed.
    /// </summary>
    internal static async Task<bool> KillAndConfirmExitAsync(Process process, TimeSpan exitWait)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* the exit check below decides */ }

        try
        {
            using var wait = new CancellationTokenSource(exitWait);
            await process.WaitForExitAsync(wait.Token);
        }
        catch { /* not confirmed within the bound */ }

        try { return process.HasExited; }
        catch { return false; }
    }
}
