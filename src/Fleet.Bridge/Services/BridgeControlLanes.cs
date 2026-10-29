using System.Collections.Concurrent;
using Fleet.Bridge.Configuration;
using Fleet.Shared.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Bridge.Services;

/// <summary>
/// Managed per-agent control lanes (#492). With <c>StockOpenShell:AuthorityDirectory</c> set, a target that
/// has a record in that trusted directory is reached only through its own <see cref="BridgeRelayService"/>
/// bound to <c>stock-&lt;epoch&gt;-control</c> in that agent's epoch vhost, with the single-file principal,
/// route and correlation checks unchanged. Ordinary targets keep the ordinary connection. A managed target
/// never falls back to it: an inactive, ambiguous, removed or unreadable record refuses.
/// </summary>
internal sealed class BridgeControlLanes(string directory, int timeoutSeconds, string exchange,
    ILogger<BridgeRelayService> logger) : IAsyncDisposable
{
    internal const string Denied = "Error: stock control admission denied.";

    /// <summary>Ordinary target: not managed. Managed: use <see cref="Lane"/>, or refuse when it is null.</summary>
    internal readonly record struct Route(bool Managed, BridgeRelayService? Lane);

    private sealed record Entry(StockActiveRecord Source, BridgeRelayService? Relay);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _lanes = new(StringComparer.Ordinal);
    // Once seen as managed, a target stays managed for this process: removing its record refuses.
    private readonly ConcurrentDictionary<string, byte> _managed = new(StringComparer.Ordinal);

    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>> Connect { get; set; } =
        (factory, ct) => factory.CreateConnectionAsync(ct);

    /// <summary>The ordinary relay's forward of admitted typed workflow replies to the Temporal receiver (#492).</summary>
    internal Func<BridgeRelayService.RelayMessage, Task>? ForwardWorkflowReply { get; init; }

    /// <summary>Opens the lane of every currently active record (best effort); refused lanes stay held.</summary>
    internal async Task OpenActiveAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, StockActiveRecord?> active;
        try { active = StockAuthoritySource.Active(directory); }
        catch (StockIdentityDenied) { logger.LogWarning("Stock control lanes not opened: authority unavailable"); return; }
        foreach (var (agent, record) in active)
            if (record is not null) await RouteAsync(agent, connect: true, ct);
    }

    /// <summary>Selects the route for a target; with <paramref name="connect"/> opens or replaces its lane.</summary>
    internal async Task<Route> RouteAsync(string targetAgent, bool connect, CancellationToken ct)
    {
        var agent = targetAgent.ToLowerInvariant();
        // Only a name a record can carry can be managed; anything else keeps the ordinary path.
        if (!StockAuthoritySource.ValidAgent(agent)) return new(false, null);
        IReadOnlyDictionary<string, StockActiveRecord?> active;
        try { active = StockAuthoritySource.Active(directory); }
        catch (StockIdentityDenied) { return new(true, null); } // unknown directory state is never "ordinary"
        // A record retired after a verified Docker handoff releases the agent back to the ordinary path.
        if (!active.ContainsKey(agent) && StockAuthoritySource.IsRetired(directory, agent))
        {
            _managed.TryRemove(agent, out _);
            return new(false, null);
        }
        if (!_managed.ContainsKey(agent) && !active.ContainsKey(agent) && !StockAuthoritySource.HasEntry(directory, agent))
            return new(false, null);
        _managed.TryAdd(agent, 0);
        if (!connect) return new(true, null);

        var fresh = active.GetValueOrDefault(agent);
        await _gate.WaitAsync(ct);
        try
        {
            if (_lanes.TryGetValue(agent, out var entry) && (fresh is null || !entry.Source.SameBinding(fresh)))
            {
                // Re-enrolled, revoked or removed: the old epoch connection never serves the agent again.
                _lanes.Remove(agent);
                await CloseAsync(entry.Relay);
                entry = null;
            }
            if (fresh is null) return new(true, null);
            if (entry is null)
            {
                entry = new(fresh, await OpenAsync(agent, fresh, ct));
                _lanes[agent] = entry;
            }
            return new(true, entry.Relay);
        }
        finally { _gate.Release(); }
    }

    private async Task<BridgeRelayService?> OpenAsync(string agent, StockActiveRecord record, CancellationToken ct)
    {
        BridgeRelayService? relay = null;
        try
        {
            var identity = new StockBrokerIdentity(record.Path,
                StockAuthoritySource.RolePassword(directory, agent, "control"), "control");
            relay = new BridgeRelayService(
                Options.Create(new BridgeOptions { TargetAgent = identity.Agent, TimeoutSeconds = timeoutSeconds }),
                Options.Create(new RabbitMqOptions { Host = identity.BrokerHost, Exchange = exchange }), logger, identity)
            { Connect = Connect, ForwardWorkflowReply = ForwardWorkflowReply };
            await relay.InitializeAsync(ct);
            return relay;
        }
        catch (Exception)
        {
            await CloseAsync(relay);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); // not a lane outcome
            // Like the single-file relay there is no stock reconnect loop: the failed lane stays held
            // (refusing) until this agent's record binding changes, i.e. re-enrollment reconciles it.
            logger.LogWarning("Stock control lane for {Agent} refused; manual reconciliation required", agent);
            return null;
        }
    }

    private async Task CloseAsync(BridgeRelayService? relay)
    {
        if (relay is null) return;
        try { await relay.DisposeAsync(); }
        catch (Exception) { logger.LogWarning("Stock control lane cleanup failed; manual reconciliation required"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var entry in _lanes.Values) await CloseAsync(entry.Relay);
            _lanes.Clear();
        }
        finally { _gate.Release(); }
    }
}
