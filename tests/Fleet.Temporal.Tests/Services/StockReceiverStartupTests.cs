using Fleet.Temporal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// #492 review: a dedicated stock receiver (StockOpenShell:AuthorityFile set) starts receiver-only. A relay
/// consumer there would ack and discard replies awaited by the ordinary bridge's registry, and namespace
/// workers there would run activities waiting on replies it never receives.
/// </summary>
public class StockReceiverStartupTests
{
    private static IConfiguration Config(string? authorityFile) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["StockOpenShell:AuthorityFile"] = authorityFile })
        .Build();

    private static bool HasRelayConsumer(IConfiguration config)
    {
        var services = new ServiceCollection();
        StockReceiverStartup.AddReplyConsumer(services, config);
        return services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TemporalRelayListener));
    }

    [Fact]
    public void DedicatedReceiver_RegistersNoRelayConsumer()
        => Assert.False(HasRelayConsumer(Config("/run/stock/authority.json")));

    [Fact]
    public void OrdinaryBridge_KeepsTheRelayConsumer()
    {
        Assert.True(HasRelayConsumer(Config(null)));
        Assert.True(HasRelayConsumer(Config("")));
    }

    [Fact]
    public void DedicatedReceiver_StartsNoNamespaceWorkers()
        => Assert.Empty(StockReceiverStartup.WorkerNamespaces(Config("/run/stock/authority.json"), ["fleet", "default"]));

    [Fact]
    public void OrdinaryBridge_KeepsConfiguredNamespaceWorkers()
        => Assert.Equal(["fleet", "default"],
            StockReceiverStartup.WorkerNamespaces(Config(null), ["fleet", " ", "default", "FLEET"]));

    private static int HostedServices(IConfiguration config)
    {
        var services = new ServiceCollection();
        StockReceiverStartup.AddFailureNotifierHost(services, config);
        return services.Count(d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void DedicatedReceiver_DoesNotHostTheWorkflowFailureNotifier()
        => Assert.Equal(0, HostedServices(Config("/run/stock/authority.json")));

    [Fact]
    public void OrdinaryBridge_HostsTheWorkflowFailureNotifier()
    {
        Assert.Equal(1, HostedServices(Config(null)));
        Assert.Equal(1, HostedServices(Config("")));
    }
}

