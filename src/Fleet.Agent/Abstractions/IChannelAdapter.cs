using Fleet.Protocol;

namespace Fleet.Agent.Abstractions;

/// <summary>
/// One adapter per channel (D15).
///
/// There is deliberately no <c>bool Owns(long)</c> predicate. A predicate lets two adapters claim
/// one conversation and makes cross-route leakage a configuration mistake rather than an
/// impossible state. Ownership is instead a registry lookup: the bus resolves the conversation's
/// <see cref="ChannelId"/> and dispatches to the adapter registered under that exact id.
/// Registering two adapters with the same <see cref="ChannelId"/> is a STARTUP failure.
/// </summary>
public interface IChannelAdapter
{
    /// <summary>
    /// The channel this adapter serves. Must be unique across all registered adapters, and must
    /// not be <c>telegram</c> or <c>relay</c> — those are owned by the existing runtime paths.
    /// </summary>
    string ChannelId { get; }

    /// <summary>
    /// Deliver one event. Called only from the pump, always under a bounded timeout, never from
    /// the executor or the Telegram send path. Exceptions are caught and counted by the pump —
    /// an adapter can never fault a turn (D11).
    /// </summary>
    Task DeliverAsync(ConversationEvent evt, CancellationToken ct);
}
