using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>Named outcome of one inbox API call. The pull loop never guesses past any of them.</summary>
public enum WorkflowOutcomeCallState
{
    Ok,
    /// <summary>404: no such batch, or no receipt yet.</summary>
    NotFound,
    /// <summary>401: missing, wrong or rotated scoped token.</summary>
    Unauthorized,
    /// <summary>403: not this coordinator's item or batch.</summary>
    Forbidden,
    /// <summary>409: stale generation, version or hash, or not delivered.</summary>
    Conflict,
    /// <summary>422: the server judged the decision unsafe or invalid.</summary>
    Unprocessable,
    /// <summary>503: feature disabled or intake misconfigured server-side.</summary>
    Unavailable,
    /// <summary>Any other status, including a redirect (never followed).</summary>
    Failed,
    /// <summary>No response (timeout, connection error, unreadable body): a POST may or may not have landed.</summary>
    NoResponse,
}

public readonly record struct WorkflowOutcomeCall<T>(WorkflowOutcomeCallState State, T? Value, int? Status);

/// <summary>201 body of a recorded decision.</summary>
public sealed record WorkflowOutcomeDecisionAccepted(Guid DecisionId, string Status);

/// <summary>
/// The coordinator's scoped client for <c>/api/workflow-outcomes</c> (#476). Only the configured
/// coordinator constructs one. It reads its own inbox and receipts and records acknowledgements
/// and decisions — nothing else: no start, approval or memory access.
/// </summary>
/// <remarks>
/// The base address is an exact configured origin and redirects are not followed, so the bearer
/// token is only ever sent to that origin. The token is attached per request and is never logged.
/// </remarks>
public sealed class WorkflowOutcomeInboxClient(HttpClient http, string scopedToken)
{
    public const string HttpClientName = "workflow-outcomes";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public const int PageLimit = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The primary handler for the named client: no redirects, recycled connections.</summary>
    public static HttpMessageHandler CreateHandler() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

    public Task<WorkflowOutcomeCall<WorkflowOutcomeInboxPage>> GetInboxPageAsync(long generation, string? cursor, CancellationToken ct)
    {
        var query = "generation=" + generation.ToString(CultureInfo.InvariantCulture)
            + "&limit=" + PageLimit.ToString(CultureInfo.InvariantCulture)
            + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        return SendAsync<WorkflowOutcomeInboxPage>(HttpMethod.Get, "api/workflow-outcomes/inbox?" + query, null, ct);
    }

    public Task<WorkflowOutcomeCall<WorkflowOutcomeReceipt>> PostDeliveredAsync(Guid batchId, WorkflowOutcomeDeliveredRequest request, CancellationToken ct) =>
        SendAsync<WorkflowOutcomeReceipt>(HttpMethod.Post, $"api/workflow-outcomes/batches/{batchId:D}/delivered", request, ct);

    public Task<WorkflowOutcomeCall<WorkflowOutcomeReceipt>> GetReceiptAsync(Guid batchId, string nonce, CancellationToken ct) =>
        SendAsync<WorkflowOutcomeReceipt>(HttpMethod.Get,
            $"api/workflow-outcomes/batches/{batchId:D}/receipt?nonce={Uri.EscapeDataString(nonce)}", null, ct);

    public Task<WorkflowOutcomeCall<WorkflowOutcomeReceipt>> PostAckAsync(Guid batchId, WorkflowOutcomeAckRequest request, CancellationToken ct) =>
        SendAsync<WorkflowOutcomeReceipt>(HttpMethod.Post, $"api/workflow-outcomes/batches/{batchId:D}/ack", request, ct);

    public Task<WorkflowOutcomeCall<WorkflowOutcomeDecisionAccepted>> PostDecisionAsync(Guid outcomeId, WorkflowOutcomeDecisionRequest request, CancellationToken ct) =>
        SendAsync<WorkflowOutcomeDecisionAccepted>(HttpMethod.Post, $"api/workflow-outcomes/{outcomeId:D}/decisions", request, ct);

    private async Task<WorkflowOutcomeCall<T>> SendAsync<T>(HttpMethod method, string relative, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(WorkflowOutcomeCallState.NoResponse, default, null);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var state = response.StatusCode switch
            {
                HttpStatusCode.OK or HttpStatusCode.Created => WorkflowOutcomeCallState.Ok,
                HttpStatusCode.NotFound => WorkflowOutcomeCallState.NotFound,
                HttpStatusCode.Unauthorized => WorkflowOutcomeCallState.Unauthorized,
                HttpStatusCode.Forbidden => WorkflowOutcomeCallState.Forbidden,
                HttpStatusCode.Conflict => WorkflowOutcomeCallState.Conflict,
                HttpStatusCode.UnprocessableEntity => WorkflowOutcomeCallState.Unprocessable,
                HttpStatusCode.ServiceUnavailable => WorkflowOutcomeCallState.Unavailable,
                _ => WorkflowOutcomeCallState.Failed,
            };
            if (state != WorkflowOutcomeCallState.Ok) return new(state, default, status);

            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
                return value is null ? new(WorkflowOutcomeCallState.NoResponse, default, status) : new(state, value, status);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or NotSupportedException
                                           || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // A 2xx with a body we could not read is as ambiguous as no response at all.
                return new(WorkflowOutcomeCallState.NoResponse, default, status);
            }
        }
    }
}

/// <summary>
/// Registered instead of the outcome client when <c>WorkflowOutcomes</c> is enabled but invalid
/// (#476): intake failure is explicit at startup, and the agent's normal transport keeps running.
/// Faults name keys only.
/// </summary>
public sealed class WorkflowOutcomeConfigFaultReporter(IReadOnlyList<string> faults, ILogger<WorkflowOutcomeConfigFaultReporter> logger)
    : IHostedService
{
    public IReadOnlyList<string> Faults { get; } = faults;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogError("Workflow outcome intake disabled: invalid configuration ({Faults}); the failure inbox stays pending "
            + "server-side and normal agent transport is unaffected", string.Join("; ", Faults));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The coordinator's failure inbox pull loop (#476). Polls every <c>PollSeconds</c>, fsyncs each
/// new batch locally BEFORE posting delivered, reconciles any ambiguous delivered/ack POST with an
/// authenticated receipt GET before resending the same request, and hands delivered batches to
/// <see cref="WorkflowOutcomeCompletionHandler"/> one turn at a time.
/// </summary>
/// <remarks>
/// <para>
/// There is no success path: a cycle with no failure batch admits no turn, so an idle coordinator
/// runs zero model turns. Every failure is logged as a named state with ids and counts only —
/// never a token, a body or an item's text — and is retried on a later cycle; none of them is
/// turned into a notification workflow, a memory or a message (the nonrecursive fallback).
/// </para>
/// <para>
/// Shutdown cancels in-flight calls and leaves every local state as it was: nothing pending is
/// marked delivered and nothing is cleared.
/// </para>
/// </remarks>
public sealed class WorkflowOutcomePullService : BackgroundService
{
    /// <summary>Upper bound on inbox pages read per cycle; the rest is read next cycle.</summary>
    public const int MaxPagesPerCycle = 100;

    /// <summary>How long a finished batch stays after the server stops listing it.</summary>
    public static readonly TimeSpan PruneAfter = TimeSpan.FromHours(24);

    private readonly WorkflowOutcomeClientOptions _options;
    private readonly WorkflowOutcomeInboxClient _client;
    private readonly WorkflowOutcomeLocalStore _store;
    private readonly WorkflowOutcomeCompletionHandler _handler;
    private readonly ILogger<WorkflowOutcomePullService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly SemaphoreSlim _kick = new(0, 1);
    private string _lastState = "starting";

    public WorkflowOutcomePullService(
        IOptions<WorkflowOutcomeClientOptions> options,
        WorkflowOutcomeInboxClient client,
        WorkflowOutcomeLocalStore store,
        WorkflowOutcomeCompletionHandler handler,
        ILogger<WorkflowOutcomePullService> logger,
        TimeProvider? time = null)
    {
        _options = options.Value;
        _client = client;
        _store = store;
        _handler = handler;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _handler.Changed += Kick;
    }

    /// <summary>The last named health state, e.g. <c>ok</c>, <c>unauthorized</c>, <c>store_error</c>.</summary>
    public string LastState => Volatile.Read(ref _lastState);

    /// <summary>True once the local inbox loaded; false means intake is disabled for this process.</summary>
    public bool Ready { get; private set; }

    /// <summary>Requests a local cycle (deliver, acknowledge, admit) without waiting for the poll.</summary>
    public void Kick()
    {
        try { _kick.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Initialize()) return;

        var poll = TimeSpan.FromSeconds(_options.PollSeconds);
        var nextFetch = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var fetch = _time.GetUtcNow() >= nextFetch;
            if (fetch) nextFetch = _time.GetUtcNow() + poll;
            try
            {
                await RunCycleAsync(fetch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Report("cycle_failed", ex.GetType().Name);
            }

            var wait = nextFetch - _time.GetUtcNow();
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            try { await _kick.WaitAsync(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Loads the durable inbox and recovers interrupted turns. An unreadable inbox disables intake
    /// for this process with an explicit error; the agent's normal transport is unaffected.
    /// </summary>
    internal bool Initialize()
    {
        try
        {
            _store.Load();
        }
        catch (WorkflowOutcomeStoreException ex)
        {
            Report("store_unreadable", ex.Message);
            _logger.LogError("Workflow outcome intake disabled: the local inbox could not be loaded and was left untouched for the operator");
            return false;
        }

        if (!_handler.Recover())
        {
            Report("store_error", _store.LastError);
            return false;
        }

        Ready = true;
        _logger.LogInformation("Workflow outcome intake started for generation {Generation} ({Count} local batch(es))",
            _options.CoordinatorGeneration, _store.Snapshot().Count);
        return true;
    }

    /// <summary>One pass: fetch (optionally), deliver, reconcile turns, acknowledge, admit, prune.</summary>
    internal async Task RunCycleAsync(bool fetch, CancellationToken ct)
    {
        if (!Ready) return;
        await _cycleGate.WaitAsync(ct);
        try
        {
            var healthy = true;
            if (fetch) healthy &= await FetchAsync(ct);
            healthy &= await DeliverAsync(ct);
            _handler.ReconcileTurns();
            healthy &= await AcknowledgeAsync(ct);
            await _handler.TryAdmitNextAsync(ct);
            _handler.ReleaseFinishedConversations();
            // Only a cycle that also read the inbox can call intake healthy.
            if (healthy && fetch) Report("ok", null);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    // ── fetch ──────────────────────────────────────────────────────────────────────────────

    private async Task<bool> FetchAsync(CancellationToken ct)
    {
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var listed = new HashSet<Guid>();
        var complete = false;

        for (var page = 0; page < MaxPagesPerCycle; page++)
        {
            var call = await _client.GetInboxPageAsync(_options.CoordinatorGeneration, cursor, ct);
            if (call.State != WorkflowOutcomeCallState.Ok)
            {
                Report(NameOf(call.State, "inbox"), call.Status?.ToString(CultureInfo.InvariantCulture));
                return false;
            }

            foreach (var batch in call.Value!.Batches ?? [])
            {
                if (batch is null) continue;
                listed.Add(batch.BatchId);
                if (!Accept(batch)) return false;
            }

            cursor = call.Value.NextCursor;
            if (cursor is null) { complete = true; break; }
            if (!cursors.Add(cursor))
            {
                Report("inbox_cursor_repeated", null);
                return false;
            }
        }

        // Only a complete listing proves which finished batches the server no longer offers.
        if (complete) RecordListing(listed);
        return true;
    }

    /// <summary>Validates and durably stores a new batch. False only when the store failed.</summary>
    private bool Accept(WorkflowOutcomeInboxBatch batch)
    {
        if (_store.Get(batch.BatchId) is { } known)
        {
            if (known.Batch.Nonce != batch.Nonce || known.Batch.ItemVersionsHash != batch.ItemVersionsHash)
                _logger.LogWarning("Workflow outcome batch {BatchId} changed nonce or membership after storage; keeping the stored copy",
                    batch.BatchId);
            return true;
        }

        if (DescribeBatchFault(batch) is { } fault)
        {
            _logger.LogWarning("Workflow outcome batch {BatchId} refused: {Fault}", batch.BatchId, fault);
            return true;
        }

        var now = _time.GetUtcNow();
        var local = new WorkflowOutcomeLocalBatch
        {
            Batch = batch,
            Conversation = WorkflowOutcomeCompletionHandler.ConversationFor(batch.BatchId, _options.Coordinator, _options.CoordinatorGeneration),
            TaskId = WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId),
            StoredAt = now,
            LastListedAt = now,
        };

        // Fetched is not delivered: without this fsynced copy no receipt is ever posted.
        if (_store.TryAdd(local)) return true;
        Report("store_error", _store.LastError);
        return false;
    }

    internal string? DescribeBatchFault(WorkflowOutcomeInboxBatch batch)
    {
        if (batch.BatchId == Guid.Empty) return "empty batch id";
        if (string.IsNullOrWhiteSpace(batch.Nonce)) return "missing nonce";
        if (batch.Generation != _options.CoordinatorGeneration) return "generation mismatch";
        if (!string.Equals(batch.Coordinator, _options.Coordinator, StringComparison.Ordinal)) return "coordinator mismatch";
        if (batch.Items is null || batch.Items.Count is 0 or > WorkflowOutcomeLimits.MaxBatchItems) return "item count out of range";
        if (batch.Items.Any(i => i is null || i.Id == Guid.Empty || i.Version <= 0)) return "invalid item reference";
        if (batch.Items.Select(i => i.Id).Distinct().Count() != batch.Items.Count) return "duplicate item";
        var hash = WorkflowOutcomeHashes.ItemVersionsHash(batch.Items.Select(i => new WorkflowOutcomeItemRef(i.Id, i.Version)));
        return string.Equals(hash, batch.ItemVersionsHash, StringComparison.Ordinal) ? null : "item versions hash mismatch";
    }

    private void RecordListing(HashSet<Guid> listed)
    {
        var now = _time.GetUtcNow();
        var ok = _store.TryReplaceAll(batches => batches
            .Select(b => listed.Contains(b.Batch.BatchId) ? b with { LastListedAt = now } : b)
            // Finished and unlisted for a day: the server no longer offers it, so it cannot return.
            .Where(b => !(b.IsTerminal && !listed.Contains(b.Batch.BatchId) && now - b.LastListedAt > PruneAfter))
            .ToList());
        if (!ok) Report("store_error", _store.LastError);
    }

    // ── delivered ──────────────────────────────────────────────────────────────────────────

    private async Task<bool> DeliverAsync(CancellationToken ct)
    {
        var healthy = true;
        foreach (var local in _store.Snapshot().Where(b => b.Delivery == WorkflowOutcomeLocalStates.DeliveryPending))
        {
            var batch = local.Batch;

            if (local.DeliveryAttempts > 0)
            {
                // An earlier POST may have landed with its response lost: read back before resending.
                var readback = await _client.GetReceiptAsync(batch.BatchId, batch.Nonce, ct);
                if (readback.State == WorkflowOutcomeCallState.Ok && ReceiptMatches(batch, readback.Value!)
                    && readback.Value!.DeliveredAt is not null)
                {
                    healthy &= Persist(batch.BatchId, b => b with { Delivery = WorkflowOutcomeLocalStates.DeliveryDelivered, Receipt = readback.Value });
                    continue;
                }
                if (readback.State == WorkflowOutcomeCallState.Conflict)
                {
                    healthy &= Supersede(batch.BatchId, "receipt");
                    continue;
                }
                if (readback.State is not (WorkflowOutcomeCallState.Ok or WorkflowOutcomeCallState.NotFound))
                {
                    Report(NameOf(readback.State, "receipt"), batch.BatchId.ToString("D"));
                    healthy = false;
                    continue;
                }
            }

            // Counted durably first, so a crash or lost response leads to a readback next time.
            if (!Persist(batch.BatchId, b => b with { DeliveryAttempts = b.DeliveryAttempts + 1 }))
            {
                healthy = false;
                continue;
            }

            var call = await _client.PostDeliveredAsync(batch.BatchId,
                new WorkflowOutcomeDeliveredRequest(batch.Nonce, batch.Generation, batch.ItemVersionsHash), ct);
            if (call.State == WorkflowOutcomeCallState.Ok && ReceiptMatches(batch, call.Value!))
            {
                healthy &= Persist(batch.BatchId, b => b with { Delivery = WorkflowOutcomeLocalStates.DeliveryDelivered, Receipt = call.Value });
            }
            else if (call.State == WorkflowOutcomeCallState.Conflict)
            {
                healthy &= Supersede(batch.BatchId, "delivered");
            }
            else
            {
                Report(call.State == WorkflowOutcomeCallState.Ok ? "delivered_receipt_mismatch" : NameOf(call.State, "delivered"),
                    batch.BatchId.ToString("D"));
                healthy = false;
            }
        }
        return healthy;
    }

    private static bool ReceiptMatches(WorkflowOutcomeInboxBatch batch, WorkflowOutcomeReceipt receipt) =>
        receipt.BatchId == batch.BatchId
        && receipt.Generation == batch.Generation
        && string.Equals(receipt.Nonce, batch.Nonce, StringComparison.Ordinal)
        && string.Equals(receipt.ItemVersionsHash, batch.ItemVersionsHash, StringComparison.Ordinal);

    private bool Supersede(Guid batchId, string route)
    {
        _logger.LogWarning("Workflow outcome batch {BatchId} superseded at {Route} (409); it gets no turn", batchId, route);
        return Persist(batchId, b => b.Turn == WorkflowOutcomeLocalStates.TurnPending
            ? b with { Delivery = WorkflowOutcomeLocalStates.DeliverySuperseded }
            : b);
    }

    // ── ack and decisions ──────────────────────────────────────────────────────────────────

    private async Task<bool> AcknowledgeAsync(CancellationToken ct)
    {
        var healthy = true;
        foreach (var local in _store.Snapshot().Where(b => !b.Completed && b.Turn == WorkflowOutcomeLocalStates.TurnCompleted))
        {
            if (local.Ack == WorkflowOutcomeLocalStates.AckPending)
                healthy &= await PostAckAsync(local, ct);

            var current = _store.Get(local.Batch.BatchId)!;
            if (current.Ack == WorkflowOutcomeLocalStates.AckAcked)
                healthy &= await PostDecisionsAsync(current, ct);

            current = _store.Get(local.Batch.BatchId)!;
            if (current.Ack is WorkflowOutcomeLocalStates.AckRejected or WorkflowOutcomeLocalStates.AckNone
                || current.Ack == WorkflowOutcomeLocalStates.AckAcked
                   && current.Decisions.All(d => d.State != WorkflowOutcomeLocalStates.DecisionPending))
            {
                healthy &= Persist(current.Batch.BatchId, b => b with { Completed = true });
            }
        }
        return healthy;
    }

    private async Task<bool> PostAckAsync(WorkflowOutcomeLocalBatch local, CancellationToken ct)
    {
        var batch = local.Batch;
        var acks = local.Output!.Ack;

        if (local.AckAttempts > 0)
        {
            var readback = await _client.GetReceiptAsync(batch.BatchId, batch.Nonce, ct);
            if (readback.State == WorkflowOutcomeCallState.Ok && ReceiptMatches(batch, readback.Value!) && Covers(readback.Value!, acks))
                return Persist(batch.BatchId, b => b with { Ack = WorkflowOutcomeLocalStates.AckAcked, Receipt = readback.Value });
            if (readback.State == WorkflowOutcomeCallState.Conflict)
                return RejectAck(batch.BatchId, "receipt_conflict");
            if (readback.State is not (WorkflowOutcomeCallState.Ok or WorkflowOutcomeCallState.NotFound))
            {
                Report(NameOf(readback.State, "receipt"), batch.BatchId.ToString("D"));
                return false;
            }
        }

        if (!Persist(batch.BatchId, b => b with { AckAttempts = b.AckAttempts + 1 })) return false;

        var call = await _client.PostAckAsync(batch.BatchId,
            new WorkflowOutcomeAckRequest(batch.Generation, batch.Nonce, acks, local.TaskId), ct);
        if (call.State == WorkflowOutcomeCallState.Ok && ReceiptMatches(batch, call.Value!) && Covers(call.Value!, acks))
            return Persist(batch.BatchId, b => b with { Ack = WorkflowOutcomeLocalStates.AckAcked, Receipt = call.Value });
        if (call.State == WorkflowOutcomeCallState.Ok)
            return RejectAck(batch.BatchId, "ack_receipt_mismatch");
        if (call.State == WorkflowOutcomeCallState.Conflict)
            return RejectAck(batch.BatchId, "ack_conflict");

        Report(NameOf(call.State, "ack"), batch.BatchId.ToString("D"));
        return false;
    }

    private static bool Covers(WorkflowOutcomeReceipt receipt, IReadOnlyList<WorkflowOutcomeItemRef> acks) =>
        acks.All(a => (receipt.AckedVersions ?? []).Contains(a));

    private bool RejectAck(Guid batchId, string reason)
    {
        _logger.LogWarning("Workflow outcome batch {BatchId} acknowledgement refused ({Reason}); left for the operator", batchId, reason);
        return Persist(batchId, b => b with { Ack = WorkflowOutcomeLocalStates.AckRejected });
    }

    private async Task<bool> PostDecisionsAsync(WorkflowOutcomeLocalBatch local, CancellationToken ct)
    {
        var healthy = true;
        foreach (var pending in local.Decisions.Where(d => d.State == WorkflowOutcomeLocalStates.DecisionPending))
        {
            var d = pending.Decision;
            var call = await _client.PostDecisionAsync(d.Id, new WorkflowOutcomeDecisionRequest(
                d.Version, d.Operation, d.Owner, d.Reason, d.EvidenceRefs, d.DueAt,
                TaskRef: null,
                // finish closes the item on an accepted artifact; the parser guaranteed one artifact ref.
                ArtifactRef: d.Operation == WorkflowOutcomeOperations.Finish
                    ? d.EvidenceRefs.FirstOrDefault(r => r.StartsWith(WorkflowOutcomeTurnOutputParser.ArtifactRefPrefix, StringComparison.Ordinal))
                    : null,
                BatchId: local.Batch.BatchId, Generation: local.Batch.Generation), ct);

            var state = call.State switch
            {
                WorkflowOutcomeCallState.Ok => WorkflowOutcomeLocalStates.DecisionAccepted,
                WorkflowOutcomeCallState.Conflict => WorkflowOutcomeLocalStates.DecisionStale,
                WorkflowOutcomeCallState.Unprocessable => WorkflowOutcomeLocalStates.DecisionRejected,
                _ => WorkflowOutcomeLocalStates.DecisionPending,
            };
            if (state == WorkflowOutcomeLocalStates.DecisionPending)
            {
                Report(NameOf(call.State, "decision"), d.Id.ToString("D"));
                healthy = false;
            }
            else if (state != WorkflowOutcomeLocalStates.DecisionAccepted)
            {
                _logger.LogWarning("Workflow outcome decision for {OutcomeId} v{Version} {State} by the server", d.Id, d.Version, state);
            }

            healthy &= Persist(local.Batch.BatchId, b => b with
            {
                Decisions = b.Decisions
                    .Select(x => x.Decision.Id == d.Id ? x with { State = state, Attempts = x.Attempts + 1 } : x)
                    .ToList(),
            });
        }
        return healthy;
    }

    // ── shared ─────────────────────────────────────────────────────────────────────────────

    private bool Persist(Guid batchId, Func<WorkflowOutcomeLocalBatch, WorkflowOutcomeLocalBatch> update)
    {
        if (_store.TryUpdate(batchId, update)) return true;
        Report("store_error", _store.LastError);
        return false;
    }

    private static string NameOf(WorkflowOutcomeCallState state, string route) => state switch
    {
        WorkflowOutcomeCallState.Unauthorized => "unauthorized",
        WorkflowOutcomeCallState.Forbidden => "forbidden",
        WorkflowOutcomeCallState.Conflict => route == "inbox" ? "generation_conflict" : route + "_conflict",
        WorkflowOutcomeCallState.Unavailable => "unavailable",
        WorkflowOutcomeCallState.NoResponse => route + "_no_response",
        WorkflowOutcomeCallState.NotFound => route + "_not_found",
        WorkflowOutcomeCallState.Unprocessable => route + "_unprocessable",
        _ => route + "_failed",
    };

    /// <summary>Logs a state change once (a repeat at Debug). Details are ids or codes, never content.</summary>
    private void Report(string state, string? detail)
    {
        var previous = Interlocked.Exchange(ref _lastState, state);
        if (state == "ok")
        {
            if (previous != "ok") _logger.LogInformation("Workflow outcome intake healthy");
            return;
        }
        if (previous == state)
            _logger.LogDebug("Workflow outcome intake still {State} ({Detail})", state, detail);
        else
            _logger.LogError("Workflow outcome intake {State} ({Detail}); work stays pending and is retried", state, detail);
    }

    public override void Dispose()
    {
        _handler.Changed -= Kick;
        base.Dispose();
    }
}
