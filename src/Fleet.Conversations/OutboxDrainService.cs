using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations;

/// <summary>
/// The hosted loop that drains both outboxes after commit.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>OutboxPublisher.DrainOnceAsync</c> has no caller and a durably accepted
/// submission sits <c>pending</c> forever with no agent dispatched to. That was the honest
/// intermediate state PR 1 shipped and stated; this is the thing that ends it.
/// </para>
/// <para>
/// <b>A publish failure is not a fault of this loop.</b> The broker being unreachable leaves rows
/// pending, raises the pending-age metric and leaves the client unaffected, because its submission is
/// already durable and dispatches on recovery. So the loop logs and continues rather than crashing
/// the host: a process that exited here would take the north routes down over a dependency the
/// routes do not need.
/// </para>
/// </remarks>
public sealed class OutboxDrainService(
    RabbitMqOutboxTransport commandTransport,
    RabbitMqOutboxTransport eventTransport,
    string connectionString,
    string agentName,
    ConversationStoreOptions options,
    ILogger<OutboxDrainService> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait after a drain that published nothing.
    /// </summary>
    /// <remarks>
    /// Short, because it is the floor on dispatch latency for a submission the client is waiting on.
    /// A drain that published a full batch does not wait at all — there is more work by definition.
    /// </remarks>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Declared before the first drain, and by THIS service. A command published to an exchange
        // with no bound queue is discarded silently by the broker, so a submission accepted before
        // the agent has ever started would be lost with the submission row still saying it was
        // dispatched.
        await DeclareAsync(stoppingToken);

        var commands = new OutboxPublisher(
            connectionString, OutboxPublisher.CommandTable, commandTransport, options, logger);

        var events = new OutboxPublisher(
            connectionString, OutboxPublisher.EventTable, eventTransport, options, logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;

            try
            {
                // The command outbox first. A client is waiting on it; nothing is waiting on the
                // event outbox in this slice, which has no production consumer yet.
                published += await commands.DrainOnceAsync(agentName, stoppingToken);

                // Events route per conversation, so the routing key comes from the row rather than
                // from here. The argument is the default for rows that carry none.
                published += await events.DrainOnceAsync(routingKey: string.Empty, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Type only: a connection string can appear in a driver's message.
                logger.LogWarning("outbox drain failed and will retry: {Error}", e.GetType().Name);
            }

            if (published == 0)
                await Delay(IdleInterval, stoppingToken);
        }
    }

    private async Task DeclareAsync(CancellationToken ct)
    {
        // Retried rather than fatal. A broker that is not up yet at container start is ordinary —
        // compose starts services in parallel — and the correct response is to keep trying, not to
        // take down a service whose client-facing routes do not need the broker at all.
        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await commandTransport.DeclareTopologyAsync(agentName, ct);
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(
                    "broker topology declaration failed (attempt {Attempt}): {Error}",
                    attempt, e.GetType().Name);

                await Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 2)), ct);
            }
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
