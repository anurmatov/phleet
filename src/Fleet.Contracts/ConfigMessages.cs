using System.Text.Json.Serialization;

namespace Fleet.Contracts;

/// <summary>
/// Published by the orchestrator on <see cref="OrchestratorBroker.Exchange"/> under
/// <see cref="OrchestratorBroker.ConfigChanged"/> when deployment configuration changes. It names
/// the keys and never carries a value: a peer that holds one of them fetches its own slice.
/// </summary>
public sealed record ConfigChangedEvent(
    [property: JsonPropertyName("changedKeys")] IReadOnlyList<string> ChangedKeys);

/// <summary>
/// The text of a <see cref="RelayMessageType.TokenUpdate"/> message: a refreshed provider OAuth
/// token, broadcast by the temporal bridge on <see cref="RelayBroker.TokenBroadcastExchange"/>.
/// </summary>
/// <remarks>
/// A credential. It is written to the broker for agents that hold the same provider's credential
/// and to nothing else: never logged, never returned. <see cref="Provider"/> is how an agent
/// decides whether the token is for it.
/// </remarks>
public sealed record TokenUpdatePayload(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt,
    [property: JsonPropertyName("scopes")] string[]? Scopes,
    [property: JsonPropertyName("subscriptionType")] string? SubscriptionType,
    [property: JsonPropertyName("rateLimitTier")] string? RateLimitTier,
    [property: JsonPropertyName("idToken")] string? IdToken,
    [property: JsonPropertyName("accountId")] string? AccountId);
