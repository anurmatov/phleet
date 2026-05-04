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
/// Supports Claude, Codex, and Gemini providers. No agent delegation needed — avoids the
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

    // --- Gemini constants ---
    private const string GeminiCredentialsPath = "/root/.gemini/oauth_creds.json";
    private const string GeminiTokenEndpoint = "https://oauth2.googleapis.com/token";
    // Google access tokens are valid for 1 hour; refresh 15 minutes before expiry.
    private static readonly TimeSpan GeminiRefreshThreshold = TimeSpan.FromMinutes(15);

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
            "gemini" => await RefreshGeminiAsync(ActivityExecutionContext.Current.CancellationToken, forceRefresh),
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
    // Gemini
    // -------------------------------------------------------------------------

    private async Task<AgentTokenResult> RefreshGeminiAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!File.Exists(GeminiCredentialsPath))
            throw new InvalidOperationException($"Gemini credentials file not found at {GeminiCredentialsPath}");

        var json = await File.ReadAllTextAsync(GeminiCredentialsPath, ct);
        var creds = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("Failed to parse Gemini oauth_creds.json");

        // Gemini oauth_creds.json stores expiry as epoch-milliseconds in expiry_date.
        var expiryDateMs = creds["expiry_date"]?.GetValue<long>()
            ?? throw new InvalidOperationException("Missing expiry_date in Gemini oauth_creds.json");

        var refreshToken = creds["refresh_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing refresh_token in Gemini oauth_creds.json");

        // client_id and client_secret are stored inline in the file (unlike Claude/Codex).
        var clientId = creds["client_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing client_id in Gemini oauth_creds.json");
        var clientSecret = creds["client_secret"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing client_secret in Gemini oauth_creds.json");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var remainingMs = expiryDateMs - nowMs;

        if (!forceRefresh && remainingMs > GeminiRefreshThreshold.TotalMilliseconds)
        {
            _logger.LogInformation(
                "Gemini token still valid for {Minutes:F0} minutes, no refresh needed",
                remainingMs / 60_000.0);
            return new AgentTokenResult(Refreshed: false, Provider: "gemini");
        }

        if (forceRefresh)
            _logger.LogInformation("Force-refreshing Gemini token (remaining: {Minutes:F0} minutes)", remainingMs / 60_000.0);

        _logger.LogInformation(
            "Gemini token expires in {Minutes:F0} minutes (threshold: {Threshold}m), refreshing",
            remainingMs / 60_000.0, GeminiRefreshThreshold.TotalMinutes);

        File.Copy(GeminiCredentialsPath, GeminiCredentialsPath + ".backup.json", overwrite: true);

        using var client = _httpClientFactory.CreateClient();
        var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
        });

        var response = await client.PostAsync(GeminiTokenEndpoint, requestContent, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            // 400 with error=invalid_grant means the refresh token was revoked — not retryable.
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && body.Contains("invalid_grant"))
                throw new ApplicationFailureException(
                    $"Gemini refresh token revoked (invalid_grant). Manual re-auth required via `gemini auth` on host. Body: {body}",
                    nonRetryable: true);

            throw new InvalidOperationException(
                $"Gemini token refresh failed with {response.StatusCode}: {body}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var tokenResponse = JsonNode.Parse(responseJson);

        var newAccessToken = tokenResponse?["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing access_token in Gemini refresh response");

        // Google does not rotate refresh tokens on access token refresh.
        var expiresIn = tokenResponse?["expires_in"]?.GetValue<long>() ?? 3600L;
        var newExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000;

        // Update oauth_creds.json atomically. Preserve all existing fields; only overwrite
        // access_token and expiry_date. refresh_token, client_id, client_secret stay unchanged.
        creds["access_token"] = newAccessToken;
        creds["expiry_date"] = newExpiresAt;

        var tmpPath = GeminiCredentialsPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath,
            creds.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Move(tmpPath, GeminiCredentialsPath, overwrite: true);

        _logger.LogInformation("Gemini token refreshed, new expiry in {ExpiresIn}s", expiresIn);

        return new AgentTokenResult(
            Refreshed: true,
            Provider: "gemini",
            AccessToken: newAccessToken,
            // Google does not issue a new refresh_token; pass the existing one for broadcast consistency.
            RefreshToken: refreshToken,
            ExpiresAt: newExpiresAt);
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
