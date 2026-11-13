using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Singleton implementation of <see cref="ICredentialsReader"/> that reads .env and caches
/// the result for up to 30 seconds. Cache is invalidated synchronously by the credentials
/// save handler so the updated value is visible immediately on the next GET call.
///
/// The parse rule is <see cref="EnvFile"/>'s, shared with provisioning and setup. A key that is
/// absent and a file that cannot be read both return null (deliberately conflated).
/// </summary>
public sealed class EnvFileCredentialsReader : ICredentialsReader
{
    private const int CacheTtlSeconds = 30;

    private readonly string _envFilePath;
    private readonly ILogger<EnvFileCredentialsReader> _logger;

    private Dictionary<string, string>? _cache;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly object _lock = new();

    public EnvFileCredentialsReader(IConfiguration config, ILogger<EnvFileCredentialsReader> logger)
    {
        _envFilePath = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
        _logger = logger;
    }

    public string? Get(string key)
    {
        var env = GetOrLoadCache();
        return env.TryGetValue(key, out var value) ? value : null;
    }

    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _cacheExpiry = DateTime.MinValue;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private Dictionary<string, string> GetOrLoadCache()
    {
        lock (_lock)
        {
            if (_cache is not null && DateTime.UtcNow < _cacheExpiry)
                return _cache;

            _cache = LoadEnvFile(_envFilePath);
            _cacheExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
            return _cache;
        }
    }

    /// <summary>The file's keys and values, by the one rule in <see cref="EnvFile"/>. Never throws.</summary>
    public static Dictionary<string, string> LoadEnvFile(string path) => EnvFile.Read(path);
}
