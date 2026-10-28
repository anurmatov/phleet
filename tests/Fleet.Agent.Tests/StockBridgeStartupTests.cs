using Fleet.Bridge.Services;
using Fleet.Shared;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public sealed class StockBridgeStartupTests
{
    [Theory]
    [InlineData("StockOpenShell:AuthorityFile")]
    [InlineData("StockOpenShell:BrokerPasswordFile")]
    public async Task StockHost_StaticConfigurationStartsWithoutPeerService(string optIn)
    {
        // Missing binding files are intentional: service registration must not read them or
        // construct a peer client. The real relay still validates them before its own connect.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [optIn] = "/synthetic/not-read.json" }).Build();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddPeerConfiguration(configuration, _ => null);
        }).Build();
        Assert.Empty(host.Services.GetServices<IHostedService>());
        await host.StartAsync();
        await host.StopAsync();
    }

    [Theory]
    [InlineData("PEER_CONFIG_KEYS", "CONFIG_KEY")]
    [InlineData("PEER_AGENT_DERIVED_KEYS", "AGENT_{name}")]
    [InlineData("PEER_CONFIG_KEYS", " ")]
    [InlineData("PEER_AGENT_DERIVED_KEYS", ",")]
    public void StockPeerKeys_RefusedBeforeServiceConstruction(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["StockOpenShell:AuthorityFile"] = "/synthetic/not-read.json" }).Build();
        var services = new ServiceCollection();
        var error = Assert.Throws<StockIdentityDenied>(() =>
            services.AddPeerConfiguration(configuration, name => name == key ? value : null));
        Assert.Equal("broker_peer_config_unsupported", error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void StockEmptyPeerKeys_StaticConfigurationRegistersNoPeerClient()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["StockOpenShell:BrokerPasswordFile"] = "/synthetic/not-read" }).Build();
        var services = new ServiceCollection();
        services.AddPeerConfiguration(configuration, _ => "");
        Assert.Empty(services);
    }

    [Fact]
    public void OrdinaryHost_StillRegistersTheActualPeerHostedService()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        // Ordinary mode must not inspect or reject peer-key requests.
        services.AddPeerConfiguration(configuration, _ => throw new InvalidOperationException("must not inspect"));
        var descriptor = Assert.Single(services);
        Assert.Equal(typeof(IHostedService), descriptor.ServiceType);
        Assert.Equal(typeof(PeerConfigHostedService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public async Task OrdinaryPeerService_EmptyKeysStillEntersActualSubscription()
    {
        var bridgeOptions = Options.Create(new Fleet.Bridge.Configuration.BridgeOptions());
        await using var relay = new BridgeRelayService(bridgeOptions,
            Options.Create(new Fleet.Bridge.Configuration.RabbitMqOptions()), NullLogger<BridgeRelayService>.Instance);
        var notifier = new TelegramNotifier(bridgeOptions,
            Options.Create(new Fleet.Bridge.Configuration.TelegramOptions()), NullLogger<TelegramNotifier>.Instance);
        // Bootstrap has no requested keys and returns immediately. Only the real subscription
        // attempts to connect, observing pre-cancellation before opening any broker connection.
        await using var client = new PeerConfigClient("", "", "broker.example.test", "fleet.orchestrator",
            [], [], NullLogger.Instance);
        await using var peer = new PeerConfigHostedService(relay, notifier,
            NullLogger<PeerConfigHostedService>.Instance, client);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = await Record.ExceptionAsync(() => peer.StartAsync(cancelled.Token));
        Assert.NotNull(error);
        Assert.True(error is OperationCanceledException or RabbitMQ.Client.Exceptions.BrokerUnreachableException,
            error.GetType().Name);
        Assert.Contains("PeerConfigClient.SubscribeAsync", error.StackTrace);
    }
}
