using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Fleet.Orchestrator.Configuration;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// The usage integration's state, decided once at start-up from <see cref="UsageOptions"/>. The relay,
/// the read endpoint and provisioning all ask it, so they can never disagree.
/// </summary>
public sealed class UsageReadiness
{
    public const string HttpClientName = "usage-receiver";

    public UsageReadiness(IConfiguration configuration)
    {
        var options = UsageOptions.From(configuration);
        Enabled = options.Enabled;
        Fault = options.Enabled ? options.DescribeFault() : null;
        Origin = options.ReceiverOrigin;
        Token = options.AuthToken;
    }

    public bool Enabled { get; }

    /// <summary>Why an enabled integration cannot run, or null.</summary>
    public string? Fault { get; }

    public bool IsReady => Enabled && Fault is null;

    internal Uri? Origin { get; }
    internal string Token { get; }

    /// <summary>The stable error code an unusable integration answers with.</summary>
    public string? Refusal => !Enabled ? "usage_disabled" : Fault is not null ? "usage_not_configured" : null;
}

/// <summary>A row accepted from the broker, waiting for the worker, and when it arrived.</summary>
internal readonly record struct RelayedUsageRow(byte[] Row, DateTimeOffset ReceivedAt);

/// <summary>
/// Forwards each agent's frozen usage row to the receiver's <c>POST /api/usage</c>, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryEnqueue"/> runs on the broker callback: it checks the row's shape and size, queues
/// it and returns at once. It never touches the agent registry. The row bytes are forwarded exactly
/// as the agent froze them, inside <c>{"rows":[row]}</c>.
/// </para>
/// <para>
/// Best effort: at most <see cref="QueueCapacity"/> rows wait, a row older than
/// <see cref="MaxAge"/> is discarded, and a row the receiver does not accept within
/// <see cref="RequestDeadline"/> is dropped. No spool, no retry. Logs carry a reason or an HTTP
/// status only, never a row or the bearer.
/// </para>
/// </remarks>
public sealed class UsageRelay(
    UsageReadiness readiness,
    IHttpClientFactory httpClientFactory,
    ILogger<UsageRelay> logger) : BackgroundService
{
    public const int QueueCapacity = 512;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(5);
    private const int MaxReceiverReplyBytes = 4 * 1024;

    private static readonly string[] RequiredStrings = ["turn_id", "agent", "provider", "model", "turn_started_at", "turn_ended_at"];
    private static readonly byte[] BatchPrefix = "{\"rows\":["u8.ToArray();
    private static readonly byte[] BatchSuffix = "]}"u8.ToArray();

    private readonly Channel<RelayedUsageRow> _queue = Channel.CreateBounded<RelayedUsageRow>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private long _dropped;
    private long _forwarded;

    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public long Dropped => Interlocked.Read(ref _dropped);
    public long Forwarded => Interlocked.Read(ref _forwarded);

    /// <summary>
    /// Queues one row from <paramref name="routingKey"/> (<c>usage.{agent}</c>). False when usage is
    /// off, the row is malformed, too large or names another agent, or the queue is full.
    /// </summary>
    public bool TryEnqueue(string routingKey, ReadOnlyMemory<byte> body)
    {
        if (!readiness.IsReady)
            return false;

        if (Validate(routingKey, body.Span) is { } reason)
        {
            Drop(reason);
            return false;
        }

        if (_queue.Writer.TryWrite(new RelayedUsageRow(body.ToArray(), Clock())))
            return true;

        Drop("queue_full");
        return false;
    }

    /// <summary>Why a row is refused, or null. Shape and identity only: the receiver validates values.</summary>
    internal static string? Validate(string routingKey, ReadOnlySpan<byte> body)
    {
        if (body.Length == 0 || body.Length > Fleet.Contracts.AgentTurnUsage.MaxBytes)
            return "row_size";

        var prefix = Fleet.Contracts.OrchestratorBroker.Usage + ".";
        if (!routingKey.StartsWith(prefix, StringComparison.Ordinal) || routingKey.Length == prefix.Length)
            return "routing_key";
        var keyAgent = routingKey[prefix.Length..];

        try
        {
            var reader = new Utf8JsonReader(body);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "row_shape";
            foreach (var name in RequiredStrings)
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(value.GetString()))
                    return "row_shape";
            }
            if (!string.Equals(root.GetProperty("agent").GetString(), keyAgent, StringComparison.Ordinal))
                return "agent_mismatch";
        }
        catch (JsonException)
        {
            return "row_json";
        }

        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!readiness.Enabled)
        {
            logger.LogInformation("Usage feed disabled");
            return;
        }
        if (readiness.Fault is { } fault)
        {
            logger.LogError("usage_misconfigured: {Fault}. The usage feed is off.", fault);
            return;
        }

        try
        {
            await foreach (var row in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (Clock() - row.ReceivedAt > MaxAge)
                {
                    Drop("expired");
                    continue;
                }
                await ForwardAsync(row.Row, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One POST of one frozen row. Never throws: every failure is a counted drop.</summary>
    internal async Task ForwardAsync(byte[] row, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(RequestDeadline);
        try
        {
            var payload = new byte[BatchPrefix.Length + row.Length + BatchSuffix.Length];
            BatchPrefix.CopyTo(payload, 0);
            row.CopyTo(payload, BatchPrefix.Length);
            BatchSuffix.CopyTo(payload, BatchPrefix.Length + row.Length);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(readiness.Origin!, "/api/usage"))
            {
                Content = new ByteArrayContent(payload),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readiness.Token);

            var client = httpClientFactory.CreateClient(UsageReadiness.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Drop($"receiver_http_{(int)response.StatusCode}");
                return;
            }

            var reply = await BoundedBody.ReadAsync(response.Content, MaxReceiverReplyBytes, deadline.Token);
            using var doc = reply is null ? null : JsonDocument.Parse(reply);
            if (doc is null
                || doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("accepted", out var accepted)
                || !accepted.TryGetInt32(out var count)
                || count != 1)
            {
                Drop("receiver_reply");
                return;
            }

            Interlocked.Increment(ref _forwarded);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            Drop(ex switch
            {
                OperationCanceledException => "receiver_timeout",
                JsonException => "receiver_reply",
                _ => ex.GetType().Name,
            });
        }
    }

    private void Drop(string reason)
    {
        var dropped = Interlocked.Increment(ref _dropped);
        logger.LogWarning("usage_row_dropped: {Reason} (dropped so far: {Dropped})", reason, dropped);
    }
}

/// <summary>
/// <c>GET /api/usage</c>: validates the dashboard's query, then asks the receiver's bounded read and
/// passes its JSON through unchanged, so every <c>reported_value</c> string and null arrives exactly
/// as the receiver wrote it.
/// </summary>
/// <remarks>
/// Only the configured receiver origin is ever called, with the bearer only this process holds.
/// Every failure is a <c>503</c> with a stable code, never an empty <c>200</c>.
/// </remarks>
public sealed class UsageReader(
    UsageReadiness readiness,
    IHttpClientFactory httpClientFactory,
    ILogger<UsageReader> logger)
{
    internal static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(5);
    internal const int MaxResponseBytes = 2 * 1024 * 1024;
    internal const int MaxRangeHours = 168;

    internal static readonly string[] Metrics =
        ["input_tokens", "output_tokens", "cache_read_tokens", "cache_creation_tokens", "cost_usd"];

    private static readonly Dictionary<string, int> FilterLengths = new(StringComparer.Ordinal)
    {
        ["agent"] = 64, ["provider"] = 32, ["model"] = 128,
    };

    public async Task<IResult> ReadAsync(IQueryCollection query, CancellationToken ct)
    {
        if (readiness.Refusal is { } refusal)
            return Results.Json(new { error = refusal }, statusCode: StatusCodes.Status503ServiceUnavailable);

        if (BuildUpstreamQuery(query, out var upstreamQuery) is { } invalid)
            return Results.Json(new { error = invalid.Error, field = invalid.Field }, statusCode: StatusCodes.Status400BadRequest);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestDeadline);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(readiness.Origin!, "/api/usage" + upstreamQuery));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readiness.Token);

            var client = httpClientFactory.CreateClient(UsageReadiness.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                return Unavailable($"receiver_http_{(int)response.StatusCode}");

            var body = await BoundedBody.ReadAsync(response.Content, MaxResponseBytes, deadline.Token);
            if (body is null)
                return Unavailable("receiver_response_too_large");

            using (var doc = JsonDocument.Parse(body))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("groups", out var groups)
                    || groups.ValueKind != JsonValueKind.Array)
                    return Unavailable("receiver_response_shape");
            }

            return Results.Bytes(body, "application/json");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return Unavailable(ex switch
            {
                OperationCanceledException => "receiver_timeout",
                JsonException => "receiver_response_shape",
                _ => ex.GetType().Name,
            });
        }
    }

    /// <summary>
    /// The receiver's query, rebuilt from validated values only: unknown or repeated parameters,
    /// times without an explicit offset, ranges over a week and bad metrics or filters are refused
    /// before anything is sent. Bounds go out as UTC with milliseconds.
    /// </summary>
    internal static (string Error, string Field)? BuildUpstreamQuery(IQueryCollection query, out string upstream)
    {
        upstream = "";
        string[] known = ["from", "to", "metric", .. FilterLengths.Keys];
        foreach (var (key, values) in query)
        {
            if (!known.Contains(key, StringComparer.Ordinal)) return ("unknown parameter", key);
            if (values.Count != 1) return ($"{key} must appear exactly once", key);
        }

        if (!TryReadTime(query, "from", out var from)) return ("from must be ISO-8601 with an explicit offset", "from");
        if (!TryReadTime(query, "to", out var to)) return ("to must be ISO-8601 with an explicit offset", "to");
        if (from >= to) return ("from must be earlier than to", "from");
        if (to - from > TimeSpan.FromHours(MaxRangeHours)) return ($"range must be at most {MaxRangeHours} hours", "to");

        var metric = query["metric"].ToString();
        if (!Metrics.Contains(metric, StringComparer.Ordinal))
            return ($"metric must be one of {string.Join(", ", Metrics)}", "metric");

        var builder = new StringBuilder();
        builder.Append("?from=").Append(Uri.EscapeDataString(Format(from)))
               .Append("&to=").Append(Uri.EscapeDataString(Format(to)))
               .Append("&metric=").Append(metric);
        foreach (var (name, max) in FilterLengths)
        {
            if (!query.TryGetValue(name, out var value)) continue;
            var text = value.ToString();
            if (text.Length == 0 || text.Length > max) return ($"{name} must be 1 to {max} characters", name);
            builder.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(text));
        }

        upstream = builder.ToString();
        return null;
    }

    /// <summary>An explicit offset is required: an offset-less time would take this host's zone.</summary>
    private static bool TryReadTime(IQueryCollection query, string name, out DateTime utc)
    {
        utc = default;
        var text = query[name].ToString();
        if (text.Length == 0
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var probe)
            || probe.Kind == DateTimeKind.Unspecified)
            return false;
        // Millisecond-aligned: the receiver stores milliseconds, so a finer bound means nothing.
        var ticks = parsed.UtcTicks - parsed.UtcTicks % TimeSpan.TicksPerMillisecond;
        utc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static string Format(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private IResult Unavailable(string reason)
    {
        logger.LogWarning("usage_read_unavailable: {Reason}", reason);
        return Results.Json(new { error = "usage_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

/// <summary>Reads at most a fixed number of bytes of a response body.</summary>
internal static class BoundedBody
{
    /// <summary>The body, or null when it is longer than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
