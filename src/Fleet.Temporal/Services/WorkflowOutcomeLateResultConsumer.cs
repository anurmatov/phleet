using System.Globalization;
using System.Text;
using System.Text.Json;
using Fleet.Shared;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Fleet.Temporal.Services;

// #476 late-result delayed handoff. An agent reply whose waiter is already gone is handed by
// TemporalRelayListener to a durable private queue (publisher confirm before the original ACK);
// WorkflowOutcomeLateResultConsumer stores it through the orchestrator's late-results API, with
// delayed replacements 5/30/60 s on failure and then a confirmed move to the quarantine queue.
// Publisher confirms give at-least-once, never exactly-once: a stable MessageId plus the API's
// result-hash dedup absorbs the duplicates an unknown confirm can cause.

/// <summary>Outcome of one confirmed publish.</summary>
public enum WorkflowOutcomePublishStatus
{
    /// <summary>The broker confirmed the message (persistent, routed to the durable queue).</summary>
    Confirmed,

    /// <summary>The mandatory publish was returned unroutable (for example the queue is missing).</summary>
    Returned,

    /// <summary>The broker refused the message, including <c>x-overflow=reject-publish</c> at the quota.</summary>
    Nacked,

    /// <summary>No confirm arrived (timeout, channel or connection loss): the message may or may not exist.</summary>
    Unknown,

    /// <summary>Capture could not activate (no broker, no publisher confirms, queue declaration refused).</summary>
    Unavailable,
}

public sealed record WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus Status, string Reason)
{
    public bool IsConfirmed => Status == WorkflowOutcomePublishStatus.Confirmed;

    public static readonly WorkflowOutcomePublishResult Confirmed = new(WorkflowOutcomePublishStatus.Confirmed, "confirmed");
}

/// <summary>
/// The broker operations late-result capture needs. <see cref="RabbitMqWorkflowOutcomeBroker"/> is
/// the real one; unit tests drive the listener and consumer logic through a fake.
/// </summary>
public interface IWorkflowOutcomeBroker
{
    /// <summary>
    /// Declares the late and quarantine queues and checks publisher-confirm support. Null when
    /// capture is usable, otherwise the reason it is blocked. Never throws for broker problems.
    /// </summary>
    Task<string?> ActivateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One persistent, mandatory publish of <paramref name="body"/> to <paramref name="queue"/>
    /// through the default exchange, awaiting the publisher confirm. Never throws for broker problems.
    /// </summary>
    Task<WorkflowOutcomePublishResult> PublishAsync(
        string queue,
        ReadOnlyMemory<byte> body,
        string messageId,
        IReadOnlyDictionary<string, object?> headers,
        CancellationToken cancellationToken);
}

/// <summary>
/// How one consumed message is settled. A message is either acknowledged or left with the broker:
/// <see cref="Pause"/> never acknowledges, it cancels the consumer so the message returns to its
/// queue and consumption resumes after the backoff.
/// </summary>
public interface IWorkflowOutcomeDelivery
{
    Task AckAsync();

    /// <summary>The legacy orphan path only (capture off): reject without requeue.</summary>
    Task RejectWithoutRequeueAsync();

    void Pause(TimeSpan backoff, string reason);
}

/// <summary>Reconnect backoff 5, 10, 20, 40, then 60 s; reset after a successful handoff.</summary>
public sealed class WorkflowOutcomeBackoff
{
    public static readonly TimeSpan Min = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    private int _failures;

    public TimeSpan Next()
    {
        var failures = Interlocked.Increment(ref _failures) - 1;
        var seconds = Min.TotalSeconds * Math.Pow(2, Math.Min(failures, 4));
        return TimeSpan.FromSeconds(Math.Min(seconds, Max.TotalSeconds));
    }

    public void Reset() => Interlocked.Exchange(ref _failures, 0);
}

/// <summary>Header names, attempt delays and queue arguments shared by the listener and the consumer.</summary>
public static class WorkflowOutcomeLateEnvelope
{
    public const string AttemptHeader = "x-fleet-late-attempt";
    public const string DueAtHeader = "x-fleet-late-due-at";              // UTC unix milliseconds
    public const string ReceivedAtHeader = "x-fleet-late-received-at";    // UTC unix milliseconds
    public const string TaskIdHeader = "x-fleet-late-task-id";
    public const string SourceQueueHeader = "x-fleet-late-source-queue";
    public const string QuarantineReasonHeader = "x-fleet-late-quarantine-reason";

    public const int MaxAttempt = 3;

    /// <summary>The longest a consumer ever holds a message before its due time.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <summary>256 MiB per queue; at the quota the broker refuses new publishes instead of dropping old ones.</summary>
    public const long MaxQueueBytes = 268435456L;

    /// <summary>Delay before a replacement attempt: 1→5 s, 2→30 s, 3→60 s.</summary>
    public static TimeSpan DelayBefore(int attempt) => attempt switch
    {
        1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromSeconds(60),
        _ => TimeSpan.Zero,
    };

    /// <summary>
    /// Durable, non-exclusive, not auto-delete, bounded by bytes with reject-publish. No TTL and no
    /// dead-letter exchange: nothing here ever deletes a message on its own.
    /// </summary>
    public static Dictionary<string, object?> QueueArguments() => new()
    {
        ["x-max-length-bytes"] = MaxQueueBytes,
        ["x-overflow"] = "reject-publish",
    };

    public static async Task DeclareQueuesAsync(IChannel channel, TemporalBridgeOptions options, CancellationToken ct)
    {
        foreach (var queue in new[] { options.WorkflowOutcomeLateQueue, options.WorkflowOutcomeQuarantineQueue })
        {
            await channel.QueueDeclareAsync(
                queue, durable: true, exclusive: false, autoDelete: false,
                arguments: QueueArguments(), cancellationToken: ct);
        }
    }

    /// <summary>The original <c>MessageId</c> when it is usable, otherwise the SHA-256 of the exact body bytes.</summary>
    public static string StableMessageId(string? original, ReadOnlySpan<byte> body) =>
        !string.IsNullOrWhiteSpace(original) && original.Length <= 256 && !original.Any(char.IsControl)
            ? original
            : WorkflowOutcomeHashes.Sha256Hex(body);

    public static Dictionary<string, object?> LateHeaders(
        int attempt, DateTimeOffset dueAt, DateTimeOffset receivedAt, string taskId) => new()
    {
        [AttemptHeader] = attempt,
        [DueAtHeader] = dueAt.ToUnixTimeMilliseconds(),
        [ReceivedAtHeader] = receivedAt.ToUnixTimeMilliseconds(),
        [TaskIdHeader] = taskId,
        [SourceQueueHeader] = TemporalBridgeOptions.RelayQueue,
    };

    internal static bool TryReadLong(IDictionary<string, object?>? headers, string name, out long value)
    {
        value = 0;
        if (headers is null || !headers.TryGetValue(name, out var raw)) return false;
        switch (raw)
        {
            case int i: value = i; return true;
            case long l: value = l; return true;
            case short s: value = s; return true;
            case byte[] bytes:
                return long.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.None, CultureInfo.InvariantCulture, out value);
            case string text:
                return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    internal static string? ReadString(IDictionary<string, object?>? headers, string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var raw)) return null;
        return raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => null,
        };
    }
}

/// <summary>
/// The real broker side: its own connection and one channel with publisher confirms and confirm
/// tracking (RabbitMQ.Client 7 awaits the confirm inside <c>BasicPublishAsync</c> and throws
/// <see cref="PublishException"/> on a nack or a mandatory return). Any failure drops the channel,
/// so the next publish re-activates: confirm support is re-checked and the queues re-declared.
/// </summary>
public sealed class RabbitMqWorkflowOutcomeBroker : IWorkflowOutcomeBroker, IAsyncDisposable
{
    /// <summary>A confirm that has not arrived by then is unknown: the original stays unacknowledged.</summary>
    internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);

    private readonly ConnectionFactory _factory;
    private readonly TemporalBridgeOptions _options;
    private readonly ILogger<RabbitMqWorkflowOutcomeBroker> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private string? _lastActivationError;

    public RabbitMqWorkflowOutcomeBroker(
        IOptions<RabbitMqOptions> rabbit,
        IOptions<TemporalBridgeOptions> options,
        ILogger<RabbitMqWorkflowOutcomeBroker> logger)
        : this(
            new ConnectionFactory
            {
                HostName = rabbit.Value.Host,
                ClientProvidedName = "fleet-temporal-bridge-outcomes",
                AutomaticRecoveryEnabled = false,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
            },
            options.Value,
            logger)
    {
    }

    internal RabbitMqWorkflowOutcomeBroker(
        ConnectionFactory factory, TemporalBridgeOptions options, ILogger<RabbitMqWorkflowOutcomeBroker> logger)
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public async Task<string?> ActivateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureChannelAsync(cancellationToken) is null ? _lastActivationError : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkflowOutcomePublishResult> PublishAsync(
        string queue,
        ReadOnlyMemory<byte> body,
        string messageId,
        IReadOnlyDictionary<string, object?> headers,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await EnsureChannelAsync(cancellationToken);
            if (channel is null)
                return new(WorkflowOutcomePublishStatus.Unavailable, _lastActivationError ?? "capture_unavailable");

            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = messageId,
                Headers = new Dictionary<string, object?>(headers),
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConfirmTimeout);
            try
            {
                await channel.BasicPublishAsync(
                    exchange: string.Empty, routingKey: queue, mandatory: true,
                    basicProperties: properties, body: body, cancellationToken: timeout.Token);
                return WorkflowOutcomePublishResult.Confirmed;
            }
            catch (PublishException ex)
            {
                await ResetAsync();
                return ex.IsReturn
                    ? new(WorkflowOutcomePublishStatus.Returned, "returned_unroutable")
                    : new(WorkflowOutcomePublishStatus.Nacked, "nacked");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await ResetAsync();
                return new(WorkflowOutcomePublishStatus.Unknown, "confirm_timeout");
            }
            catch (OperationCanceledException)
            {
                await ResetAsync();
                return new(WorkflowOutcomePublishStatus.Unknown, "cancelled");
            }
            catch (Exception ex)
            {
                await ResetAsync();
                return new(WorkflowOutcomePublishStatus.Unknown, ex.GetType().Name);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Null when activation failed; <see cref="_lastActivationError"/> then says why.</summary>
    private async Task<IChannel?> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true } && _connection is { IsOpen: true })
            return _channel;

        await ResetAsync();
        try
        {
            _connection = await _factory.CreateConnectionAsync(ct);
            if (!SupportsPublisherConfirms(_connection.ServerProperties))
                return await BlockAsync("broker_lacks_publisher_confirms");

            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                ct);
            await WorkflowOutcomeLateEnvelope.DeclareQueuesAsync(_channel, _options, ct);
            _lastActivationError = null;
            return _channel;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ResetAsync();
            throw;
        }
        catch (OperationInterruptedException ex)
        {
            // A queue declared with other arguments, or a broker refusing them, closes the channel.
            return await BlockAsync($"queue_declare_refused_{ex.ShutdownReason?.ReplyCode.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
        }
        catch (Exception ex)
        {
            return await BlockAsync($"broker_unavailable_{ex.GetType().Name}");
        }
    }

    private async Task<IChannel?> BlockAsync(string reason)
    {
        _lastActivationError = reason;
        _logger.LogError(
            "Workflow outcome late-result capture activation blocked: {Reason}. Late replies are left unacknowledged with the broker, never discarded",
            reason);
        await ResetAsync();
        return null;
    }

    internal static bool SupportsPublisherConfirms(IDictionary<string, object?>? serverProperties) =>
        serverProperties is not null &&
        serverProperties.TryGetValue("capabilities", out var raw) &&
        raw is IDictionary<string, object?> capabilities &&
        capabilities.TryGetValue("publisher_confirms", out var confirms) &&
        confirms is true;

    private async Task ResetAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        try { if (channel is not null) await channel.DisposeAsync(); } catch { /* already closing */ }
        try { if (connection is not null) await connection.DisposeAsync(); } catch { /* already closing */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await ResetAsync(); }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// One deduplicated health alarm per quarantine reason: an Error the first time and then at most
/// every 15 minutes with the running count. Quarantine has no consumer, so this log line (and the
/// queue depth) is how an operator learns about it. Ids and counts only, never bodies.
/// </summary>
public sealed class WorkflowOutcomeLateResultAlarm(ILogger logger, TimeProvider time)
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, (int Count, DateTimeOffset LastAlarm)> _byReason = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyDictionary<string, int> Counts
    {
        get { lock (_gate) return _byReason.ToDictionary(kv => kv.Key, kv => kv.Value.Count); }
    }

    public void Raise(string reason, string messageId)
    {
        var now = time.GetUtcNow();
        bool alarm;
        int count;
        lock (_gate)
        {
            _byReason.TryGetValue(reason, out var entry);
            count = entry.Count + 1;
            alarm = entry.Count == 0 || now - entry.LastAlarm >= Interval;
            _byReason[reason] = (count, alarm ? now : entry.LastAlarm);
        }

        if (alarm)
        {
            logger.LogError(
                "workflow_outcome_late_result_quarantined reason={Reason} count={Count} messageId={MessageId}: a late agent reply could not be stored and is held in quarantine for operator review",
                reason, count, messageId);
        }
        else
        {
            logger.LogDebug(
                "workflow_outcome_late_result_quarantined reason={Reason} count={Count} messageId={MessageId}",
                reason, count, messageId);
        }
    }
}

/// <summary>
/// Consumes the late queue one message at a time (prefetch 1) and stores each reply through
/// <c>POST api/workflow-outcomes/late-results</c> on the admin-bearer orchestrator client. The
/// source relay queue is never touched here; a delayed message holds later ones for at most 60 s
/// (the accepted head-of-line cost). It never requeues hot and never starts a Temporal workflow.
/// </summary>
public sealed class WorkflowOutcomeLateResultConsumer : BackgroundService
{
    public const string HttpClientName = "orchestrator-workflow-outcomes";
    internal const string LateResultsPath = "api/workflow-outcomes/late-results";
    internal const string ReadinessPath = "api/workflow-outcomes/health/public";
    internal static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporalBridgeOptions _options;
    private readonly IWorkflowOutcomeBroker _broker;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WorkflowOutcomeLateResultConsumer> _logger;
    private readonly TimeProvider _time;
    private readonly WorkflowOutcomeBackoff _backoff = new();
    private readonly ConnectionFactory _connectionFactory;

    public WorkflowOutcomeLateResultConsumer(
        IOptions<RabbitMqOptions> rabbit,
        IOptions<TemporalBridgeOptions> options,
        IWorkflowOutcomeBroker broker,
        IHttpClientFactory httpClientFactory,
        ILogger<WorkflowOutcomeLateResultConsumer> logger,
        TimeProvider? timeProvider = null)
        : this(rabbit.Value, options.Value, broker, httpClientFactory, logger, timeProvider ?? TimeProvider.System, null)
    {
    }

    internal WorkflowOutcomeLateResultConsumer(
        RabbitMqOptions rabbit,
        TemporalBridgeOptions options,
        IWorkflowOutcomeBroker broker,
        IHttpClientFactory httpClientFactory,
        ILogger<WorkflowOutcomeLateResultConsumer> logger,
        TimeProvider timeProvider,
        ConnectionFactory? connectionFactory)
    {
        _options = options;
        _broker = broker;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _time = timeProvider;
        Alarm = new WorkflowOutcomeLateResultAlarm(logger, timeProvider);
        _connectionFactory = connectionFactory ?? new ConnectionFactory
        {
            HostName = rabbit.Host,
            ClientProvidedName = "fleet-temporal-bridge-late-results",
            AutomaticRecoveryEnabled = false,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
    }

    public WorkflowOutcomeLateResultAlarm Alarm { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkflowOutcomeLateCaptureEnabled) return;

        if (string.IsNullOrEmpty(_connectionFactory.HostName))
        {
            _logger.LogWarning("RabbitMq:Host not configured — workflow outcome late-result consumer disabled");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.OrchestratorUrl) || string.IsNullOrWhiteSpace(_options.OrchestratorAuthToken))
        {
            // Not staged yet: captured replies stay durable in the late queue until the store is reachable.
            _logger.LogError(
                "Workflow outcome late-result consumer not started: TemporalBridge:OrchestratorUrl and OrchestratorAuthToken are required. Captured late replies remain in {Queue}",
                _options.WorkflowOutcomeLateQueue);
            return;
        }

        try
        {
            await WaitForStoreReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan backoff;
            try
            {
                backoff = await ConsumeUntilPausedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                backoff = _backoff.Next();
                _logger.LogWarning(
                    "Workflow outcome late-result consumer could not consume {Queue} ({Error}); retrying in {Backoff}s",
                    _options.WorkflowOutcomeLateQueue, ex.GetType().Name, backoff.TotalSeconds);
            }

            try
            {
                await Task.Delay(backoff, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Staged activation: consumption starts only once the orchestrator's workflow-outcome API
    /// answers (any 2xx from the anonymous public health route). Until then captured replies wait,
    /// durable, in the late queue — they are not burned through the 5/30/60 s retries into quarantine.
    /// </summary>
    internal async Task WaitForStoreReadyAsync(CancellationToken ct)
    {
        var backoff = new WorkflowOutcomeBackoff();
        var warned = false;
        while (true)
        {
            string detail;
            var client = _httpClientFactory.CreateClient(HttpClientName);
            try
            {
                using var response = await client.GetAsync(ReadinessPath, ct);
                if (response.IsSuccessStatusCode)
                {
                    if (warned)
                        _logger.LogInformation("Workflow outcome late-results API is ready; consuming {Queue}", _options.WorkflowOutcomeLateQueue);
                    return;
                }

                detail = $"http_{(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name;
            }

            var wait = backoff.Next();
            if (!warned)
            {
                _logger.LogWarning(
                    "Workflow outcome late-results API not ready ({Detail}); captured late replies stay in {Queue}. Retrying every {Backoff}s up to 60s",
                    detail, _options.WorkflowOutcomeLateQueue, wait.TotalSeconds);
                warned = true;
            }

            await Task.Delay(wait, _time, ct);
        }
    }

    /// <summary>
    /// One consumer session. Returns the backoff once it pauses (or the connection drops); closing
    /// the channel hands any unacknowledged message back to the queue.
    /// </summary>
    private async Task<TimeSpan> ConsumeUntilPausedAsync(CancellationToken stoppingToken)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await WorkflowOutcomeLateEnvelope.DeclareQueuesAsync(channel, _options, stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var paused = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Lost()
        {
            // A dropped connection or channel is a pause too; our own close after a pause is not.
            if (!paused.Task.IsCompleted) paused.TrySetResult(_backoff.Next());
            return Task.CompletedTask;
        }
        connection.ConnectionShutdownAsync += (_, _) => Lost();
        channel.ChannelShutdownAsync += (_, _) => Lost();

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var delivery = new RabbitMqOutcomeDelivery(channel, ea.DeliveryTag, (backoff, _) => paused.TrySetResult(backoff));
            try
            {
                await ProcessAsync(ea.Body, ea.BasicProperties, delivery, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown: leave the message unacknowledged; the channel close requeues it.
            }
            catch (Exception ex)
            {
                delivery.Pause(_backoff.Next(), ex.GetType().Name);
            }
        };

        var consumerTag = await channel.BasicConsumeAsync(
            _options.WorkflowOutcomeLateQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        _logger.LogInformation(
            "Workflow outcome late-result consumer started on {Queue} (prefetch 1)", _options.WorkflowOutcomeLateQueue);

        TimeSpan backoff;
        try
        {
            backoff = await paused.Task.WaitAsync(stoppingToken);
        }
        finally
        {
            try { if (channel.IsOpen) await channel.BasicCancelAsync(consumerTag); } catch { /* closing anyway */ }
            try { if (channel.IsOpen) await channel.CloseAsync(); } catch { /* closing anyway */ }
        }

        _logger.LogWarning(
            "Workflow outcome late-result consumer paused; reconnecting in {Backoff}s", backoff.TotalSeconds);
        return backoff;
    }

    /// <summary>
    /// Settles one late-queue message. Acknowledges only after the API stored it (2xx) or after a
    /// confirmed replacement/quarantine publish; any publish failure pauses without acknowledging.
    /// </summary>
    internal async Task ProcessAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyBasicProperties? properties,
        IWorkflowOutcomeDelivery delivery,
        CancellationToken ct)
    {
        var messageId = WorkflowOutcomeLateEnvelope.StableMessageId(properties?.MessageId, body.Span);
        var headers = properties?.Headers;
        var taskId = WorkflowOutcomeLateEnvelope.ReadString(headers, WorkflowOutcomeLateEnvelope.TaskIdHeader);

        if (body.Length > WorkflowOutcomeLimits.MaxLateResultBytes)
        {
            await QuarantineAsync(body, messageId, taskId, headers, "oversize", delivery, ct);
            return;
        }

        if (!WorkflowOutcomeLateEnvelope.TryReadLong(headers, WorkflowOutcomeLateEnvelope.AttemptHeader, out var attempt) ||
            attempt is < 0 or > WorkflowOutcomeLateEnvelope.MaxAttempt)
        {
            await QuarantineAsync(body, messageId, taskId, headers, "invalid_attempt", delivery, ct);
            return;
        }

        if (!WorkflowOutcomeLateEnvelope.TryReadLong(headers, WorkflowOutcomeLateEnvelope.DueAtHeader, out var dueAtMs) ||
            dueAtMs <= 0 || dueAtMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            await QuarantineAsync(body, messageId, taskId, headers, "invalid_due_at", delivery, ct);
            return;
        }

        if (!TryParseReply(body.Span, out var reply))
        {
            await QuarantineAsync(body, messageId, taskId, headers, "unparseable", delivery, ct);
            return;
        }

        // The source correlation travels beside the bytes; a reply that disagrees with it is not
        // attributable to the task it was captured for.
        if (taskId is null || !string.Equals(taskId, reply.TaskId, StringComparison.Ordinal))
        {
            await QuarantineAsync(body, messageId, taskId, headers, "correlation_mismatch", delivery, ct);
            return;
        }

        var receivedAt = WorkflowOutcomeLateEnvelope.TryReadLong(headers, WorkflowOutcomeLateEnvelope.ReceivedAtHeader, out var receivedMs) &&
                         receivedMs > 0 && receivedMs <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(receivedMs)
            : _time.GetUtcNow();

        // Wait in place until due (never more than 60 s): no hot requeue, no new workflow.
        var wait = DateTimeOffset.FromUnixTimeMilliseconds(dueAtMs) - _time.GetUtcNow();
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait > WorkflowOutcomeLateEnvelope.MaxWait ? WorkflowOutcomeLateEnvelope.MaxWait : wait, _time, ct);

        var parsed = TemporalRelayListener.ParseAgentResult(reply.Text, reply.Type);
        var request = new WorkflowOutcomeLateResultRequest(
            Namespace: null,                 // a TaskId does not name its namespace; the API resolves it
            TaskId: reply.TaskId!,
            MessageId: messageId,
            ResultSha256: WorkflowOutcomeHashes.Sha256Hex(parsed.Text),
            Status: parsed.Status,
            Text: parsed.Text,
            Attempt: (int)attempt,
            ReceivedAt: receivedAt);

        var (stored, code, error) = await StoreAsync(request, ct);
        if (stored)
        {
            await delivery.AckAsync();
            _backoff.Reset();
            _logger.LogInformation(
                "Late reply stored for TaskId={TaskId} (messageId={MessageId}, attempt={Attempt}, http={Status})",
                reply.TaskId, messageId, attempt, code);
            return;
        }

        // A definitive refusal will not change on retry.
        if (code is 400 or 413 or 422)
        {
            await QuarantineAsync(body, messageId, taskId, headers, $"store_rejected_{code}", delivery, ct);
            return;
        }

        _logger.LogWarning(
            "Late reply store failed for TaskId={TaskId} (messageId={MessageId}, attempt={Attempt}, error={Error})",
            reply.TaskId, messageId, attempt, error);

        if (attempt >= WorkflowOutcomeLateEnvelope.MaxAttempt)
        {
            await QuarantineAsync(body, messageId, taskId, headers, "store_failed_after_retries", delivery, ct);
            return;
        }

        var next = (int)attempt + 1;
        var replacementHeaders = WorkflowOutcomeLateEnvelope.LateHeaders(
            next, _time.GetUtcNow() + WorkflowOutcomeLateEnvelope.DelayBefore(next), receivedAt, taskId);
        var published = await _broker.PublishAsync(_options.WorkflowOutcomeLateQueue, body, messageId, replacementHeaders, ct);
        if (published.IsConfirmed)
        {
            // Only now is the previous copy redundant. A failed ACK leaves two copies; the stable
            // MessageId and the API's result-hash dedup make the second one a no-op.
            await delivery.AckAsync();
            _backoff.Reset();
            return;
        }

        PauseFor(delivery, $"replacement_{published.Status}_{published.Reason}", messageId);
    }

    private async Task QuarantineAsync(
        ReadOnlyMemory<byte> body,
        string messageId,
        string? taskId,
        IDictionary<string, object?>? original,
        string reason,
        IWorkflowOutcomeDelivery delivery,
        CancellationToken ct)
    {
        var headers = new Dictionary<string, object?>
        {
            [WorkflowOutcomeLateEnvelope.QuarantineReasonHeader] = reason,
            [WorkflowOutcomeLateEnvelope.SourceQueueHeader] = _options.WorkflowOutcomeLateQueue,
        };
        if (taskId is not null) headers[WorkflowOutcomeLateEnvelope.TaskIdHeader] = taskId;
        if (WorkflowOutcomeLateEnvelope.TryReadLong(original, WorkflowOutcomeLateEnvelope.AttemptHeader, out var attempt))
            headers[WorkflowOutcomeLateEnvelope.AttemptHeader] = attempt;
        if (WorkflowOutcomeLateEnvelope.TryReadLong(original, WorkflowOutcomeLateEnvelope.ReceivedAtHeader, out var received))
            headers[WorkflowOutcomeLateEnvelope.ReceivedAtHeader] = received;

        var published = await _broker.PublishAsync(_options.WorkflowOutcomeQuarantineQueue, body, messageId, headers, ct);
        if (!published.IsConfirmed)
        {
            PauseFor(delivery, $"quarantine_{published.Status}_{published.Reason}", messageId);
            return;
        }

        await delivery.AckAsync();
        _backoff.Reset();
        Alarm.Raise(reason, messageId);
    }

    private void PauseFor(IWorkflowOutcomeDelivery delivery, string reason, string messageId)
    {
        var backoff = _backoff.Next();
        _logger.LogWarning(
            "Late reply messageId={MessageId} not handed off ({Reason}) — left unacknowledged; consumer paused for {Backoff}s",
            messageId, reason, backoff.TotalSeconds);
        delivery.Pause(backoff, reason);
    }

    private async Task<(bool Stored, int? Code, string? Error)> StoreAsync(
        WorkflowOutcomeLateResultRequest request, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
            return (false, null, "orchestrator_url_missing");

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(request, WebOptions), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(LateResultsPath, content, ct);
            var code = (int)response.StatusCode;
            return (response.IsSuccessStatusCode, code, response.IsSuccessStatusCode ? null : $"http_{code}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, "timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type: transport messages can carry internal addresses.
            return (false, null, ex.GetType().Name);
        }
    }

    private static bool TryParseReply(ReadOnlySpan<byte> body, out RelayMessage reply)
    {
        reply = null!;
        try
        {
            var parsed = JsonSerializer.Deserialize<RelayMessage>(body);
            if (parsed is null || string.IsNullOrEmpty(parsed.TaskId) || parsed.Text is null) return false;
            if (parsed.Type is not (RelayMessageType.Response or RelayMessageType.PartialResponse)) return false;
            reply = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Settles one delivery on the channel it arrived on.</summary>
internal sealed class RabbitMqOutcomeDelivery(IChannel channel, ulong deliveryTag, Action<TimeSpan, string> pause)
    : IWorkflowOutcomeDelivery
{
    public async Task AckAsync() => await channel.BasicAckAsync(deliveryTag, multiple: false);

    public async Task RejectWithoutRequeueAsync() =>
        await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false);

    public void Pause(TimeSpan backoff, string reason) => pause(backoff, reason);
}
