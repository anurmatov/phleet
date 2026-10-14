using System.Diagnostics;
using System.Text;
using Fleet.Agent.Services;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// The isolated local tool runner (#455 §3.3, §3.6, MUST NOT 4): the exact argument list, the
/// environment, and what timeout, cap and cancellation do to real child processes. Linux only:
/// it runs util-linux <c>prlimit</c> and coreutils, like the agent image.
/// </summary>
public sealed class PiLocalToolsTests
{
    private static readonly Dictionary<string, string> Environment = new()
    {
        ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
        ["ANTHROPIC_API_KEY"] = "placeholder-a",
        ["OPENAI_API_KEY"] = "placeholder-o",
        ["AWS_SECRET_ACCESS_KEY"] = "placeholder-w",
        ["GH_TOKEN"] = "kept-for-bash",
    };

    private static PiLocalTools Tools(TimeSpan? callTimeout = null, string prlimit = PiLocalTools.PrlimitBinary) =>
        new(callTimeout, prlimit, Environment);

    private static PiProcessingBudget Budget(double seconds = 60) => new(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void BuildStartInfo_RunsTheToolThroughPrlimit_AsAnArgumentList_WithNoShell()
    {
        var psi = Tools().BuildStartInfo("pdftotext", ["-enc", "UTF-8", "a file; rm -rf x.pdf", "-"], "/workspace/example");

        Assert.Equal("prlimit", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(
            ["--as=1073741824", "--cpu=60", "--nofile=256", "--", "pdftotext", "-enc", "UTF-8", "a file; rm -rf x.pdf", "-"],
            psi.ArgumentList);
        Assert.Equal("/workspace/example", psi.WorkingDirectory);
    }

    [Fact]
    public void BuildStartInfo_StripsEveryCredentialName_AndSetsMallocArenaMax()
    {
        var psi = Tools().BuildStartInfo("pdfinfo", ["x.pdf"], "/workspace/example");

        Assert.DoesNotContain(psi.Environment.Keys, PiLocalModel.IsCredentialEnvName);
        Assert.Equal("2", psi.Environment["MALLOC_ARENA_MAX"]);
        Assert.Equal("kept-for-bash", psi.Environment["GH_TOKEN"]);
        Assert.Equal(Environment["PATH"], psi.Environment["PATH"]);
    }

    [Fact]
    public void DownscaleArgs_AreSingleThreadedEverywhere_AndNeverUpscale()
    {
        var png = PiAttachments.DownscaleArgs("in.png", "out.png", jpegFilter: null);
        var jpeg = PiAttachments.DownscaleArgs("in.jpg", "out.jpg", PiAttachments.ScaleFilter);

        foreach (var args in new[] { png, jpeg })
        {
            Assert.Equal(["-nostdin", "-threads", "1", "-filter_threads", "1", "-i"], args.Take(6));
            // The encoder's own threads: a second -threads 1 after the input, before the output.
            var afterInput = args.Skip(args.ToList().IndexOf("-i") + 2).ToList();
            Assert.Equal("1", afterInput[afterInput.IndexOf("-threads") + 1]);
            Assert.Contains(args, a => a.StartsWith("scale=w='min(iw,2000)':h='min(ih,2000)':force_original_aspect_ratio=decrease", StringComparison.Ordinal));
        }
        Assert.DoesNotContain("-q:v", png);
        Assert.Equal("3", jpeg[jpeg.ToList().IndexOf("-q:v") + 1]);
        Assert.Contains("overlay", PiAttachments.FlattenFilter);
    }

    [Fact]
    public async Task RunAsync_TheChildEnvironment_HoldsNoCredentialName()
    {
        var result = await Tools().RunAsync("env", [], Budget(), 64 * 1024, CancellationToken.None);

        var env = Encoding.UTF8.GetString(result.Stdout);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("MALLOC_ARENA_MAX=2", env);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", env);
        Assert.DoesNotContain("OPENAI_API_KEY", env);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", env);
    }

    [Fact]
    public async Task RunAsync_RunsInAPrivateTempDirectory_DeletedAfterTheCall()
    {
        var result = await Tools().RunAsync("pwd", [], Budget(), 4096, CancellationToken.None);

        var dir = Encoding.UTF8.GetString(result.Stdout).Trim();
        Assert.Contains("fleet-pi-tool-", dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsTheProcessTree_AndReportsTimedOut()
    {
        // sh is only the test's way to start a grandchild; Fleet's own calls never use a shell.
        var clock = Stopwatch.StartNew();
        var result = await Tools(TimeSpan.FromMilliseconds(700))
            .RunAsync("sh", ["-c", "sleep 30 & echo $!; wait"], Budget(), 4096, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        var grandchild = int.Parse(Encoding.UTF8.GetString(result.Stdout).Trim());
        await AssertGoneAsync(grandchild);
    }

    [Fact]
    public async Task RunAsync_StdoutOverTheCap_KillsTheProcess_AndKeepsExactlyTheCap()
    {
        var clock = Stopwatch.StartNew();
        var result = await Tools().RunAsync("yes", [], Budget(), 10_000, CancellationToken.None);

        Assert.True(result.Capped);
        Assert.False(result.TimedOut);
        Assert.Equal(10_000, result.Stdout.Length);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_Cancellation_KillsTheProcessTree_AndThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tools().RunAsync("sleep", ["30"], Budget(), 4096, cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_TheCallTimeout_IsCutToWhatIsLeftOfTheCommandDeadline()
    {
        var budget = Budget(seconds: 1);
        var clock = Stopwatch.StartNew();

        var first = await Tools().RunAsync("sleep", ["30"], budget, 4096, CancellationToken.None);
        var second = await Tools().RunAsync("true", [], budget, 4096, CancellationToken.None);

        Assert.True(first.TimedOut);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(6));
        // The deadline is spent: nothing more starts.
        Assert.Same(PiToolResult.Expired, second);
    }

    [Fact]
    public async Task RunAsync_AMissingTool_IsUnavailable()
    {
        var result = await Tools().RunAsync("fleet-no-such-tool", [], Budget(), 4096, CancellationToken.None);

        Assert.True(result.Unavailable);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_AMissingPrlimit_IsUnavailable()
    {
        var result = await Tools(prlimit: "fleet-no-such-prlimit").RunAsync("true", [], Budget(), 4096, CancellationToken.None);

        Assert.True(result.Unavailable);
    }

    [Fact]
    public void TheDefaults_AreTheSpecValues()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), PiLocalModel.CommandProcessingDeadline);
        Assert.Equal(TimeSpan.FromSeconds(30), PiLocalModel.LocalToolCallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), new PiExecutorSettings().CommandProcessingDeadline);
    }

    private static async Task AssertGoneAsync(int pid)
    {
        for (var i = 0; i < 50 && Directory.Exists($"/proc/{pid}"); i++)
            await Task.Delay(100);
        Assert.False(Directory.Exists($"/proc/{pid}") && !IsZombie(pid), $"process {pid} survived the kill");
    }

    private static bool IsZombie(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/stat").Split(' ')[2] == "Z";
        }
        catch (IOException)
        {
            return true;
        }
    }
}
