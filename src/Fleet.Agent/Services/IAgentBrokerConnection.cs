using RabbitMQ.Client;

namespace Fleet.Agent.Services;

/// <summary>
/// The one RabbitMQ connection this agent holds, for anything that needs a channel on it.
/// </summary>
/// <remarks>
/// <para>
/// The agent already connects to this broker for the task, relay and orchestrator exchanges, and
/// <see cref="GroupRelayService"/> owns that connection. A second connection string to the same
/// broker would give a credential rotation two places to land and one to miss, so the conversation
/// consumer takes its own <b>channel</b> on this connection instead of opening its own.
/// </para>
/// <para>
/// <b>A channel, never the connection's lifetime.</b> Consumers of this interface must not close or
/// dispose what they are handed: the owner outlives them and relay traffic runs on it. A channel
/// fault is isolated to the channel that faulted, which is the property that makes sharing safe.
/// </para>
/// <para>
/// <c>null</c> means "not connected yet" — the owner connects with its own bounded retry, and a
/// caller that needs it before then retries rather than blocking host startup.
/// </para>
/// </remarks>
public interface IAgentBrokerConnection
{
    /// <summary>The live connection, or null while the owner has not connected.</summary>
    IConnection? Connection { get; }
}
