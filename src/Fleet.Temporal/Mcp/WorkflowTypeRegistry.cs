namespace Fleet.Temporal.Mcp;

using Fleet.Temporal.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

/// <summary>Known workflow type with its description, input schema, and Temporal namespace.</summary>
public sealed record WorkflowTypeInfo(
    string Name,
    string Description,
    string InputSchema,
    string Namespace);

/// <summary>
/// Fetches the workflow type registry from the orchestrator's /api/workflow-types endpoint.
/// The orchestrator is the single source of truth — it merges hardcoded C# workflows with
/// UWE workflow definitions from the DB and auto-derives input schemas from {{input.X}} tokens.
/// Results are cached for 5 minutes with stale-while-revalidate.
/// </summary>
public sealed class WorkflowTypeRegistry(
    IHttpClientFactory httpClientFactory,
    IOptions<TemporalBridgeOptions> options,
    ILogger<WorkflowTypeRegistry> logger)
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("orchestrator");
    private readonly TemporalBridgeOptions _opts = options.Value;

    private List<WorkflowTypeInfo>? _cache;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// Returns the merged workflow type list from the orchestrator.
    /// Cold start blocks on the initial fetch so the first caller sees real types;
    /// subsequent stale reads return stale-while-revalidate.
    /// On orchestrator error: returns whatever is cached, or an empty list on cold-start failure.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowTypeInfo>> GetAllAsync()
    {
        if (string.IsNullOrEmpty(_opts.OrchestratorUrl))
            return [];

        // Fresh cache — use it
        if (_cache != null && DateTime.UtcNow < _cacheExpiry)
            return _cache;

        // Stale cache — return stale + refresh in background
        if (_cache != null)
        {
            _ = RefreshCacheAsync();
            return _cache;
        }

        // Cold start — block on the initial fetch so the caller sees real types
        await RefreshCacheAsync();
        return _cache ?? [];
    }

    private async Task RefreshCacheAsync()
    {
        if (!await _cacheLock.WaitAsync(0))
            return; // another refresh already in progress

        try
        {
            var types = await _http.GetFromJsonAsync<WorkflowTypeResponse[]>(
                "api/workflow-types");

            if (types is null) return;

            _cache = types
                .Select(t => new WorkflowTypeInfo(
                    Name: t.Name,
                    Description: t.Description,
                    InputSchema: t.InputSchema ?? "{}",
                    Namespace: t.Namespace))
                .ToList();

            _cacheExpiry = DateTime.UtcNow.AddMinutes(5);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WorkflowTypeRegistry: could not fetch workflow types from orchestrator");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private sealed record WorkflowTypeResponse(
        string Name,
        string Description,
        string Namespace,
        string TaskQueue,
        string? InputSchema);
}
