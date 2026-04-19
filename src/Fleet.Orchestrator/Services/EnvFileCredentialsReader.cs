using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Singleton implementation of <see cref="ICredentialsReader"/> that reads .env and caches
/// the result for up to 30 seconds. Cache is invalidated synchronously by the credentials
/// save handler so the updated value is visible immediately on the next GET call.
///
/// Parser rules (match save handler in CredentialsService):
///   - Skip blank lines and lines starting with '#'
///   - Split on first '=' only (preserves base64 values containing '=')
///   - Trim surrounding '"' or '\'' from value
///   - Return null for absent key or unreadable file (deliberately conflated)
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

    public static Dictionary<string, string> LoadEnvFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
                var idx = trimmed.IndexOf('=');
                var key = trimmed[..idx].Trim();
                var val = trimmed[(idx + 1)..].Trim().Trim('"').Trim('\'');
                if (!string.IsNullOrEmpty(key))
                    result[key] = val;
            }
        }
        catch
        {
            // Return empty dict — caller treats missing file and unreadable file identically
        }
        return result;
    }
}
