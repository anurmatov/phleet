using System.Reflection;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// When the real relay says it is ready: only once the bridge has fetched its configuration from
/// the orchestrator AND there is an escalation target to send a timeout notice to. The durable
/// service sends nothing before that (<see cref="DurableDelegationStartUpTests"/>).
/// </summary>
[Collection("fleet-workflow-config-static")]
public sealed class DelegationRelayReadinessTests : IDisposable
{
    private static void Reset(string? ctoAgent, bool loaded)
    {
        typeof(FleetWorkflowConfig)
            .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, ctoAgent is null ? null : new FleetWorkflowOptions { CtoAgent = ctoAgent });
        typeof(FleetWorkflowConfig)
            .GetField("_peerConfigLoaded", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, loaded ? 1 : 0);
    }

    public void Dispose() => Reset(null, loaded: false);

    private static IDelegationRelay Relay() => new DelegationRelay(
        Options.Create(new RabbitMqOptions()), Options.Create(new TemporalBridgeOptions()),
        Substitute.For<IConnectionFactory>(), Substitute.For<IHttpClientFactory>(),
        NullLogger<DelegationRelay>.Instance);

    [Fact]
    public void TheRelayIsNotReady_UntilThePeerConfigurationIsLoaded()
    {
        Reset(null, loaded: false);
        Assert.False(Relay().Ready);

        // A target known from the start-up options alone is not the deployment's configuration.
        Reset("coordinator", loaded: false);
        Assert.False(Relay().Ready);

        FleetWorkflowConfig.MarkPeerConfigLoaded();
        Assert.True(FleetWorkflowConfig.PeerConfigLoaded);
        Assert.True(Relay().Ready);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void TheRelayIsNotReady_WithoutAnEscalationTarget(string ctoAgent)
    {
        Reset(ctoAgent, loaded: true);
        Assert.False(Relay().Ready);

        Reset(null, loaded: true);
        Assert.False(Relay().Ready);
    }

    /// <summary>
    /// The bootstrap marks the configuration as loaded only after it has fetched it and checked
    /// the coordinator agent, and before it does anything else. Read from the source: starting the
    /// service itself needs an orchestrator and a broker.
    /// </summary>
    [Fact]
    public void TheBootstrap_MarksTheConfigurationLoaded_AfterItHasFetchedAndCheckedIt()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src/Fleet.Temporal/Services/PeerConfigHostedService.cs"));

        var fetched = source.IndexOf("await _client.BootstrapAsync(cancellationToken);", StringComparison.Ordinal);
        var checkedTarget = source.IndexOf("FleetWorkflowConfig.Instance.CtoAgent", fetched, StringComparison.Ordinal);
        var marked = source.IndexOf("FleetWorkflowConfig.MarkPeerConfigLoaded();", StringComparison.Ordinal);

        Assert.True(fetched > 0 && checkedTarget > fetched && marked > checkedTarget);
        Assert.Equal(marked, source.LastIndexOf("FleetWorkflowConfig.MarkPeerConfigLoaded();", StringComparison.Ordinal));
    }
}
