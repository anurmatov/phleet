using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Fleet.Shared;

namespace Fleet.Agent.Services;

/// <summary>
/// One command's local processing deadline (#455 §3.6): every image downscale and PDF tool call of
/// one <c>prompt</c> or <c>steer</c> shares it, and each call gets at most what is left of it.
/// </summary>
internal sealed class PiProcessingBudget(TimeSpan total)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public TimeSpan Total { get; } = total;

    public TimeSpan Elapsed => _clock.Elapsed;

    public TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, (Total - _clock.Elapsed).Ticks));

    public bool Expired => Remaining <= TimeSpan.Zero;
}

/// <summary>
/// What one tool call returned. <see cref="Stdout"/> holds at most the caller's cap; the stderr tail
/// is for classification only and is never logged (it can name the file).
/// </summary>
internal sealed record PiToolResult(
    int ExitCode, byte[] Stdout, string StderrTail, bool TimedOut, bool Capped, bool Unavailable)
{
    public static PiToolResult Expired { get; } = new(-1, [], "", TimedOut: true, Capped: false, Unavailable: false);
}

/// <summary>The local tool runner <see cref="PiAttachments"/> and <see cref="PiPdfExtractor"/> use; injectable for tests.</summary>
internal interface IPiLocalTools
{
    /// <summary>
    /// Runs <paramref name="tool"/> with <paramref name="args"/> under the §3.3 isolation. The call
    /// times out at min(per-call timeout, what is left of <paramref name="budget"/>); an expired
    /// budget starts nothing. Cancellation kills the process tree and throws.
    /// </summary>
    Task<PiToolResult> RunAsync(
        string tool, IReadOnlyList<string> args, PiProcessingBudget budget, int stdoutCap, CancellationToken ct);
}

/// <summary>
/// The isolated runner for <c>pdfinfo</c>, <c>pdftotext</c>, <c>pdftoppm</c> and <c>ffmpeg</c>
/// (#455 §3.3, MUST NOT 4): no shell, an argument list, <c>prlimit</c> address-space, CPU and
/// open-file limits, a wall timeout, an stdout cap, the credential-stripped environment plus
/// <c>MALLOC_ARENA_MAX=2</c>, and a private temporary working directory. Timeout, cap and
/// cancellation kill the whole process tree.
/// </summary>
/// <remarks>
/// The inputs are untrusted files handed to C parsers; these limits bound what a hostile file can
/// cost. They are not a sandbox: the tools run as the agent's user, like the model's own shell.
/// </remarks>
internal sealed class PiLocalTools : IPiLocalTools
{
    public const string PrlimitBinary = "prlimit";

    /// <summary>util-linux <c>prlimit</c>'s exit when it cannot execute the command (127: not found).</summary>
    private const int ExecNotFound = 127;
    private const int ExecFailed = 126;

    private const int StderrTailChars = 2048;

    private readonly TimeSpan _callTimeout;
    private readonly string _prlimit;
    private readonly IReadOnlyDictionary<string, string>? _baseEnvironment;

    public PiLocalTools(
        TimeSpan? callTimeout = null, string prlimit = PrlimitBinary,
        IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        _callTimeout = callTimeout ?? PiLocalModel.LocalToolCallTimeout;
        _prlimit = prlimit;
        _baseEnvironment = baseEnvironment;
    }

    /// <summary>The start info for one call, exactly as run. Exposed for the argument tests.</summary>
    public ProcessStartInfo BuildStartInfo(string tool, IReadOnlyList<string> args, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _prlimit,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add($"--as={PiLocalModel.LocalToolAddressSpaceBytes}");
        psi.ArgumentList.Add($"--cpu={PiLocalModel.LocalToolCpuSeconds}");
        psi.ArgumentList.Add($"--nofile={PiLocalModel.LocalToolOpenFiles}");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(tool);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (_baseEnvironment is { } environment)
        {
            psi.Environment.Clear();
            foreach (var (name, value) in environment)
                psi.Environment[name] = value;
        }
        foreach (var name in psi.Environment.Keys.Where(PiLocalModel.IsCredentialEnvName).ToList())
            psi.Environment.Remove(name);
        // One malloc arena per thread would count against --as on a many-core host.
        psi.Environment["MALLOC_ARENA_MAX"] = "2";
        return psi;
    }

    public async Task<PiToolResult> RunAsync(
        string tool, IReadOnlyList<string> args, PiProcessingBudget budget, int stdoutCap, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var timeout = budget.Remaining < _callTimeout ? budget.Remaining : _callTimeout;
        if (timeout <= TimeSpan.Zero)
            return PiToolResult.Expired;

        var workDir = Directory.CreateTempSubdirectory("fleet-pi-tool-").FullName;
        try
        {
            Process process;
            try
            {
                process = Process.Start(BuildStartInfo(tool, args, workDir))
                          ?? throw new InvalidOperationException($"{_prlimit} did not start");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // prlimit itself is missing.
                return new PiToolResult(-1, [], "", TimedOut: false, Capped: false, Unavailable: true);
            }

            using (process)
            {
                process.StandardInput.Close();
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timer.CancelAfter(timeout);

                var stderr = ReadTailAsync(process.StandardError, CancellationToken.None);
                using var stdout = new MemoryStream();
                var capped = false;
                var timedOut = false;
                try
                {
                    capped = await ReadCappedAsync(process.StandardOutput.BaseStream, stdout, stdoutCap, timer.Token);
                    if (capped)
                        KillTree(process);
                    await process.WaitForExitAsync(timer.Token);
                }
                catch (OperationCanceledException)
                {
                    KillTree(process);
                    if (ct.IsCancellationRequested)
                        throw;
                    timedOut = true;
                }

                await WaitForExitAfterKillAsync(process);
                var tail = await stderr;
                var exitCode = process.HasExited ? process.ExitCode : -1;
                var unavailable = !timedOut && !capped && exitCode is ExecNotFound or ExecFailed
                                  && tail.Contains("failed to execute", StringComparison.Ordinal);
                // On a timeout the partial output is returned too; callers drop it (§3.3 step 4).
                return new PiToolResult(exitCode, stdout.ToArray(), tail, timedOut, capped, unavailable);
            }
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>Reads up to <paramref name="cap"/> bytes into <paramref name="buffer"/>; true when more was there.</summary>
    private static async Task<bool> ReadCappedAsync(Stream stream, MemoryStream buffer, int cap, CancellationToken ct)
    {
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
                return false;

            var room = cap - (int)buffer.Length;
            if (read > room)
            {
                buffer.Write(chunk, 0, room);
                return true;
            }
            buffer.Write(chunk, 0, read);
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader, CancellationToken ct)
    {
        var tail = new StringBuilder();
        var chunk = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(chunk, ct)) > 0)
            {
                tail.Append(chunk, 0, read);
                if (tail.Length > StderrTailChars)
                    tail.Remove(0, tail.Length - StderrTailChars);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        return tail.ToString();
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
