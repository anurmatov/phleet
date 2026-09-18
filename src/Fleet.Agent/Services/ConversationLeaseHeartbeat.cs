using Fleet.Agent.Configuration;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Renews the lease on every attempt this process owns and has not committed (#303).
/// </summary>
/// <remarks>
/// <para>
/// The owned set is <see cref="ConversationSouthConsumer.OwnedAttemptIds"/> — running attempts AND
/// dispositioned-but-unstarted ones. Renewing only the running attempt abandons a full queue at the
/// lease bound and reports it to the client as an unknown outcome.
/// </para>
/// <para>
/// <b>A failed heartbeat never kills a live turn.</b> The turn keeps running and the next tick
/// retries. If the reconciler abandons the attempt anyway, the terminal arrives later as a recovered
/// answer — which the store preserves rather than discards. Stopping the executor because an HTTP
/// call failed would turn a transient store outage into lost work.
/// </para>
/// </remarks>
public sealed class ConversationLeaseHeartbeat : BackgroundService
{
    private readonly ConversationSouthConsumer _consumer;
    private readonly ConversationSouthClient _client;
    private readonly ConversationSouthCounters _counters;
    private readonly ConversationsOptions _options;
    private readonly ILogger<ConversationLeaseHeartbeat> _logger;

    public ConversationLeaseHeartbeat(
        ConversationSouthConsumer consumer,
        ConversationSouthClient client,
        ConversationSouthCounters counters,
        IOptions<ConversationsOptions> options,
        ILogger<ConversationLeaseHeartbeat> logger)
    {
        _consumer = consumer;
        _client = client;
        _counters = counters;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            var attempts = _consumer.OwnedAttemptIds;
            if (attempts.Count == 0) continue;

            try
            {
                var result = await _client.HeartbeatAsync(
                    new HeartbeatRequest { AttemptIds = [.. attempts], Owner = _consumer.Owner },
                    stoppingToken);

                if (result.NotOwned.Count > 0)
                {
                    // Reported rather than silently ignored: an attempt this process no longer owns
                    // has been abandoned, committed or leased elsewhere, and the work on it will not
                    // land. Identifiers only.
                    _counters.HeartbeatNotOwned(result.NotOwned.Count);
                    _logger.LogWarning(
                        "south heartbeat reported {Count} attempt(s) this process no longer owns",
                        result.NotOwned.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _counters.HeartbeatFailure();
                _logger.LogWarning("south heartbeat failed: {Error}", e.GetType().Name);
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
