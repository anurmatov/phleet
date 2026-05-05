using Temporalio.Client;

namespace Fleet.Temporal.Mcp;

/// <summary>
/// Abstraction over <see cref="TemporalClientFactory"/> for per-namespace client creation.
/// Allows <see cref="TemporalWorkflowDispatcher"/> to be unit-tested without a live Temporal server.
/// </summary>
public interface ITemporalClientFactory
{
    /// <summary>Returns a cached <see cref="ITemporalClient"/> for the given namespace.</summary>
    Task<ITemporalClient> GetClientAsync(string @namespace);
}
