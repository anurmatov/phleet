using Fleet.Agent.Abstractions;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// The <see cref="IChannelAdapter"/> for the <c>client</c> channel. <b>Translation only</b>
/// (#303 D10).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConversationEventPump.DeliverAsync"/> wraps every adapter call in a five-second
/// timeout and swallows every exception, so an adapter can never fault a turn. That is the right
/// property for delivery and the wrong place for durability: a bounded retry loop written here would
/// be cancelled mid-retry, and its failure discarded silently, while the turn was counted as
/// delivered. So this class performs <b>no HTTP, no retry and no blocking wait</b> (MUST NOT 4). It
/// converts, classifies, hands off without blocking, and returns.
/// </para>
/// <para>
/// It also allocates <b>no ordinal</b> (MUST NOT 9, D7a). The ordinal is taken at the send site
/// inside <see cref="ConversationSouthConsumer"/>, because the bus delivers a terminal ahead of
/// earlier progress by design and an ordinal minted here would describe an order nothing keeps.
/// <c>ConversationSouthAdapterTests</c> asserts the absence of that dependency structurally —
/// a comment is not a guard.
/// </para>
/// <para>
/// Two kinds are <b>suppressed</b> rather than forwarded, and both for the same reason: their
/// endpoint already writes them, and append idempotency keys on <c>eventId</c>, so forwarding the
/// runtime's copy writes a SECOND row rather than deduplicating.
/// </para>
/// <list type="bullet">
///   <item><c>submission.accepted</c> — written by <c>/submissions:disposition</c> (D4a).</item>
///   <item><c>turn.started</c> — written by <c>/turns:start</c>, which is called <i>because</i> this
///   event arrived, using its <c>identity.turnId</c> (D4).</item>
/// </list>
/// </remarks>
public sealed class ConversationSouthAdapter : IChannelAdapter
{
    private readonly ConversationSouthHandoff _handoff;
    private readonly ConversationSouthCounters _counters;
    private readonly ILogger<ConversationSouthAdapter> _logger;

    public ConversationSouthAdapter(
        ConversationSouthHandoff handoff,
        ConversationSouthCounters counters,
        ILogger<ConversationSouthAdapter> logger)
    {
        _handoff = handoff;
        _counters = counters;
        _logger = logger;
    }

    /// <summary>
    /// <c>client</c> — the same channel id the service stamps on every command envelope it
    /// publishes.
    /// </summary>
    public string ChannelId => ClientChannel;

    /// <summary>The channel id the north surface uses for first-party client conversations.</summary>
    public const string ClientChannel = "client";

    /// <inheritdoc/>
    public Task DeliverAsync(ConversationEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // D4a / D4: the endpoint owns these two, and the event ids differ, so forwarding duplicates.
        if (evt.Kind is ConversationEventKind.SubmissionAccepted)
        {
            _counters.AdapterDiscarded("submission_accepted_suppressed");
            return Task.CompletedTask;
        }

        var conversationId = evt.Identity.ConversationId;

        if (string.IsNullOrEmpty(conversationId))
        {
            // A pre-turn rejection built by ConversationIdentity.ForChannel carries no conversation,
            // so there is nothing durable to append it to. Counted rather than dropped in silence.
            _counters.AdapterDiscarded("no_conversation");
            return Task.CompletedTask;
        }

        var action = Classify(evt.Kind);

        if (action is null)
        {
            // An outbound kind with no south meaning. Not an error — but not invisible either.
            _counters.AdapterDiscarded(evt.Kind);
            return Task.CompletedTask;
        }

        var item = new SouthOutboundItem
        {
            ConversationId = conversationId,
            Action = action.Value,
            // The locally-assigned seq is deliberately NOT carried (MUST NOT 7). The store allocates
            // seq at durable append and rebuilds the envelope on the way out; a number sent here
            // would be ignored and replaced, and is only ever a chance to leak the wrong one.
            Event = new EventDescriptor
            {
                Kind = evt.Kind,
                EventId = evt.EventId,
                PayloadJson = evt.Payload?.ToJsonString(),
            },
            RetentionClass = RetentionFor(evt.Kind),
            SubmissionId = string.IsNullOrEmpty(evt.Identity.SubmissionId) ? null : evt.Identity.SubmissionId,
            TurnId = evt.Identity.TurnId,
        };

        if (!_handoff.TryEnqueue(item))
        {
            _counters.HandoffRefused(evt.Kind);

            if (action is SouthOutboundAction.Commit)
            {
                // Correct but LOSSY: the attempt now degrades to lease expiry and the client is told
                // `turn.outcome_unknown { attempt_abandoned }` for a turn that actually answered.
                // Error, and its own counter, because this is not the same event as shedding a
                // typing ping.
                _counters.TerminalDropped();
                _logger.LogError(
                    "south hand-off refused a TERMINAL: kind={Kind} conversationId={ConversationId} eventId={EventId}",
                    evt.Kind, conversationId, evt.EventId);
            }
            else
            {
                _logger.LogWarning(
                    "south hand-off refused an event: kind={Kind} conversationId={ConversationId} eventId={EventId}",
                    evt.Kind, conversationId, evt.EventId);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Which south call an outbound kind becomes. <c>null</c> means "not carried south".
    /// </summary>
    /// <remarks>
    /// A terminal goes to <c>/turns:commit</c>, never <c>/events:append</c> (D6). Appending one
    /// would write the event without closing the attempt, and the reconciler would later abandon an
    /// attempt that had already answered.
    /// </remarks>
    internal static SouthOutboundAction? Classify(string kind)
    {
        if (ConversationEventKind.Terminal.Contains(kind))
            return SouthOutboundAction.Commit;

        return kind switch
        {
            ConversationEventKind.TurnStarted => SouthOutboundAction.StartTurn,

            ConversationEventKind.TurnProgress
                or ConversationEventKind.TurnNotice
                or ConversationEventKind.TurnRecoveredAnswer
                or ConversationEventKind.ControlAck
                or ConversationEventKind.ProtocolRejected => SouthOutboundAction.Append,

            // submission.accepted is handled before this call; conversation.replay_gap is synthetic
            // and never stored. Anything else is a kind with no south producer path.
            _ => null,
        };
    }

    /// <summary>
    /// Retention class per kind (D5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ephemeral: <c>turn.progress</c> and <c>turn.notice</c>. Their absence at a seq is not a gap.
    /// </para>
    /// <para>
    /// Durable: the four terminals, <c>turn.recovered_answer</c>, <c>control.ack</c> — and
    /// <c>protocol.rejected</c>. The last is not named in D5's list; it is classified durable here
    /// because it is the client's only record that a submission was refused, and a refusal that
    /// silently ages out reads to a returning client as a submission that was never made.
    /// </para>
    /// </remarks>
    internal static EventRetentionClass RetentionFor(string kind) => kind switch
    {
        ConversationEventKind.TurnProgress or ConversationEventKind.TurnNotice
            => EventRetentionClass.Ephemeral,
        _ => EventRetentionClass.Durable,
    };
}
