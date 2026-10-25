using System.Diagnostics;
using System.IO.Pipes;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Fleet.WorkflowOutcomeQualification;

/// <summary>
/// A dedicated Linux session leader stays alive after the target exits. Its reserved
/// PID/session prevents cleanup from depending on the target's parent lifetime.
/// This is owned-process cleanup, not containment of a hostile process escaping its session.
/// </summary>
internal static class OwnedCommandSession
{
    [DllImport("libc", SetLastError = true)] private static extern int setsid();
    [DllImport("libc", SetLastError = true)] private static extern int getsid(int pid);
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int command, int value);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_open(int pid, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_send_signal(int fd, int signal, IntPtr info, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    internal static ProcessStartInfo StartInfo(PlannedCommand command, string pipe)
    {
        if (!OperatingSystem.IsLinux())
            throw new QualificationBlockedException("owned process-session cleanup is supported only on Linux");
        var executable = Environment.ProcessPath
            ?? throw new QualificationBlockedException("qualification process executable is unavailable");
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
            info.ArgumentList.Add(typeof(OwnedCommandSession).Assembly.Location);
        info.ArgumentList.Add("__owned-command");
        info.ArgumentList.Add(pipe);
        info.Environment.Clear();
        foreach (var pair in command.Environment) info.Environment[pair.Key] = pair.Value;
        return info;
    }

    internal static void Kill(Process leader)
    {
        // The helper never exits normally before controller cleanup. Do not signal a numeric
        // session after an unexpected leader exit, where PID reuse cannot be disproved.
        if (leader.HasExited)
            throw new QualificationException("owned session leader exited unexpectedly; cleanup is unconfirmed");
        var leaderHandle = pidfd_open(leader.Id, 0);
        if (leaderHandle < 0)
            throw new QualificationException("owned session leader could not be pinned");
        var confirmed = false;
        try
        {
            if (leader.HasExited)
                throw new QualificationException("owned session leader exited unexpectedly; cleanup is unconfirmed");
            confirmed = true; // Own unreaped child is still alive at the time its handle was pinned.
            if (getsid(leader.Id) == leader.Id) TerminateDescendants(leader.Id, leaderHandle);
            // Before setsid/READY no target is allowed to start.
        }
        finally
        {
            try
            {
                // Stable leader handle is signalled last, never a possibly reused numeric PID.
                if (confirmed && pidfd_send_signal(leaderHandle, 9, IntPtr.Zero, 0) != 0
                    && Marshal.GetLastPInvokeError() != 3 /* ESRCH */)
                    throw new QualificationException("owned process-session leader termination failed");
            }
            finally { close(leaderHandle); }
        }
    }

    private static void TerminateDescendants(int session, int leaderHandle)
    {
        var clock = Stopwatch.StartNew();
        var emptyReads = 0;
        do
        {
            if (pidfd_send_signal(leaderHandle, 0, IntPtr.Zero, 0) != 0 || getsid(session) != session)
                throw new QualificationException("reserved session identity was lost; cleanup is unconfirmed");
            var found = false;
            // A process group is not a session: setpgrp descendants need their own stable handles.
            foreach (var path in Directory.EnumerateDirectories("/proc"))
            {
                if (pidfd_send_signal(leaderHandle, 0, IntPtr.Zero, 0) != 0)
                    throw new QualificationException("reserved session leader was lost; cleanup is unconfirmed");
                if (!int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    || pid == session || SessionOf(pid) != session) continue;
                var fd = pidfd_open(pid, 0);
                if (fd < 0)
                {
                    if (Marshal.GetLastPInvokeError() == 3 /* ESRCH */) continue;
                    throw new QualificationException("owned session member could not be pinned");
                }
                try
                {
                    // Recheck membership after pinning; the descriptor cannot signal a reused PID.
                    if (SessionOf(pid) != session) continue;
                    string record;
                    try { record = File.ReadAllText(path + "/stat"); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    var stateIndex = record.LastIndexOf(')') + 2;
                    if (stateIndex < 2 || stateIndex >= record.Length)
                        throw new QualificationException("owned session member state is unreadable");
                    if (record[stateIndex] is 'Z' or 'X') continue; // Already dead, cannot retain pipes or execute.
                    found = true;
                    if (pidfd_send_signal(fd, 9, IntPtr.Zero, 0) != 0
                        && Marshal.GetLastPInvokeError() != 3 /* ESRCH */)
                        throw new QualificationException("owned session member termination failed");
                }
                finally { close(fd); }
            }
            // Repeat the empty inventory: a parent can exit after forking while /proc is enumerated.
            emptyReads = found ? 0 : emptyReads + 1;
            if (emptyReads == 2) return;
            Thread.Sleep(10);
        } while (clock.Elapsed < TimeSpan.FromSeconds(2));
        throw new QualificationException("owned session still has live descendants; cleanup is unconfirmed");
    }

    private static int SessionOf(int pid)
    {
        var session = getsid(pid);
        if (session < 0 && Marshal.GetLastPInvokeError() != 3 /* ESRCH */)
            throw new QualificationException("process session membership is unreadable; cleanup is unconfirmed");
        return session;
    }

    internal static async Task<int> RunAsync(string handle)
    {
        if (!OperatingSystem.IsLinux() || !int.TryParse(handle, out var fd) || setsid() < 0)
            return 125;
        // Refuse before target launch when the stable PID-handle primitive is unavailable.
        var ownHandle = pidfd_open(Environment.ProcessId, 0);
        if (ownHandle < 0) return 125;
        // Keep this exact self handle until final cleanup.
        // Target executables must not inherit the controller's private status channel.
        if (fcntl(fd, 2 /* F_SETFD */, 1 /* FD_CLOEXEC */) != 0) return 125;
        using var pipe = new AnonymousPipeClientStream(PipeDirection.Out, handle);
        await using var status = new StreamWriter(pipe) { AutoFlush = true };
        await status.WriteLineAsync("READY");
        try
        {
            var json = await Console.In.ReadLineAsync();
            if (json is null || json.Length > 1024 * 1024)
                throw new QualificationException("owned command envelope missing or too large");
            var command = JsonSerializer.Deserialize<PlannedCommand>(json)
                ?? throw new QualificationException("owned command envelope missing");
            var info = new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = false, WorkingDirectory = command.WorkingDirectory,
                RedirectStandardInput = true
            };
            foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
            info.Environment.Clear();
            foreach (var pair in command.Environment) info.Environment[pair.Key] = pair.Value;
            using var target = Process.Start(info) ?? throw new QualificationException("owned target did not start");
            target.StandardInput.Close();
            await status.WriteLineAsync("START " + target.Id);
            // Console.In may synchronously block even for ReadLineAsync. Keep that
            // one EOF read off the status/target-completion path; process cleanup
            // bounds its lifetime and closes the pipe on every controller path.
            var controllerClosed = Task.Run(() => Console.In.ReadLine());
            var completed = target.WaitForExitAsync();
            if (await Task.WhenAny(completed, controllerClosed) != completed)
                return 125; // finally terminates the owned session if the controller disappears.
            await completed;
            await status.WriteLineAsync("EXIT " + target.ExitCode);
            // Reserve the leader identity after target exit until the controller terminates
            // us or closes its input. EOF cannot leave this helper waiting indefinitely.
            await controllerClosed;
        }
        catch
        {
            try
            {
                await status.WriteLineAsync("EXIT 125"); // Never disclose raw argv/config.
                await Console.In.ReadLineAsync();
            }
            catch (IOException) { }
        }
        finally
        {
            // This process is itself the live reserved session leader, never a reused PID.
            // This also covers lost-controller EOF and unexpected helper errors.
            try { TerminateDescendants(Environment.ProcessId, ownHandle); }
            finally
            {
                pidfd_send_signal(ownHandle, 9, IntPtr.Zero, 0);
                close(ownHandle);
            }
        }
        return 125;
    }
}
