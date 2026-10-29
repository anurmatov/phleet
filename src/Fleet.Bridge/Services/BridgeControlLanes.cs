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

    /// <summary>A lane for one record binding; a failed open (null relay) is retried a bounded number of times.</summary>
    private sealed record Entry(StockActiveRecord Source, BridgeRelayService? Relay, int Attempts = 1, DateTimeOffset RetryAt = default);

    /// <summary>Opens of one record binding before its failed lane stays held until the binding changes.</summary>
    internal const int MaxLaneAttempts = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _lanes = new(StringComparer.Ordinal);
    // Once seen as managed, a target stays managed for this process: removing its record refuses.
    private readonly ConcurrentDictionary<string, byte> _managed = new(StringComparer.Ordinal);

    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>> Connect { get; set; } =
        (factory, ct) => factory.CreateConnectionAsync(ct);

    /// <summary>The ordinary relay's forward of admitted typed workflow replies to the Temporal receiver (#492).</summary>
    internal Func<BridgeRelayService.RelayMessage, Task>? ForwardWorkflowReply { get; init; }

    /// <summary>First backoff before a lane requeues a reply whose forward failed (#492 R4).</summary>
    internal TimeSpan ForwardRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>First backoff before a failed lane of an unchanged binding is reopened; doubles per attempt.</summary>
    internal TimeSpan LaneRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Quiet period after a trusted-directory change before the lanes are reconciled.</summary>
    internal TimeSpan ReconcileDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    private readonly CancellationTokenSource _stopping = new();
    private FileSystemWatcher? _watcher;
    private int _reconcilePending;

    /// <summary>
    /// Opens the lane of every currently active record and keeps the lanes reconciled with the trusted
    /// authority directory (#492 R3): enrollment or re-epoch opens or replaces the agent's lane (declaring its
    /// epoch reply queue and its sole consumer) without waiting for a bridge ask, and a vanished, revoked or
    /// ambiguous record closes it. The trigger is a watcher on that local directory, not a broker poller.
    /// </summary>
    internal async Task OpenActiveAsync(CancellationToken ct)
    {
        Watch();
        await ReconcileAsync(ct);
    }

    /// <summary>Opens, replaces or retries the lane of each active record and closes lanes without one.</summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, StockActiveRecord?> active;
        try { active = StockAuthoritySource.Active(directory); }
        catch (StockIdentityDenied) { logger.LogWarning("Stock control lanes not reconciled: authority unavailable"); return; }
        // Ordinal order: lane opens do not depend on the directory listing order.
        foreach (var (agent, record) in active.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (record is not null) await RouteAsync(agent, connect: true, ct);
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var agent in _lanes.Keys.Where(a => active.GetValueOrDefault(a) is null).ToList())
            {
                // Removed, revoked or ambiguous: the old epoch connection never serves the agent again.
                var entry = _lanes[agent];
                _lanes.Remove(agent);
                await CloseAsync(entry.Relay);
            }
        }
        finally { _gate.Release(); }
    }

    private void Watch()
    {
        if (_watcher is not null) return;
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
            };
            watcher.Created += OnAuthorityChanged;
            watcher.Changed += OnAuthorityChanged;
            watcher.Deleted += OnAuthorityChanged;
            watcher.Renamed += OnAuthorityChanged;
            watcher.Error += (_, _) => ScheduleReconcile(ReconcileDelay); // overflow: re-read the whole directory
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception)
        {
            logger.LogWarning("Stock authority directory not watched; lanes reconcile only at startup and on bridge asks");
        }
    }

    private void OnAuthorityChanged(object sender, FileSystemEventArgs e)
    {
        // Coalesce a burst (record and password written together) into one reconcile after a quiet period.
        if (Interlocked.Exchange(ref _reconcilePending, 1) == 0) ScheduleReconcile(ReconcileDelay, coalesced: true);
    }

    private void ScheduleReconcile(TimeSpan delay, bool coalesced = false)
    {
        if (_stopping.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _stopping.Token);
                if (coalesced) Interlocked.Exchange(ref _reconcilePending, 0);
                await ReconcileAsync(_stopping.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { logger.LogWarning("Stock control lane reconciliation failed; manual reconciliation required"); }
        });
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
            if (_stopping.IsCancellationRequested) return new(true, null); // disposed: never reopen
            if (_lanes.TryGetValue(agent, out var entry) && (fresh is null || !entry.Source.SameBinding(fresh)))
            {
                // Re-enrolled, revoked or removed: the old epoch connection never serves the agent again.
                _lanes.Remove(agent);
                await CloseAsync(entry.Relay);
                entry = null;
            }
            if (fresh is null) return new(true, null);
            // A failed lane of the same binding is retried with a doubling backoff, at most MaxLaneAttempts
            // opens in all; after that it stays held (refusing) until re-enrollment changes the binding.
            if (entry is null || (entry.Relay is null && entry.Attempts < MaxLaneAttempts && DateTimeOffset.UtcNow >= entry.RetryAt))
            {
                var attempts = entry is null ? 1 : entry.Attempts + 1;
                var relay = await OpenAsync(agent, fresh, ct);
                var delay = LaneRetryDelay * (1 << (attempts - 1));
                entry = new(fresh, relay, attempts, DateTimeOffset.UtcNow + delay);
                _lanes[agent] = entry;
                // Bounded retry of this binding only; it reads the local directory again, not the broker.
                if (relay is null && attempts < MaxLaneAttempts) ScheduleReconcile(delay + TimeSpan.FromMilliseconds(50));
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
            { Connect = Connect, ForwardWorkflowReply = ForwardWorkflowReply, ForwardRetryDelay = ForwardRetryDelay };
            await relay.InitializeAsync(ct);
            return relay;
        }
        catch (Exception)
        {
            await CloseAsync(relay);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); // not a lane outcome
            // No reconnect loop: the caller retries this binding a bounded number of times, then the failed
            // lane stays held (refusing) until this agent's record binding changes.
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
        _stopping.Cancel();
        _watcher?.Dispose();
        await _gate.WaitAsync();
        try
        {
            foreach (var entry in _lanes.Values) await CloseAsync(entry.Relay);
            _lanes.Clear();
        }
        finally { _gate.Release(); }
    }
}
