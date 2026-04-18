using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Handles post-install setup: reading setup status, atomic .env writes,
/// Telegram/GitHub credential validation, and surgical container restarts.
/// Shared SemaphoreSlim(1,1) ensures only one setup operation runs at a time.
/// </summary>
public sealed class SetupService
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

    // Known-weak default values that count as "not configured"
    private static readonly HashSet<string> WeakDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "changeme", "your-secret-token-here", "0", "false",
        "minioadmin", "fleetroot", "fleetpass",
        "123456", "base64-encoded-private-key-here",
    };

    // Containers that are compose-managed (no DB record) and must be recreated via Docker API
    private static readonly string[] InfraContainerNames =
        ["fleet-bridge", "fleet-telegram", "fleet-temporal-bridge"];

    // Dependency map: env key → which infra containers need restart
    private static readonly Dictionary<string, string[]> InfraDepMap = new()
    {
        ["TELEGRAM_CTO_BOT_TOKEN"]      = ["fleet-telegram"],
        ["TELEGRAM_NOTIFIER_BOT_TOKEN"] = ["fleet-bridge", "fleet-telegram"],
        ["FLEET_GROUP_CHAT_ID"]         = ["fleet-bridge", "fleet-temporal-bridge"],
        ["TELEGRAM_USER_ID"]            = [],
        ["GITHUB_APP_ID"]               = [],
        ["GITHUB_APP_PEM"]              = [],
    };

    private readonly string _envFilePath;
    private readonly IConfiguration _config;
    private readonly DockerService _docker;
    private readonly ContainerProvisioningService _provisioning;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SetupService> _logger;

    public SetupService(
        IConfiguration config,
        DockerService docker,
        ContainerProvisioningService provisioning,
        IServiceScopeFactory scopeFactory,
        ILogger<SetupService> logger)
    {
        _config = config;
        _docker = docker;
        _provisioning = provisioning;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _envFilePath = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public SetupStatusDto GetStatus()
    {
        Dictionary<string, string> env;
        try { env = LoadEnvFile(_envFilePath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read .env at {Path} — returning all-false status", _envFilePath);
            return new SetupStatusDto(
                new TelegramStatusDto(false, false),
                new GitHubStatusDto(false));
        }

        var telegramConfigured =
            IsConfigured(env, "TELEGRAM_CTO_BOT_TOKEN") &&
            IsConfigured(env, "TELEGRAM_NOTIFIER_BOT_TOKEN");

        var groupEnabled =
            IsConfigured(env, "FLEET_GROUP_CHAT_ID") &&
            env.TryGetValue("FLEET_GROUP_CHAT_ID", out var g) && g != "0";

        var githubConfigured =
            IsConfigured(env, "GITHUB_APP_ID") &&
            IsConfigured(env, "GITHUB_APP_PEM");

        return new SetupStatusDto(
            new TelegramStatusDto(telegramConfigured, groupEnabled),
            new GitHubStatusDto(githubConfigured));
    }

    /// <summary>
    /// Returns rich connection status with masked current values and last-validated timestamps.
    /// </summary>
    public RichConnectionsStatusDto GetRichConnectionsStatus()
    {
        Dictionary<string, string> env;
        try { env = LoadEnvFile(_envFilePath); }
        catch { env = new(); }

        string? Mask(string key, int visibleSuffix = 4)
        {
            if (!env.TryGetValue(key, out var v) || string.IsNullOrEmpty(v)) return null;
            if (v.Length <= visibleSuffix) return new string('•', 3) + v;
            return new string('•', 3) + v[^visibleSuffix..];
        }

        var telegramConfigured =
            IsConfigured(env, "TELEGRAM_CTO_BOT_TOKEN") &&
            IsConfigured(env, "TELEGRAM_NOTIFIER_BOT_TOKEN");

        var githubConfigured =
            IsConfigured(env, "GITHUB_APP_ID") &&
            IsConfigured(env, "GITHUB_APP_PEM");

        DateTime? ParseUtc(string key) =>
            env.TryGetValue(key, out var v) && DateTime.TryParse(v, out var dt) ? dt : null;

        var userId = env.TryGetValue("TELEGRAM_USER_ID", out var uid) && !string.IsNullOrEmpty(uid) ? uid : null;

        return new RichConnectionsStatusDto(
            new RichTelegramStatusDto(
                telegramConfigured,
                IsConfigured(env, "FLEET_GROUP_CHAT_ID"),
                Mask("TELEGRAM_CTO_BOT_TOKEN"),
                Mask("TELEGRAM_NOTIFIER_BOT_TOKEN"),
                env.TryGetValue("FLEET_GROUP_CHAT_ID", out var g) && !string.IsNullOrEmpty(g) ? g : null,
                userId,
                ParseUtc("TELEGRAM_LAST_VALIDATED_UTC")),
            new RichGitHubStatusDto(
                githubConfigured,
                Mask("GITHUB_APP_ID"),
                env.TryGetValue("GITHUB_APP_PEM", out var pem) && !string.IsNullOrEmpty(pem)
                    ? $"•••[{pem.Length} chars]" : null,
                ParseUtc("GITHUB_LAST_VALIDATED_UTC")));
    }

    public async Task UpdateLastValidatedAsync(string provider)
    {
        var key = provider.Equals("github", StringComparison.OrdinalIgnoreCase)
            ? "GITHUB_LAST_VALIDATED_UTC"
            : "TELEGRAM_LAST_VALIDATED_UTC";
        try
        {
            await AtomicWriteEnvAsync(new Dictionary<string, string>
            {
                [key] = DateTime.UtcNow.ToString("O")
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write last-validated timestamp for {Provider}", provider);
        }
    }

    /// <summary>
    /// Returns infra container names that depend on the given env key (from InfraDepMap).
    /// Used by PUT /api/env-vars/{key} to tell the UI which services need a restart.
    /// </summary>
    public List<string> GetAffectedServices(string key) =>
        InfraDepMap.TryGetValue(key, out var containers)
            ? [.. containers]
            : [];

    // Keys managed via the Connections cards — excluded from generic env-vars table
    private static readonly HashSet<string> ConnectionManagedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "TELEGRAM_CTO_BOT_TOKEN", "TELEGRAM_NOTIFIER_BOT_TOKEN", "FLEET_GROUP_CHAT_ID",
        "TELEGRAM_USER_ID", "GITHUB_APP_ID", "GITHUB_APP_PEM",
        "TELEGRAM_LAST_VALIDATED_UTC", "GITHUB_LAST_VALIDATED_UTC",
    };

    private static readonly HashSet<string> SensitiveKeyPatterns =
        new(StringComparer.OrdinalIgnoreCase) { "KEY", "SECRET", "TOKEN", "PASSWORD", "CREDENTIAL", "PEM" };

    private static bool IsSensitive(string key) =>
        SensitiveKeyPatterns.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static string MaskValue(string key, string value)
    {
        if (!IsSensitive(key)) return value;
        if (value.Length <= 4) return new string('•', 3);
        return "•••" + value[^4..];
    }

    public async Task<List<EnvVarDto>> GetEnvVarsAsync(IServiceScopeFactory scopeFactory, CancellationToken ct = default)
    {
        Dictionary<string, string> env;
        try { env = LoadEnvFile(_envFilePath); }
        catch { env = new(); }

        // Build used-by map from DB
        var usedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var refs = await db.AgentEnvRefs
                .Include(r => r.Agent)
                .ToListAsync(ct);
            foreach (var r in refs)
            {
                if (!usedBy.ContainsKey(r.EnvKeyName)) usedBy[r.EnvKeyName] = [];
                usedBy[r.EnvKeyName].Add(r.Agent.Name);
            }
        }
        catch { /* DB optional */ }

        return env
            .Where(kv => !ConnectionManagedKeys.Contains(kv.Key))
            .Select(kv =>
            {
                var consumers = usedBy.TryGetValue(kv.Key, out var ub) ? [.. ub] : new List<string>();
                // Also surface docker-compose infra containers that depend on this key
                if (InfraDepMap.TryGetValue(kv.Key, out var infraContainers))
                    foreach (var c in infraContainers)
                        if (!consumers.Contains(c, StringComparer.OrdinalIgnoreCase))
                            consumers.Add(c);
                return new EnvVarDto(kv.Key, MaskValue(kv.Key, kv.Value), IsSensitive(kv.Key), consumers);
            })
            .OrderBy(v => v.Key)
            .ToList();
    }

    public async Task SetEnvVarAsync(string key, string value)
    {
        if (ConnectionManagedKeys.Contains(key))
            throw new InvalidOperationException($"Key '{key}' is managed via the Connections section — use the Telegram or GitHub card to update it.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Z][A-Z0-9_]*$"))
            throw new ArgumentException($"Key '{key}' must be uppercase letters, digits, and underscores.");

        await AtomicWriteEnvAsync(new Dictionary<string, string> { [key] = value });
    }

    public async Task DeleteEnvVarAsync(string key)
    {
        if (ConnectionManagedKeys.Contains(key))
            throw new InvalidOperationException($"Key '{key}' is managed via the Connections section.");

        var lines = File.Exists(_envFilePath)
            ? await File.ReadAllLinesAsync(_envFilePath)
            : Array.Empty<string>();

        var updated = lines.Where(line =>
        {
            var t = line.Trim();
            if (t.StartsWith('#') || !t.Contains('=')) return true;
            var k = t[..t.IndexOf('=')].Trim();
            return !k.Equals(key, StringComparison.Ordinal);
        }).ToArray();

        var tmpPath = _envFilePath + ".tmp";
        await File.WriteAllLinesAsync(tmpPath, updated);
        File.Move(tmpPath, _envFilePath, overwrite: true);
    }

    public string? RevealEnvVar(string key)
    {
        try
        {
            var env = LoadEnvFile(_envFilePath);
            return env.TryGetValue(key, out var v) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the installer's Telegram user ID from TELEGRAM_USER_ID in .env,
    /// or null if the key is absent, empty, or a non-numeric placeholder.
    /// </summary>
    public long? GetTelegramUserId()
    {
        try
        {
            var env = LoadEnvFile(_envFilePath);
            if (env.TryGetValue("TELEGRAM_USER_ID", out var val)
                && !string.IsNullOrWhiteSpace(val)
                && long.TryParse(val, out var id))
                return id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read TELEGRAM_USER_ID from .env");
        }
        return null;
    }

    // ── Telegram ──────────────────────────────────────────────────────────────

    public async Task<TelegramValidateResult> ValidateTelegramAsync(
        TelegramSetupRequest req, CancellationToken ct)
    {
        Bot? ctoBot = null, notifierBot = null;
        GroupInfo? groupInfo = null;

        if (req.CtoBotToken is { Length: > 0 } ctoTok)
        {
            if (!IsBotTokenFormat(ctoTok))
                return TelegramValidateResult.Fail("cto_token_invalid", "Token format invalid");
            var (ok, info, err) = await CallTelegramGetMeAsync(ctoTok, ct);
            if (!ok) return TelegramValidateResult.Fail("cto_token_invalid", err ?? "");
            ctoBot = info;
        }

        if (req.NotifierBotToken is { Length: > 0 } notTok)
        {
            if (!IsBotTokenFormat(notTok))
                return TelegramValidateResult.Fail("notifier_token_invalid", "Token format invalid");
            var (ok, info, err) = await CallTelegramGetMeAsync(notTok, ct);
            if (!ok) return TelegramValidateResult.Fail("notifier_token_invalid", err ?? "");
            notifierBot = info;
        }

        if (req.GroupChatId is { Length: > 0 } gid && gid != "0")
        {
            if (!Regex.IsMatch(gid, @"^-?\d+$"))
                return TelegramValidateResult.Fail("group_chat_invalid", "groupChatId must be a numeric string");

            var (ok, info, err) = await CallTelegramGetChatAsync(req.CtoBotToken ?? "", gid, ct);
            if (!ok) return TelegramValidateResult.Fail("group_chat_invalid", err ?? "");
            groupInfo = info;
        }

        return new TelegramValidateResult(true, ctoBot, notifierBot, groupInfo, [], null);
    }

    public async Task<SetupWriteResult> WriteTelegramAsync(
        TelegramSetupRequest req, CancellationToken ct)
    {
        // Validate first
        var validation = await ValidateTelegramAsync(req, ct);
        if (!validation.Valid)
            return SetupWriteResult.Fail(validation.ErrorCode!, validation.ErrorDetail!);

        // Acquire mutex — wait up to 30 s
        if (!await _lock.WaitAsync(TimeSpan.FromSeconds(30), ct))
            return SetupWriteResult.Fail("setup_locked", "Another setup operation is in progress");

        try
        {
            // Determine which agent containers need restart (need DB — fail before writing)
            List<string> agentContainersToRestart;
            try
            {
                agentContainersToRestart = await GetAgentContainersWithEnvRefsAsync(
                    ["TELEGRAM_CTO_BOT_TOKEN", "TELEGRAM_NOTIFIER_BOT_TOKEN", "FLEET_GROUP_CHAT_ID"], ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB unavailable — aborting telegram setup write");
                return SetupWriteResult.Fail("db_unavailable", "Could not determine container restart targets — try again shortly");
            }

            var updates = new Dictionary<string, string>();
            if (req.CtoBotToken is { Length: > 0 } cto)
                updates["TELEGRAM_CTO_BOT_TOKEN"] = cto;
            if (req.NotifierBotToken is { Length: > 0 } notifier)
                updates["TELEGRAM_NOTIFIER_BOT_TOKEN"] = notifier;

            // groupChatId: explicit empty/"0" = clear; null/omitted = no-op
            if (req.GroupChatId is not null)
                updates["FLEET_GROUP_CHAT_ID"] = (req.GroupChatId == "0" || req.GroupChatId == "") ? "" : req.GroupChatId;

            // userId: store as TELEGRAM_USER_ID so agents can auto-add the installer to their allowlist
            if (req.UserId is { Length: > 0 } uid && long.TryParse(uid, out _))
                updates["TELEGRAM_USER_ID"] = uid;

            await AtomicWriteEnvAsync(updates);

            var written = updates.Keys.ToList();
            var (restarted, restartErrors) = await RestartContainersAsync(written, agentContainersToRestart, ct);

            return new SetupWriteResult(true, written, restarted, [], restartErrors, null, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── GitHub ────────────────────────────────────────────────────────────────

    public async Task<GitHubValidateResult> ValidateGitHubAsync(
        GitHubSetupRequest req, CancellationToken ct)
    {
        // Step 1: numeric app ID
        if (!Regex.IsMatch(req.AppId ?? "", @"^\d+$"))
            return GitHubValidateResult.Fail("app_id_invalid", "appId must be a numeric string");

        // Step 2: PEM structure
        var pem = req.PrivateKeyPem?.Trim() ?? "";
        if (!pem.StartsWith("-----BEGIN") || !pem.Contains("-----END"))
            return GitHubValidateResult.Fail("pem_invalid", "privateKeyPem must be a valid PEM-encoded RSA private key");

        // Step 3: JWT generation test
        string? jwtForStep4 = null;
        try
        {
            jwtForStep4 = GenerateGitHubJwt(req.AppId!, pem);
        }
        catch (Exception ex)
        {
            return GitHubValidateResult.Fail("pem_invalid", $"Could not parse RSA private key: {ex.Message}");
        }

        // Step 4: best-effort GitHub API call
        string? appName = null;
        var warnings = new List<string>();
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwtForStep4}");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            http.DefaultRequestHeaders.Add("User-Agent", "fleet-orchestrator");

            var resp = await http.GetAsync("https://api.github.com/app", ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                appName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            }
            else
            {
                return GitHubValidateResult.Fail("app_id_mismatch",
                    $"GitHub API returned {(int)resp.StatusCode} — check App ID and private key");
            }
        }
        catch (Exception ex)
        {
            warnings.Add("github_api_unreachable");
            _logger.LogWarning(ex, "GitHub API call failed during validation — proceeding without app name verification");
        }

        return new GitHubValidateResult(true, req.AppId, appName, warnings, null, null);
    }

    public async Task<SetupWriteResult> WriteGitHubAsync(
        GitHubSetupRequest req, CancellationToken ct)
    {
        var validation = await ValidateGitHubAsync(req, ct);
        if (!validation.Valid)
            return SetupWriteResult.Fail(validation.ErrorCode!, validation.ErrorDetail!);

        if (!await _lock.WaitAsync(TimeSpan.FromSeconds(30), ct))
            return SetupWriteResult.Fail("setup_locked", "Another setup operation is in progress");

        try
        {
            List<string> agentContainersToRestart;
            try
            {
                agentContainersToRestart = await GetAgentContainersWithEnvRefsAsync(
                    ["GITHUB_APP_ID", "GITHUB_APP_PEM"], ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB unavailable — aborting GitHub setup write");
                return SetupWriteResult.Fail("db_unavailable", "Could not determine container restart targets — try again shortly");
            }

            var updates = new Dictionary<string, string>();
            if (req.AppId is { Length: > 0 })
                updates["GITHUB_APP_ID"] = req.AppId;
            if (req.PrivateKeyPem is { Length: > 0 } pem)
            {
                // Store as single-line base64 so it's a valid env-file value
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));
                updates["GITHUB_APP_PEM"] = b64;
            }

            await AtomicWriteEnvAsync(updates);

            var written = updates.Keys.ToList();
            var (restarted, restartErrors) = await RestartContainersAsync(written, agentContainersToRestart, ct);

            return new SetupWriteResult(true, written, restarted, validation.Warnings, restartErrors, null, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── CTO agent ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes FLEET_CTO_AGENT to .env and restarts fleet-bridge + fleet-temporal-bridge.
    /// Called once when the first co-cto agent is created via the dashboard.
    /// </summary>
    public async Task<(List<string> Restarted, Dictionary<string, string> Errors)>
        WriteCtoAgentAsync(string agentName, CancellationToken ct)
    {
        if (!await _lock.WaitAsync(TimeSpan.FromSeconds(30), ct))
            return ([], new Dictionary<string, string> { ["_lock"] = "Another setup operation is in progress" });

        try
        {
            await AtomicWriteEnvAsync(new Dictionary<string, string>
            {
                ["FLEET_CTO_AGENT"] = agentName
            });

            var restarted = new List<string>();
            var errors = new Dictionary<string, string>();

            foreach (var container in new[] { "fleet-bridge", "fleet-temporal-bridge" })
            {
                try
                {
                    await InfraContainerRecreateAsync(container, ct);
                    restarted.Add(container);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not restart {Container} after writing FLEET_CTO_AGENT", container);
                    errors[container] = ex.Message;
                }
            }

            return (restarted, errors);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task AtomicWriteEnvAsync(Dictionary<string, string> updates)
    {
        var lines = File.Exists(_envFilePath)
            ? await File.ReadAllLinesAsync(_envFilePath)
            : [];

        var updated = UpsertEnvLines(lines, updates);

        var tmpPath = _envFilePath + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(tmpPath, updated);
            File.Move(tmpPath, _envFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Leave .tmp in place for debugging
            throw new InvalidOperationException(
                $"Could not atomically write .env — check disk space and file permissions: {ex.Message}", ex);
        }
    }

    internal static string[] UpsertEnvLines(string[] lines, Dictionary<string, string> updates)
    {
        // Validate: no newlines in values
        foreach (var (k, v) in updates)
        {
            if (v.Contains('\n') || v.Contains('\r'))
                throw new ArgumentException($"Value for {k} contains a newline character — not allowed in .env files");
        }

        var result = new List<string>(lines.Length + updates.Count);
        var written = new HashSet<string>(StringComparer.Ordinal);

        // Track last index of each key to handle duplicate-key case (keep last)
        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
            var key = trimmed[..trimmed.IndexOf('=')].Trim();
            lastIndex[key] = i;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('='))
            {
                result.Add(lines[i]);
                continue;
            }

            var eqIdx = trimmed.IndexOf('=');
            var key = trimmed[..eqIdx].Trim();

            // Skip duplicate keys that will be overwritten by the last occurrence
            if (lastIndex.TryGetValue(key, out var last) && last != i)
                continue;

            if (updates.TryGetValue(key, out var newVal))
            {
                result.Add($"{key}={newVal}");
                written.Add(key);
            }
            else
            {
                result.Add(lines[i]);
            }
        }

        // Append any keys from updates not yet written
        foreach (var (k, v) in updates)
        {
            if (!written.Contains(k))
                result.Add($"{k}={v}");
        }

        return result.ToArray();
    }

    private async Task<List<string>> GetAgentContainersWithEnvRefsAsync(
        IEnumerable<string> envKeys, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var keys = envKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var containers = await db.Agents
            .Where(a => a.EnvRefs.Any(r => keys.Contains(r.EnvKeyName)))
            .Select(a => a.ContainerName)
            .Distinct()
            .ToListAsync(ct);

        return containers;
    }

    private async Task<(List<string> restarted, Dictionary<string, string> errors)>
        RestartContainersAsync(
            IEnumerable<string> writtenKeys,
            List<string> agentContainers,
            CancellationToken ct)
    {
        var restarted = new List<string>();
        var errors = new Dictionary<string, string>();

        var keySet = writtenKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Determine which infra containers to restart
        var infraToRestart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, containers) in InfraDepMap)
        {
            if (keySet.Contains(key))
                foreach (var c in containers)
                    infraToRestart.Add(c);
        }

        // Restart infra containers (inspect-and-recreate)
        foreach (var name in infraToRestart)
        {
            try
            {
                await InfraContainerRecreateAsync(name, ct);
                restarted.Add(name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recreate infra container {Name}", name);
                errors[name] = ex.Message;
            }
        }

        // Reprovision agent containers (stop → remove → recreate with fresh .env)
        foreach (var name in agentContainers)
        {
            try
            {
                var result = await _provisioning.ReprovisionAsync(name, ct: ct);
                if (result.Success)
                    restarted.Add(name);
                else
                    errors[name] = result.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reprovision agent container {Name}", name);
                errors[name] = ex.Message;
            }
        }

        return (restarted, errors);
    }

    private async Task InfraContainerRecreateAsync(string containerName, CancellationToken ct)
    {
        // Read current container config via Docker inspect
        var json = await _docker.InspectContainerAsync(containerName, ct);
        if (json is null)
        {
            _logger.LogWarning("Infra container {Name} not found — skipping recreate", containerName);
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var image = root.GetProperty("Config").GetProperty("Image").GetString() ?? "";
        var memory = root.GetProperty("HostConfig").GetProperty("Memory").GetInt64();

        // Extract existing env key=value list
        var existingEnv = new List<string>();
        if (root.GetProperty("Config").TryGetProperty("Env", out var envArr))
            foreach (var e in envArr.EnumerateArray())
                if (e.GetString() is { } s) existingEnv.Add(s);

        var binds = new List<string>();
        if (root.GetProperty("HostConfig").TryGetProperty("Binds", out var bindsEl) && bindsEl.ValueKind != JsonValueKind.Null)
            foreach (var b in bindsEl.EnumerateArray())
                if (b.GetString() is { } s) binds.Add(s);

        var networks = new List<string>();
        if (root.TryGetProperty("NetworkSettings", out var netSettings) &&
            netSettings.TryGetProperty("Networks", out var nets))
            foreach (var n in nets.EnumerateObject())
                networks.Add(n.Name);

        var primaryNetwork = networks.FirstOrDefault() ?? "fleet-net";

        // MergeEnvOverrides: only update keys already present in the container's env.
        // New keys from .env are intentionally NOT injected — only the orchestrator
        // (via DB-driven provisioning) controls what env vars a container receives.
        var envValues = LoadEnvFile(_envFilePath);
        var mergedEnv = MergeEnvOverrides(existingEnv, envValues);

        // Stop — tolerate "already stopped" (304 Not Modified is handled by StopContainerAsync)
        await _docker.StopContainerAsync(containerName);
        await _docker.RemoveContainerAsync(containerName, ct);

        var id = await _docker.CreateContainerAsync(
            containerName, image, memory, mergedEnv, binds, primaryNetwork, ct: ct);

        if (id is null)
            throw new InvalidOperationException($"Docker create returned null for {containerName}");

        await _docker.StartContainerAsync(containerName);
        _logger.LogInformation("Infra container {Name} recreated successfully", containerName);
    }

    internal static List<string> MergeEnvOverrides(
        IEnumerable<string> existingEnv, Dictionary<string, string> envValues)
    {
        // Only update keys already present in the container's env.
        // Never inject new keys from .env into an existing container.
        var result = new List<string>();
        foreach (var entry in existingEnv)
        {
            var eqIdx = entry.IndexOf('=');
            if (eqIdx < 0) { result.Add(entry); continue; }
            var key = entry[..eqIdx];
            result.Add(envValues.TryGetValue(key, out var newVal) ? $"{key}={newVal}" : entry);
        }
        return result;
    }

    // ── Telegram API helpers ──────────────────────────────────────────────────

    private static bool IsBotTokenFormat(string token) =>
        Regex.IsMatch(token, @"^\d{5,}:[A-Za-z0-9_-]{35,}$");

    private static async Task<(bool ok, Bot? info, string? error)> CallTelegramGetMeAsync(
        string token, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync($"https://api.telegram.org/bot{token}/getMe", ct);
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"Telegram returned {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var result = doc.RootElement.GetProperty("result");
            var id = result.GetProperty("id").GetInt64();
            var username = result.TryGetProperty("username", out var u) ? u.GetString() : null;
            return (true, new Bot(id, username ?? ""), null);
        }
        catch (TaskCanceledException)
        {
            return (false, null, "Telegram API unreachable (timeout)");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static async Task<(bool ok, GroupInfo? info, string? error)> CallTelegramGetChatAsync(
        string token, string chatId, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync(
                $"https://api.telegram.org/bot{token}/getChat?chat_id={Uri.EscapeDataString(chatId)}", ct);
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"Telegram getChat returned {(int)resp.StatusCode} — CTO bot may not be a member of this group");

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var result = doc.RootElement.GetProperty("result");
            var id = result.GetProperty("id").GetInt64();
            var title = result.TryGetProperty("title", out var t) ? t.GetString() : null;
            return (true, new GroupInfo(id, title ?? ""), null);
        }
        catch (TaskCanceledException)
        {
            return (false, null, "Telegram API unreachable (timeout)");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    // ── GitHub JWT ────────────────────────────────────────────────────────────

    private static string GenerateGitHubJwt(string appId, string pem)
    {
        // Minimal RS256 JWT using only BCL — no external library needed.
        // Header.Payload signed with RSA private key from PEM.
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(pem);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now - 60,
            exp = now + 60,
            iss = appId,
        }));
        var data = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var sig = rsa.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64UrlEncode(sig)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Env file helpers ──────────────────────────────────────────────────────

    private static bool IsConfigured(Dictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var val) && !WeakDefaults.Contains(val) && IsValidFormat(key, val);

    private static bool IsValidFormat(string key, string val) => key switch
    {
        "TELEGRAM_CTO_BOT_TOKEN" or "TELEGRAM_NOTIFIER_BOT_TOKEN" =>
            Regex.IsMatch(val, @"^\d+:[A-Za-z0-9_-]{35,}$"),
        "GITHUB_APP_ID" =>
            val.All(char.IsDigit) && val.Length >= 5,
        _ => true
    };

    private static Dictionary<string, string> LoadEnvFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
            var idx = trimmed.IndexOf('=');
            var key = trimmed[..idx].Trim();
            var val = trimmed[(idx + 1)..].Trim().Trim('"');
            result[key] = val;
        }
        return result;
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record SetupStatusDto(TelegramStatusDto Telegram, GitHubStatusDto GitHub);
public sealed record TelegramStatusDto(bool Configured, bool GroupChatEnabled);
public sealed record GitHubStatusDto(bool Configured);

public sealed record TelegramSetupRequest(
    string? CtoBotToken,
    string? NotifierBotToken,
    string? GroupChatId,
    string? UserId);

public sealed record GitHubSetupRequest(
    string? AppId,
    string? PrivateKeyPem);

public sealed record Bot(long Id, string Username);
public sealed record GroupInfo(long Id, string Title);

public sealed record TelegramValidateResult(
    bool Valid,
    Bot? CtoBot,
    Bot? NotifierBot,
    GroupInfo? GroupChat,
    List<string> Warnings,
    string? ErrorCode,
    string? ErrorDetail = null)
{
    public static TelegramValidateResult Fail(string code, string detail) =>
        new(false, null, null, null, [], code, detail);
}

public sealed record GitHubValidateResult(
    bool Valid,
    string? AppId,
    string? AppName,
    List<string> Warnings,
    string? ErrorCode,
    string? ErrorDetail)
{
    public static GitHubValidateResult Fail(string code, string detail) =>
        new(false, null, null, [], code, detail);
}

public sealed record SetupWriteResult(
    bool Success,
    List<string> Written,
    List<string> Restarted,
    List<string> Warnings,
    Dictionary<string, string> RestartErrors,
    string? ErrorCode,
    string? ErrorDetail)
{
    public static SetupWriteResult Fail(string code, string detail) =>
        new(false, [], [], [], [], code, detail);
}

public sealed record RichConnectionsStatusDto(RichTelegramStatusDto Telegram, RichGitHubStatusDto GitHub);

public sealed record RichTelegramStatusDto(
    bool Configured,
    bool GroupChatEnabled,
    string? MaskedCtoBotToken,
    string? MaskedNotifierBotToken,
    string? GroupChatId,
    string? UserId,
    DateTime? LastValidatedUtc);

public sealed record RichGitHubStatusDto(
    bool Configured,
    string? MaskedAppId,
    string? MaskedPrivateKey,
    DateTime? LastValidatedUtc);

public sealed record EnvVarDto(string Key, string MaskedValue, bool IsSensitive, List<string> UsedBy);
