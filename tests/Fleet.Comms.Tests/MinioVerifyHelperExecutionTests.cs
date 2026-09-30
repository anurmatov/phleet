using System.Diagnostics;

namespace Fleet.Comms.Tests;

/// <summary>
/// Executes <c>scripts/verify-minio-init-in-image.sh</c> against a scripted <c>docker</c> stand-in so
/// its stdin plumbing, failure paths and teardown are behaviourally tested.
/// </summary>
/// <remarks>
/// <para>
/// The static sibling (<see cref="MinioVerifyHelperInvocationTests"/>) reads the script's text, and
/// that was not enough: the harness passed every static check and still failed on the deploy host.
/// <c>docker run</c> without <c>-i</c> does not attach the piped stdin, so <c>mc alias set</c> read
/// EOF and stored an EMPTY credential pair while printing "Added successfully". Only running the
/// script and asserting on what the stand-in actually received catches that.
/// </para>
/// <para>
/// The stand-in is a bash script placed first on <c>PATH</c>. It reproduces the one Docker behaviour
/// that mattered — <c>run</c> without <c>-i</c> delivers nothing to the container's stdin — plus the
/// specific faults each test needs.
/// </para>
/// <para>
/// MUST NOT: drop <c>-i</c> from <c>run_in_image()</c>, reuse the init config directory for the scoped
/// probe, name resources at second granularity, or let a cleanup step fail unverified.
/// </para>
/// </remarks>
public sealed class MinioVerifyHelperExecutionTests : IDisposable
{
    private const string DockerStandIn = """
        #!/bin/bash
        # Scripted docker stand-in. MOCK_BEHAVIOR selects the fault under test.
        #
        # ⚠️ ORDER MATTERS, AND IT COST AN HOUR. `docker run -i` inherits the container's stdin, so the
        # stand-in must drain stdin BEFORE it writes its log. A script that logs first and reads later
        # deadlocks against a pipe whose writer is still alive (`printf a:b | docker run -i …` never
        # reaches EOF), and every test hangs instead of failing. That mirrors the real behaviour, not a
        # convenience: the harness has to be as faithful about stdin as the thing it stands in for.
        interactive=0
        for a in "$@"; do
          case "$a" in -i|--interactive) interactive=1 ;; esac
        done
        if [ "$interactive" -eq 1 ]; then
          while IFS= read -r line; do printf 'STDIN: %s\n' "$line" >> "$MOCK_LOG"; done
        fi

        log() { printf '%s\n' "$*" >> "$MOCK_LOG"; }
        log "ARGS: $*"

        # Record the host directory mounted at the container's /mc, so config isolation is checkable.
        for a in "$@"; do
          case "$a" in
            *":/mc") log "MCMOUNT: ${a%%:/mc}" ;;
          esac
        done

        # Surface the disposable network name so uniqueness across runs can be asserted.
        if [ "${MOCK_BEHAVIOR:-}" = "capture-names" ]; then
          for a in "$@"; do
            case "$a" in zz-minio-verify-*) printf 'NETWORK=%s\n' "$a" >&2 ;; esac
          done
        fi

        case "$1" in
          image)   echo "image=sha256:mockclient"; echo "created=2026-01-01T00:00:00Z"; exit 0 ;;
          pull)    exit 0 ;;
          rm)      exit 0 ;;
          volume)
            case "$2" in
              create) mkdir -p "$MOCK_VOL"; exit 0 ;;
              rm)      rmdir "$MOCK_VOL/root-owned" 2>/dev/null; rm -f "$MOCK_VOL/root-owned/file" 2>/dev/null
                       rmdir "$MOCK_VOL" 2>/dev/null; exit 0 ;;
              inspect)
                if [ "${MOCK_BEHAVIOR:-}" = "bind-survives" ] || [ -d "$MOCK_VOL" ]; then
                  echo "[{}]"; exit 0
                fi
                echo "Error: No such volume" >&2; exit 1 ;;
            esac
            ;;
          inspect)
            # `-f …` is the readiness probe asking for the server IP; a bare inspect is the removal
            # verification. They must answer differently or the script never gets past readiness.
            if [ "$2" = "-f" ]; then echo "172.99.0.5"; exit 0; fi
            if [ "${MOCK_BEHAVIOR:-}" = "server-survives" ]; then echo "[{}]"; exit 0; fi
            echo "Error: No such object" >&2; exit 1
            ;;
          network)
            case "$2" in
              create)  echo "mocknetworkid"; exit 0 ;;
              rm)      exit 0 ;;
              inspect)
                if [ "${MOCK_BEHAVIOR:-}" = "network-survives" ]; then echo "[{}]"; exit 0; fi
                echo "Error: No such network" >&2; exit 1
                ;;
            esac
            ;;
        esac

        # Simulated init.sh: like the real one, it writes a root alias into its /mc.
        case "$*" in
          *"/init/init.sh"*)
            if [ "${MOCK_BEHAVIOR:-}" = "init-fails" ]; then echo "simulated init.sh failure" >&2; exit 7; fi
            echo "Added 'comms' successfully."
            exit 0
            ;;
        esac

        # Simulated scoped listing.
        case "$*" in
          *"mc ls"*)
            if [ "${MOCK_BEHAVIOR:-}" = "list-denied" ]; then
              echo "mc: <ERROR> Unable to list folder. Access Denied." >&2; exit 1
            fi
            exit 0
            ;;
        esac

        # Simulated in-container removal. The bind is asserted elsewhere; here the container simply
        # does what it is told, so the normal path proves the removal command is well-formed and the
        # script still reaches a verified PASS.
        case "$*" in
          *"rm -rf -- "*) exit 0 ;;
        esac

        exit 0
        """;

    private readonly string _root;
    private readonly string _log;

    public MinioVerifyHelperExecutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "minioverify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        _log = Path.Combine(_root, "docker.log");
        File.WriteAllText(_log, string.Empty);

        var docker = Path.Combine(_root, "bin", "docker");
        File.WriteAllText(docker, DockerStandIn);
        File.SetUnixFileMode(
            docker,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Scratch directory; a leftover cannot change a verdict.
        }
    }

    /// <summary>
    /// Both host-piped credential invocations must actually deliver their bytes. Without
    /// <c>-i</c> the container sees EOF and <c>mc alias set</c> stores empty credentials.
    /// </summary>
    [Fact]
    public void Piped_stdin_credentials_reach_the_container()
    {
        var (exit, output) = Run(behavior: "default");
        Assert.True(exit == 0, $"helper exited {exit}:\n{output}");

        var lines = File.ReadAllLines(_log);
        var stdin = lines.Where(l => l.StartsWith("STDIN: ", StringComparison.Ordinal)).ToList();

        Assert.True(
            stdin.Count >= 4,
            $"expected both credential pipes (root readiness + scoped probe) to deliver two lines each, "
            + $"got {stdin.Count}.\n{output}");

        Assert.Contains(stdin, l => l.Contains("zzverifyroot", StringComparison.Ordinal));
        Assert.Contains(stdin, l => l.StartsWith("STDIN: zzverify", StringComparison.Ordinal));
    }

    /// <summary>A failing <c>init.sh</c> must stop the run and must not print PASS.</summary>
    [Fact]
    public void A_failing_init_sh_is_reported_and_does_not_pass()
    {
        var (exit, output) = Run(behavior: "init-fails");

        Assert.NotEqual(0, exit);
        Assert.Contains("simulated init.sh failure", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS:", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failing scoped listing must stop the run and must not print PASS — this is the check that
    /// proves the policy is attached rather than merely created.
    /// </summary>
    [Fact]
    public void A_failing_scoped_listing_is_reported_and_does_not_pass()
    {
        var (exit, output) = Run(behavior: "list-denied");

        Assert.NotEqual(0, exit);
        Assert.Contains("Access Denied", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS:", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bind volume that survives teardown must be reported and must not exit 0. The first version
    /// printed PASS over a failed teardown because every cleanup step ended in <c>|| true</c>.
    /// </summary>
    [Fact]
    public void A_surviving_bind_volume_is_reported_and_does_not_exit_zero()
    {
        var (exit, output) = Run(behavior: "bind-survives");

        Assert.NotEqual(0, exit);
        Assert.Contains("CLEANUP FAIL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS:", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The in-container cleanup must actually get its bind: <c>-v</c> after the image name is argv
    /// for the entrypoint, so the mount silently never happens and the removal "succeeds" against an
    /// empty directory while the host still holds root-owned files.
    /// </summary>
    [Fact]
    public void The_cleanup_run_mounts_the_bind_before_the_image_name()
    {
        Run(behavior: "default");

        var cleanup = File.ReadAllLines(_log)
            .Where(l => l.StartsWith("ARGS: ", StringComparison.Ordinal)
                        && l.Contains("rm -rf --", StringComparison.Ordinal))
            .ToList();

        Assert.Single(cleanup);

        var args = cleanup[0]["ARGS: ".Length..];
        var volume = args.IndexOf(" -v ", StringComparison.Ordinal);
        var image = args.IndexOf("minio/mc:", StringComparison.Ordinal);

        Assert.True(volume >= 0, $"the cleanup run mounts nothing: {args}");
        Assert.True(image >= 0, $"the cleanup run names no image: {args}");
        Assert.True(
            volume < image,
            $"`-v` appears after the image name, so docker hands it to the entrypoint as argv and the "
            + $"bind never happens: {args}");
    }

    /// <summary>A server that survives teardown must be reported, not swallowed by <c>|| true</c>.</summary>
    [Fact]
    public void A_surviving_server_is_reported_and_does_not_exit_zero()
    {
        var (exit, output) = Run(behavior: "server-survives");

        Assert.NotEqual(0, exit);
        Assert.Contains("CLEANUP FAIL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS:", output, StringComparison.Ordinal);
    }

    /// <summary>A network that survives teardown must be reported too.</summary>
    [Fact]
    public void A_surviving_network_is_reported_and_does_not_exit_zero()
    {
        var (exit, output) = Run(behavior: "network-survives");

        Assert.NotEqual(0, exit);
        Assert.Contains("CLEANUP FAIL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS:", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Names must be unique per run, so two runs cannot tear each other's resources down.
    /// </summary>
    [Fact]
    public void Two_runs_do_not_share_resource_names()
    {
        var (firstExit, firstOut) = Run(behavior: "capture-names");
        var (secondExit, secondOut) = Run(behavior: "capture-names");

        Assert.True(firstExit == 0, $"first run failed:\n{firstOut}");
        Assert.True(secondExit == 0, $"second run failed:\n{secondOut}");

        Assert.NotEqual(NetworkName(firstOut), NetworkName(secondOut), StringComparer.Ordinal);
    }

    /// <summary>
    /// The scoped probe must not mount the config directory <c>init.sh</c> wrote its root alias into.
    /// </summary>
    [Fact]
    public void The_scoped_probe_uses_a_config_dir_the_root_alias_was_not_written_to()
    {
        var (exit, output) = Run(behavior: "default");
        Assert.True(exit == 0, $"helper exited {exit}:\n{output}");

        var mounts = File.ReadAllLines(_log)
            .Where(l => l.StartsWith("MCMOUNT: ", StringComparison.Ordinal))
            .Select(l => l["MCMOUNT: ".Length..])
            .ToList();

        Assert.True(mounts.Count >= 2, $"expected >=2 /mc mounts, got: {string.Join(", ", mounts)}");

        // init.sh mounts one directory, the scoped probe another, and the probe's is the runtime one.
        Assert.True(
            mounts.Distinct(StringComparer.Ordinal).Count() >= 2,
            "the scoped probe reused init.sh's config directory and would inherit the root `comms` "
            + "alias, passing while proving nothing about the scoped credential: "
            + string.Join(", ", mounts));

        Assert.Contains(mounts, m => m.EndsWith("runtime-config", StringComparison.Ordinal));
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────

    private (int Exit, string Output) Run(string behavior)
    {
        var script = Path.Combine(RepositoryRoot().FullName, "scripts", "verify-minio-init-in-image.sh");

        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(script);

        psi.Environment["PATH"] = Path.Combine(_root, "bin") + ":" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["MOCK_LOG"] = _log;
        psi.Environment["MOCK_VOL"] = Path.Combine(_root, "volume");
        psi.Environment["MOCK_BEHAVIOR"] = behavior;
        psi.Environment["FLEET_COMMS_VERIFY_SERVER_IMAGE"] = "mock/minio-server:latest";
        psi.Environment["TMPDIR"] = _root;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(120_000), "helper did not finish within 120s");

        return (process.ExitCode, stdout + stderr);
    }

    private static string NetworkName(string output)
    {
        var line = output
            .Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("NETWORK=", StringComparison.Ordinal));

        Assert.NotNull(line);
        return line!.Trim();
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "could not locate the repository root");
        return directory!;
    }
}
