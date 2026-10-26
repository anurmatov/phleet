using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Bridge.Services;

internal static class BridgePeerConfiguration
{
    internal static void AddPeerConfiguration(this IServiceCollection services, IConfiguration configuration,
        Func<string, string?>? environment = null)
    {
        // Match the relay's opt-in, including incomplete configuration that must fail closed
        // when the relay validates its authority before connecting.
        var stock = !string.IsNullOrEmpty(configuration["StockOpenShell:AuthorityFile"])
            || !string.IsNullOrEmpty(configuration["StockOpenShell:BrokerPasswordFile"]);
        if (!stock)
        {
            services.AddHostedService<PeerConfigHostedService>();
            return;
        }

        environment ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(environment("PEER_CONFIG_KEYS"))
            || !string.IsNullOrEmpty(environment("PEER_AGENT_DERIVED_KEYS")))
            throw new StockIdentityDenied("broker_peer_config_unsupported");
        // Stock Bridge uses static configuration and only the epoch-bound control connection.
        // Never construct the legacy peer client or add a guest/default-vhost connection.
    }
}
