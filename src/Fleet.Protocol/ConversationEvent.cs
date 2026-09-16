using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fleet.Protocol;

/// <summary>
/// The versioned envelope (D4).
///
/// <para><see cref="Seq"/> is monotonic per conversation, assigned synchronously at publish,
/// starting at 1. It is a gap-detection aid, NOT a durability or delivery guarantee — no store
/// exists in this phase and a dropped event leaves a permanent gap (Constraint 14).</para>
///
/// <para><see cref="EventId"/> is unique per emitted event. A redelivered event keeps its
/// <see cref="EventId"/> and increments <c>identity.attempt</c>; receivers dedupe on
/// <see cref="EventId"/>.</para>
///
/// <para>Unknown <see cref="Kind"/> values and unknown payload fields MUST be ignored by a
/// receiver, never treated as fatal.</para>
/// </summary>
public sealed record ConversationEvent
{
    public required string Protocol { get; init; }
    public required string EventId { get; init; }
    public required long Seq { get; init; }
    public required DateTimeOffset EmittedAt { get; init; }
    public required string Kind { get; init; }
    public required ConversationIdentity Identity { get; init; }

    /// <summary>
    /// The kind-specific payload. Typed as <see cref="JsonNode"/> so the envelope itself stays
    /// one shape on the wire while each kind keeps a sealed payload record (D12: enforcement is
    /// structural — a denied field cannot be attached without editing the contract).
    /// </summary>
    public JsonNode? Payload { get; init; }

    /// <summary>True for the four terminal kinds (D11).</summary>
    public bool IsTerminal => ConversationEventKind.Terminal.Contains(Kind);

    /// <summary>
    /// Build an envelope from a typed payload record. The payload is serialized through
    /// <see cref="FleetProtocolJson.Options"/>, which is the only supported configuration.
    /// </summary>
    public static ConversationEvent Create<TPayload>(
        string kind,
        ConversationIdentity identity,
        string eventId,
        long seq,
        DateTimeOffset emittedAt,
        TPayload? payload) where TPayload : class =>
        new()
        {
            Protocol = ProtocolVersion.Current,
            EventId = eventId,
            Seq = seq,
            EmittedAt = emittedAt,
            Kind = kind,
            Identity = identity,
            Payload = payload is null
                ? null
                : JsonSerializer.SerializeToNode(payload, FleetProtocolJson.Options),
        };

    /// <summary>Deserialize the payload as a typed record, or null when absent/incompatible.</summary>
    public TPayload? PayloadAs<TPayload>() where TPayload : class =>
        Payload is null ? null : Payload.Deserialize<TPayload>(FleetProtocolJson.Options);
}
