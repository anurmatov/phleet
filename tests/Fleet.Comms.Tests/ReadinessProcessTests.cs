using System.Diagnostics;
using System.Net;

namespace Fleet.Comms.Tests;

public sealed class ReadinessProcessTests
{
    private static readonly Uri Ready = new("http://127.0.0.1:34567/ready");
    private static readonly Uri North = new("http://127.0.0.1:34568");

    [Fact]
    public async Task SafeStderrAndEarlyExitAreRetainedPromptly()
    {
        using var child = Child("printf 'fixture-startup-failure secret=do-not-retain\\n' >&2; exit 7");
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
            ReadinessProcess.WaitAsync(child, Ready, North, Unreachable, TimeSpan.FromSeconds(10)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains("exit=7", error.Message);
        Assert.Contains("fixture-startup-failure", error.Message);
        Assert.DoesNotContain("do-not-retain", error.Message);
        Assert.Contains(Ready.AbsoluteUri, error.Message);
        Assert.Contains(North.AbsoluteUri, error.Message);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task NeverReadyFailsAndReapsTheChild()
    {
        using var child = Child("exec sleep 30");
        var error = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
            ReadinessProcess.WaitAsync(child, Ready, North, Unreachable, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("deadline", error.Message);
        Assert.Contains("ConnectionError", error.Message);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task BothPipesBeyondCapacityAreDrainedWithoutDeadlock()
    {
        using var child = Child("for ((i=0;i<10000;i++)); do printf 'fixture-startup-failure padding padding\\n'; printf 'fixture-startup-failure padding padding\\n' >&2; done; exit 9");
        var error = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
            ReadinessProcess.WaitAsync(child, Ready, North, Unreachable, TimeSpan.FromSeconds(10)));
        Assert.Contains("exit=9", error.Message);
        var counts = System.Text.RegularExpressions.Regex.Matches(error.Message, "characters=([0-9]+)");
        Assert.Equal(2, counts.Count);
        Assert.All(counts.Cast<System.Text.RegularExpressions.Match>(),
            count => Assert.True(long.Parse(count.Groups[1].Value) > 65536));
        Assert.True(error.Message.Length < 10000);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task ServiceUnavailableIsAnExplicitFailureNotReadiness()
    {
        using var child = Child("exec sleep 30");
        var error = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
            ReadinessProcess.WaitAsync(child, Ready, North,
                _ => Task.FromResult(HttpStatusCode.ServiceUnavailable), TimeSpan.FromSeconds(1)));
        Assert.Contains("HTTP 503", error.Message);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task OnlyOkPassesAndStillCleansUp()
    {
        using var child = Child("exec sleep 30");
        await ReadinessProcess.WaitAsync(child, Ready, North,
            _ => Task.FromResult(HttpStatusCode.OK), TimeSpan.FromSeconds(1));
        Assert.True(child.HasExited);
    }

    private static Task<HttpStatusCode> Unreachable(CancellationToken _) =>
        throw new HttpRequestException(HttpRequestError.ConnectionError, "not retained");

    private static Process Child(string script)
    {
        var info = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        return Process.Start(info)!;
    }
}
