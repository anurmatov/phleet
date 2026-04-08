using Fleet.Temporal.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Temporalio.Client;
using Temporalio.Converters;

namespace Fleet.Temporal.Mcp;

/// <summary>
/// Creates (and caches) per-namespace Temporal clients on demand for MCP tool use.
/// Temporal clients are lightweight and safe to cache as singletons per namespace.
/// </summary>
public sealed class TemporalClientFactory(IOptions<TemporalBridgeOptions> options)
{
    private static readonly DataConverter CaseInsensitiveDataConverter = DataConverter.Default with
    {
        PayloadConverter = new DefaultPayloadConverter(new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };

    private readonly TemporalBridgeOptions _opts = options.Value;
    private readonly Dictionary<string, ITemporalClient> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Returns a cached client for the given namespace, creating it on first access.
    /// </summary>
    public async Task<ITemporalClient> GetClientAsync(string @namespace)
    {
        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(@namespace, out var cached))
                return cached;

            var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(_opts.TemporalAddress)
            {
                Namespace = @namespace,
                DataConverter = CaseInsensitiveDataConverter
            });

            _cache[@namespace] = client;
            return client;
        }
        finally
        {
            _lock.Release();
        }
    }
}
