using System.Reflection;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Extensions.Hosting;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// The activity and the listener take the durable service as an OPTIONAL constructor parameter, so
/// that every test written before it existed still constructs them. The cost of that is a silent
/// failure mode: a container that did not pass it would leave the feature switched on in
/// configuration and doing nothing. This builds the registrations the bridge makes and requires
/// that both receive the one service.
/// </summary>
public sealed class DurableDelegationWiringTests
{
    private static ServiceProvider BridgeServices(string stateFile)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(Options.Create(new RabbitMqOptions()));
        services.AddSingleton(Options.Create(new TemporalBridgeOptions()));
        services.AddSingleton(Options.Create(new DurableDelegationOptions { Enabled = true, StateFile = stateFile }));
        services.AddSingleton(Substitute.For<IConnectionFactory>());
        services.AddSingleton(Substitute.For<ITemporalClientFactory>());

        // As in Program.cs.
        services.AddSingleton<TaskCompletionRegistry>();
        services.AddSingleton<IDelegationRelay, DelegationRelay>();
        services.AddSingleton<IAsyncActivityGateway, TemporalAsyncActivityGateway>();
        services.AddSingleton<DurableDelegationService>();
        services.AddSingleton<TemporalRelayListener>();
        services
            .AddHostedTemporalWorker("localhost:7233", "fleet", "fleet")
            .AddScopedActivities<DelegateToAgentActivity>();

        return services.BuildServiceProvider();
    }

    private static object? DurableOf(object owner) =>
        owner.GetType().GetField("_durable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner);

    [Fact]
    public async Task TheActivityAndTheListener_ReceiveTheOneDurableService()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), "durable-wiring-" + Guid.NewGuid().ToString("N"), "pending.json");
        await using var provider = BridgeServices(stateFile);
        await using var scope = provider.CreateAsyncScope();

        var durable = provider.GetRequiredService<DurableDelegationService>();
        Assert.True(durable.Enabled);
        Assert.Same(durable, DurableOf(provider.GetRequiredService<TemporalRelayListener>()));
        Assert.Same(durable, DurableOf(scope.ServiceProvider.GetRequiredService<DelegateToAgentActivity>()));
    }
}
