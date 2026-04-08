namespace Fleet.Temporal.Models;

/// <summary>
/// Optional input for AuthTokenRefreshWorkflow.
/// </summary>
public sealed record AuthTokenRefreshWorkflowInput(
    /// <summary>
    /// Providers to refresh. Defaults to ["claude"] if null/empty.
    /// Supported values: "claude", "codex".
    /// </summary>
    string[]? Providers = null,
    /// <summary>
    /// When true, skip the expiry threshold check and refresh immediately.
    /// Use after placing fresh credentials to rotate the refresh token and claim it for fleet.
    /// </summary>
    bool ForceRefresh = false);

/// <summary>
/// Token data returned by RefreshAuthTokenActivity.
/// Token fields are only populated when Refreshed=true.
/// </summary>
public sealed record AgentTokenResult(
    bool Refreshed,
    string Provider = "claude",
    string? AccessToken = null,
    string? RefreshToken = null,
    long? ExpiresAt = null,
    // Claude-specific fields
    string[]? Scopes = null,
    string? SubscriptionType = null,
    string? RateLimitTier = null,
    // Codex-specific fields
    string? IdToken = null,
    string? AccountId = null);
