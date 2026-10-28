using Fleet.Orchestrator.Configuration;
using Fleet.Shared.Security;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Orchestrator.Services;

/// <summary>Optional, independently connected observer. Failure never stops ordinary consumption.</summary>
public sealed class StockHeartbeatObserverService(
    IOptions<RabbitMqOptions> options,
    AgentRegistry registry,
    AgentConfigPublisherService publisher,
    IConfiguration configuration,
    StockRuntimeMutationHold hold,
    ILogger<HeartbeatConsumerService> consumerLogger,
    ILogger<StockHeartbeatObserverService> logger) : BackgroundService
{
    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>>? Connect { get; init; }

    /// <summary>How often managed lanes (<c>StockOpenShell:AuthorityDirectory</c>, else <c>&lt;ConfigDir&gt;/managed/authority</c>) are reconciled.</summary>
    internal TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(30);

    private sealed record Lane(StockActiveRecord Source, HeartbeatConsumerService? Receiver);

    // Touched only by the single directory loop (or a test driving ReconcileAsync without starting the service).
    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Agents with a lane, and whether that lane's receiver started.</summary>
    internal IReadOnlyDictionary<string, bool> Lanes => _lanes.ToDictionary(l => l.Key, l => l.Value.Receiver is not null);

    /// <summary>Same derivation as the mutation hold, so shipped ConfigDir-only wiring still observes managed lanes.</summary>
    internal string? ManagedDirectory => configuration["StockOpenShell:AuthorityDirectory"] ?? hold.ManagedDirectory;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = ManagedDirectory;
        return string.IsNullOrEmpty(directory) ? ObserveEnrolledAsync(stoppingToken)
            : Task.WhenAll(ObserveEnrolledAsync(stoppingToken), ObserveManagedAsync(directory, stoppingToken));
    }

    /// <summary>The single-file enrollment (<c>AuthorityFile</c>/<c>BrokerPasswordFile</c>), unchanged.</summary>
    private async Task ObserveEnrolledAsync(CancellationToken stoppingToken)
    {
        HeartbeatConsumerService? receiver = null;
        try
        {
            var identity = StockBrokerIdentity.Optional(configuration["StockOpenShell:AuthorityFile"],
                configuration["StockOpenShell:BrokerPasswordFile"], "observer");
            if (identity is null) return;
            // Select the enrolled endpoint without mutating the ordinary consumer's options.
            // Configure still checks this exact host against fresh authority before connecting.
            var observerOptions = Options.Create(new RabbitMqOptions
            {
                Host = identity.BrokerHost,
                Exchange = options.Value.Exchange
            });
            receiver = new(observerOptions, registry, publisher, configuration, consumerLogger, hold, identity) { Connect = Connect };
            await receiver.StartAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Do not expose credentials or exception details, retry, or fall back to the default broker.
            logger.LogWarning("Stock observer stopped; manual reconciliation required");
        }
        finally
        {
            if (receiver is not null)
            {
                try { await receiver.DisposeAsync(); }
                catch (Exception) { logger.LogWarning("Stock observer cleanup failed; manual reconciliation required"); }
            }
        }
    }

    /// <summary>One observer receiver per active managed epoch, each in its own vhost as <c>-observer</c>.</summary>
    private async Task ObserveManagedAsync(string directory, CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await ReconcileAsync(directory, stoppingToken);
                await Task.Delay(ReconcileInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await StopLanesAsync(); }
    }

    /// <summary>
    /// Starts a receiver for each new or re-enrolled record and disposes those whose record vanished, was
    /// revoked or changed binding. A lane that failed to start, or whose connection later drops, is NOT
    /// retried by this reconcile: like <see cref="StockBrokerIdentity.Configure"/> (automatic recovery off,
    /// no stock reconnect loop) it stays held for manual reconciliation until that agent's record binding
    /// changes, e.g. a new epoch. One lane never affects another lane or the ordinary consumer.
    /// </summary>
    internal async Task ReconcileAsync(string directory, CancellationToken ct)
    {
        IReadOnlyDictionary<string, StockActiveRecord?> active;
        try { active = StockAuthoritySource.Active(directory); }
        catch (StockIdentityDenied)
        {
            // Existing receivers re-check their own record on every delivery; nothing new starts.
            logger.LogWarning("Stock observer directory unavailable; manual reconciliation required");
            return;
        }
        foreach (var (agent, lane) in _lanes.ToList())
        {
            if (active.GetValueOrDefault(agent) is { } fresh && lane.Source.SameBinding(fresh)) continue;
            _lanes.Remove(agent);
            await CloseAsync(lane.Receiver);
        }
        foreach (var (agent, fresh) in active)
        {
            if (fresh is null || _lanes.ContainsKey(agent)) continue;
            _lanes[agent] = new(fresh, await StartLaneAsync(directory, fresh, ct));
        }
    }

    private async Task<HeartbeatConsumerService?> StartLaneAsync(string directory, StockActiveRecord record, CancellationToken ct)
    {
        HeartbeatConsumerService? receiver = null;
        try
        {
            var identity = new StockBrokerIdentity(record.Path,
                StockAuthoritySource.RolePassword(directory, record.Record.Agent, "observer"), "observer");
            // The record's own BrokerHost, never the ordinary consumer's options; Configure rechecks it.
            receiver = new(Options.Create(new RabbitMqOptions { Host = identity.BrokerHost, Exchange = options.Value.Exchange }),
                registry, publisher, configuration, consumerLogger, hold, identity) { Connect = Connect };
            await receiver.StartAsync(ct);
            return receiver;
        }
        catch (Exception)
        {
            await CloseAsync(receiver);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            logger.LogWarning("Stock observer lane for {Agent} stopped; manual reconciliation required", record.Record.Agent);
            return null;
        }
    }

    internal async Task StopLanesAsync()
    {
        foreach (var lane in _lanes.Values) await CloseAsync(lane.Receiver);
        _lanes.Clear();
    }

    private async Task CloseAsync(HeartbeatConsumerService? receiver)
    {
        if (receiver is null) return;
        try { await receiver.DisposeAsync(); }
        catch (Exception) { logger.LogWarning("Stock observer cleanup failed; manual reconciliation required"); }
    }
}
