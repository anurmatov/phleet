using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services.Push;

/// <summary>
/// Watches the fleet and sends a push notification when something needs a person: a gate is
/// waiting, a workflow failed, an agent went down. Off by default.
/// </summary>
/// <remarks>
/// It reads what other services already keep — the poller's workflow snapshot, the registry's
/// heartbeats — and changes none of it. A failure to send is logged by kind and reason, never with
/// a device token or any part of the key, and never stops the loop.
/// </remarks>
public sealed class PushNotifierService(
    IOptions<PushOptions> options,
    PushReadiness readiness,
    WorkflowStore workflows,
    AgentRegistry agents,
    TemporalClientRegistry temporal,
    OperatorStops stops,
    IServiceScopeFactory scopes,
    IServiceProvider services,
    TimeProvider time,
    ILogger<PushNotifierService> logger) : BackgroundService
{
    /// <summary>How far back a failed run is looked for. Also bounds what the detector remembers.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!readiness.Enabled)
        {
            logger.LogInformation("Push notifications disabled");
            return;
        }
        if (readiness.Fault is { } fault)
        {
            // Named once, and treated as off: a half-configured sender must not look like a
            // working one that happens to be quiet. /api/push reads the same answer.
            logger.LogError("push_misconfigured: {Fault}. Push notifications are off.", fault);
            return;
        }

        // The key was already read once by the readiness check, so this cannot fail on it.
        var client = services.GetRequiredService<IApnsClient>();
        var detector = new PushEventDetector(TimeSpan.FromSeconds(o.EffectiveAgentDownGraceSeconds), stops);
        var delivery = new PushDelivery(scopes, client, logger);
        logger.LogInformation("PushNotifierService starting, interval={Interval}s", o.EffectiveIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var failed = await RecentlyFailedAsync(ct);
                await PassAsync(detector, delivery, workflows.GetAll(), failed, agents.GetAll(), time.GetUtcNow(), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError("Push notifier pass failed ({Error})", ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(o.EffectiveIntervalSeconds), time, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// One look at the fleet: what is new goes to every device, and what a device missed on an
    /// earlier pass is sent to that device again while it is still true.
    /// </summary>
    public static async Task PassAsync(
        PushEventDetector detector, PushDelivery delivery,
        IReadOnlyCollection<WorkflowSummary> running, IReadOnlyCollection<WorkflowSummary> recentlyFailed,
        IReadOnlyCollection<AgentState> agents, DateTimeOffset now, CancellationToken ct)
    {
        var messages = detector.Observe(running, recentlyFailed, agents, now);
        await delivery.DeliverAsync(messages, detector.IsCurrent, ct);
    }

    private async Task<IReadOnlyCollection<WorkflowSummary>> RecentlyFailedAsync(CancellationToken ct)
    {
        if (!temporal.IsConfigured) return [];
        try { return await temporal.ListRecentFailuresAsync(FailureWindow, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Returning an empty list here would make the detector forget what it had seen and
            // announce it all again on the next pass. Throwing skips this pass instead.
            throw new InvalidOperationException("failure query unavailable", ex);
        }
    }
}

/// <summary>
/// Sends each new message to each registered device, sends it again on later passes to a device
/// that did not accept it, and forgets devices Apple says are gone.
/// </summary>
/// <remarks>
/// What a device is owed is held in memory only, so a restart owes nothing and stays quiet. One
/// entry per event and device at most, and each ends when that device accepts, when the event is no
/// longer current (the gate decided, the agent back), when the device is removed, or after
/// <see cref="MaxAttempts"/> sends. A device that accepted is never sent the same event again.
/// </remarks>
public sealed class PushDelivery(IServiceScopeFactory scopes, IApnsClient client, ILogger logger)
{
    /// <summary>
    /// Sends of one notification to one device, the first included: about five minutes at the default
    /// interval. A refusal that is not transient, such as a wrong key or topic, stops here.
    /// </summary>
    public const int MaxAttempts = 10;

    private readonly Dictionary<(string Key, string DeviceId), (PushMessage Message, int Attempts)> _owed = [];

    /// <summary>Notifications a device has not accepted yet and will be sent again.</summary>
    public int Owed => _owed.Count;

    public async Task DeliverAsync(IReadOnlyList<PushMessage> messages, Func<string, bool> isCurrent, CancellationToken ct)
    {
        foreach (var stale in _owed.Keys.Where(k => !isCurrent(k.Key)).ToList()) _owed.Remove(stale);
        if (messages.Count == 0 && _owed.Count == 0) return;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return;

        var devices = await db.PushDevices.ToListAsync(ct);
        var byId = devices.ToDictionary(d => d.DeviceId, StringComparer.Ordinal);
        foreach (var removed in _owed.Keys.Where(k => !byId.ContainsKey(k.DeviceId)).ToList()) _owed.Remove(removed);

        // What a device missed earlier, then each new message to every device. Recorded as owed
        // before it is sent, so a pass that stops part-way leaves the rest for the next one.
        var sends = _owed.Keys.ToList();
        foreach (var message in messages)
        {
            foreach (var device in devices)
            {
                var key = (message.Key, device.DeviceId);
                if (_owed.TryAdd(key, (message, 0))) sends.Add(key);
            }
        }

        var forget = new List<PushDevice>();
        foreach (var key in sends)
        {
            var device = byId[key.DeviceId];
            if (forget.Contains(device)) continue;
            var (message, attempts) = _owed[key];
            var result = await client.SendAsync(device.Token, message, ct);
            switch (result.Outcome)
            {
                case ApnsOutcome.Accepted:
                    _owed.Remove(key);
                    logger.LogInformation("Push sent: {Kind}", message.Kind);
                    break;
                case ApnsOutcome.Unregistered:
                    // Apple says this token is finished. Keeping it would mean a failed
                    // request on every event from now on.
                    logger.LogInformation("Push device {DeviceId} is no longer registered ({Reason}); forgetting it", device.DeviceId, result.Reason);
                    forget.Add(device);
                    break;
                default:
                    if (attempts + 1 < MaxAttempts)
                    {
                        _owed[key] = (message, attempts + 1);
                        logger.LogWarning("Push not sent: {Kind} ({Reason}); will try again", message.Kind, result.Reason);
                    }
                    else
                    {
                        _owed.Remove(key);
                        logger.LogWarning("Push not sent: {Kind} ({Reason}); given up after {Attempts} attempts", message.Kind, result.Reason, MaxAttempts);
                    }
                    break;
            }
        }

        if (forget.Count > 0)
        {
            foreach (var gone in _owed.Keys.Where(k => forget.Any(d => d.DeviceId == k.DeviceId)).ToList()) _owed.Remove(gone);
            db.PushDevices.RemoveRange(forget);
            await db.SaveChangesAsync(ct);
        }
    }
}
