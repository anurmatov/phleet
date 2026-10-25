using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Fleet.WorkflowOutcomeQualification;

public interface IProcessCommandRunner
{
    Task<ProcessCommandResult> RunAsync(PlannedCommand command, CancellationToken ct = default);
}

public sealed record ProcessCommandResult(
    string Operation,
    int ExitCode,
    int ProcessId,
    IReadOnlyList<ProcessAncestryRow> Ancestry,
    string Stdout,
    string Stderr,
    string StdoutSha256,
    string StderrSha256,
    TimeSpan Duration,
    bool TimedOut);

public sealed record ProcessAncestryRow(int ProcessId, int ParentProcessId, string Name);

public sealed class ProcessCommandRunner : IProcessCommandRunner
{
    private const int MaxOutputChars = 1024 * 1024;

    public async Task<ProcessCommandResult> RunAsync(PlannedCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.FileName) || command.Arguments.Any(string.IsNullOrWhiteSpace))
            throw new QualificationException("refusing a command with an empty argv element");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(command.Deadline);
        if (command.Deadline <= TimeSpan.Zero || command.Deadline > TimeSpan.FromMinutes(5))
            throw new QualificationException("command deadline must be positive and at most five minutes");
        if (!Path.IsPathFullyQualified(command.FileName))
            throw new QualificationException("command executable must be an absolute pinned path");
        var started = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = command.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment.Clear();
        foreach (var pair in command.Environment) process.StartInfo.Environment[pair.Key] = pair.Value;

        if (!process.Start()) throw new QualificationException("failed to start '" + command.Operation + "'");
        var ancestry = ProcessAncestry.Capture(process.Id);
        var stdout = CaptureAsync(process.StandardOutput);
        var stderr = CaptureAsync(process.StandardError);
        var timedOut = false;
        try
        {
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { timedOut = true; }
        }
        finally
        {
            // Disposal is not termination. Cancellation and all unexpected failures must reap
            // the owned child, while output readers keep draining both pipes concurrently.
            TryKill(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        var output = await stdout.WaitAsync(TimeSpan.FromSeconds(2));
        var error = await stderr.WaitAsync(TimeSpan.FromSeconds(2));
        if (output.Overflow || error.Overflow)
            throw new QualificationException("command output exceeded the bounded capture size");

        return new ProcessCommandResult(
            command.Operation, timedOut ? 124 : process.ExitCode, process.Id, ancestry,
            output.Text, error.Text, Sha256(output.Text), Sha256(error.Text), started.Elapsed, timedOut);
    }

    private sealed record Capture(string Text, bool Overflow);

    private static async Task<Capture> CaptureAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var overflow = false;
        while (true)
        {
            // Do not abandon a full pipe on command cancellation or at the capture limit.
            // Kill/reap above closes it. Continue draining but retain at most the bounded prefix.
            var read = await reader.ReadAsync(buffer);
            if (read == 0) break;
            var keep = Math.Min(read, MaxOutputChars - builder.Length);
            builder.Append(buffer, 0, keep);
            overflow |= keep < read;
        }
        return new Capture(builder.ToString(), overflow);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class ProcessAncestry
{
    public static IReadOnlyList<ProcessAncestryRow> Capture(int processId)
    {
        var rows = new List<ProcessAncestryRow>();
        var seen = new HashSet<int>();
        var current = processId;
        while (current > 1 && seen.Add(current))
        {
            var row = Read(current);
            if (row is null) break;
            rows.Add(row);
            current = row.ParentProcessId;
        }
        return rows;
    }

    private static ProcessAncestryRow? Read(int processId)
    {
        try
        {
            var lines = File.ReadAllLines("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/status");
            var name = "";
            var parent = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("Name:", StringComparison.Ordinal)) name = line[5..].Trim();
                else if (line.StartsWith("PPid:", StringComparison.Ordinal)
                         && int.TryParse(line[5..].Trim(), CultureInfo.InvariantCulture, out var parsed)) parent = parsed;
            }
            return name.Length == 0 ? null : new ProcessAncestryRow(processId, parent, name);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
