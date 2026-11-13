using Fleet.Comms.Configuration;

namespace Fleet.Comms.Tests;

/// <summary>
/// The review round that found these described them as reproduced failures, not as style. Each test
/// here pins the behaviour that was wrong, so reverting any one of the fixes goes red.
/// </summary>
public sealed class DeploymentHardeningTests
{
    // ── the ops listener may not be published ────────────────────────────────

    [Theory]
    [InlineData("http://127.0.0.1:8081")]
    [InlineData("http://localhost:8081")]
    [InlineData("http://[::1]:8081")]
    [InlineData("http://127.0.0.1:8081;http://localhost:9091")]
    public void LoopbackOpsAddressesAreAccepted(string url) =>
        OpsListenerAddress.EnsureLoopbackOnly(url);

    /// <summary>
    /// The invariant was previously true of the default value and of nothing else: the configured
    /// address went straight to <c>UseUrls</c>, so any of these published the readiness oracle —
    /// which reports whether the conversation store is answering.
    /// </summary>
    [Theory]
    [InlineData("http://0.0.0.0:8081")]
    [InlineData("http://*:8081")]
    [InlineData("http://+:8081")]
    [InlineData("http://192.168.1.10:8081")]
    [InlineData("http://comms.example.com:8081")]
    [InlineData("http://[::]:8081")]
    // One good address does not excuse a wildcard beside it.
    [InlineData("http://127.0.0.1:8081;http://0.0.0.0:9091")]
    [InlineData("")]
    public void NonLoopbackOpsAddressesAreRefusedBeforeAnythingStarts(string url) =>
        Assert.Throws<InvalidOperationException>(() => OpsListenerAddress.EnsureLoopbackOnly(url));
}
