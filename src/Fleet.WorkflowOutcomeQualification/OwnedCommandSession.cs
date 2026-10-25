using System.Diagnostics;
using System.IO.Pipes;
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
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int command, int value);

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
        if (getsid(leader.Id) == leader.Id)
        {
            if (kill(-leader.Id, 9) != 0)
                throw new QualificationException("owned process-session termination failed");
        }
        else leader.Kill(entireProcessTree: true); // No target can start before setsid + READY.
    }

    internal static async Task<int> RunAsync(string handle)
    {
        if (!OperatingSystem.IsLinux() || !int.TryParse(handle, out var fd) || setsid() < 0)
            return 125;
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
            kill(-Environment.ProcessId, 9);
        }
        return 125;
    }
}
