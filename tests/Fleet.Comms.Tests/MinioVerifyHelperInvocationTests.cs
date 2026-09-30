using System.Text.RegularExpressions;

namespace Fleet.Comms.Tests;

/// <summary>
/// The host-side verifier must not be able to damage a live deployment, and its docker invocations
/// must be real invocations.
/// </summary>
/// <remarks>
/// <para>
/// MUST NOT: point the helper at <c>comms-minio</c> or the <c>comms-media</c> network, drop
/// <c>--entrypoint</c> from the <c>init.sh</c> run, or move a docker flag after the image name.
/// </para>
/// <para>
/// The first version of <c>scripts/verify-minio-init-in-image.sh</c> had all three. It ran the
/// provisioner against the LIVE server, and because <c>init.sh</c> hardcodes the policy name
/// <c>comms-journal-runtime</c>, its own cleanup would have removed the live policy — leaving the
/// scoped runtime user with no policy and every call answered <c>Access Denied</c>. Separately, the
/// image's <c>ENTRYPOINT</c> is <c>["mc"]</c>, so an invocation without <c>--entrypoint</c> execs
/// <c>mc /bin/bash /init/init.sh</c> and verifies nothing. And flags written after the image name are
/// not flags: Docker hands them to the entrypoint as argv, so the cleanup credentials never arrived
/// while every step was <c>|| true</c> into <c>/dev/null</c> — a failed teardown still printed PASS.
/// </para>
/// <para>
/// These are argument-level assertions on the shipped script, not a docker mock: the repo has no
/// Docker daemon in CI, and the defects were all visible in the argument list the script builds.
/// </para>
/// </remarks>
public sealed class MinioVerifyHelperInvocationTests
{
    private const string ScriptPath = "scripts/verify-minio-init-in-image.sh";

    /// <summary>Flags that mean something to docker; after the image name they are argv instead.</summary>
    private static readonly string[] SilentFlags =
    [
        "-e", "--env", "-v", "--volume", "--network", "--entrypoint", "--platform", "--name",
    ];

    private static string Script =>
        File.ReadAllText(System.IO.Path.Combine(RepositoryRoot().FullName, ScriptPath));

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, "could not locate the repository root");
        return directory!;
    }

    /// <summary>
    /// Every live resource the helper must never name. The policy name <c>comms-journal-runtime</c>
    /// is legitimately present — it comes from <c>init.sh</c> — so only the network, the server and
    /// the real bucket are off limits.
    /// </summary>
    [Fact]
    public void The_verifier_never_names_a_live_resource()
    {
        // Comments are stripped: the script is ALLOWED to explain that it avoids the live stack, and
        // it legitimately passes the compose service name to `--add-host` so init.sh stays unmodified.
        // What it must never do is JOIN that network or name that server/real bucket as a target.
        var executable = string.Join(
            "\n",
            Script.Split('\n')
                .Select((line, i) => (line, i))
                .Where(x => x.i > 0 && !x.line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                .Select(x => x.line));

        foreach (var live in new[] { "comms-media", "root/comms-journal", "runtime/comms-journal" })
        {
            Assert.False(
                executable.Contains(live, StringComparison.Ordinal),
                $"the verifier targets `{live}`. It must run against its own disposable network and "
                + "server: init.sh hardcodes the policy name, so pointed at the live server its own "
                + "cleanup would delete the live runtime policy.");
        }

        // `--network` must name a per-run disposable network, never a fixed one.
        var networks = Regex.Matches(executable, @"--network\s+""?([^""\s]+)""?")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.NotEmpty(networks);
        foreach (var network in networks)
        {
            Assert.True(
                network == "none" || network.Contains("RUNID", StringComparison.Ordinal) || network.StartsWith("$", StringComparison.Ordinal),
                $"`--network {network}` is a fixed network. It must be the per-run disposable one, "
                + "so a failed run cannot leave the verifier pointed at shared infrastructure.");
        }

        Assert.Contains("docker network create", executable, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT", executable, StringComparison.Ordinal);

        // init.sh must run against the compose service name, aliased to the disposable server —
        // that is what keeps the shipped file byte-for-byte unmodified.
        Assert.Contains("--add-host", executable, StringComparison.Ordinal);
        Assert.Contains("/init/init.sh", executable, StringComparison.Ordinal);
    }

    /// <summary>
    /// The image's own ENTRYPOINT is <c>["mc"]</c>, so the init.sh run has to replace it.
    /// </summary>
    [Fact]
    public void The_init_invocation_overrides_the_image_entrypoint()
    {
        var script = Script;

        // Find the invocation that runs the provisioner, and require --entrypoint in the same one.
        var invocations = SplitDockerRun(script);
        var initRun = invocations
            .Where(i => i.Contains("/init/init.sh", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(initRun);
        foreach (var invocation in initRun)
        {
            Assert.Contains("--entrypoint", invocation, StringComparison.Ordinal);
            Assert.Contains("/bin/bash", invocation, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A docker flag written after the image name is passed to the entrypoint as argv, so the flag
    /// silently does nothing. This is what made the original cleanup run without credentials.
    /// </summary>
    [Fact]
    public void No_docker_flag_is_placed_after_the_image_name()
    {
        foreach (var invocation in SplitDockerRun(Script))
        {
            var tokens = invocation.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(t => t != "\\" && !t.StartsWith("#", StringComparison.Ordinal))
                .ToList();

            // Start after the invocation verb(s) — `docker run`, or the `run_in_image` wrapper —
            // otherwise the verb is mistaken for the image and every flag looks like it followed it.
            var start = tokens.Count > 1 && tokens[1] == "run"
                ? 2
                : tokens.Count > 0 && tokens[0] == "run_in_image"
                    ? 1
                    : 1;
            var imageIndex = -1;
            for (var i = start; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    // Skip a flag's own value so its content is never mistaken for the image.
                    if (FlagTakesValue(token))
                    {
                        i++;
                    }

                    continue;
                }

                imageIndex = i;
                break;
            }

            if (imageIndex < 0)
            {
                continue;
            }

            var after = tokens.Skip(imageIndex + 1).Where(t => SilentFlags.Contains(t)).Distinct().ToList();
            Assert.False(
                after.Count > 0,
                $"`{string.Join(" ", after)}` appears after the image name in a docker invocation, so "
                + "docker passes it to the entrypoint as argv instead of applying it. Put every docker "
                + "flag before the image.");
        }
    }

    /// <summary>
    /// Teardown must be verified, not swallowed: `|| true` into /dev/null let the original print PASS
    /// over a failed cleanup.
    /// </summary>
    [Fact]
    public void The_verifier_fails_closed_instead_of_printing_pass_over_errors()
    {
        var script = Script;

        // The success line must be reachable only after the exit codes are actually checked.
        Assert.Matches(@"INIT_EXIT\s*-\neq\s*0|INIT_EXIT.*-ne\s*0", script);
        Assert.Contains("LS_EXIT", script, StringComparison.Ordinal);

        // `mc ls` on the scoped credentials is the attachment proof; it must not be silenced.
        Assert.DoesNotContain("mc ls \"runtime/$BUCKET\" >/dev/null", script, StringComparison.Ordinal);

        // Cleanup of the disposable stack is checked, and a failure has to be visible.
        var cleanup = script[script.IndexOf("cleanup()", StringComparison.Ordinal)..];
        Assert.Contains("docker rm -f", cleanup, StringComparison.Ordinal);
        Assert.Contains("docker network rm", cleanup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits each statement that invokes the container out of the script, continuations joined.
    /// </summary>
    /// <remarks>
    /// The script funnels its short invocations through a <c>run_in_image()</c> wrapper that supplies
    /// <c>--rm --platform</c>, so both the literal <c>docker run</c> and the wrapper are statements
    /// that build a container invocation and both are checked.
    /// </remarks>
    private static List<string> SplitDockerRun(string script)
    {
        // Join shell line continuations first, so a multi-line invocation is one statement and its
        // flags can be checked against its image name.
        var joined = Regex.Replace(script, @"\\[\r\n]+[ \t]*", " ");
        return joined
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("docker run", StringComparison.Ordinal)
                        || l.Contains("run_in_image ", StringComparison.Ordinal))
            .Where(l => !l.TrimStart().StartsWith("run_in_image()", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Flags that consume the token after them. This list must be complete: a value-taking flag left
    /// out makes the test mistake that flag's VALUE for the image name, and then every real flag
    /// after it looks like a defect. `--add-host` was missing once and did exactly that.
    /// </summary>
    private static bool FlagTakesValue(string token) =>
        token is "-e" or "--env" or "-v" or "--volume" or "--network" or "--name" or "--entrypoint"
            or "--platform" or "--format" or "--driver" or "--workdir" or "--user" or "--restart"
            or "--add-host" or "--hostname" or "--label" or "-l" or "--pull" or "--memory" or "-m";
}
