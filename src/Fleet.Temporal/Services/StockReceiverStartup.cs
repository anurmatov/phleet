using Fleet.Shared.Security;

namespace Fleet.Temporal.Services;

/// <summary>
/// #492: a dedicated receiver for managed stock workloads (engaged only when
/// <c>StockOpenShell:AuthorityFile</c> is set) runs beside the ordinary bridge and serves MCP only.
/// It must not consume the shared <c>fleet.agent.temporal-bridge</c> reply queue — its own
/// <see cref="TaskCompletionRegistry"/> holds none of the other instance's waiters, so it would ack and
/// discard their replies — and must not poll namespace task queues, whose activities wait on that registry.
/// Unset leaves the ordinary bridge's relay consumer and workers unchanged.
/// </summary>
public static class StockReceiverStartup
{
    public static bool IsReceiverOnly(IConfiguration configuration) =>
        !string.IsNullOrEmpty(configuration[StockServiceIdentityMiddleware.AuthorityFileKey]);

    /// <summary>Registers the reply-queue consumer unless this instance is a dedicated stock receiver.</summary>
    public static void AddReplyConsumer(IServiceCollection services, IConfiguration configuration)
    {
        if (IsReceiverOnly(configuration)) return;
        services.AddHostedService<TemporalRelayListener>();
    }

    /// <summary>
    /// Hosts the workflow-failure notifier (#488) unless this instance is a dedicated stock receiver: it runs no
    /// workflow machinery, so it must not poll namespaces or send a second notice for the ordinary bridge's failures.
    /// </summary>
    public static void AddFailureNotifierHost(IServiceCollection services, IConfiguration configuration)
    {
        if (IsReceiverOnly(configuration)) return;
        services.AddHostedService(sp => sp.GetRequiredService<WorkflowFailureNotifier>());
    }

    /// <summary>Namespaces to start workers for: none for a dedicated stock receiver.</summary>
    public static string[] WorkerNamespaces(IConfiguration configuration, IEnumerable<string> configured) =>
        IsReceiverOnly(configuration)
            ? []
            : configured
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}
