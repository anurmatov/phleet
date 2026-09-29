using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations.Journal;

/// <summary>
/// The media gate: whether the object store is currently usable, and the loop that keeps that
/// answer fresh.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three states, and the difference between two of them is the whole design.</b>
/// <c>disabled</c> means no bucket is configured — no upload route exists at all.
/// <c>enabled</c> means the probe answered. <c>degraded</c> means the probe could not reach the
/// bucket, and the service is <b>running</b>: uploads answer <c>503 media_unavailable</c>, the rest
/// of the journal works, and the container stays healthy.
/// </para>
/// <para>
/// ⚠️ A store that is merely unreachable must never be a startup failure. The bucket is a
/// dependency the deployment can restart, and a Comms that refused to start because MinIO was
/// cycling would take the text journal down with it — a dependency failure promoted into an outage
/// of a feature that does not use the dependency.
/// </para>
/// <para>
/// ⚠️ A store whose credentials are wrong is NOT degraded. That is refused at startup by
/// <see cref="S3ObjectStore.ProbeAsync"/>, because it will not repair itself and a service that
/// starts degraded forever is how a bad deployment hides.
/// </para>
/// </remarks>
public sealed class JournalMediaHealth(
    IJournalObjectStore store,
    ILogger logger,
    TimeProvider? time = null) : BackgroundService, JournalMediaGate
{
    /// <summary>How long a probe answer is trusted.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private volatile bool _available;
    private long _validUntilTicks;

    /// <summary>
    /// Set once at startup by the host, from the probe that decided the process is allowed to run.
    /// Without it the first 30 s of traffic would be told <c>media_unavailable</c> on a healthy
    /// deployment, because nothing has probed yet.
    /// </summary>
    public void MarkStartupState(bool available)
    {
        _available = available;
        Interlocked.Exchange(ref _validUntilTicks, (_time.GetUtcNow() + CacheDuration).UtcTicks);
    }

    public string MediaState => _available ? "enabled" : "degraded";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_time.GetUtcNow().UtcTicks < Interlocked.Read(ref _validUntilTicks)) return _available;

        var fresh = await ProbeOnceAsync(ct);
        Interlocked.Exchange(ref _validUntilTicks, (_time.GetUtcNow() + CacheDuration).UtcTicks);
        return fresh;
    }

    /// <summary>One probe, and the only place <see cref="_available"/> changes at run time.</summary>
    public async Task<bool> ProbeOnceAsync(CancellationToken ct)
    {
        bool reachable;
        try
        {
            reachable = await store.ProbeAsync(ct);
        }
        catch (JournalProbeFailureException)
        {
            // The operator has to change something. Logged once per probe with a fixed code; the
            // exception's own text is deliberately not repeated here.
            logger.LogWarning("journal media credentials_rejected");
            reachable = false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("journal media probe failed: {Error}", e.GetType().Name);
            reachable = false;
        }

        if (reachable != _available)
        {
            _available = reachable;
            logger.LogInformation("journal media state is now {State}", MediaState);
        }

        return reachable;
    }

    /// <summary>
    /// Re-probes while degraded, so a bucket that comes back is picked up without a restart. While
    /// healthy it idles on the same interval — the cost is one HEAD per 30 s, and the alternative
    /// is a cached answer that can outlive an outage by an unbounded amount.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeOnceAsync(stoppingToken);
                Interlocked.Exchange(ref _validUntilTicks, (_time.GetUtcNow() + CacheDuration).UtcTicks);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning("journal media probe loop failed: {Error}", e.GetType().Name);
            }

            try
            {
                await Task.Delay(CacheDuration, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
