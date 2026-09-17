using System.Diagnostics;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// The <c>/bin/cat</c> stand-in OS process used by the Claude and Codex L1 replays (D2).
///
/// <para>This is a STAND-IN, not the provider. <c>ClaudeExecutor.ExecuteAsync</c> and
/// <c>CodexExecutor.TryInjectMessageAsync</c> both refuse to run without a live process and a
/// writable stdin, so a replay that drives their real code path needs *an* OS process — it does
/// not need, and never starts, a provider CLI. That is why D2 words L1 as "no provider process"
/// rather than "no process".</para>
///
/// <para>Extracted from the two copies that previously lived in
/// <c>ClaudeExecutorTerminalResultTests</c> and <c>CodexExecutorTests</c>. A third copy would be a
/// maintenance defect.</para>
///
/// <para>Absence of the stand-in is a FAILURE, never a skip: the constructor asserts the path
/// exists so the failure names its cause instead of surfacing later as an opaque timeout. A
/// skipped provider is an unmeasured provider reported as green.</para>
/// </summary>
internal sealed class StandInProcess : IDisposable
{
    /// <summary>The stand-in binary. Absolute by necessity — it is an OS path, not fixture content.</summary>
    public const string ExecutablePath = "/bin/cat"; // hygiene-ok: OS stand-in binary, not provider data

    public StandInProcess()
    {
        if (!File.Exists(ExecutablePath))
        {
            throw new InvalidOperationException(
                $"The stand-in process '{ExecutablePath}' is required by the L1 replay and was not found. " +
                "This is a hard failure by design: a skipped provider is an unmeasured provider reported as green.");
        }

        Process = Process.Start(new ProcessStartInfo
        {
            FileName = ExecutablePath,
            RedirectStandardInput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start the stand-in process '{ExecutablePath}'.");
    }

    public Process Process { get; }

    /// <summary>The stand-in's stdin, for executors that write their injection frame to it.</summary>
    public StreamWriter StandardInput => Process.StandardInput;

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Teardown only — a stand-in that already exited is not an error.
        }

        Process.Dispose();
    }
}
