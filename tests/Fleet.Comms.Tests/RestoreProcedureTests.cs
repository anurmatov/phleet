using System.Text.RegularExpressions;

namespace Fleet.Comms.Tests;

/// <summary>
/// Executes the disaster-restore procedure <b>from the document itself</b>, with a recording Docker
/// substitute, and injects a failure at each step.
///
/// <para>The document previously claimed "every step aborts on failure" and it was false: <c>set
/// -e</c> was inside the helper container's <c>sh -c</c> only, so the surrounding sequence ran on
/// regardless. A rejected backup still reached the replacement; a failed
/// <c>devices revoke --all</c> still reached <c>up -d</c> and reopened ingress with every restored
/// credential live.</para>
///
/// <para><b>The block is extracted, not retyped.</b> A reconstructed copy of the procedure would
/// pass forever while the published one drifted — and the published one is what an operator runs at
/// three in the morning.</para>
/// </summary>
public class RestoreProcedureTests
{
    [Fact]
    public void TheHappyPathRunsEveryStepInOrder()
    {
        var run = Execute(failAt: null);

        Assert.True(run.Exit == 0, $"expected success, got {run.Exit}\n{run.Output}");
        AssertOrder(run,
            "store verify",
            "stop fleet-comms",
            "inspect",
            "cp /backups/",
            "devices list",
            "devices revoke --all",
            "enroll issue",
            "up -d fleet-comms");
    }

    /// <summary>
    /// The case that matters most. A failed revocation must not be followed by a start: the whole
    /// point of invalidating before ingress is that there is no window in which a restored
    /// credential works.
    /// </summary>
    [Fact]
    public void AFailedRevocationNeverReachesTheStart()
    {
        var run = Execute(failAt: "devices revoke --all");

        Assert.NotEqual(0, run.Exit);
        Assert.Contains("devices revoke --all", run.Invocations, StringComparison.Ordinal);
        Assert.DoesNotContain("up -d fleet-comms", run.Invocations, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedVerificationNeverReachesTheReplacement()
    {
        var run = Execute(failAt: "store verify");

        Assert.NotEqual(0, run.Exit);
        Assert.DoesNotContain("cp /backups/", run.Invocations, StringComparison.Ordinal);
        Assert.DoesNotContain("up -d fleet-comms", run.Invocations, StringComparison.Ordinal);

        // And the service was never stopped, so a rejected backup costs no downtime at all.
        Assert.DoesNotContain("stop fleet-comms", run.Invocations, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedReplacementNeverReachesTheStart()
    {
        var run = Execute(failAt: "cp /backups/");

        Assert.NotEqual(0, run.Exit);
        Assert.DoesNotContain("devices revoke --all", run.Invocations, StringComparison.Ordinal);
        Assert.DoesNotContain("up -d fleet-comms", run.Invocations, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unresolvable volume is the silent-failure case: writing to a guessed name creates a
    /// different empty volume, and the service then starts on its untouched database and reports
    /// ready — a restore that appears to have worked and changed nothing.
    /// </summary>
    [Fact]
    public void AnUnresolvableVolumeStopsBeforeAnythingIsWritten()
    {
        var run = Execute(failAt: null, emptyVolume: true);

        Assert.NotEqual(0, run.Exit);
        Assert.Contains("refusing to continue", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cp /backups/", run.Invocations, StringComparison.Ordinal);
        Assert.DoesNotContain("up -d fleet-comms", run.Invocations, StringComparison.Ordinal);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private sealed record Run(int Exit, string Output, string Invocations);

    private static Run Execute(string? failAt, bool emptyVolume = false)
    {
        var workspace = Directory.CreateTempSubdirectory("fleet-comms-restore-").FullName;
        try
        {
            var log = Path.Combine(workspace, "invocations.log");

            // A `docker` on PATH that records its arguments and succeeds — except where the test
            // injects a failure. `sh -c` bodies are recorded too, so the replacement step inside the
            // helper container is observable.
            var stub = Path.Combine(workspace, "bin", "docker");
            Directory.CreateDirectory(Path.GetDirectoryName(stub)!);
            var inspectOutput = emptyVolume ? "" : "fleet_fleet_comms_auth";
            var stubScript = string.Join('\n',
                "#!/usr/bin/env bash",
                "printf '%s\\n' \"$*\" >> \"" + log + "\"",
                "if [ -n \"${FAIL_AT:-}\" ] && [[ \"$*\" == *\"$FAIL_AT\"* ]]; then",
                "  echo \"injected failure: $FAIL_AT\" >&2",
                "  exit 1",
                "fi",
                "if [[ \"$*\" == *inspect* ]]; then",
                "  printf '" + inspectOutput + "'",
                "fi",
                "exit 0",
                "");
            File.WriteAllText(stub, stubScript);
            File.SetUnixFileMode(stub,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // The checkout layout the procedure expects.
            var checkout = Path.Combine(workspace, "checkout");
            Directory.CreateDirectory(Path.Combine(checkout, "fleet", "backups"));

            var script = Path.Combine(workspace, "restore.sh");
            File.WriteAllText(script,
                ExtractRestoreProcedure().Replace("CHECKOUT=/path/to/checkout", $"CHECKOUT={checkout}"));

            var info = new System.Diagnostics.ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workspace,
            };
            info.ArgumentList.Add(script);
            info.Environment["PATH"] =
                Path.GetDirectoryName(stub) + ":" + Environment.GetEnvironmentVariable("PATH");
            if (failAt is not null)
                info.Environment["FAIL_AT"] = failAt;

            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new Run(process.ExitCode, output,
                File.Exists(log) ? File.ReadAllText(log) : "");
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Pull the restore block out of the published document. Located by content — the fenced block
    /// that both verifies a backup and starts the service — so it cannot silently bind to some
    /// other snippet if the document is reorganised.
    /// </summary>
    private static string ExtractRestoreProcedure()
    {
        var document = Path.Combine(RepositoryRoot(), "docs", "comms-deployment.md");
        Assert.True(File.Exists(document), $"deployment document not found at {document}");

        var blocks = Regex.Matches(File.ReadAllText(document), "```bash\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(b => b.Contains("store verify --in", StringComparison.Ordinal)
                        && b.Contains("up -d fleet-comms", StringComparison.Ordinal))
            .ToList();

        Assert.True(blocks.Count == 1,
            $"expected exactly one restore block in the deployment document, found {blocks.Count}");
        return blocks[0];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static void AssertOrder(Run run, params string[] expected)
    {
        var position = -1;
        foreach (var step in expected)
        {
            var next = run.Invocations.IndexOf(step, StringComparison.Ordinal);
            Assert.True(next > position,
                $"'{step}' did not appear after the previous step.\n{run.Invocations}");
            position = next;
        }
    }
}
