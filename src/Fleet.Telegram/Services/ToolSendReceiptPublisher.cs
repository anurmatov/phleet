using System.Threading.Channels;
using Fleet.Shared.Journal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Fleet.Telegram.Services;

/// <summary>
/// Publishes tool-send receipts (#394) to <c>fleet.journal.tool-sends</c>, fire-and-forget.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enqueue"/> is all a tool call touches: a non-blocking write into a bounded channel
/// of <see cref="Capacity"/>. The broker sits behind it, on this service's own loop, so a broker
/// that is down, slow or full can lose a receipt but never delays, fails or changes a tool result.
/// A lost receipt is counted in <see cref="Counters"/> and warned about at most once a minute.
/// </para>
/// <para>
/// Off unless <c>Journal:ToolSendReceipts</c> is <c>true</c>. Off, there is no transport at all:
/// nothing connects, nothing is declared, and <see cref="Enqueue"/> returns at once.
/// </para>
/// </remarks>
public sealed class ToolSendReceiptPublisher : BackgroundService
{
    public const int Capacity = 1_000;

    /// <summary>A publish the broker has not confirmed by then is dropped.</summary>
    public static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private readonly IToolSendReceiptTransport? _transport;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    // Wait, not DropWrite: in a drop mode TryWrite reports success for the receipt it discards, and
    // the drop would go uncounted. TryWrite never waits in either mode.
    private readonly Channel<ToolSendReceipt> _pending = Channel.CreateBounded<ToolSendReceipt>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private long _lastWarningTicks;

    /// <param name="transport">Null when receipts are off.</param>
    public ToolSendReceiptPublisher(
        IToolSendReceiptTransport? transport, ILogger<ToolSendReceiptPublisher> logger, TimeProvider? time = null)
    {
        _transport = transport;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool Enabled => _transport is not null;

    public ToolSendReceiptCounters Counters { get; } = new();

    /// <summary>Receipts waiting for the broker.</summary>
    public int Pending => _pending.Reader.Count;

    /// <summary>Queues a receipt for the broker. Never blocks, never throws.</summary>
    public void Enqueue(ToolSendReceipt receipt)
    {
        if (_transport is null) return;
        if (!_pending.Writer.TryWrite(receipt)) Dropped("overflow");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_transport is null) return;

        try
        {
            await foreach (var receipt in _pending.Reader.ReadAllAsync(stoppingToken))
                await PublishAsync(_transport, receipt, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // What is still queued at shutdown will never be published.
        while (_pending.Reader.TryRead(out _)) Dropped("shutdown");
    }

    private async Task PublishAsync(IToolSendReceiptTransport transport, ToolSendReceipt receipt, CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(PublishTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        try
        {
            await transport.PublishAsync(
                ToolSendReceipts.RoutingKey(receipt.Agent), ToolSendReceipts.Serialize(receipt), linked.Token);
            Counters.CountPublished();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Dropped("shutdown");
            throw;
        }
        catch (Exception e)
        {
            Dropped(timeout.IsCancellationRequested ? "timeout" : "broker", e);
        }
    }

    /// <summary>Counts one lost receipt; warns at most once per <see cref="WarningInterval"/>.</summary>
    internal void Dropped(string reason, Exception? error = null)
    {
        var dropped = Counters.CountDropped();

        var now = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if (last != 0 && now - last < WarningInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastWarningTicks, now, last) != last) return;

        // Fixed codes and the exception type only. A broker error message can carry a host, and the
        // receipt itself carries message text and a chat id.
        _logger.LogWarning(
            "journal: tool-send receipt dropped (reason={Reason}, error={Error}); "
            + "journal_receipts_dropped={Dropped} journal_receipts_published={Published} since start. "
            + "Repeats at most once a minute",
            reason, error?.GetType().Name ?? "none", dropped, Counters.Published);
    }
}

/// <summary>Registration for tool-send receipts, keyed off <see cref="EnabledKey"/>.</summary>
public static class ToolSendReceiptRegistration
{
    /// <summary><c>Journal__ToolSendReceipts</c>. Anything but <c>true</c> is off.</summary>
    public const string EnabledKey = "Journal:ToolSendReceipts";

    /// <summary>The broker, from the same variable the peer-config subscription reads.</summary>
    public const string BrokerUrlKey = "RABBITMQ_URL";

    public const string DefaultBrokerUrl = "amqp://rabbitmq:5672/";

    /// <summary>
    /// Registers <see cref="TelegramSender"/> and the publisher. Off, the publisher has no
    /// transport and no hosted loop is registered, so fleet-telegram opens nothing on the broker for
    /// receipts and declares no exchange. An unreadable value is off rather than a failed start: the
    /// send tools must not depend on the journal.
    /// </summary>
    public static IServiceCollection AddToolSendReceipts(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<TelegramSender>();

        if (!bool.TryParse(configuration[EnabledKey], out var enabled) || !enabled)
        {
            services.AddSingleton(sp => new ToolSendReceiptPublisher(
                transport: null, sp.GetRequiredService<ILogger<ToolSendReceiptPublisher>>()));
            return services;
        }

        var brokerUrl = configuration[BrokerUrlKey] is { Length: > 0 } url ? url : DefaultBrokerUrl;

        services.AddSingleton<IToolSendReceiptTransport>(sp => new RabbitMqToolSendReceiptTransport(
            brokerUrl, sp.GetRequiredService<ILogger<RabbitMqToolSendReceiptTransport>>()));
        services.AddSingleton(sp => new ToolSendReceiptPublisher(
            sp.GetRequiredService<IToolSendReceiptTransport>(),
            sp.GetRequiredService<ILogger<ToolSendReceiptPublisher>>()));
        services.AddHostedService(sp => sp.GetRequiredService<ToolSendReceiptPublisher>());

        return services;
    }
}

/// <summary>
/// The publisher's counters, in-process like the agent's <c>JournalCounters</c>. The drop warning
/// carries both totals.
/// </summary>
public sealed class ToolSendReceiptCounters
{
    private long _published;
    private long _dropped;

    /// <summary><c>journal_receipts_published</c>: receipts the broker confirmed.</summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary><c>journal_receipts_dropped</c>: receipts lost to overflow, a broker error or shutdown.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    internal void CountPublished() => Interlocked.Increment(ref _published);

    internal long CountDropped() => Interlocked.Increment(ref _dropped);
}

/// <summary>The broker behind <see cref="ToolSendReceiptPublisher"/>. Called from its one loop only.</summary>
public interface IToolSendReceiptTransport : IAsyncDisposable
{
    /// <summary>Returns once the broker has the message; throws when it does not.</summary>
    Task PublishAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct);
}

/// <summary>
/// RabbitMQ: one auto-recovering connection, opened on the first receipt, and one confirming
/// channel that declares the exchange. No lock: the publisher's loop is the only caller.
/// </summary>
public sealed class RabbitMqToolSendReceiptTransport(
    string brokerUrl, ILogger<RabbitMqToolSendReceiptTransport> logger, TimeProvider? time = null)
    : IToolSendReceiptTransport
{
    /// <summary>After a failed connect, receipts are dropped without another attempt for this long.</summary>
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private IConnection? _connection;
    private IChannel? _channel;
    private DateTimeOffset _nextConnect = DateTimeOffset.MinValue;

    public async Task PublishAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var channel = await ChannelAsync(ct);

        try
        {
            // With confirmations and tracking on the channel this returns once the broker has the
            // message and throws on a nack, so "published" counts only what the broker took.
            await channel.BasicPublishAsync(
                ToolSendReceipts.Exchange, routingKey, mandatory: false,
                basicProperties: new BasicProperties
                {
                    DeliveryMode = DeliveryModes.Persistent,
                    ContentType = "application/json",
                },
                body: body, cancellationToken: ct);
        }
        catch (Exception)
        {
            // A nacked or abandoned publish can leave the channel unusable; the next receipt opens a
            // fresh one.
            await ResetChannelAsync();
            throw;
        }
    }

    private async Task<IChannel> ChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await ResetChannelAsync();

        if (_connection is null)
        {
            if (_time.GetUtcNow() < _nextConnect) throw new BrokerReconnectPendingException();

            try
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(brokerUrl),
                    ClientProvidedName = "fleet-telegram-tool-sends",
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                    RequestedHeartbeat = TimeSpan.FromSeconds(30),
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                    RequestedConnectionTimeout = ToolSendReceiptPublisher.PublishTimeout,
                };
                _connection = await factory.CreateConnectionAsync(ct);
                logger.LogInformation("journal: tool-send receipts connected to the broker");
            }
            catch (Exception)
            {
                _nextConnect = _time.GetUtcNow() + ReconnectDelay;
                throw;
            }
        }

        // While the connection is recovering this throws and the receipt is dropped; once it has
        // recovered the next receipt gets a channel again.
        var channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);

        try
        {
            await channel.ExchangeDeclareAsync(
                ToolSendReceipts.Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        }
        catch (Exception)
        {
            await DisposeQuietlyAsync(channel);
            throw;
        }

        _channel = channel;
        return channel;
    }

    private async Task ResetChannelAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is not null) await DisposeQuietlyAsync(channel);
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try { await disposable.DisposeAsync(); }
        catch (Exception) { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetChannelAsync();

        var connection = _connection;
        _connection = null;
        if (connection is not null) await DisposeQuietlyAsync(connection);
    }

    /// <summary>A connect failed moments ago; this receipt is dropped without another attempt.</summary>
    private sealed class BrokerReconnectPendingException : Exception;
}
