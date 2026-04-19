namespace Fleet.Orchestrator.Services;

/// <summary>
/// Publishes a <c>config.changed</c> event on the fleet.orchestrator RabbitMQ exchange
/// so peer infra services (fleet-telegram, fleet-bridge, fleet-temporal-bridge) can
/// refresh their in-memory caches without restarting.
/// </summary>
public interface IConfigChangedPublisher
{
    /// <summary>
    /// Attempts to publish a <c>config.changed</c> event. Failures are logged at
    /// warning level and swallowed — callers must not roll back their write on failure.
    /// </summary>
    Task TryPublishAsync(string? agentName = null, CancellationToken ct = default);
}
