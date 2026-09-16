using System.Text.Json.Serialization;

namespace Fleet.Protocol;

/// <summary>
/// Immutable routing identity, carried end to end (D3).
///
/// An instance is never mutated. A redelivery or a turn-bearing derivation produces a NEW
/// record via <c>with</c>, differing only in <see cref="TurnId"/> or <see cref="Attempt"/>;
/// every other field is copied, never recomputed.
///
/// The relay correlation id is deliberately NOT part of identity — it stays on the relay path
/// and is never client-visible (D12).
/// </summary>
public sealed record ConversationIdentity
{
    /// <summary>Opaque, stable per human. Never a numeric platform id (D9).</summary>
    public required string PrincipalId { get; init; }

    /// <summary>v1 accepts <see cref="PrincipalRole.Owner"/> only.</summary>
    public required PrincipalRole Role { get; init; }

    /// <summary>Owning channel, fixed at first use. No cross-channel fan-out in this phase.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Opaque, stable per conversation.</summary>
    public required string ConversationId { get; init; }

    /// <summary>One user submission.</summary>
    public required string SubmissionId { get; init; }

    /// <summary>One executor turn. Null before a turn exists, so pre-turn events are expressible (D7).</summary>
    public string? TurnId { get; init; }

    /// <summary>1-based. Incremented only by the in-process resume path (Constraint 14).</summary>
    public required int Attempt { get; init; }

    /// <summary>Client-visible reply target. An event id, not text (D8.1).</summary>
    public string? ReplyToEventId { get; init; }

    /// <summary>Reserved for future per-agent policy. Always absent on the v1 wire (D3).</summary>
    public string? PolicyScope { get; init; }

    /// <summary>
    /// Derive a turn-bearing identity. Every other field is copied, never recomputed.
    /// </summary>
    public ConversationIdentity WithTurn(string turnId) => this with { TurnId = turnId };

    /// <summary>
    /// Derive a redelivery identity. Keeps the event id at the call site; only the attempt moves.
    /// </summary>
    public ConversationIdentity WithAttempt(int attempt) => this with { Attempt = attempt };

    /// <summary>
    /// Identity for a pre-turn rejection, where only the channel is known (D7). The remaining
    /// required fields carry the empty string and are omitted from the wire by the
    /// <c>protocol.rejected</c> serialization contract.
    /// </summary>
    public static ConversationIdentity ForChannel(string channelId) => new()
    {
        PrincipalId = "",
        Role = PrincipalRole.Owner,
        ChannelId = channelId,
        ConversationId = "",
        SubmissionId = "",
        Attempt = 1,
    };
}

/// <summary>
/// How a client asserts which principal it is (D9). This is a BINDING, not authentication:
/// a shared operator-set token exists so the owner check is deterministic rather than a guess.
/// Real authentication — device registration, per-principal credentials, revocation, replay
/// resistance — is a separate issue. <see cref="Scheme"/> exists rather than a bare string so
/// future schemes are additive.
/// </summary>
public sealed record PrincipalBinding
{
    /// <summary>The only v1 scheme. Anything else yields <c>unauthorized</c>.</summary>
    public const string LegacyOwnerScheme = "legacy-owner";

    public required string Scheme { get; init; }

    /// <summary>The opaque operator-set token the client presents. Never logged, never echoed.</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }
}
