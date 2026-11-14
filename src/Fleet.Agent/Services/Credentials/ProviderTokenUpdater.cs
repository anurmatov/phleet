using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Agent.Configuration;
using Fleet.Shared;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.Credentials;

/// <summary>
/// Applies an OAuth token broadcast to this agent's own provider: writes the new token into the
/// provider CLI's credential file (claude, codex or gemini, each in its own format) and restarts
/// the provider process so it reads it. A broadcast for another provider, and one for an agent
/// that holds no provider credential (a local Claude model, a hosted Codex provider), changes nothing.
/// </summary>
internal sealed class ProviderTokenUpdater(AgentOptions agentConfig, IAgentExecutor executor, ILogger logger)
{
    private readonly AgentOptions _agentConfig = agentConfig;
    private readonly IAgentExecutor _executor = executor;
    private readonly ILogger _logger = logger;

    /// <summary>Where a codex token broadcast writes. Settable so tests do not touch the real home.</summary>
    internal string CodexAuthPath { get; set; } = "/root/.codex/auth.json";

    /// <summary>Where a claude token broadcast writes. Settable so tests do not touch the real home.</summary>
    internal string ClaudeCredentialsPath { get; set; } = "/root/.claude/.credentials.json";

    internal async Task ApplyAsync(string tokenJson)
    {
        var shared = JsonNode.Parse(tokenJson);
        var newAccessToken = shared?["accessToken"]?.GetValue<string>();
        var newRefreshToken = shared?["refreshToken"]?.GetValue<string>();
        var newExpiresAt = shared?["expiresAt"]?.GetValue<long>();
        if (newAccessToken is null || newExpiresAt is null)
        {
            _logger.LogWarning("Invalid token update payload");
            return;
        }

        // Route by provider — default to "claude" for backward compat with old broadcasts
        var provider = shared?["provider"]?.GetValue<string>() ?? "claude";
        var agentProvider = _agentConfig.Provider.ToLowerInvariant();

        if (!provider.Equals(agentProvider, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Token update for provider '{Provider}' ignored — this agent uses '{AgentProvider}'",
                provider, agentProvider);
            return;
        }

        if (provider == "codex")
        {
            // A hosted-provider agent holds no OpenAI credential (#335 D6), and this broadcast would
            // recreate auth.json in the persisted workspace volume. Nothing to restart either.
            if (_agentConfig.HostedProvider)
            {
                _logger.LogDebug("Codex token update ignored — this agent routes to a hosted provider");
                return;
            }

            await ApplyCodexTokenUpdateAsync(shared!, newAccessToken, newRefreshToken, newExpiresAt.Value);
        }
        else if (provider == "gemini")
        {
            await ApplyGeminiTokenUpdateAsync(newAccessToken, newExpiresAt.Value);
        }
        else
        {
            // A local-model agent holds no Claude credential (#340 D3), and this broadcast would
            // create one in the persisted /root/.claude. Nothing to restart either.
            if (ClaudeLocalModel.IsEnabled(_agentConfig.Provider, _agentConfig.AnthropicBaseUrl))
            {
                _logger.LogInformation("Claude token update ignored — local model mode");
                return;
            }

            await ApplyClaudeTokenUpdateAsync(newAccessToken, newRefreshToken, newExpiresAt.Value);
        }

        if (!await _executor.TryStopProcessAsync())
            _executor.RequestRestart();
    }

    private async Task ApplyClaudeTokenUpdateAsync(string newAccessToken, string? newRefreshToken, long newExpiresAt)
    {
        var credsPath = ClaudeCredentialsPath;

        JsonNode creds;
        if (File.Exists(credsPath))
        {
            creds = JsonNode.Parse(await File.ReadAllTextAsync(credsPath))!;
        }
        else
        {
            _logger.LogInformation("Claude credentials file not found, creating from token update");
            Directory.CreateDirectory(Path.GetDirectoryName(credsPath)!);
            creds = JsonNode.Parse("{}")!;
        }

        var oauth = creds["claudeAiOauth"];
        if (oauth is null)
        {
            oauth = JsonNode.Parse("{}")!;
            creds["claudeAiOauth"] = oauth;
        }

        oauth["accessToken"] = newAccessToken;
        oauth["expiresAt"] = newExpiresAt;
        if (newRefreshToken is not null)
            oauth["refreshToken"] = newRefreshToken;

        var tmpPath = credsPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, creds!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmpPath, credsPath, overwrite: true);

        _logger.LogInformation("Claude credentials updated, new expiry: {ExpiresAt}", newExpiresAt);
    }

    private async Task ApplyCodexTokenUpdateAsync(JsonNode payload, string newAccessToken, string? newRefreshToken, long newExpiresAt)
    {
        var authPath = CodexAuthPath;

        var newIdToken = payload["idToken"]?.GetValue<string>();
        var newAccountId = payload["accountId"]?.GetValue<string>();

        JsonNode auth;
        if (File.Exists(authPath))
        {
            auth = JsonNode.Parse(await File.ReadAllTextAsync(authPath))!;
        }
        else
        {
            _logger.LogInformation("Codex auth.json not found, creating from token update");
            Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
            auth = JsonNode.Parse("""{"auth_mode":"chatgpt","OPENAI_API_KEY":null,"tokens":{}}""")!;
        }

        var tokens = auth["tokens"];
        if (tokens is null)
        {
            tokens = JsonNode.Parse("{}")!;
            auth["tokens"] = tokens;
        }

        tokens["access_token"] = newAccessToken;
        if (newRefreshToken is not null)
            tokens["refresh_token"] = newRefreshToken;
        if (newIdToken is not null)
            tokens["id_token"] = newIdToken;
        if (newAccountId is not null)
            tokens["account_id"] = newAccountId;
        auth["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");

        var tmpPath = authPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, auth.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmpPath, authPath, overwrite: true);

        _logger.LogInformation("Codex auth.json updated, new expiry: {ExpiresAt}", newExpiresAt);
    }

    private async Task ApplyGeminiTokenUpdateAsync(string newAccessToken, long newExpiresAt)
    {
        const string credsPath = "/root/.gemini/oauth_creds.json";

        JsonNode creds;
        if (File.Exists(credsPath))
        {
            creds = JsonNode.Parse(await File.ReadAllTextAsync(credsPath))!;
        }
        else
        {
            _logger.LogInformation("Gemini oauth_creds.json not found, creating from token update");
            Directory.CreateDirectory(Path.GetDirectoryName(credsPath)!);
            creds = JsonNode.Parse("{}")!;
        }

        // Only update access_token and expiry_date — refresh_token, client_id, client_secret
        // must be preserved exactly as-is (Google does not rotate the refresh token).
        creds["access_token"] = newAccessToken;
        creds["expiry_date"] = newExpiresAt;

        var content = creds.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            await WriteCredentialFileAsync(credsPath, content);
            _logger.LogInformation("Gemini oauth_creds.json updated, new expiry: {ExpiresAt}", newExpiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write Gemini oauth_creds.json (path: {Path})", credsPath);
        }
    }

    // Write credential content to a bind-mounted file via copy-then-delete.
    // File.Move / rename(2) fails with EBUSY on Docker bind-mount targets; writing
    // through an open file descriptor (File.Copy overwrite) succeeds because it
    // opens the existing inode for writing rather than replacing the directory entry.
    internal static async Task WriteCredentialFileAsync(string finalPath, string content)
    {
        var tmpPath = finalPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, content);
        try
        {
            File.Copy(tmpPath, finalPath, overwrite: true);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(finalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }
}
