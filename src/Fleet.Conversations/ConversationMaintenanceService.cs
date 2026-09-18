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
    private DateTimeOffset _holdOffUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    /// <summary>What one tick did. Exposed so the loop's own sequencing is testable.</summary>
    internal sealed record TickResult
    {
        /// <summary>How long the service had been silent before this tick stamped.</summary>
        public required TimeSpan Gap { get; init; }

        /// <summary>True when this tick deliberately skipped the scan.</summary>
        public required bool HeldOff { get; init; }

        /// <summary>Attempts abandoned by this tick. Zero while held off.</summary>
        public required int Abandoned { get; init; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
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

    /// <summary>
    /// One tick: measure the silence, arm a hold-off if there was one, stamp, then scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>The order of the first three steps is the whole mechanism, and getting it wrong is
    /// silent.</b> This loop previously stamped first and scanned second, so the scan's own grace
    /// check always read a stamp this tick had just written, always measured a gap of zero, and
    /// never held off. The grace period existed in <see cref="Reconciler"/>, passed its unit tests —
    /// which set the stamp by hand — and could not fire in a deployment.
    /// </para>
    /// <para>
    /// The failure that leaves is the one the grace period exists to prevent: after a store outage
    /// or a restart, every lease in the database looks expired, because nothing was renewing them
    /// while this service was away. The first tick back would abandon every in-flight attempt at
    /// once and report a mass failure to every client for a fault entirely on this side.
    /// </para>
    /// <para>
    /// <paramref name="now"/> is a parameter rather than a read of the clock so a test can drive the
    /// window without waiting out two minutes of it.
    /// </para>
    /// </remarks>
    internal async Task<TickResult> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        // 1. Measure BEFORE stamping. After the stamp the answer is always zero.
        var gap = await reconciler.ReadHealthGapAsync(ct);

        // 2. Arm the hold-off if the silence was longer than one scan plus one agent heartbeat.
        //
        //    That tolerance, and not a bare scan interval, because the question is not "did this
        //    loop miss a tick" — scheduling jitter does that — but "was this service away long
        //    enough that an agent could have missed a renewal because of it". One heartbeat is
        //    exactly that span.
        var tolerance = options.ReconcilerScanInterval + options.HeartbeatInterval;

        if (gap > tolerance)
        {
            _holdOffUntil = now + options.ReconcilerGraceAfterRecovery;

            logger.LogInformation(
                "conversation maintenance was silent for {Seconds:F0}s; holding off the reconciler "
                + "for {GraceSeconds:F0}s so agents can renew",
                gap.TotalSeconds, options.ReconcilerGraceAfterRecovery.TotalSeconds);
        }

        // 3. Stamp, so the NEXT tick measures the silence since this one.
        await reconciler.RecordHealthyAsync(ct);

        if (now < _holdOffUntil)
        {
            ConversationMetrics.ReconcilerActions.Add(1, new KeyValuePair<string, object?>(
                "outcome", "held_within_grace"));

            return new TickResult { Gap = gap, HeldOff = true, Abandoned = 0 };
        }

        var scan = await reconciler.ScanOnceAsync(ct);

        // Garbage collection is hourly and independent of the hold-off: pruning expired rows is
        // safe whether or not an agent has had a chance to heartbeat.
        if (now >= _nextSweep)
        {
            await collector.SweepOnceAsync(ct);
            _nextSweep = now + options.GarbageCollectionInterval;
        }

        return new TickResult { Gap = gap, HeldOff = false, Abandoned = scan.Abandoned };
    }
}
