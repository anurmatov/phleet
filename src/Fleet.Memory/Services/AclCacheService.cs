using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Fleet.Memory.Configuration;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Services;

/// <summary>
/// Holds the project-scoped ACL map fetched from the orchestrator's
/// GET /internal/agent-project-access endpoint.
///
/// Fail-closed: when the cache is empty and the orchestrator is unreachable,
/// IsAvailable returns false and all read-tool callers must return 503.
///
/// Stale-on-failure: once the cache is populated, a failed refresh retains
/// the previous snapshot and logs a warning. The cache is never expired on
/// a failed refresh.
/// </summary>
public sealed class AclCacheService : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackgroundRetryInterval = TimeSpan.FromSeconds(30);

    // Retry delays for the initial fetch (and post-config-changed fetch)
    private static readonly TimeSpan[] FetchRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private readonly IOptions<AclOptions> _aclOptions;
    private readonly IOptions<OrchestratorOptions> _orchestratorOptions;
    private readonly ILogger<AclCacheService> _logger;
    private readonly HttpClient _http;

    // Keyed by lowercase agent name, value is list of lowercase project names (or ["*"] for wildcard).
    private volatile Dictionary<string, List<string>> _cache = [];
    private volatile bool _isAvailable = false;
    private DateTimeOffset _cacheLoadedAt = DateTimeOffset.MinValue;

    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;

    public AclCacheService(
        IOptions<AclOptions> aclOptions,
        IOptions<OrchestratorOptions> orchestratorOptions,
        ILogger<AclCacheService> logger)
    {
        _aclOptions = aclOptions;
        _orchestratorOptions = orchestratorOptions;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _backgroundTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_backgroundTask is not null)
                await _backgroundTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Background loop ───────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        // Initial fetch with retries; fail-closed until first success
        await FetchWithRetryAsync(isInitial: true, ct);

        // Warn if the operator agent lacks a wildcard row
        CheckOperatorWildcard();

        // Periodic refresh every 5 minutes
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Whether the EnableProjectScopedAcl feature flag is on.</summary>
    public bool IsAclEnabled => _aclOptions.Value.EnableProjectScopedAcl;

    /// <summary>
    /// True once the cache has been successfully populated at least once.
    /// False during cold-start before the first successful fetch.
    /// </summary>
    public bool IsAvailable => _isAvailable;

    /// <summary>
    /// Determines whether the given agent can read the given memory project.
    /// Returns (true, null) if allowed; (false, reason) if denied.
    ///
    /// Call sites must check IsAvailable first and return 503 when false.
    /// When EnableProjectScopedAcl is false, always returns (true, null).
    /// </summary>
    public (bool allowed, string? denyReason) CanRead(string agentName, string? project)
    {
        if (!_aclOptions.Value.EnableProjectScopedAcl)
            return (true, null);

        var name = agentName.ToLowerInvariant();
        var cache = _cache;
        var opts = _aclOptions.Value;

        // Wildcard check
        if (cache.TryGetValue(name, out var agentProjects) && agentProjects.Contains("*"))
            return (true, null);

        // Normalize memory project (empty string means no project set)
        var memProject = (project ?? "").Trim().ToLowerInvariant();

        // No project set → denied unless agent has wildcard (already checked above)
        if (string.IsNullOrEmpty(memProject))
            return (false, "memory has no project — denied to non-wildcard agents");

        // Built-in public projects
        foreach (var pub in opts.AclPublicProjects)
        {
            if (string.Equals(pub.Trim(), memProject, StringComparison.OrdinalIgnoreCase))
                return (true, null);
        }

        // Agent allow-list
        if (agentProjects is not null && agentProjects.Contains(memProject))
            return (true, null);

        return (false, $"project '{memProject}' not in agent '{name}' allow-list");
    }

    /// <summary>
    /// Triggers an immediate ACL refresh (non-blocking). Called by PeerConfigHostedService
    /// on config.changed events.
    /// </summary>
    public void TriggerRefresh()
    {
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "ACL refresh triggered by config.changed failed"); }
        });
    }

    // ── Fetch internals ───────────────────────────────────────────────────────

    private async Task FetchWithRetryAsync(bool isInitial, CancellationToken ct)
    {
        for (var i = 0; i <= FetchRetryDelays.Length; i++)
        {
            try
            {
                await FetchOnceAsync(ct);
                return; // success
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (i < FetchRetryDelays.Length)
                {
                    _logger.LogWarning(ex, "ACL fetch attempt {Attempt}/{Max} failed, retrying",
                        i + 1, FetchRetryDelays.Length + 1);
                    await Task.Delay(FetchRetryDelays[i], ct);
                }
                else
                {
                    if (isInitial)
                    {
                        _logger.LogError(ex,
                            "ACL cache could not be populated after {Attempts} attempts — entering fail-closed mode. " +
                            "All read tools will return 503 until the orchestrator is reachable.",
                            FetchRetryDelays.Length + 1);
                        // Start background retry loop
                        _ = Task.Run(async () =>
                        {
                            while (!_isAvailable && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(BackgroundRetryInterval, ct).ConfigureAwait(false);
                                try { await FetchOnceAsync(ct); return; }
                                catch (Exception retryEx)
                                {
                                    _logger.LogWarning(retryEx, "ACL background retry failed");
                                }
                            }
                        }, ct);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "ACL refresh failed after {Attempts} attempts — retaining stale cache (age: {Age:F0}s)",
                            FetchRetryDelays.Length + 1,
                            (DateTimeOffset.UtcNow - _cacheLoadedAt).TotalSeconds);
                    }
                }
            }
        }
    }

    /// <summary>Refresh the cache; retains stale on failure (not fail-closed like initial).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
        => await FetchWithRetryAsync(isInitial: false, ct);

    private async Task FetchOnceAsync(CancellationToken ct)
    {
        var url = $"{_orchestratorOptions.Value.BaseUrl.TrimEnd('/')}/internal/agent-project-access";
        var resp = await _http.GetFromJsonAsync<AclResponse>(url, ct)
            ?? throw new InvalidOperationException("Empty response from /internal/agent-project-access");

        var newCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agent, projects) in resp.Acl)
        {
            newCache[agent.ToLowerInvariant()] = projects
                .Select(p => p.ToLowerInvariant())
                .ToList();
        }

        _cache = newCache;
        _cacheLoadedAt = DateTimeOffset.UtcNow;
        _isAvailable = true;
        _logger.LogInformation("ACL cache refreshed: {AgentCount} agents", newCache.Count);
    }

    private void CheckOperatorWildcard()
    {
        var operatorAgent = _aclOptions.Value.AclOperatorAgent.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(operatorAgent)) return;

        if (!_cache.TryGetValue(operatorAgent, out var projects) || !projects.Contains("*"))
        {
            _logger.LogError(
                "ACL startup check: operator agent '{Agent}' does not have a wildcard '*' row in agent_project_access. " +
                "Use PUT /api/agents/{Agent}/project-access with {{\"projects\":[\"*\"]}} to grant full access.",
                operatorAgent, operatorAgent);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
        _http.Dispose();
    }

    // ── Test helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Injects a pre-built ACL map and marks the cache as available.
    /// Used only by tests to bypass HTTP fetch without a live orchestrator.
    /// </summary>
    internal void InjectAclForTesting(Dictionary<string, List<string>> acl)
    {
        var normalized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agent, projects) in acl)
            normalized[agent.ToLowerInvariant()] = projects.Select(p => p.ToLowerInvariant()).ToList();
        _cache = normalized;
        _isAvailable = true;
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private sealed class AclResponse
    {
        [JsonPropertyName("acl")]
        public Dictionary<string, List<string>> Acl { get; set; } = [];
    }
}
