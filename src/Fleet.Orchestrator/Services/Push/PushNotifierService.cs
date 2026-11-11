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
        if (!o.Enabled)
        {
            logger.LogInformation("Push notifications disabled");
            return;
        }
        if (o.DescribeFault() is { } fault)
        {
            // Named once, and treated as off: a half-configured sender must not look like a
            // working one that happens to be quiet.
            logger.LogError("push_misconfigured: {Fault}. Push notifications are off.", fault);
            return;
        }

        IApnsClient client;
        try { client = services.GetRequiredService<IApnsClient>(); }
        catch (FormatException ex)
        {
            logger.LogError("push_misconfigured: {Reason}. Push notifications are off.", ex.Message);
            return;
        }

        var detector = new PushEventDetector(TimeSpan.FromSeconds(o.EffectiveAgentDownGraceSeconds), stops);
        logger.LogInformation("PushNotifierService starting, interval={Interval}s", o.EffectiveIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var failed = await RecentlyFailedAsync(ct);
                var messages = detector.Observe(workflows.GetAll(), failed, agents.GetAll(), time.GetUtcNow());
                if (messages.Count > 0) await DeliverAsync(client, messages, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError("Push notifier pass failed ({Error})", ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(o.EffectiveIntervalSeconds), time, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
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

    private Task DeliverAsync(IApnsClient client, IReadOnlyList<PushMessage> messages, CancellationToken ct) =>
        PushDelivery.DeliverAsync(scopes, client, messages, logger, ct);
}

/// <summary>Sends each message to each registered device, and forgets devices Apple says are gone.</summary>
public static class PushDelivery
{
    public static async Task DeliverAsync(
        IServiceScopeFactory scopes, IApnsClient client, IReadOnlyList<PushMessage> messages, ILogger logger, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return;

        var devices = await db.PushDevices.ToListAsync(ct);
        if (devices.Count == 0) return;

        var forget = new List<PushDevice>();
        foreach (var message in messages)
        {
            foreach (var device in devices)
            {
                if (forget.Contains(device)) continue;
                var result = await client.SendAsync(device.Token, message, ct);
                switch (result.Outcome)
                {
                    case ApnsOutcome.Accepted:
                        logger.LogInformation("Push sent: {Kind}", message.Kind);
                        break;
                    case ApnsOutcome.Unregistered:
                        // Apple says this token is finished. Keeping it would mean a failed
                        // request on every event from now on.
                        logger.LogInformation("Push device {DeviceId} is no longer registered ({Reason}); forgetting it", device.DeviceId, result.Reason);
                        forget.Add(device);
                        break;
                    default:
                        logger.LogWarning("Push not sent: {Kind} ({Reason})", message.Kind, result.Reason);
                        break;
                }
            }
        }

        if (forget.Count > 0)
        {
            db.PushDevices.RemoveRange(forget);
            await db.SaveChangesAsync(ct);
        }
    }
}
