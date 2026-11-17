using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Shared.Mcp;

/// <summary>
/// Every agent's grants on the fleet's MCP servers, as the orchestrator serves them at
/// <c>GET /internal/agent-mcp-grants</c>, kept by a server that is not the orchestrator.
/// </summary>
/// <remarks>
/// <para>
/// Read every <see cref="RefreshEvery"/>, and once more when an agent nobody has heard of calls
/// (it may have been created since the last read), no more often than <see cref="MissRefreshEvery"/>.
/// A grant added to a known agent is honoured at the next read.
/// </para>
/// <para>
/// What is held goes stale: after <see cref="StaleAfter"/> without a successful read the answer is
/// "unknown", which a server refuses under enforcement. A server that cannot reach the orchestrator
/// for that long must not go on serving agents from a list nobody can vouch for.
/// </para>
/// <para>
/// Without an identity key no caller is ever a verified agent, so nothing is read at all.
/// MUST read from the configured orchestrator address only: the answer is what this server
/// believes every agent may do.
/// </para>
/// </remarks>
public sealed class OrchestratorGrantCache : BackgroundService, IAgentGrantSource
{
    public const string HttpClientName = "fleet.agent-grants";
    public const string DefaultOrchestratorUrl = "http://fleet-orchestrator:3600";

    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MissRefreshEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _clients;
    private readonly AgentIdentitySettings _settings;
    private readonly ILogger<OrchestratorGrantCache> _logger;
    private readonly TimeProvider _time;
    private readonly Uri _url;
    private readonly SemaphoreSlim _reading = new(1, 1);

    private volatile Snapshot? _snapshot;
    private long _lastAttemptTicks = long.MinValue;
    private bool _saidUnavailable;

    private sealed record Snapshot(IReadOnlyDictionary<string, IReadOnlyCollection<string>> Agents, DateTimeOffset ReadAt);

    public OrchestratorGrantCache(
        IHttpClientFactory clients, AgentIdentitySettings settings, IConfiguration configuration,
        ILogger<OrchestratorGrantCache> logger, TimeProvider? time = null)
    {
        _clients = clients;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        var orchestrator = configuration[$"{AgentIdentitySettings.Section}:OrchestratorUrl"];
        if (string.IsNullOrWhiteSpace(orchestrator)) orchestrator = DefaultOrchestratorUrl;
        _url = new Uri(orchestrator.TrimEnd('/') + "/internal/agent-mcp-grants");
    }

    public async ValueTask<AgentGrantAnswer> GrantsOfAsync(string agent, CancellationToken ct)
    {
        var snapshot = Fresh();
        // Never read, gone stale, or an agent this server has not heard of: ask once more.
        if (snapshot is null || !snapshot.Agents.ContainsKey(agent))
        {
            await RefreshAsync(force: false, ct);
            snapshot = Fresh();
        }

        if (snapshot is null) return AgentGrantAnswer.Unknown;
        // An agent the orchestrator does not list is an answer: it is granted nothing.
        return new AgentGrantAnswer(true, snapshot.Agents.TryGetValue(agent, out var grants) ? grants : []);
    }

    private Snapshot? Fresh() =>
        _snapshot is { } snapshot && _time.GetUtcNow() - snapshot.ReadAt <= StaleAfter ? snapshot : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.HasKey) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(force: true, stoppingToken);
            try { await Task.Delay(RefreshEvery, _time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Reads the grants now. A read asked for by a call (not forced) waits its turn and is rate-limited.</summary>
    public async Task RefreshAsync(bool force, CancellationToken ct)
    {
        if (!_settings.HasKey) return;

        var now = _time.GetTimestamp();
        if (!force)
        {
            var last = Interlocked.Read(ref _lastAttemptTicks);
            if (last != long.MinValue && _time.GetElapsedTime(last, now) < MissRefreshEvery) return;
        }

        await _reading.WaitAsync(ct);
        try
        {
            Interlocked.Exchange(ref _lastAttemptTicks, _time.GetTimestamp());
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(ReadBound);
            using var http = _clients.CreateClient(HttpClientName);
            var body = await http.GetFromJsonAsync<JsonElement>(_url, bound.Token);
            _snapshot = new Snapshot(Read(body), _time.GetUtcNow());
            if (_saidUnavailable)
            {
                _saidUnavailable = false;
                _logger.LogInformation("agent_grants_read: the orchestrator answered again");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Once per outage, by kind: the address is configuration and the body is never logged.
            if (!_saidUnavailable)
            {
                _saidUnavailable = true;
                _logger.LogWarning("agent_grants_unavailable: could not read agents' grants from the orchestrator ({Error})", ex.GetType().Name);
            }
        }
        finally
        {
            _reading.Release();
        }
    }

    /// <summary><c>{ "agents": { "name": ["mcp__server__tool", …] } }</c>. Anything else is not an answer.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("agents", out var agents)
            || agents.ValueKind != JsonValueKind.Object)
            throw new JsonException("the grants answer has no agents object");

        var read = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var agent in agents.EnumerateObject())
        {
            if (agent.Value.ValueKind != JsonValueKind.Array)
                throw new JsonException("an agent's grants are not a list");
            read[agent.Name] = [.. agent.Value.EnumerateArray()
                .Where(g => g.ValueKind == JsonValueKind.String)
                .Select(g => g.GetString()!)];
        }
        return read;
    }
}

public static class OrchestratorGrantCacheRegistration
{
    /// <summary>
    /// A server that is not the orchestrator: identity from the token, and grants read from the
    /// orchestrator. Call <see cref="AgentToolAuthorization.AddAgentToolAuthorization"/> on the MCP
    /// builder and <see cref="AgentIdentityApp.UseAgentIdentity"/> on the app as well.
    /// </summary>
    public static IServiceCollection AddAgentIdentityFromOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAgentIdentity(configuration);
        services.AddHttpClient(OrchestratorGrantCache.HttpClientName);
        services.AddSingleton<OrchestratorGrantCache>();
        services.AddSingleton<IAgentGrantSource>(sp => sp.GetRequiredService<OrchestratorGrantCache>());
        services.AddHostedService(sp => sp.GetRequiredService<OrchestratorGrantCache>());
        return services;
    }
}
