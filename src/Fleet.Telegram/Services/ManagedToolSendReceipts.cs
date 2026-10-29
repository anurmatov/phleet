using Fleet.Shared.Journal;
using Fleet.Shared.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Fleet.Telegram.Services;

/// <summary>
/// Managed stock tool-send receipts (#492, existing-role routing): a receipt for a verified managed
/// record goes over that epoch's existing control lane — <c>fleet.group</c>, routed to the agent's
/// own queue, published as the epoch's control principal — never over the ordinary receipt exchange.
/// </summary>
public static class ManagedToolSendReceipts
{
    /// <summary>The existing per-epoch direct exchange the control principal may write.</summary>
    public const string Exchange = "fleet.group";

    /// <summary>
    /// Admission for the Telegram receiver: pins the receipt context from the verified record (and the
    /// request's MCP session), then proceeds unchanged. Every verified request is pinned, so a managed
    /// receipt can never take the ordinary route; one without a session is refused at publish.
    /// </summary>
    public static Task Pin(HttpContext context, StockEnrollmentAuthority record, string? tool,
        CancellationToken admission, Func<Task> proceed)
    {
        context.Items[ManagedToolSendPin.ItemKey] = new ManagedToolSendPin(
            ToolSendReceipts.RoutingKey(record.Agent), record.SandboxId, record.Epoch,
            context.Request.Headers["Mcp-Session-Id"].FirstOrDefault() ?? "");
        return proceed();
    }
}

/// <summary>Publishes one managed receipt; throws <see cref="StockIdentityDenied"/> for a stale context.</summary>
public interface IManagedToolSendReceiptTransport : IAsyncDisposable
{
    Task PublishAsync(ToolSendReceipt receipt, CancellationToken ct);
}

/// <summary>One producer-only connection/channel for one agent's epoch. A seam for tests.</summary>
public interface IManagedReceiptLane : IAsyncDisposable
{
    bool IsOpen { get; }

    Task PublishAsync(string exchange, string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body,
        CancellationToken ct);
}

/// <summary>
/// Per-agent control producers. Before every publish the agent's record is read again from the
/// trusted directory: it must still be active with the pinned epoch and sandbox, else the receipt is
/// refused (no retargeting, no ordinary-broker fallback). A lane whose binding changed — revoked,
/// re-epoched, rebound — is closed. Only the selected epoch's <c>control</c> credential is read;
/// nothing is declared, bound or consumed. Called from the publisher's one loop only.
/// </summary>
public sealed class StockControlReceiptTransport(
    string authorityDirectory,
    ILogger<StockControlReceiptTransport> logger,
    Func<ConnectionFactory, CancellationToken, Task<IManagedReceiptLane>>? openLane = null)
    : IManagedToolSendReceiptTransport
{
    private readonly Func<ConnectionFactory, CancellationToken, Task<IManagedReceiptLane>> _openLane
        = openLane ?? RabbitMqManagedReceiptLane.OpenAsync;

    private readonly Dictionary<string, (StockActiveRecord Record, StockBrokerIdentity Identity, IManagedReceiptLane Lane)> _lanes
        = new(StringComparer.Ordinal);

    public async Task PublishAsync(ToolSendReceipt receipt, CancellationToken ct)
    {
        var context = receipt.Managed ?? throw new StockIdentityDenied("receipt_context_missing");
        var agent = ToolSendReceipts.RoutingKey(receipt.Agent);
        if (!StockAuthoritySource.ValidAgent(agent) || context.Destination != receipt.Chat.Id
            || context.Session is not { Length: > 0 and <= 256 })
            throw new StockIdentityDenied("receipt_context_invalid");

        var active = StockAuthoritySource.Active(authorityDirectory);
        if (!active.TryGetValue(agent, out var current) || current is null
            || current.Record.Agent != agent || current.Record.Epoch != context.Epoch || current.Record.SandboxId != context.Sandbox)
        {
            await CloseAsync(agent);
            throw new StockIdentityDenied("receipt_context_stale");
        }

        var lane = await LaneAsync(agent, current, ct);
        var properties = lane.Identity.Properties(); // re-checks the binding; UserId = the epoch's control principal
        properties.ContentType = "application/json";
        properties.Type = ToolSendReceipts.ManagedMessageType;
        try
        {
            await lane.Lane.PublishAsync(ManagedToolSendReceipts.Exchange, agent, properties, ToolSendReceipts.Serialize(receipt), ct);
        }
        catch (Exception)
        {
            await CloseAsync(agent);
            throw;
        }
    }

    private async Task<(StockActiveRecord Record, StockBrokerIdentity Identity, IManagedReceiptLane Lane)> LaneAsync(
        string agent, StockActiveRecord current, CancellationToken ct)
    {
        if (_lanes.TryGetValue(agent, out var cached) && cached.Record.SameBinding(current) && cached.Lane.IsOpen)
            return cached;
        await CloseAsync(agent);

        var identity = new StockBrokerIdentity(current.Path,
            StockAuthoritySource.RolePassword(authorityDirectory, agent, "control"), "control");
        identity.RequireBinding(current.Record);
        var factory = new ConnectionFactory
        {
            HostName = identity.BrokerHost,
            ClientProvidedName = "fleet-telegram-stock-receipts",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            RequestedConnectionTimeout = ToolSendReceiptPublisher.PublishTimeout,
        };
        identity.Configure(factory);
        var lane = await _openLane(factory, ct);
        logger.LogInformation("journal: managed receipt control lane opened");
        return _lanes[agent] = (current, identity, lane);
    }

    private async Task CloseAsync(string agent)
    {
        if (!_lanes.Remove(agent, out var lane)) return;
        try { await lane.Lane.DisposeAsync(); }
        catch (Exception) { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _lanes.Keys.ToArray()) await CloseAsync(agent);
    }
}

/// <summary>RabbitMQ lane: no recovery (Configure disables it), one confirming channel, nothing declared.</summary>
internal sealed class RabbitMqManagedReceiptLane(IConnection connection, IChannel channel) : IManagedReceiptLane
{
    public static async Task<IManagedReceiptLane> OpenAsync(ConnectionFactory factory, CancellationToken ct)
    {
        var connection = await factory.CreateConnectionAsync(ct);
        try
        {
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
            return new RabbitMqManagedReceiptLane(connection, channel);
        }
        catch (Exception)
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public bool IsOpen => connection.IsOpen && channel.IsOpen;

    public async Task PublishAsync(string exchange, string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body,
        CancellationToken ct)
        => await channel.BasicPublishAsync(exchange, routingKey, mandatory: false, basicProperties: properties, body: body,
            cancellationToken: ct);

    public async ValueTask DisposeAsync()
    {
        try { await channel.DisposeAsync(); } catch (Exception) { /* already gone */ }
        await connection.DisposeAsync();
    }
}
