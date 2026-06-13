using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class SetConfigValuesTool(
    ConfigService configService,
    IConfiguration configuration,
    IHttpContextAccessor httpContextAccessor,
    ILogger<SetConfigValuesTool> logger)
{
    private static readonly Regex ValidKeyRegex = new(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    [McpServerTool(Name = "set_config_values")]
    [Description("""
        Writes one or more keys to the orchestrator .env file and broadcasts config.changed to all peer-config consumers (fleet-telegram, fleet-temporal-bridge, etc.).

        Auth: requires Authorization: Bearer <ORCHESTRATOR_CONFIG_TOKEN> (not the regular auth token). Calls without a valid config token return an authorization error.

        Parameter:
          values_json (string): JSON object mapping env var keys to string values.
                                Keys must match ^[A-Z][A-Z0-9_]*$ (uppercase, digits, underscores; must start with a letter).
                                Empty string is a valid value (sets KEY= with no value).
                                Denylisted keys (DB creds, secrets, JWT keys) are rejected with an error listing the offenders; nothing is written.

        Returns JSON:
          { "created": ["KEY1"], "updated": ["KEY2"], "unchanged": ["KEY3"], "broadcast": true }
          broadcast is false when all provided values were already identical (idempotent re-write — no peers notified).
        """)]
    public async Task<string> SetConfigValuesAsync(
        [Description(
            "JSON object mapping environment variable keys to string values. " +
            "Keys must match ^[A-Z][A-Z0-9_]*$. " +
            "Example: {\"TELEGRAM_FOO_BOT_TOKEN\":\"abc123\",\"SOME_API_KEY\":\"xyz\"}"
        )] string values_json,
        CancellationToken cancellationToken = default)
    {
        // 1. Auth — must present ORCHESTRATOR_CONFIG_TOKEN
        var configToken = configuration["Orchestrator:ConfigToken"] ?? "";
        if (string.IsNullOrWhiteSpace(configToken))
            return Err("config_api_unavailable", "ORCHESTRATOR_CONFIG_TOKEN is not configured on this orchestrator.");

        var authHeader = httpContextAccessor.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        var bearerToken = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? authHeader["Bearer ".Length..].Trim()
            : null;

        if (bearerToken != configToken)
            return Err("unauthorized",
                "Missing or invalid Authorization header. Provide the config token as 'Authorization: Bearer <ORCHESTRATOR_CONFIG_TOKEN>'.");

        // 2. Parse values_json
        Dictionary<string, string>? kvs;
        try
        {
            kvs = JsonSerializer.Deserialize<Dictionary<string, string>>(values_json, JsonOpts);
        }
        catch (JsonException ex)
        {
            return Err("invalid_json", $"Failed to parse values_json: {ex.Message}");
        }

        if (kvs is null || kvs.Count == 0)
            return Err("empty_payload", "values_json must be a non-empty JSON object.");

        // 3. Validate key format
        var invalidKeys = kvs.Keys.Where(k => !ValidKeyRegex.IsMatch(k)).ToList();
        if (invalidKeys.Count > 0)
            return Err("invalid_keys",
                $"Keys must match ^[A-Z][A-Z0-9_]*$. Invalid key(s): {string.Join(", ", invalidKeys)}");

        // 4. Check denylisted keys — reject entire batch, nothing written
        var denylisted = kvs.Keys.Where(ConfigService.IsDenylisted).ToList();
        if (denylisted.Count > 0)
            return Err("denylisted",
                $"These keys cannot be written via the config API: {string.Join(", ", denylisted)}");

        // 5. Snapshot which keys already exist (before write) to classify created vs updated
        var existingBefore = configService.GetExistingKeys(kvs.Keys);

        // 6. Atomic write + broadcast (reuses ConfigService.PutValuesAsync — no duplicated write logic)
        List<string> changedKeys;
        try
        {
            changedKeys = await configService.PutValuesAsync(kvs, cancellationToken);
        }
        catch (DenylistedException ex)
        {
            // Defense-in-depth: ConfigService also validates the denylist
            return Err("denylisted", ex.Message);
        }
        catch (TimeoutException ex)
        {
            return Err("write_timeout", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "set_config_values: write failed for {Count} key(s)", kvs.Count);
            return Err("write_failed", ex.Message);
        }

        // 7. Build structured response
        var changedSet = changedKeys.ToHashSet(StringComparer.Ordinal);
        var created   = changedKeys.Where(k => !existingBefore.Contains(k)).ToList();
        var updated   = changedKeys.Where(k =>  existingBefore.Contains(k)).ToList();
        var unchanged = kvs.Keys.Where(k => !changedSet.Contains(k)).ToList();
        var broadcast = changedKeys.Count > 0;

        // Log key names + value lengths only — never raw values
        foreach (var k in changedKeys)
        {
            logger.LogInformation(
                "set_config_values: {Action} key '{Key}' (value_length={Len})",
                existingBefore.Contains(k) ? "updated" : "created",
                k, kvs[k].Length);
        }
        if (unchanged.Count > 0)
            logger.LogDebug("set_config_values: {Count} key(s) unchanged — no broadcast", unchanged.Count);

        return JsonSerializer.Serialize(new { created, updated, unchanged, broadcast }, JsonOpts);
    }

    private static string Err(string code, string detail) =>
        JsonSerializer.Serialize(new { error = code, detail }, JsonOpts);
}
