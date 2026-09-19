using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fleet.Agent.Configuration;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Typed client for the seven south endpoints (#303).
/// </summary>
/// <remarks>
/// <para>
/// Bodies are serialized and read through <see cref="FleetProtocolJson"/>, because that is the wire
/// vocabulary the listener binds. The service learned that the hard way: a body carrying the
/// protocol's own <c>"disposition":"ran"</c> failed to bind under the framework's web defaults and
/// the caller got an empty-bodied 400, on exactly the two endpoints that make the surface work.
/// </para>
/// <para>
/// ⚠️ The bearer is attached per request and <b>never logged</b>. <see cref="SouthCallException"/>
/// carries a status code and a path, never a header, never a body that could contain one
/// (MUST NOT 19).
/// </para>
/// <para>
/// Retry policy lives here as <see cref="ExecuteWithRetryAsync{T}"/>, but <b>which</b> calls are
/// retried is the consumer's decision — the failure table in #303 assigns a different behaviour to
/// each endpoint, and a client that retried everything would retry a claim whose correct response is
/// a requeue.
/// </para>
/// </remarks>
public sealed class ConversationSouthClient
{
    private readonly HttpClient _http;
    private readonly ConversationsOptions _options;
    private readonly ILogger<ConversationSouthClient> _logger;

    public ConversationSouthClient(
        HttpClient http,
        IOptions<ConversationsOptions> options,
        ILogger<ConversationSouthClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;

        _http.BaseAddress = new Uri(_options.SouthBaseUrl, UriKind.Absolute);
        _http.Timeout = ConversationsOptions.RequestTimeout;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.SouthBearerToken);
    }

    public Task<ClaimDeliveryResult> ClaimAsync(ClaimDeliveryRequest request, CancellationToken ct) =>
        PostAsync<ClaimDeliveryRequest, ClaimDeliveryResult>("/deliveries:claim", request, ct);

    public Task<DispositionResult> RecordDispositionAsync(RecordDispositionRequest request, CancellationToken ct) =>
        PostAsync<RecordDispositionRequest, DispositionResult>("/submissions:disposition", request, ct);

    public Task<DispositionResult> CompleteDeliveryAsync(CompleteDeliveryRequest request, CancellationToken ct) =>
        PostAsync<CompleteDeliveryRequest, DispositionResult>("/deliveries:complete", request, ct);

    public Task<StartTurnResult> StartTurnAsync(StartTurnRequest request, CancellationToken ct) =>
        PostAsync<StartTurnRequest, StartTurnResult>("/turns:start", request, ct);

    public Task<CommitTerminalResult> CommitTerminalAsync(CommitTerminalRequest request, CancellationToken ct) =>
        PostAsync<CommitTerminalRequest, CommitTerminalResult>("/turns:commit", request, ct);

    public Task<AppendBatchResult> AppendAsync(AppendBatchRequest request, CancellationToken ct) =>
        PostAsync<AppendBatchRequest, AppendBatchResult>("/events:append", request, ct);

    public Task<HeartbeatResult> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) =>
        PostAsync<HeartbeatRequest, HeartbeatResult>("/leases:heartbeat", request, ct);

    /// <summary>
    /// Fetch one attachment's bytes over the south listener (#308 D7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ONLY byte-returning call on this client, and the only one that is not JSON. It goes south
    /// because the agent must never hold a device session bearer — the north download route exists
    /// for the client and requires one (MUST NOT 10).
    /// </para>
    /// <para>
    /// <b>Null means "not coming", and it is not an error.</b> A 404 (unknown, unsealed, or swept)
    /// and a 410 (the row survives, the bytes do not) both return null so the caller runs the turn
    /// on its text and names the missing image in a notice. Every other status throws and is
    /// retried by the ordinary policy.
    /// </para>
    /// <para>
    /// The response is bounded by the same cap the service enforces on upload, so a compromised or
    /// misbehaving origin cannot make this allocate without limit.
    /// </para>
    /// </remarks>
    public async Task<(byte[] Bytes, string ContentType)?> FetchAttachmentAsync(
        string attachmentId, CancellationToken ct)
    {
        var path = $"/attachments/{attachmentId}/content";

        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _http.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;

        if (!response.IsSuccessStatusCode) throw new SouthCallException(path, response.StatusCode);

        // Declared length checked before reading, then the read itself is bounded — a truthful
        // header is not something to depend on.
        if (response.Content.Headers.ContentLength is { } declared
            && declared > ProtocolLimits.MaxAttachmentBytes)
        {
            _logger.LogWarning("south attachment exceeded the accepted size; dropping it");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;

            if (buffer.Length + read > ProtocolLimits.MaxAttachmentBytes)
            {
                _logger.LogWarning("south attachment exceeded the accepted size mid-stream; dropping it");
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        // The type the SERVICE sniffed. Nothing here re-decides it.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

        return (buffer.ToArray(), contentType);
    }

    /// <summary>
    /// Bounded exponential backoff. Returns the first success; rethrows the last failure when the
    /// budget is exhausted or the token is cancelled.
    /// </summary>
    /// <remarks>
    /// A <see cref="SouthCallException"/> whose status is 4xx is NOT retried: the store refused the
    /// request, and sending it again unchanged asks the same question and gets the same answer while
    /// holding a claim.
    /// </remarks>
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> call, string label, int attempts, CancellationToken ct)
    {
        var delay = BackoffBase;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call(ct);
            }
            catch (SouthCallException e) when (e.IsPermanent)
            {
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt >= attempts) throw;

                // Type and status only. A broker or HTTP error message can carry a host, and a
                // south error body is a protocol error code, not free text we should widen.
                _logger.LogWarning(
                    "south call {Label} failed (attempt {Attempt}/{Attempts}): {Error}",
                    label, attempt, attempts, Describe(e));

                await Task.Delay(delay, ct);
                delay = Backoff(delay);
            }
        }
    }

    /// <summary>The next backoff step, doubled and clamped.</summary>
    public TimeSpan Backoff(TimeSpan current)
    {
        var doubled = current + current;
        var ceiling = BackoffCeiling;
        return doubled > ceiling ? ceiling : doubled;
    }

    /// <summary>The first backoff step. The consumer's own requeue backoff starts here too.</summary>
    public TimeSpan InitialBackoff => BackoffBase;

    /// <summary>
    /// Test-only compression of the retry schedule. Never set outside tests.
    /// </summary>
    /// <remarks>
    /// The schedule is a constant because no operator will ever tune it — but a test that exercises
    /// an exhausted retry would otherwise sit through the real one, and a suite slow enough to be
    /// skipped is a suite that stops catching things. This is a seam, not a knob: nothing reads
    /// configuration for it, and production always runs the constants.
    /// </remarks>
    internal TimeSpan? RetryScheduleOverride { get; set; }

    private TimeSpan BackoffBase => RetryScheduleOverride ?? ConversationsOptions.RetryBaseDelay;

    private TimeSpan BackoffCeiling =>
        RetryScheduleOverride is { } compressed
            ? compressed + compressed + compressed + compressed
            : ConversationsOptions.RetryMaxDelay;

    private static string Describe(Exception e) =>
        e is SouthCallException south ? $"{south.GetType().Name}({(int)south.Status})" : e.GetType().Name;

    private async Task<TResult> PostAsync<TRequest, TResult>(
        string path, TRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                FleetProtocolJson.Serialize(request), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(message, ct);

        if (!response.IsSuccessStatusCode)
            throw new SouthCallException(path, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(ct);

        return FleetProtocolJson.Deserialize<TResult>(json)
            ?? throw new SouthCallException(path, response.StatusCode, "unreadable response body");
    }
}

/// <summary>
/// A south call that did not return a usable result.
/// </summary>
/// <remarks>
/// Carries the path and the status code and nothing else. The response body of a refusal is a fixed
/// protocol error; the request body can contain conversation text; the header carries the bearer.
/// None of the three belongs in an exception message that will be logged.
/// </remarks>
public sealed class SouthCallException(string path, HttpStatusCode status, string? detail = null)
    : Exception($"south call {path} returned {(int)status}{(detail is null ? "" : $" ({detail})")}")
{
    public string Path { get; } = path;
    public HttpStatusCode Status { get; } = status;

    /// <summary>
    /// True when retrying the identical request cannot change the answer.
    /// </summary>
    /// <remarks>
    /// 401 is deliberately NOT permanent for the transport's purposes — the consumer treats it as a
    /// requeue-and-back-off so a rotated credential recovers without a restart, and so a
    /// misconfigured token cannot spin a hot loop. It is classified here only so a blind retry
    /// wrapper does not hammer it.
    /// </remarks>
    public bool IsPermanent => (int)Status is >= 400 and < 500;
}
