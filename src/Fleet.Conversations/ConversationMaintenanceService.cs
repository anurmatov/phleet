using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations;

/// <summary>
/// The two background sweeps: the reconciler every 30 seconds, garbage collection hourly.
/// </summary>
/// <remarks>
/// <para>
/// One hosted service rather than two, because the two share a liveness fact. The reconciler's
/// grace period is measured from <c>service_health</c>, and the thing that writes
/// <c>service_health</c> is this loop being alive — so a second host that could stop independently
/// would let the record go stale while the reconciler still ran, and every lease in the database
/// would look expired for a reason that had nothing to do with any agent.
/// </para>
/// <para>
/// Neither sweep is allowed to take the process down. A failure here is a dependency failure, and
/// the north routes do not need either sweep to answer.
/// </para>
/// </remarks>
public sealed class ConversationMaintenanceService(
    Reconciler reconciler,
    GarbageCollector collector,
    ConversationStoreOptions options,
    ILogger<ConversationMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSweep = DateTimeOffset.UtcNow + options.GarbageCollectionInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Liveness FIRST, and before the scan that depends on it. Written every tick, so the
                // grace window measures how long this service has been back rather than how long
                // ago it last did any work.
                await reconciler.RecordHealthyAsync(stoppingToken);

                var scan = await reconciler.ScanOnceAsync(stoppingToken);

                if (scan.WithinGrace)
                {
                    logger.LogDebug(
                        "reconciler scan held off: the service has not been healthy for the grace period");
                }

                if (DateTimeOffset.UtcNow >= nextSweep)
                {
                    await collector.SweepOnceAsync(stoppingToken);
                    nextSweep = DateTimeOffset.UtcNow + options.GarbageCollectionInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Type only. A driver's message can carry the connection string.
                logger.LogWarning("conversation maintenance failed and will retry: {Error}",
                    e.GetType().Name);
            }

            try
            {
                await Task.Delay(options.ReconcilerScanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
