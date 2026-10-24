using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Shared;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using ApiWorkflowExecution = Temporalio.Api.Common.V1.WorkflowExecution;

namespace Fleet.Orchestrator.Services;

// Workflow failure accountability (#476): the observer's only window into Temporal. Everything is
// read with raw WorkflowService RPCs (exact RunId, opaque page tokens, every page), never through
// the capped dashboard feeds, and an error is always an error — never an empty result.

/// <summary>Why a Temporal read did not produce a result. Reasons are payload-free.</summary>
public enum WorkflowOutcomeReadFailure
{
    /// <summary>Namespace unreachable, RPC error or the 10 s bound elapsed: retry later, cursor unchanged.</summary>
    Unavailable,

    /// <summary>The exact run (or its history) no longer exists: evidence missing, coverage gap.</summary>
    NotFound,

    /// <summary>The server rejected the request, e.g. an expired/invalid page token.</summary>
    InvalidArgument,

    /// <summary>The server returned the token it was given: no progress is possible, the window stays incomplete.</summary>
    RepeatedToken,

    /// <summary>The response cannot be interpreted (raw history blobs, event-id gap).</summary>
    Unsupported,
}

public sealed class WorkflowOutcomeReadException(WorkflowOutcomeReadFailure kind, string reason, Exception? inner = null)
    : Exception($"workflow outcome read failed: {kind} ({reason})", inner)
{
    public WorkflowOutcomeReadFailure Kind { get; } = kind;

    /// <summary>Short machine reason (status code or exception type); never a payload or URL.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Raw RPC seam over one namespace's WorkflowService (faked in tests). Each call is bounded by RpcTimeoutSeconds.</summary>
public interface IWorkflowOutcomeRpc
{
    Task<ListWorkflowExecutionsResponse> ListAsync(ListWorkflowExecutionsRequest request, CancellationToken ct);
    Task<GetWorkflowExecutionHistoryResponse> GetHistoryAsync(GetWorkflowExecutionHistoryRequest request, CancellationToken ct);
    Task<DescribeWorkflowExecutionResponse> DescribeAsync(DescribeWorkflowExecutionRequest request, CancellationToken ct);
    Task<DescribeNamespaceResponse> DescribeNamespaceAsync(DescribeNamespaceRequest request, CancellationToken ct);
}

/// <summary>The production RPC seam: the shared <see cref="TemporalClientRegistry"/> clients, 10 s bound per call.</summary>
public sealed class TemporalWorkflowOutcomeRpc(TemporalClientRegistry registry, IOptions<WorkflowOutcomeOptions> options) : IWorkflowOutcomeRpc
{
    private TimeSpan Bound => TimeSpan.FromSeconds(options.Value.RpcTimeoutSeconds);

    public Task<ListWorkflowExecutionsResponse> ListAsync(ListWorkflowExecutionsRequest request, CancellationToken ct) =>
        CallAsync(request.Namespace, ct, (c, rpc) => c.WorkflowService.ListWorkflowExecutionsAsync(request, rpc));

    public Task<GetWorkflowExecutionHistoryResponse> GetHistoryAsync(GetWorkflowExecutionHistoryRequest request, CancellationToken ct) =>
        CallAsync(request.Namespace, ct, (c, rpc) => c.WorkflowService.GetWorkflowExecutionHistoryAsync(request, rpc));

    public Task<DescribeWorkflowExecutionResponse> DescribeAsync(DescribeWorkflowExecutionRequest request, CancellationToken ct) =>
        CallAsync(request.Namespace, ct, (c, rpc) => c.WorkflowService.DescribeWorkflowExecutionAsync(request, rpc));

    public Task<DescribeNamespaceResponse> DescribeNamespaceAsync(DescribeNamespaceRequest request, CancellationToken ct) =>
        CallAsync(request.Namespace, ct, (c, rpc) => c.WorkflowService.DescribeNamespaceAsync(request, rpc));

    private async Task<T> CallAsync<T>(string ns, CancellationToken ct, Func<ITemporalClient, RpcOptions, Task<T>> call)
    {
        var bound = Bound;
        var connected = await registry.TryGetClientAsync(ns, ct);
        if (connected.Client is null)
            throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unavailable, connected.Error ?? "namespace_unavailable");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(bound);
        try
        {
            // WaitAsync keeps the bound even if the SDK ignores the token.
            return await call(connected.Client, new RpcOptions { CancellationToken = cts.Token, Timeout = bound }).WaitAsync(bound, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RpcException ex)
        {
            throw ex.Code switch
            {
                RpcException.StatusCode.NotFound => new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.NotFound, "not_found", ex),
                RpcException.StatusCode.InvalidArgument => new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.InvalidArgument, "invalid_argument", ex),
                _ => new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unavailable, "rpc_" + ex.Code.ToString().ToLowerInvariant(), ex),
            };
        }
        catch (TimeoutException ex) { throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unavailable, "timeout", ex); }
        catch (OperationCanceledException ex) { throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unavailable, "timeout", ex); }
        catch (Exception ex) when (ex is not WorkflowOutcomeReadException)
        {
            throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unavailable, ex.GetType().Name, ex);
        }
    }
}

// ─── Read results ─────────────────────────────────────────────────────────────

/// <summary>One visibility row. Visibility is eventually consistent: only Describe/history are authoritative.</summary>
public sealed record WorkflowOutcomeListedRun(
    string WorkflowId,
    string RunId,
    string WorkflowType,
    string Status,
    DateTime? StartTime,
    DateTime? CloseTime,
    string? ParentWorkflowId,
    string? ParentRunId);

/// <summary>One visibility page; <see cref="NextPageToken"/> null means the listing is complete.</summary>
public sealed record WorkflowOutcomeListPage(IReadOnlyList<WorkflowOutcomeListedRun> Runs, byte[]? NextPageToken);

/// <summary>Authoritative Describe of an exact run.</summary>
public sealed record WorkflowOutcomeDescribed(
    string WorkflowId,
    string RunId,
    string WorkflowType,
    string Status,
    DateTime? StartTime,
    DateTime? CloseTime);

/// <summary>DescribeNamespace facts recorded at enrollment.</summary>
public sealed record WorkflowOutcomeNamespaceInfo(long RetentionSeconds, string HistoryArchivalState, string VisibilityArchivalState);

/// <summary>
/// A decoded payload. <see cref="Supported"/> false (binary, protobuf-binary, encrypted or unknown
/// encodings, invalid JSON) means the evidence exists but cannot be read: it is recorded as
/// pending/quarantined evidence, never omitted and never interpreted.
/// </summary>
public sealed record WorkflowOutcomePayload(string Encoding, string Sha256, string? Json, bool IsNull, bool Supported);

/// <summary>
/// One scheduled <c>DelegateToAgent</c> activity and how it closed. <see cref="Outcome"/> is
/// completed, failed, timed_out, canceled or open (no close event before the run closed).
/// </summary>
public sealed record WorkflowOutcomeDelegate(
    long ScheduledEventId,
    string? Target,
    string? TaskId,
    int Attempt,
    string Outcome,
    long? CloseEventId,
    string? CloseEventType,
    WorkflowOutcomePayload? Result,
    string? ResultText,
    string? ResultStatus,
    string? FailureType,
    string? FailureMessage,
    bool ArgumentsUnsupported);

public sealed record WorkflowOutcomeSignal(long EventId, string Name, string? InputSha256);

/// <summary>The run's closing event, exactly as recorded.</summary>
public sealed record WorkflowOutcomeTerminal(
    long EventId,
    string EventType,
    string Status,
    DateTime? EventTime,
    string AttributesJson,
    string EventSha256,
    string? FailureType,
    string? FailureMessage,
    string? RetryState,
    string? NewExecutionRunId,
    WorkflowOutcomePayload? Result,
    bool HasFailure);

/// <summary>Everything the classifier and ownership resolver may use about one exact run.</summary>
public sealed record WorkflowOutcomeRunFacts
{
    public required string Namespace { get; init; }
    public required string WorkflowId { get; init; }
    public required string RunId { get; init; }
    public required string WorkflowType { get; init; }
    public DateTime? StartTime { get; init; }

    /// <summary>First Started input payload (hash always; JSON only when decodable).</summary>
    public WorkflowOutcomePayload? Input { get; init; }
    public string? FirstExecutionRunId { get; init; }
    public string? ContinuedExecutionRunId { get; init; }

    /// <summary>ContinueAsNewInitiator name (Workflow, Retry, CronSchedule) when the run continues another.</summary>
    public string? Initiator { get; init; }
    public string? CronSchedule { get; init; }
    public int Attempt { get; init; }
    public string? ParentNamespace { get; init; }
    public string? ParentWorkflowId { get; init; }
    public string? ParentRunId { get; init; }

    /// <summary>From this run's own LoadWorkflowDefinition result; never the current DB row.</summary>
    public string? DefinitionName { get; init; }
    public int? DefinitionVersion { get; init; }

    /// <summary>
    /// Lowercase hex SHA-256 of the exact LoadWorkflowDefinition result payload data bytes as
    /// recorded in this run's own history (the first payload of the ActivityTaskCompleted event
    /// for the first LoadWorkflowDefinition schedule). Null for runs without that activity
    /// (compiled workflows) or when the payload encoding is not JSON.
    /// </summary>
    public string? DefinitionHash { get; init; }
    public bool DefinitionUnsupported { get; init; }

    /// <summary>
    /// True when the history schedules LoadWorkflowDefinition at all (resolved or not). Only a
    /// complete history with this false proves a compiled workflow with no loaded definition.
    /// </summary>
    public bool DefinitionScheduled { get; init; }

    /// <summary>
    /// The worker build id recorded on every <c>WorkflowTaskCompleted</c> event of this run, when
    /// there is at least one and they all carry the same non-empty value; otherwise null (none
    /// recorded, one missing, or the run spanned workers of different builds). The bridge stamps
    /// its image's source commit here, so this is the run's own source provenance.
    /// </summary>
    public string? WorkerBuildId { get; init; }

    public IReadOnlyList<WorkflowOutcomeDelegate> Delegates { get; init; } = [];
    public IReadOnlyList<WorkflowOutcomeSignal> Signals { get; init; } = [];
    public IReadOnlyList<long> CancelRequestEventIds { get; init; } = [];
    public WorkflowOutcomeTerminal? Terminal { get; init; }

    /// <summary>True only when every page was read and the event ids are contiguous from 1.</summary>
    public bool HistoryComplete { get; init; }
    public long EventCount { get; init; }

    /// <summary>Refs (<c>event:&lt;id&gt;:&lt;field&gt;</c>) of payloads that exist but could not be decoded.</summary>
    public IReadOnlyList<string> UnsupportedPayloadRefs { get; init; } = [];

    /// <summary>Started-event evidence ref.</summary>
    public string StartedEventRef => "event:1";

    public string RunRef => "run:" + Namespace + "/" + WorkflowId + "/" + RunId;
}

/// <summary>
/// Resumable progress of one exact-RunId history read. A run is classified only from a
/// <see cref="Complete"/> progress; a slice that hits the page budget returns the next token.
/// </summary>
public sealed class WorkflowOutcomeHistoryProgress(string @namespace, string workflowId, string runId)
{
    public string Namespace { get; } = @namespace;
    public string WorkflowId { get; } = workflowId;
    public string RunId { get; } = runId;
    public WorkflowOutcomeFactsBuilder Builder { get; } = new(@namespace, workflowId, runId);
    public byte[]? NextPageToken { get; internal set; }
    public int PagesRead { get; internal set; }
    public bool Complete { get; internal set; }

    public WorkflowOutcomeRunFacts Facts() => Builder.Build(Complete);
}

public interface IWorkflowOutcomeHistoryReader
{
    /// <summary>One visibility page of <paramref name="query"/> (all statuses and types unless the query says otherwise).</summary>
    Task<WorkflowOutcomeListPage> ListPageAsync(string @namespace, string query, byte[]? pageToken, CancellationToken ct);

    /// <summary>Describe an exact run; null when it no longer exists.</summary>
    Task<WorkflowOutcomeDescribed?> DescribeAsync(string @namespace, string workflowId, string runId, CancellationToken ct);

    /// <summary>
    /// Reads at most MaxHistoryPagesPerSlice more pages of the exact run's raw history into
    /// <paramref name="progress"/> (a new progress when null). Never truncates: an incomplete
    /// progress carries the next token and must be resumed.
    /// </summary>
    Task<WorkflowOutcomeHistoryProgress> ReadHistorySliceAsync(
        string @namespace, string workflowId, string runId, WorkflowOutcomeHistoryProgress? progress, CancellationToken ct);

    /// <summary>
    /// The first history page only, for the Started event (input, parent links) of a run whose
    /// ownership a child inherits. Never used to classify the run itself.
    /// </summary>
    Task<WorkflowOutcomeRunFacts> ReadStartedAsync(string @namespace, string workflowId, string runId, CancellationToken ct);

    Task<WorkflowOutcomeNamespaceInfo> DescribeNamespaceAsync(string @namespace, CancellationToken ct);
}

/// <summary>Raw, paged, bounded reads; at most MaxConcurrentReads RPCs in flight.</summary>
public sealed class WorkflowOutcomeHistoryReader : IWorkflowOutcomeHistoryReader
{
    private readonly IWorkflowOutcomeRpc _rpc;
    private readonly WorkflowOutcomeOptions _options;
    private readonly SemaphoreSlim _reads;

    public WorkflowOutcomeHistoryReader(IWorkflowOutcomeRpc rpc, IOptions<WorkflowOutcomeOptions> options)
    {
        _rpc = rpc;
        _options = options.Value;
        _reads = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentReads));
    }

    public async Task<WorkflowOutcomeListPage> ListPageAsync(string @namespace, string query, byte[]? pageToken, CancellationToken ct)
    {
        var request = new ListWorkflowExecutionsRequest
        {
            Namespace = @namespace,
            PageSize = _options.ListPageSize,
            Query = query,
            NextPageToken = pageToken is null ? ByteString.Empty : ByteString.CopyFrom(pageToken),
        };
        var response = await GatedAsync(() => _rpc.ListAsync(request, ct), ct);
        var next = response.NextPageToken is { IsEmpty: false } t ? t.ToByteArray() : null;
        if (next is not null && pageToken is not null && next.AsSpan().SequenceEqual(pageToken))
            throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.RepeatedToken, "repeated_list_token");

        var runs = response.Executions.Select(e => new WorkflowOutcomeListedRun(
            WorkflowId: e.Execution?.WorkflowId ?? "",
            RunId: e.Execution?.RunId ?? "",
            WorkflowType: e.Type?.Name ?? "",
            Status: StatusName(e.Status),
            StartTime: e.StartTime?.ToDateTime(),
            CloseTime: e.CloseTime?.ToDateTime(),
            ParentWorkflowId: NullIfEmpty(e.ParentExecution?.WorkflowId),
            ParentRunId: NullIfEmpty(e.ParentExecution?.RunId))).ToArray();
        return new WorkflowOutcomeListPage(runs, next);
    }

    public async Task<WorkflowOutcomeDescribed?> DescribeAsync(string @namespace, string workflowId, string runId, CancellationToken ct)
    {
        try
        {
            var response = await GatedAsync(() => _rpc.DescribeAsync(new DescribeWorkflowExecutionRequest
            {
                Namespace = @namespace,
                Execution = new ApiWorkflowExecution { WorkflowId = workflowId, RunId = runId },
            }, ct), ct);
            var info = response.WorkflowExecutionInfo
                ?? throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unsupported, "describe_without_info");
            // Exact identity only: a Describe that answers for another run is not this run's state.
            if (info.Execution?.RunId != runId || info.Execution?.WorkflowId != workflowId)
                throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unsupported, "describe_identity_mismatch");
            return new WorkflowOutcomeDescribed(workflowId, runId, info.Type?.Name ?? "", StatusName(info.Status),
                info.StartTime?.ToDateTime(), info.CloseTime?.ToDateTime());
        }
        catch (WorkflowOutcomeReadException ex) when (ex.Kind == WorkflowOutcomeReadFailure.NotFound)
        {
            return null;
        }
    }

    public Task<WorkflowOutcomeHistoryProgress> ReadHistorySliceAsync(
        string @namespace, string workflowId, string runId, WorkflowOutcomeHistoryProgress? progress, CancellationToken ct) =>
        ReadPagesAsync(@namespace, workflowId, runId, progress, _options.MaxHistoryPagesPerSlice, ct);

    public async Task<WorkflowOutcomeRunFacts> ReadStartedAsync(string @namespace, string workflowId, string runId, CancellationToken ct)
    {
        var progress = await ReadPagesAsync(@namespace, workflowId, runId, null, 1, ct);
        return progress.Facts();
    }

    private async Task<WorkflowOutcomeHistoryProgress> ReadPagesAsync(
        string @namespace, string workflowId, string runId, WorkflowOutcomeHistoryProgress? progress, int maxPages, CancellationToken ct)
    {
        progress ??= new WorkflowOutcomeHistoryProgress(@namespace, workflowId, runId);
        if (progress.Namespace != @namespace || progress.WorkflowId != workflowId || progress.RunId != runId)
            throw new ArgumentException("progress belongs to another run", nameof(progress));
        if (progress.Complete) return progress;

        for (var page = 0; page < maxPages; page++)
        {
            var token = progress.NextPageToken;
            var response = await GatedAsync(() => _rpc.GetHistoryAsync(new GetWorkflowExecutionHistoryRequest
            {
                Namespace = @namespace,
                Execution = new ApiWorkflowExecution { WorkflowId = workflowId, RunId = runId },
                MaximumPageSize = _options.HistoryPageSize,
                NextPageToken = token is null ? ByteString.Empty : ByteString.CopyFrom(token),
                // Archival is read only where it is already configured and authorized; the
                // observer itself never fetches archived history.
                SkipArchival = true,
            }, ct), ct);

            if (response.Archived)
                throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.NotFound, "history_archived");
            if (response.History is null && response.RawHistory.Count > 0)
                throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unsupported, "raw_history");

            foreach (var e in response.History?.Events ?? [])
                progress.Builder.Add(e);
            progress.PagesRead++;

            var next = response.NextPageToken is { IsEmpty: false } t ? t.ToByteArray() : null;
            if (next is null)
            {
                progress.NextPageToken = null;
                progress.Complete = true;
                if (progress.Builder.EventCount == 0)
                    throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.NotFound, "history_empty");
                return progress;
            }
            if (token is not null && next.AsSpan().SequenceEqual(token))
                throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.RepeatedToken, "repeated_history_token");
            progress.NextPageToken = next;
        }
        return progress;
    }

    public async Task<WorkflowOutcomeNamespaceInfo> DescribeNamespaceAsync(string @namespace, CancellationToken ct)
    {
        var response = await GatedAsync(() => _rpc.DescribeNamespaceAsync(new DescribeNamespaceRequest { Namespace = @namespace }, ct), ct);
        var config = response.Config
            ?? throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unsupported, "namespace_without_config");
        var retention = config.WorkflowExecutionRetentionTtl?.ToTimeSpan() ?? TimeSpan.Zero;
        if (retention <= TimeSpan.Zero)
            throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unsupported, "namespace_retention_unknown");
        return new WorkflowOutcomeNamespaceInfo((long)retention.TotalSeconds,
            config.HistoryArchivalState.ToString(), config.VisibilityArchivalState.ToString());
    }

    private async Task<T> GatedAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        await _reads.WaitAsync(ct);
        try { return await call(); }
        finally { _reads.Release(); }
    }

    internal static string StatusName(WorkflowExecutionStatus status) => status switch
    {
        WorkflowExecutionStatus.Running => "Running",
        WorkflowExecutionStatus.Completed => "Completed",
        WorkflowExecutionStatus.Failed => "Failed",
        WorkflowExecutionStatus.Canceled => "Canceled",
        WorkflowExecutionStatus.Terminated => "Terminated",
        WorkflowExecutionStatus.ContinuedAsNew => "ContinuedAsNew",
        WorkflowExecutionStatus.TimedOut => "TimedOut",
        WorkflowExecutionStatus.Paused => "Paused",
        _ => "Unspecified",
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// Incrementally extracts <see cref="WorkflowOutcomeRunFacts"/> from raw history events, page by
/// page, keeping only typed facts (never instruction text). Payload contents are evidence data,
/// never instructions.
/// </summary>
public sealed class WorkflowOutcomeFactsBuilder(string @namespace, string workflowId, string runId)
{
    /// <summary>Same value as <c>Fleet.Temporal.Activities.DelegateToAgentActivity.ActivityName</c>.</summary>
    public const string DelegateActivityName = "DelegateToAgent";

    /// <summary>The UWE engine's first activity (<c>UniversalWorkflow.RunAsync</c>).</summary>
    public const string LoadDefinitionActivityName = "LoadWorkflowDefinition";

    private sealed class DelegateState
    {
        public long ScheduledEventId;
        public string? Target;
        public string? TaskId;
        public bool ArgumentsUnsupported;
        public int Attempt = 1;
        public string Outcome = "open";
        public long? CloseEventId;
        public string? CloseEventType;
        public WorkflowOutcomePayload? Result;
        public string? ResultText;
        public string? ResultStatus;
        public string? FailureType;
        public string? FailureMessage;
    }

    private readonly List<DelegateState> _delegates = [];
    private readonly Dictionary<long, DelegateState> _delegatesByScheduled = [];
    private readonly List<WorkflowOutcomeSignal> _signals = [];
    private readonly List<long> _cancelRequests = [];
    private readonly List<string> _unsupported = [];
    private long _lastEventId;
    private bool _gap;
    private string _workflowType = "";
    private DateTime? _startTime;
    private WorkflowOutcomePayload? _input;
    private string? _firstExecutionRunId, _continuedRunId, _initiator, _cron, _parentNs, _parentWf, _parentRun;
    private int _attempt;
    private long? _loadScheduledId;
    private bool _loadResolved;
    private string? _definitionName, _definitionHash;
    private int? _definitionVersion;
    private bool _definitionUnsupported;
    private int _workflowTasksCompleted;
    private string? _workerBuildId;
    private bool _workerBuildIdUnverified;
    private WorkflowOutcomeTerminal? _terminal;

    public long EventCount { get; private set; }

    public void Add(HistoryEvent e)
    {
        EventCount++;
        // Contiguous ids from 1: a gap means the read is not the whole history.
        if (e.EventId != _lastEventId + 1) _gap = true;
        _lastEventId = e.EventId;

        switch (e.EventType)
        {
            case EventType.WorkflowExecutionStarted when e.WorkflowExecutionStartedEventAttributes is { } a:
                _workflowType = a.WorkflowType?.Name ?? "";
                _startTime = e.EventTime?.ToDateTime();
                if (a.Input?.Payloads_ is { Count: > 0 } input) _input = Track(Decode(input[0]), e.EventId, "input");
                _firstExecutionRunId = Empty(a.FirstExecutionRunId);
                _continuedRunId = Empty(a.ContinuedExecutionRunId);
                _initiator = _continuedRunId is null || a.Initiator == ContinueAsNewInitiator.Unspecified ? null : a.Initiator.ToString();
                _cron = Empty(a.CronSchedule);
                _attempt = a.Attempt;
                _parentNs = Empty(a.ParentWorkflowNamespace);
                _parentWf = Empty(a.ParentWorkflowExecution?.WorkflowId);
                _parentRun = Empty(a.ParentWorkflowExecution?.RunId);
                break;

            case EventType.ActivityTaskScheduled when e.ActivityTaskScheduledEventAttributes is { } a:
                var name = a.ActivityType?.Name;
                if (name == LoadDefinitionActivityName && _loadScheduledId is null)
                {
                    _loadScheduledId = e.EventId;
                }
                else if (name == DelegateActivityName)
                {
                    var d = new DelegateState { ScheduledEventId = e.EventId };
                    var args = a.Input?.Payloads_;
                    d.Target = StringArg(args, 0, ref d.ArgumentsUnsupported);
                    d.TaskId = StringArg(args, 2, ref d.ArgumentsUnsupported);
                    if (d.ArgumentsUnsupported) _unsupported.Add("event:" + e.EventId + ":input");
                    _delegates.Add(d);
                    _delegatesByScheduled[e.EventId] = d;
                }
                break;

            case EventType.ActivityTaskStarted when e.ActivityTaskStartedEventAttributes is { } a:
                if (_delegatesByScheduled.TryGetValue(a.ScheduledEventId, out var started) && a.Attempt > 0)
                    started.Attempt = a.Attempt;
                break;

            case EventType.ActivityTaskCompleted when e.ActivityTaskCompletedEventAttributes is { } a:
                if (a.ScheduledEventId == _loadScheduledId && !_loadResolved)
                {
                    _loadResolved = true;
                    ReadDefinition(a.Result?.Payloads_, e.EventId);
                }
                else if (_delegatesByScheduled.TryGetValue(a.ScheduledEventId, out var done))
                {
                    Close(done, "completed", e);
                    if (a.Result?.Payloads_ is { Count: > 0 } result)
                    {
                        done.Result = Track(Decode(result[0]), e.EventId, "result");
                        if (done.Result is { Supported: true, Json: { } json }) ReadAgentResult(done, json);
                    }
                }
                break;

            case EventType.ActivityTaskFailed when e.ActivityTaskFailedEventAttributes is { } a:
                if (_delegatesByScheduled.TryGetValue(a.ScheduledEventId, out var failed))
                {
                    Close(failed, "failed", e);
                    failed.FailureType = FailureType(a.Failure);
                    failed.FailureMessage = a.Failure?.Message;
                }
                break;

            case EventType.ActivityTaskTimedOut when e.ActivityTaskTimedOutEventAttributes is { } a:
                if (_delegatesByScheduled.TryGetValue(a.ScheduledEventId, out var timedOut))
                {
                    Close(timedOut, "timed_out", e);
                    timedOut.FailureType = FailureType(a.Failure);
                    timedOut.FailureMessage = a.Failure?.Message;
                }
                break;

            case EventType.ActivityTaskCanceled when e.ActivityTaskCanceledEventAttributes is { } a:
                if (_delegatesByScheduled.TryGetValue(a.ScheduledEventId, out var canceled))
                    Close(canceled, "canceled", e);
                break;

            case EventType.WorkflowExecutionSignaled when e.WorkflowExecutionSignaledEventAttributes is { } a:
                string? inputSha = null;
                if (a.Input?.Payloads_ is { Count: > 0 } signalInput)
                    inputSha = WorkflowOutcomeHashes.Sha256Hex(signalInput[0].Data.Span);
                _signals.Add(new WorkflowOutcomeSignal(e.EventId, a.SignalName ?? "", inputSha));
                break;

            case EventType.WorkflowExecutionCancelRequested:
                _cancelRequests.Add(e.EventId);
                break;

            case EventType.WorkflowTaskCompleted when e.WorkflowTaskCompletedEventAttributes is { } a:
                _workflowTasksCompleted++;
#pragma warning disable CS0618 // the field the bridge's (unversioned) worker build id is recorded in
                var buildId = a.WorkerVersion?.BuildId;
#pragma warning restore CS0618
                if (string.IsNullOrEmpty(buildId) || (_workerBuildId is not null && _workerBuildId != buildId))
                    _workerBuildIdUnverified = true;
                _workerBuildId ??= Empty(buildId);
                break;

            case EventType.WorkflowExecutionCompleted when e.WorkflowExecutionCompletedEventAttributes is { } a:
                SetTerminal(e, "Completed", a, null, null, a.NewExecutionRunId, a.Result?.Payloads_);
                break;
            case EventType.WorkflowExecutionFailed when e.WorkflowExecutionFailedEventAttributes is { } a:
                SetTerminal(e, "Failed", a, a.Failure, a.RetryState, a.NewExecutionRunId, null);
                break;
            case EventType.WorkflowExecutionTimedOut when e.WorkflowExecutionTimedOutEventAttributes is { } a:
                SetTerminal(e, "TimedOut", a, null, a.RetryState, a.NewExecutionRunId, null);
                break;
            case EventType.WorkflowExecutionCanceled when e.WorkflowExecutionCanceledEventAttributes is { } a:
                SetTerminal(e, "Canceled", a, null, null, null, null);
                break;
            case EventType.WorkflowExecutionTerminated when e.WorkflowExecutionTerminatedEventAttributes is { } a:
                SetTerminal(e, "Terminated", a, null, null, null, null);
                break;
            case EventType.WorkflowExecutionContinuedAsNew when e.WorkflowExecutionContinuedAsNewEventAttributes is { } a:
                SetTerminal(e, "ContinuedAsNew", a, null, null, a.NewExecutionRunId, null);
                break;
        }
    }

    public WorkflowOutcomeRunFacts Build(bool pagesComplete) => new()
    {
        Namespace = @namespace,
        WorkflowId = workflowId,
        RunId = runId,
        WorkflowType = _workflowType,
        StartTime = _startTime,
        Input = _input,
        FirstExecutionRunId = _firstExecutionRunId,
        ContinuedExecutionRunId = _continuedRunId,
        Initiator = _initiator,
        CronSchedule = _cron,
        Attempt = _attempt,
        ParentNamespace = _parentNs,
        ParentWorkflowId = _parentWf,
        ParentRunId = _parentRun,
        DefinitionName = _definitionName,
        DefinitionVersion = _definitionVersion,
        DefinitionHash = _definitionHash,
        DefinitionUnsupported = _definitionUnsupported,
        DefinitionScheduled = _loadScheduledId is not null,
        WorkerBuildId = _workflowTasksCompleted > 0 && !_workerBuildIdUnverified ? _workerBuildId : null,
        Delegates = _delegates.Select(d => new WorkflowOutcomeDelegate(d.ScheduledEventId, d.Target, d.TaskId, d.Attempt,
            d.Outcome, d.CloseEventId, d.CloseEventType, d.Result, d.ResultText, d.ResultStatus, d.FailureType,
            d.FailureMessage, d.ArgumentsUnsupported)).ToArray(),
        Signals = _signals.ToArray(),
        CancelRequestEventIds = _cancelRequests.ToArray(),
        Terminal = _terminal,
        // The started event must be event 1 and every id contiguous; otherwise this is partial.
        HistoryComplete = pagesComplete && !_gap && EventCount > 0 && _workflowType.Length > 0,
        EventCount = EventCount,
        UnsupportedPayloadRefs = _unsupported.ToArray(),
    };

    /// <summary>Builds facts from an in-memory event list (tests and single-page fixtures).</summary>
    public static WorkflowOutcomeRunFacts FromEvents(string @namespace, string workflowId, string runId, IEnumerable<HistoryEvent> events)
    {
        var builder = new WorkflowOutcomeFactsBuilder(@namespace, workflowId, runId);
        foreach (var e in events) builder.Add(e);
        return builder.Build(true);
    }

    private void SetTerminal(HistoryEvent e, string status, IMessage attributes, Temporalio.Api.Failure.V1.Failure? failure,
        RetryState? retry, string? newRunId, Google.Protobuf.Collections.RepeatedField<Payload>? result)
    {
        WorkflowOutcomePayload? decoded = null;
        if (result is { Count: > 0 }) decoded = Track(Decode(result[0]), e.EventId, "result");
        _terminal = new WorkflowOutcomeTerminal(
            EventId: e.EventId,
            EventType: e.EventType.ToString(),
            Status: status,
            EventTime: e.EventTime?.ToDateTime(),
            AttributesJson: JsonFormatter.Default.Format(attributes),
            EventSha256: WorkflowOutcomeHashes.Sha256Hex(e.ToByteArray()),
            FailureType: failure is null ? null : FailureType(failure),
            FailureMessage: failure?.Message,
            RetryState: retry is null or RetryState.Unspecified ? null : retry.ToString(),
            NewExecutionRunId: Empty(newRunId),
            Result: decoded,
            HasFailure: failure is not null);
    }

    private void ReadDefinition(Google.Protobuf.Collections.RepeatedField<Payload>? payloads, long eventId)
    {
        if (payloads is not { Count: > 0 }) { _definitionUnsupported = true; return; }
        var payload = Decode(payloads[0]);
        if (!payload.Supported || payload.Json is null) { _definitionUnsupported = true; _unsupported.Add("event:" + eventId + ":definition"); return; }
        // The hash is over the exact recorded data bytes, so a pinned classifier matches only
        // the definition snapshot this run actually loaded.
        _definitionHash = payload.Sha256;
        try
        {
            using var doc = JsonDocument.Parse(payload.Json);
            if (EpicJson.Property(doc.RootElement, "Name") is { ValueKind: JsonValueKind.String } n) _definitionName = n.GetString();
            if (EpicJson.Property(doc.RootElement, "Version") is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var version))
                _definitionVersion = version;
        }
        catch (JsonException) { _definitionUnsupported = true; }
    }

    private static void ReadAgentResult(DelegateState d, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (EpicJson.Property(doc.RootElement, "Text") is { ValueKind: JsonValueKind.String } t) d.ResultText = t.GetString();
            if (EpicJson.Property(doc.RootElement, "Status") is { ValueKind: JsonValueKind.String } s) d.ResultStatus = s.GetString();
        }
        catch (JsonException) { /* Decode already validated; unreachable for valid JSON. */ }
    }

    private static void Close(DelegateState d, string outcome, HistoryEvent e)
    {
        d.Outcome = outcome;
        d.CloseEventId = e.EventId;
        d.CloseEventType = e.EventType.ToString();
    }

    private WorkflowOutcomePayload Track(WorkflowOutcomePayload payload, long eventId, string field)
    {
        if (!payload.Supported) _unsupported.Add("event:" + eventId + ":" + field);
        return payload;
    }

    private static string? StringArg(Google.Protobuf.Collections.RepeatedField<Payload>? args, int index, ref bool unsupported)
    {
        if (args is null || args.Count <= index) return null;
        var p = Decode(args[index]);
        if (!p.Supported) { unsupported = true; return null; }
        if (p.Json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(p.Json);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
        }
        catch (JsonException) { unsupported = true; return null; }
    }

    /// <summary>
    /// Decodes a payload. Only <c>json/plain</c>, <c>json/protobuf</c> and <c>binary/null</c> are
    /// readable; anything else (including codec-encrypted payloads) is kept as an unsupported
    /// hash-only fact.
    /// </summary>
    public static WorkflowOutcomePayload Decode(Payload payload)
    {
        var encoding = payload.Metadata.TryGetValue("encoding", out var bytes) ? bytes.ToStringUtf8() : "";
        var sha = WorkflowOutcomeHashes.Sha256Hex(payload.Data.Span);
        if (encoding == "binary/null") return new WorkflowOutcomePayload(encoding, sha, null, IsNull: true, Supported: true);
        if (encoding is not ("json/plain" or "json/protobuf")) return new WorkflowOutcomePayload(encoding, sha, null, false, false);
        var text = payload.Data.ToStringUtf8();
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            return new WorkflowOutcomePayload(encoding, sha, text, doc.RootElement.ValueKind == JsonValueKind.Null, true);
        }
        catch (JsonException)
        {
            return new WorkflowOutcomePayload(encoding, sha, null, false, false);
        }
    }

    private static string FailureType(Temporalio.Api.Failure.V1.Failure? failure) => failure switch
    {
        null => "unknown",
        { ApplicationFailureInfo: { } app } when !string.IsNullOrEmpty(app.Type) => app.Type,
        { TimeoutFailureInfo: { } timeout } => "timeout:" + timeout.TimeoutType,
        _ => failure.FailureInfoCase.ToString(),
    };

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
