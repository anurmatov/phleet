using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Activities;

/// <summary>
/// Temporal activity that checks OAuth token expiry and refreshes directly via HTTP.
/// Supports Claude and Codex providers. No agent delegation needed — avoids the
/// chicken-and-egg problem where an expired token prevents the agent from refreshing.
/// </summary>
public sealed class RefreshAuthTokenActivity
{
    // --- Claude constants ---
    private const string ClaudeCredentialsPath = "/root/.claude/.credentials.json";
    private const string ClaudeTokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private static readonly TimeSpan ClaudeRefreshThreshold = TimeSpan.FromMinutes(45);

    // --- Codex constants ---
    private const string CodexCredentialsPath = "/root/.codex/auth.json";
    private const string CodexTokenEndpoint = "https://auth.openai.com/oauth/token";
    private static readonly TimeSpan CodexRefreshThreshold = TimeSpan.FromDays(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RefreshAuthTokenActivity> _logger;
    private readonly AuthTokenRefreshOptions _opts;

    public RefreshAuthTokenActivity(
        IHttpClientFactory httpClientFactory,
        IOptions<AuthTokenRefreshOptions> opts,
        ILogger<RefreshAuthTokenActivity> logger)
    {
        _httpClientFactory = httpClientFactory;
        _opts = opts.Value;
        _logger = logger;
    }

    [Activity]
    public async Task<AgentTokenResult> CheckAndRefreshAsync(string provider = "claude", bool forceRefresh = false)
    {
        return provider.ToLowerInvariant() switch
        {
            "codex" => await RefreshCodexAsync(ActivityExecutionContext.Current.CancellationToken, forceRefresh),
            _ => await RefreshClaudeAsync(ActivityExecutionContext.Current.CancellationToken, forceRefresh),
        };
    }

    // -------------------------------------------------------------------------
    // Claude
    // -------------------------------------------------------------------------

    private async Task<AgentTokenResult> RefreshClaudeAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!File.Exists(ClaudeCredentialsPath))
            throw new InvalidOperationException($"Credentials file not found at {ClaudeCredentialsPath}");

        var json = await File.ReadAllTextAsync(ClaudeCredentialsPath, ct);
        var creds = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("Failed to parse Claude credentials JSON");

        var oauth = creds["claudeAiOauth"]
            ?? throw new InvalidOperationException("No claudeAiOauth section in credentials");

        var expiresAt = oauth["expiresAt"]?.GetValue<long>()
            ?? throw new InvalidOperationException("Missing expiresAt in credentials");

        var refreshToken = oauth["refreshToken"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing refreshToken in credentials");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresInMs = expiresAt - nowMs;

        if (!forceRefresh && expiresInMs > ClaudeRefreshThreshold.TotalMilliseconds)
        {
            _logger.LogInformation(
                "Claude token still valid for {Minutes:F0} minutes, no refresh needed",
                expiresInMs / 60_000.0);
            return new AgentTokenResult(Refreshed: false, Provider: "claude");
        }

        if (forceRefresh)
            _logger.LogInformation("Force-refreshing Claude token (remaining: {Minutes:F0} minutes)", expiresInMs / 60_000.0);

        _logger.LogInformation(
            "Claude token expires in {Minutes:F0} minutes (threshold: {Threshold}m), refreshing",
            expiresInMs / 60_000.0, ClaudeRefreshThreshold.TotalMinutes);

        File.Copy(ClaudeCredentialsPath, ClaudeCredentialsPath + ".backup.json", overwrite: true);

        using var client = _httpClientFactory.CreateClient();
        var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _opts.ClaudeClientId,
            ["refresh_token"] = refreshToken,
        });

        var response = await client.PostAsync(ClaudeTokenEndpoint, requestContent, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Claude token refresh failed with {response.StatusCode}: {body}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var tokenResponse = JsonNode.Parse(responseJson);

        var newAccessToken = tokenResponse?["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing access_token in Claude refresh response");
        var newRefreshToken = tokenResponse?["refresh_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing refresh_token in Claude refresh response");
        var expiresIn = tokenResponse?["expires_in"]?.GetValue<long>()
            ?? throw new InvalidOperationException("Missing expires_in in Claude refresh response");

        var newExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000;

        oauth["accessToken"] = newAccessToken;
        oauth["refreshToken"] = newRefreshToken;
        oauth["expiresAt"] = newExpiresAt;

        var tmpPath = ClaudeCredentialsPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath,
            creds.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Copy(tmpPath, ClaudeCredentialsPath, overwrite: true);
        File.Delete(tmpPath);

        var scopes = oauth["scopes"]?.AsArray()
            .Select(s => s?.GetValue<string>())
            .Where(s => s is not null)
            .Cast<string>()
            .ToArray();
        var subscriptionType = oauth["subscriptionType"]?.GetValue<string>();
        var rateLimitTier = oauth["rateLimitTier"]?.GetValue<string>();

        _logger.LogInformation("Claude token refreshed, new expiry in {ExpiresIn}s", expiresIn);

        return new AgentTokenResult(
            Refreshed: true,
            Provider: "claude",
            AccessToken: newAccessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: newExpiresAt,
            Scopes: scopes,
            SubscriptionType: subscriptionType,
            RateLimitTier: rateLimitTier);
    }

    // -------------------------------------------------------------------------
    // Codex
    // -------------------------------------------------------------------------

    private async Task<AgentTokenResult> RefreshCodexAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!File.Exists(CodexCredentialsPath))
            throw new InvalidOperationException($"Codex credentials file not found at {CodexCredentialsPath}");

        var json = await File.ReadAllTextAsync(CodexCredentialsPath, ct);
        var auth = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("Failed to parse Codex auth.json");

        var tokens = auth["tokens"]
            ?? throw new InvalidOperationException("No tokens section in Codex auth.json");

        var accessToken = tokens["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing tokens.access_token in Codex auth.json");

        var refreshToken = tokens["refresh_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing tokens.refresh_token in Codex auth.json");

        var accountId = tokens["account_id"]?.GetValue<string>();
        var existingIdToken = tokens["id_token"]?.GetValue<string>();

        // Decode JWT exp claim (Unix seconds) to determine remaining validity
        long expSeconds;
        try
        {
            expSeconds = DecodeJwtExpiry(accessToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decode Codex access_token JWT exp claim — refreshing unconditionally");
            expSeconds = 0;
        }

        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var remainingSeconds = expSeconds - nowSeconds;

        if (!forceRefresh && remainingSeconds > CodexRefreshThreshold.TotalSeconds)
        {
            _logger.LogInformation(
                "Codex token still valid for {Days:F1} days, no refresh needed",
                remainingSeconds / 86400.0);
            return new AgentTokenResult(Refreshed: false, Provider: "codex");
        }

        if (forceRefresh)
            _logger.LogInformation("Force-refreshing Codex token (remaining: {Days:F1} days)", remainingSeconds / 86400.0);

        _logger.LogInformation(
            "Codex token expires in {Days:F1} days (threshold: {Threshold}d), refreshing",
            remainingSeconds / 86400.0, CodexRefreshThreshold.TotalDays);

        File.Copy(CodexCredentialsPath, CodexCredentialsPath + ".backup.json", overwrite: true);

        using var client = _httpClientFactory.CreateClient();
        var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _opts.CodexClientId,
            ["refresh_token"] = refreshToken,
        });

        var response = await client.PostAsync(CodexTokenEndpoint, requestContent, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            // 401 = refresh token revoked — not retryable, requires manual re-login
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new ApplicationFailureException(
                    $"Codex refresh token revoked (401). Manual re-login required via `codex login` on host machine. Body: {body}",
                    nonRetryable: true);

            throw new InvalidOperationException(
                $"Codex token refresh failed with {response.StatusCode}: {body}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var tokenResponse = JsonNode.Parse(responseJson);

        var newAccessToken = tokenResponse?["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing access_token in Codex refresh response");
        var newRefreshToken = tokenResponse?["refresh_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing refresh_token in Codex refresh response");
        var newIdToken = tokenResponse?["id_token"]?.GetValue<string>() ?? existingIdToken;

        // Decode new expiry for broadcast
        long newExpSeconds;
        try { newExpSeconds = DecodeJwtExpiry(newAccessToken); }
        catch { newExpSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (int)TimeSpan.FromDays(10).TotalSeconds; }

        var newExpiresAt = newExpSeconds * 1000L; // convert to milliseconds for broadcast consistency

        // Update auth.json atomically, preserving auth_mode, OPENAI_API_KEY, account_id
        tokens["access_token"] = newAccessToken;
        tokens["refresh_token"] = newRefreshToken;
        if (newIdToken is not null)
            tokens["id_token"] = newIdToken;
        auth["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");

        var tmpPath = CodexCredentialsPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath,
            auth.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Copy(tmpPath, CodexCredentialsPath, overwrite: true);
        File.Delete(tmpPath);

        _logger.LogInformation("Codex token refreshed, new expiry: {Expiry}",
            DateTimeOffset.FromUnixTimeSeconds(newExpSeconds));

        return new AgentTokenResult(
            Refreshed: true,
            Provider: "codex",
            AccessToken: newAccessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: newExpiresAt,
            IdToken: newIdToken,
            AccountId: accountId);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Decodes the exp claim (Unix seconds) from a JWT without a full JWT library.</summary>
    private static long DecodeJwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            throw new InvalidOperationException("Invalid JWT format — expected 3 parts");

        // Base64Url → Base64
        var base64 = parts[1].Replace('-', '+').Replace('_', '/');
        var padding = (4 - base64.Length % 4) % 4;
        base64 += new string('=', padding);

        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        var node = JsonNode.Parse(payloadJson);
        return node?["exp"]?.GetValue<long>()
            ?? throw new InvalidOperationException("Missing exp claim in JWT payload");
    }
}
