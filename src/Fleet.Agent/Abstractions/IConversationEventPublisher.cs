using Fleet.Protocol;

namespace Fleet.Agent.Abstractions;

/// <summary>
/// Runtime → adapters (D15).
///
/// <see cref="Publish"/> is deliberately <c>void</c> and synchronous. The executor, the Telegram
/// send path and the task manager must NEVER wait on an adapter (Constraint 6), so this method
/// assigns the sequence number and event id, hands the event to a bounded structure, and returns.
/// It never awaits delivery and never blocks on a lock held across I/O.
/// </summary>
public interface IConversationEventPublisher
{
    /// <summary>
    /// Publish an already-built event. Returns immediately; delivery happens on the pump.
    /// Never throws — a publication failure is counted, not propagated into a turn.
    /// </summary>
    void Publish(ConversationEvent evt);

    /// <summary>
    /// Build and publish in one step, assigning <c>seq</c> and <c>eventId</c> at the publish
    /// moment so <c>seq</c> reflects EMISSION order rather than delivery order (D11).
    /// </summary>
    /// <returns>
    /// True when the event was accepted into a queue. False when it was dropped — used by the
    /// terminal-publication bookkeeping, never to retry.
    /// </returns>
    bool Publish<TPayload>(long runtimeConversationKey, string kind, ConversationIdentity identity, TPayload? payload)
        where TPayload : class;
}
