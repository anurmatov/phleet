using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Generic config service: reads .env as an opaque bag, enforces a server-side denylist,
/// expands agent-derived key templates, and broadcasts <c>config.changed</c> on reload/write.
///
/// Replaces the registry-driven CredentialsService propagation model (issue #69).
/// </summary>
public sealed class ConfigService
{
    // ── Denylist ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Keys that may never be returned or written via the config API, regardless of auth.
    /// These are database credentials or the general orchestrator auth token (mounted into
    /// every agent container — exposing it via the config API would let any agent read all
    /// peer bot tokens).
    /// </summary>
    private static readonly string[] DenylistPrefixes =
        ["MYSQL_", "DB_", "FLEET_MYSQL_", "MINIO_", "CLAUDE_CREDENTIALS_", "CODEX_CREDENTIALS_"];

    private static readonly HashSet<string> DenylistExact =
        new([
            "ORCHESTRATOR_AUTH_TOKEN",
            "ORCHESTRATOR_CONFIG_TOKEN",
            "JWT_SECRET",
            "TOTP_SECRET",
            "GITHUB_APP_PEM",
            "POLYMARKET_PRIVATE_KEY",
        ], StringComparer.OrdinalIgnoreCase);

    public static bool IsDenylisted(string key) =>
        DenylistExact.Contains(key) ||
        DenylistPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // ── Agent-derived template ────────────────────────────────────────────────

    private const string ShortnamePlaceholder = "{SHORTNAME}";

    public static bool IsAgentDerivedTemplate(string keyTemplate) =>
        keyTemplate.Contains(ShortnamePlaceholder, StringComparison.OrdinalIgnoreCase);

    public static Regex TemplateToRegex(string keyTemplate)
    {
        var escaped = Regex.Escape(keyTemplate).Replace(
            Regex.Escape(ShortnamePlaceholder), @"[^_]+", StringComparison.OrdinalIgnoreCase);
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase);
    }

    // ── Write mutex ───────────────────────────────────────────────────────────

    private static readonly SemaphoreSlim WriteMutex = new(1, 1);

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly string _envFilePath;
    private readonly string _rabbitHost;
    private readonly string _rabbitExchange;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConfigService> _logger;

    private Dictionary<string, string>? _cache;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly object _cacheLock = new();

    public ConfigService(
        IConfiguration config,
        IOptions<RabbitMqOptions> rabbitOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<ConfigService> logger)
    {
        _envFilePath = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
        _rabbitHost = rabbitOptions.Value.Host;
        _rabbitExchange = rabbitOptions.Value.Exchange;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    private Dictionary<string, string> LoadCache()
    {
        lock (_cacheLock)
        {
            if (_cache is not null && DateTime.UtcNow < _cacheExpiry)
                return _cache;
            _cache = EnvFileCredentialsReader.LoadEnvFile(_envFilePath);
            _cacheExpiry = DateTime.UtcNow.AddSeconds(30);
            return _cache;
        }
    }

    private void InvalidateCache()
    {
        lock (_cacheLock) { _cache = null; _cacheExpiry = DateTime.MinValue; }
    }

    // ── API ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full .env map minus denylisted keys.
    /// Used by GET /api/config/all (dashboard Credentials table).
    /// </summary>
    public Dictionary<string, string> GetAll()
    {
        var env = LoadCache();
        return env
            .Where(kv => !IsDenylisted(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves the requested keys, splitting into literal and agent-derived.
    /// Denylisted keys are silently omitted. Unknown literal keys are silently omitted.
    /// Agent-derived templates are expanded by joining the agents table.
    /// On MySQL failure the agentDerived map is empty (logged as warning).
    /// </summary>
    public async Task<ConfigValuesResult> GetValuesAsync(
        IEnumerable<string> keys, CancellationToken ct = default)
    {
        var env = LoadCache();
        var literals = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new List<string>();

        foreach (var key in keys)
        {
            if (IsDenylisted(key)) continue;
            if (IsAgentDerivedTemplate(key))
                templates.Add(key);
            else if (env.TryGetValue(key, out var val))
                literals[key] = val;
            // unknown literal keys are silently omitted
        }

        var agentDerived = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        if (templates.Count > 0)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
                if (db is not null)
                {
                    var agents = await db.Agents
                        .AsNoTracking()
                        .Select(a => new { a.ShortName, a.Name })
                        .ToListAsync(ct);

                    foreach (var template in templates)
                    {
                        var perAgent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var agent in agents)
                        {
                            var shortName = string.IsNullOrWhiteSpace(agent.ShortName)
                                ? agent.Name
                                : agent.ShortName;
                            var expandedKey = template.Replace(
                                ShortnamePlaceholder, shortName,
                                StringComparison.OrdinalIgnoreCase);
                            if (env.TryGetValue(expandedKey, out var tokenVal) &&
                                !string.IsNullOrEmpty(tokenVal))
                                perAgent[shortName] = tokenVal;
                        }
                        agentDerived[template] = perAgent;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MySQL unavailable during agent-derived key expansion — agentDerived will be empty");
            }
        }

        return new ConfigValuesResult(literals, agentDerived);
    }

    /// <summary>
    /// Re-reads .env from disk, diffs against in-memory cache, and publishes a single
    /// <c>config.changed</c> event with the changed key names (denylisted keys excluded).
    /// Returns the list of changed non-denylisted keys.
    /// RabbitMQ publish failure is logged but does not fail the reload (returns changed keys anyway).
    /// </summary>
    public async Task<List<string>> ReloadAsync(CancellationToken ct = default)
    {
        var before = LoadCache();
        InvalidateCache();
        var after = EnvFileCredentialsReader.LoadEnvFile(_envFilePath);

        lock (_cacheLock)
        {
            _cache = after;
            _cacheExpiry = DateTime.UtcNow.AddSeconds(30);
        }

        // Diff: keys that were added, changed, or removed
        var changedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (k, v) in after)
        {
            if (!before.TryGetValue(k, out var prev) || prev != v)
                changedKeys.Add(k);
        }
        foreach (var k in before.Keys)
        {
            if (!after.ContainsKey(k))
                changedKeys.Add(k);
        }

        // Filter out denylisted keys before broadcasting
        var publicChanged = changedKeys.Where(k => !IsDenylisted(k)).ToList();

        if (publicChanged.Count > 0)
            await PublishConfigChangedAsync(publicChanged, ct);

        return publicChanged;
    }

    /// <summary>
    /// Atomically writes the given key-value pairs to .env (mutex + bak + tmp rename),
    /// then triggers a reload (diff + publish config.changed).
    /// Denylisted keys in the payload are rejected with an exception.
    /// Returns the list of changed non-denylisted keys.
    /// </summary>
    public async Task<List<string>> PutValuesAsync(
        Dictionary<string, string> kvs, CancellationToken ct = default)
    {
        foreach (var key in kvs.Keys)
        {
            if (IsDenylisted(key))
                throw new DenylistedException($"Key '{key}' is not writable via the config API.");
        }

        if (!await WriteMutex.WaitAsync(TimeSpan.FromSeconds(30), ct))
            throw new TimeoutException("Another config write is in progress. Try again shortly.");

        try
        {
            await AtomicWriteAsync(kvs, ct);
        }
        finally
        {
            WriteMutex.Release();
        }

        return await ReloadAsync(ct);
    }

    // ── Atomic write ──────────────────────────────────────────────────────────

    /// <summary>
    /// Writes multiple key-value pairs atomically to .env.
    /// Must be called with <see cref="WriteMutex"/> held.
    /// </summary>
    internal async Task AtomicWriteAsync(Dictionary<string, string> kvs, CancellationToken ct)
    {
        foreach (var (k, v) in kvs)
        {
            if (v.Contains('\n') || v.Contains('\r'))
                throw new ArgumentException($"Value for '{k}' contains a newline — not allowed in .env files");
        }

        var lines = File.Exists(_envFilePath)
            ? await File.ReadAllLinesAsync(_envFilePath, ct)
            : [];

        var updated = SetupService.UpsertEnvLines(lines, kvs);

        if (File.Exists(_envFilePath))
        {
            try { File.Copy(_envFilePath, _envFilePath + ".bak", overwrite: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not write .env.bak"); }
        }

        var tmp = _envFilePath + ".tmp";
        await File.WriteAllLinesAsync(tmp, updated, ct);
        try { File.Move(tmp, _envFilePath, overwrite: true); }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }

        InvalidateCache();
    }

    // ── RabbitMQ publish ──────────────────────────────────────────────────────

    private async Task PublishConfigChangedAsync(List<string> changedKeys, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_rabbitHost))
        {
            _logger.LogDebug("RabbitMQ not configured — skipping config.changed publish");
            return;
        }

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _rabbitHost,
                ClientProvidedName = "fleet-orchestrator-config",
                AutomaticRecoveryEnabled = false,
                RequestedHeartbeat = TimeSpan.FromSeconds(10),
            };

            await using var conn = await factory.CreateConnectionAsync(ct);
            await using var channel = await conn.CreateChannelAsync(cancellationToken: ct);

            await channel.ExchangeDeclareAsync(
                _rabbitExchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: ct);

            var payload = JsonSerializer.SerializeToUtf8Bytes(new { changedKeys });
            var props = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };

            await channel.BasicPublishAsync(
                exchange: _rabbitExchange,
                routingKey: "config.changed",
                mandatory: false,
                basicProperties: props,
                body: payload,
                cancellationToken: ct);

            _logger.LogInformation("config.changed published: {Keys}", string.Join(", ", changedKeys));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish config.changed event — peers will update on next reload");
        }
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record ConfigValuesResult(
    Dictionary<string, string> Literals,
    Dictionary<string, Dictionary<string, string>> AgentDerived);

public sealed class DenylistedException(string message) : Exception(message);
