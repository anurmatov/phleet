using System.Collections.Concurrent;
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
/// <remarks>
/// A receiver that cannot resolve the authority directory, the control credential or the broker
/// identity never drops a receipt silently (#492 W6): each refusal is counted per fixed reason in
/// <see cref="Misconfigured"/> and warned about — content-free, the reason code and its count only —
/// on the first occurrence of each reason and then at most once per
/// <see cref="ToolSendReceiptPublisher.WarningInterval"/>. The receipt is still not sent.
/// </remarks>
public sealed class StockControlReceiptTransport(
    string authorityDirectory,
    ILogger<StockControlReceiptTransport> logger,
    Func<ConnectionFactory, CancellationToken, Task<IManagedReceiptLane>>? openLane = null,
    TimeProvider? time = null)
    : IManagedToolSendReceiptTransport
{
    /// <summary>No usable (set, fully qualified) <c>StockOpenShell:AuthorityDirectory</c>.</summary>
    public const string AuthorityDirectoryUnset = "authority_directory_unset";

    /// <summary>The epoch's <c>&lt;agent&gt;.control.password</c> is missing, unreadable or a link.</summary>
    public const string ControlCredentialUnavailable = "control_credential_unavailable";

    /// <summary>The epoch's control credential was read but is not a usable password.</summary>
    public const string ControlCredentialInvalid = "control_credential_invalid";

    private readonly Func<ConnectionFactory, CancellationToken, Task<IManagedReceiptLane>> _openLane
        = openLane ?? RabbitMqManagedReceiptLane.OpenAsync;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly Dictionary<string, (StockActiveRecord Record, StockBrokerIdentity Identity, IManagedReceiptLane Lane)> _lanes
        = new(StringComparer.Ordinal);

    private readonly Dictionary<string, long> _lastWarningTicks = new(StringComparer.Ordinal);

    /// <summary>Managed receipts refused because this receiver is misconfigured, per fixed reason.</summary>
    public ManagedReceiptDropCounters Misconfigured { get; } = new();

    public async Task PublishAsync(ToolSendReceipt receipt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authorityDirectory) || !Path.IsPathFullyQualified(authorityDirectory))
            throw Misconfiguration(AuthorityDirectoryUnset);

        var context = receipt.Managed ?? throw new StockIdentityDenied("receipt_context_missing");
        var agent = ToolSendReceipts.RoutingKey(receipt.Agent);
        if (!StockAuthoritySource.ValidAgent(agent) || context.Destination != receipt.Chat.Id
            || context.Session is not { Length: > 0 and <= 256 })
            throw new StockIdentityDenied("receipt_context_invalid");

        IReadOnlyDictionary<string, StockActiveRecord?> active;
        try { active = StockAuthoritySource.Active(authorityDirectory); }
        catch (StockIdentityDenied e) { throw Misconfiguration(e.Message); } // unreadable or oversized directory
        if (!active.TryGetValue(agent, out var current) || current is null
            || current.Record.Agent != agent || current.Record.Epoch != context.Epoch || current.Record.SandboxId != context.Sandbox)
        {
            await CloseAsync(agent);
            throw new StockIdentityDenied("receipt_context_stale");
        }

        (StockActiveRecord Record, StockBrokerIdentity Identity, IManagedReceiptLane Lane) lane;
        try { lane = await LaneAsync(agent, current, ct); }
        catch (StockIdentityDenied e)
        {
            // Credential or broker identity resolution failed: the lane can never open as configured.
            throw Misconfiguration(e.Message switch
            {
                "broker_credential_unavailable" => ControlCredentialUnavailable,
                "broker_credential_invalid" => ControlCredentialInvalid,
                var code => code,
            });
        }
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

    /// <summary>
    /// Counts one refusal under its fixed reason and warns (first time per reason, then at most once per
    /// interval). Never logs the receipt, its chat, session, agent or any credential: the reason is a
    /// fixed code, and anything else is reduced to <c>identity_denied</c>. Returns the refusal to throw.
    /// </summary>
    private StockIdentityDenied Misconfiguration(string? code)
    {
        var reason = FixedReason(code);
        var count = Misconfigured.Count(reason);

        var now = _time.GetUtcNow().UtcTicks;
        if (!_lastWarningTicks.TryGetValue(reason, out var last) || now - last >= ToolSendReceiptPublisher.WarningInterval.Ticks)
        {
            _lastWarningTicks[reason] = now;
            logger.LogWarning(
                "journal: managed tool-send receipt dropped, receiver misconfigured (reason={Reason}); "
                + "managed_receipts_misconfigured={Count} for this reason since start. "
                + "Repeats at most once a minute per reason",
                reason, count);
        }
        return new StockIdentityDenied(reason);
    }

    private static string FixedReason(string? code) =>
        code is { Length: > 0 and <= 64 } && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            ? code : "identity_denied";

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

/// <summary>
/// Per-reason drop counters for managed receipts, in-process like <see cref="ToolSendReceiptCounters"/>.
/// Keys are fixed reason codes only.
/// </summary>
public sealed class ManagedReceiptDropCounters
{
    private readonly ConcurrentDictionary<string, long> _byReason = new(StringComparer.Ordinal);
    private long _total;

    /// <summary>Every counted refusal, all reasons together.</summary>
    public long Total => Interlocked.Read(ref _total);

    /// <summary>Refusals counted under <paramref name="reason"/>; 0 when never seen.</summary>
    public long this[string reason] => _byReason.TryGetValue(reason, out var count) ? count : 0;

    /// <summary>A snapshot of every reason seen so far and its count.</summary>
    public IReadOnlyDictionary<string, long> ByReason => new Dictionary<string, long>(_byReason, StringComparer.Ordinal);

    internal long Count(string reason)
    {
        Interlocked.Increment(ref _total);
        return _byReason.AddOrUpdate(reason, 1, (_, count) => count + 1);
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
