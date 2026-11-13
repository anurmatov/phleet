using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.Push;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fleet.Orchestrator.Application;

public static class WriteServiceRegistration
{
    /// <summary>
    /// Registers the writers that REST routes and MCP tools both call. Every write to an agent, an
    /// instruction or a workflow definition goes through one of them; an adapter that reaches for
    /// the database to change one of these is a second write path.
    /// </summary>
    /// <remarks>
    /// The narrow interfaces the writers depend on are mapped to the services that implement them
    /// when nothing else has registered one, so a host that registers only the concrete services
    /// (the orchestrator, and the endpoint tests) needs this one call.
    /// </remarks>
    public static IServiceCollection AddAgentWrites(this IServiceCollection services)
    {
        services.TryAddSingleton<ITelegramSetup>(sp => sp.GetRequiredService<SetupService>());
        services.TryAddSingleton<IAclChangeNotifier>(sp => sp.GetRequiredService<ConfigService>());
        services.TryAddSingleton<IConfigWriter>(sp => sp.GetRequiredService<ConfigService>());
        services.TryAddSingleton<OperatorStops>();

        services.TryAddSingleton<AgentConfigWriter>();
        services.TryAddSingleton<AgentCreator>();
        services.TryAddSingleton<AgentLifecycle>();
        services.TryAddSingleton<AgentAllowlist>();
        services.TryAddSingleton<AgentProjectAccessWriter>();
        return services;
    }
}
