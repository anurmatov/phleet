using Temporalio.Extensions.Hosting;

namespace Fleet.Temporal.Extensibility;

/// <summary>
/// Wraps per-namespace Temporal worker builders. Plugins call <see cref="GetWorkerBuilder"/>
/// to register their workflows and activities on the correct worker.
/// </summary>
public sealed class WorkerRegistrationContext
{
    private readonly Dictionary<string, ITemporalWorkerServiceOptionsBuilder> _builders;

    public WorkerRegistrationContext(Dictionary<string, ITemporalWorkerServiceOptionsBuilder> builders)
    {
        _builders = builders;
    }

    /// <summary>
    /// Get the worker builder for a specific namespace.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown if the namespace has no worker configured.</exception>
    public ITemporalWorkerServiceOptionsBuilder GetWorkerBuilder(string @namespace)
    {
        if (!_builders.TryGetValue(@namespace, out var builder))
            throw new KeyNotFoundException(
                $"No worker configured for namespace '{@namespace}'. Available: {string.Join(", ", _builders.Keys)}");
        return builder;
    }

    /// <summary>All namespace names that have workers configured.</summary>
    public IReadOnlyCollection<string> Namespaces => _builders.Keys;
}
