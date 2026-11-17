using System.Diagnostics;

namespace Fleet.LocalEndpoint;

/// <summary>How a started service ended. <see cref="ExitCode"/> is null when it was still running at the limit and was stopped.</summary>
internal sealed record ServiceRun(int? ExitCode, string Output, string Errors);

/// <summary>
/// Starts one of the fleet's services as its own process, the way its container does
/// (<c>dotnet Fleet.Example.dll</c>), from the copy the test project's build put beside the tests.
/// For the startup refusals: what the process prints and the code it exits with are the behaviour,
/// and neither can be read from inside the test's own process. Compiled into each test project that
/// needs it (shared source, like <c>tests/fixtures/wire</c>).
/// </summary>
internal static class ServiceProcess
{
    /// <param name="assembly">The service's assembly file name, e.g. <c>Fleet.Memory.dll</c>.</param>
    /// <param name="environment">Settings, as environment variables (<c>Section__Key</c>).</param>
    /// <param name="limit">How long the service may run. One that refuses to start is gone in a second or two.</param>
    public static async Task<ServiceRun> RunAsync(string assembly, IReadOnlyDictionary<string, string> environment, TimeSpan limit)
    {
        // An empty working directory: no appsettings.json, so the settings are the variables given.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "fleet-service-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var start = new ProcessStartInfo(DotnetPath())
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, assembly));
            // Should a refusal ever be missing, the service must not take a real port.
            start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            foreach (var (name, value) in environment)
                start.Environment[name] = value;

            using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {assembly}");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var stop = new CancellationTokenSource(limit);
            int? exitCode;
            try
            {
                await process.WaitForExitAsync(stop.Token);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                exitCode = null;
            }

            return new ServiceRun(exitCode, await output, await errors);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The <c>dotnet</c> the tests run under, so the service starts on the same runtime.</summary>
    private static string DotnetPath() =>
        Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? path
            : "dotnet";
}
