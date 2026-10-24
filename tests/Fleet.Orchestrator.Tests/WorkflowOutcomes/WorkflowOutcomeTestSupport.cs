using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.Failure.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.Namespace.V1;
using Temporalio.Api.Workflow.V1;
using Temporalio.Api.WorkflowService.V1;
using ApiWorkflowExecution = Temporalio.Api.Common.V1.WorkflowExecution;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class OutcomeClock(DateTimeOffset now) : TimeProvider
{
    private long _ticks = now.UtcTicks;
    public DateTimeOffset Now
    {
        get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }
    public DateTime UtcNow => Now.UtcDateTime;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Owned fixtures under tests/Fleet.Orchestrator.Tests/WorkflowOutcomes/Fixtures (generic placeholders only).</summary>
internal static class OutcomeFixtures
{
    public const string SourceCommit = "8c7c9ddf87147a16556acc59483ad0a9b1e57676";

    public static string Dir
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
                directory = directory.Parent;
            if (directory is null) throw new InvalidOperationException("repository root (Fleet.sln) not found");
            return Path.Combine(directory.FullName, "tests", "Fleet.Orchestrator.Tests", "WorkflowOutcomes", "Fixtures");
        }
    }

    public static string ClassifiersPath => Path.Combine(Dir, "classifiers.fixture.json");
    public static string OwnershipPath => Path.Combine(Dir, "ownership.fixture.json");

    /// <summary>The exact recorded LoadWorkflowDefinition result payload bytes for a public seed type.</summary>
    public static byte[] Loaded(string workflowType) => File.ReadAllBytes(Path.Combine(Dir, "loaded", workflowType + ".json"));

    public static string LoadedHash(string workflowType) => WorkflowOutcomeHashes.Sha256Hex(Loaded(workflowType));

    public static readonly IReadOnlyList<(string Name, bool IsActive)> Projects =
        [("project-a", true), ("project-b", true), ("project-c", true), ("project-old", false)];

    public static WorkflowOutcomeClassifierPolicy Classifiers() => WorkflowOutcomeClassifierPolicy.Parse(File.ReadAllBytes(ClassifiersPath));
    public static WorkflowOutcomeOwnershipPolicy Ownership() => WorkflowOutcomeOwnershipPolicy.Parse(File.ReadAllBytes(OwnershipPath), Projects);
}

/// <summary>Builds raw Temporal history events exactly as the server records them.</summary>
internal sealed class HistoryBuilder
{
    private readonly List<HistoryEvent> _events = [];
    private DateTime _time;

    public string WorkflowId { get; }
    public string WorkflowType { get; }

    public HistoryBuilder(string workflowType, string workflowId, object? input = null, DateTime? start = null,
        Action<WorkflowExecutionStartedEventAttributes>? started = null)
    {
        WorkflowType = workflowType;
        WorkflowId = workflowId;
        _time = start ?? new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var attrs = new WorkflowExecutionStartedEventAttributes
        {
            WorkflowType = new WorkflowType { Name = workflowType },
            TaskQueue = new Temporalio.Api.TaskQueue.V1.TaskQueue { Name = "fleet" },
            Attempt = 1,
        };
        if (input is not null) attrs.Input = new Payloads { Payloads_ = { Json(input) } };
        started?.Invoke(attrs);
        Add(EventType.WorkflowExecutionStarted, e => e.WorkflowExecutionStartedEventAttributes = attrs);
    }

    public IReadOnlyList<HistoryEvent> Events => _events;

    /// <summary>The next event is recorded one second after <paramref name="time"/>.</summary>
    public HistoryBuilder At(DateTime time)
    {
        _time = time.AddSeconds(-1);
        return this;
    }
    public long NextId => _events.Count + 1;
    public DateTime Time => _time;

    public static Payload Json(object? value) => new()
    {
        Metadata = { ["encoding"] = ByteString.CopyFromUtf8("json/plain") },
        Data = ByteString.CopyFromUtf8(JsonSerializer.Serialize(value)),
    };

    public static Payload RawJson(string json) => new()
    {
        Metadata = { ["encoding"] = ByteString.CopyFromUtf8("json/plain") },
        Data = ByteString.CopyFromUtf8(json),
    };

    public static Payload RawBytes(byte[] data, string encoding = "json/plain") => new()
    {
        Metadata = { ["encoding"] = ByteString.CopyFromUtf8(encoding) },
        Data = ByteString.CopyFrom(data),
    };

    public static Payload Null() => new() { Metadata = { ["encoding"] = ByteString.CopyFromUtf8("binary/null") } };

    private HistoryEvent Add(EventType type, Action<HistoryEvent> set)
    {
        _time = _time.AddSeconds(1);
        var e = new HistoryEvent { EventId = NextId, EventType = type, EventTime = Timestamp.FromDateTime(_time) };
        set(e);
        _events.Add(e);
        return e;
    }

    public HistoryBuilder LoadDefinition(byte[] payloadBytes, string encoding = "json/plain")
    {
        var scheduled = Add(EventType.ActivityTaskScheduled, e => e.ActivityTaskScheduledEventAttributes = new ActivityTaskScheduledEventAttributes
        {
            ActivityId = "1", ActivityType = new ActivityType { Name = "LoadWorkflowDefinition" },
            Input = new Payloads { Payloads_ = { Json(WorkflowType) } },
        });
        Add(EventType.ActivityTaskStarted, e => e.ActivityTaskStartedEventAttributes = new ActivityTaskStartedEventAttributes { ScheduledEventId = scheduled.EventId, Attempt = 1 });
        Add(EventType.ActivityTaskCompleted, e => e.ActivityTaskCompletedEventAttributes = new ActivityTaskCompletedEventAttributes
        {
            ScheduledEventId = scheduled.EventId, Result = new Payloads { Payloads_ = { RawBytes(payloadBytes, encoding) } },
        });
        return this;
    }

    public HistoryBuilder LoadSeed(string workflowType) => LoadDefinition(OutcomeFixtures.Loaded(workflowType));

    /// <summary>A LoadWorkflowDefinition that was scheduled and started but never completed.</summary>
    public HistoryBuilder LoadDefinitionUnresolved()
    {
        var scheduled = Add(EventType.ActivityTaskScheduled, e => e.ActivityTaskScheduledEventAttributes = new ActivityTaskScheduledEventAttributes
        {
            ActivityId = "1", ActivityType = new ActivityType { Name = "LoadWorkflowDefinition" },
            Input = new Payloads { Payloads_ = { Json(WorkflowType) } },
        });
        Add(EventType.ActivityTaskStarted, e => e.ActivityTaskStartedEventAttributes = new ActivityTaskStartedEventAttributes { ScheduledEventId = scheduled.EventId, Attempt = 1 });
        return this;
    }

    /// <summary>A DelegateToAgent with the engine's exact argument shape and taskId <c>workflowId/step</c>.</summary>
    public long ScheduleDelegate(string step, string target, string? workflowId = null, Payload? taskIdPayload = null)
    {
        var scheduled = Add(EventType.ActivityTaskScheduled, e => e.ActivityTaskScheduledEventAttributes = new ActivityTaskScheduledEventAttributes
        {
            ActivityId = NextId.ToString(), ActivityType = new ActivityType { Name = "DelegateToAgent" },
            Input = new Payloads
            {
                Payloads_ =
                {
                    Json(target), Json("instruction text is never read"), taskIdPayload ?? Json((workflowId ?? WorkflowId) + "/" + step), Json(false), Json(0),
                },
            },
        });
        Add(EventType.ActivityTaskStarted, e => e.ActivityTaskStartedEventAttributes = new ActivityTaskStartedEventAttributes { ScheduledEventId = scheduled.EventId, Attempt = 1 });
        return scheduled.EventId;
    }

    public HistoryBuilder Delegate(string step, string target, string text, string status = "completed")
    {
        var scheduled = ScheduleDelegate(step, target);
        Add(EventType.ActivityTaskCompleted, e => e.ActivityTaskCompletedEventAttributes = new ActivityTaskCompletedEventAttributes
        {
            ScheduledEventId = scheduled, StartedEventId = scheduled + 1,
            Result = new Payloads { Payloads_ = { Json(new Dictionary<string, string> { ["Text"] = text, ["Status"] = status }) } },
        });
        return this;
    }

    public HistoryBuilder DelegateFailed(string step, string target, bool timedOut = false)
    {
        var scheduled = ScheduleDelegate(step, target);
        if (timedOut)
            Add(EventType.ActivityTaskTimedOut, e => e.ActivityTaskTimedOutEventAttributes = new ActivityTaskTimedOutEventAttributes
            {
                ScheduledEventId = scheduled, StartedEventId = scheduled + 1,
                Failure = new Failure { Message = "activity StartToClose timeout", TimeoutFailureInfo = new TimeoutFailureInfo { TimeoutType = TimeoutType.StartToClose } },
            });
        else
            Add(EventType.ActivityTaskFailed, e => e.ActivityTaskFailedEventAttributes = new ActivityTaskFailedEventAttributes
            {
                ScheduledEventId = scheduled, StartedEventId = scheduled + 1,
                Failure = new Failure { Message = "agent unavailable", ApplicationFailureInfo = new ApplicationFailureInfo { Type = "AgentUnavailable" } },
            });
        return this;
    }

    /// <summary>An open delegate: scheduled and started, never closed (worker loss / termination).</summary>
    public HistoryBuilder OpenDelegate(string step, string target)
    {
        ScheduleDelegate(step, target);
        return this;
    }

    public HistoryBuilder Signal(string name)
    {
        Add(EventType.WorkflowExecutionSignaled, e => e.WorkflowExecutionSignaledEventAttributes = new WorkflowExecutionSignaledEventAttributes
        {
            SignalName = name, Input = new Payloads { Payloads_ = { Json(new { Decision = "rejected" }) } },
        });
        return this;
    }

    public HistoryBuilder CancelRequested()
    {
        Add(EventType.WorkflowExecutionCancelRequested, e => e.WorkflowExecutionCancelRequestedEventAttributes = new WorkflowExecutionCancelRequestedEventAttributes
        {
            Cause = "operator asked for it", Identity = "dashboard",
        });
        return this;
    }

    /// <summary>Workflow-task bookkeeping events that make a history long without adding facts.</summary>
    public HistoryBuilder Filler(int count)
    {
        for (var i = 0; i < count; i++)
            Add(EventType.WorkflowTaskScheduled, e => e.WorkflowTaskScheduledEventAttributes = new WorkflowTaskScheduledEventAttributes
            {
                TaskQueue = new Temporalio.Api.TaskQueue.V1.TaskQueue { Name = "fleet" }, Attempt = 1,
            });
        return this;
    }

    public HistoryBuilder Completed(Payload? result, string? newRunId = null)
    {
        var attrs = new WorkflowExecutionCompletedEventAttributes { NewExecutionRunId = newRunId ?? "" };
        if (result is not null) attrs.Result = new Payloads { Payloads_ = { result } };
        Add(EventType.WorkflowExecutionCompleted, e => e.WorkflowExecutionCompletedEventAttributes = attrs);
        return this;
    }

    public HistoryBuilder CompletedWith(object? value) => Completed(value is null ? null : Json(value));

    public HistoryBuilder Failed(string message = "boom", RetryState retry = RetryState.RetryPolicyNotSet, string? newRunId = null)
    {
        Add(EventType.WorkflowExecutionFailed, e => e.WorkflowExecutionFailedEventAttributes = new WorkflowExecutionFailedEventAttributes
        {
            Failure = new Failure { Message = message, ApplicationFailureInfo = new ApplicationFailureInfo { Type = "InfraError" } },
            RetryState = retry, NewExecutionRunId = newRunId ?? "",
        });
        return this;
    }

    public HistoryBuilder TimedOut(RetryState retry = RetryState.Timeout, string? newRunId = null)
    {
        Add(EventType.WorkflowExecutionTimedOut, e => e.WorkflowExecutionTimedOutEventAttributes = new WorkflowExecutionTimedOutEventAttributes
        {
            RetryState = retry, NewExecutionRunId = newRunId ?? "",
        });
        return this;
    }

    public HistoryBuilder Canceled()
    {
        Add(EventType.WorkflowExecutionCanceled, e => e.WorkflowExecutionCanceledEventAttributes = new WorkflowExecutionCanceledEventAttributes());
        return this;
    }

    public HistoryBuilder Terminated(string reason = "failure: task crashed, terminating", string identity = "operator@dashboard")
    {
        Add(EventType.WorkflowExecutionTerminated, e => e.WorkflowExecutionTerminatedEventAttributes = new WorkflowExecutionTerminatedEventAttributes
        {
            Reason = reason, Identity = identity,
        });
        return this;
    }

    public HistoryBuilder ContinuedAsNew(string newRunId)
    {
        Add(EventType.WorkflowExecutionContinuedAsNew, e => e.WorkflowExecutionContinuedAsNewEventAttributes = new WorkflowExecutionContinuedAsNewEventAttributes
        {
            NewExecutionRunId = newRunId, WorkflowType = new WorkflowType { Name = WorkflowType },
        });
        return this;
    }

    public WorkflowOutcomeRunFacts Facts(string ns = "namespace-a", string runId = "run-1") =>
        WorkflowOutcomeFactsBuilder.FromEvents(ns, WorkflowId, runId, _events);
}

/// <summary>One fake execution in the fake Temporal.</summary>
internal sealed class FakeExecution
{
    public required string Namespace { get; init; }
    public required string WorkflowId { get; init; }
    public required string RunId { get; init; }
    public required string WorkflowType { get; init; }
    public required DateTime StartTime { get; set; }
    public string Status { get; set; } = "Running";
    public DateTime? CloseTime { get; set; }
    public string? ParentWorkflowId { get; set; }
    public string? ParentRunId { get; set; }
    public List<HistoryEvent> Events { get; set; } = [];

    /// <summary>Visible in Visibility from this instant (eventual consistency).</summary>
    public DateTime VisibleAt { get; set; } = DateTime.MinValue;

    /// <summary>History gone (retention/deleted) while Visibility may still list it.</summary>
    public bool HistoryDeleted { get; set; }
    public bool Archived { get; set; }
}

/// <summary>
/// In-memory WorkflowService for the observer: StartTime query evaluation, opaque offset tokens,
/// history paging and fault injection. It exposes only read RPCs — the observer cannot start,
/// signal or cancel anything through it.
/// </summary>
internal sealed class FakeTemporal(OutcomeClock clock) : IWorkflowOutcomeRpc
{
    private readonly object _gate = new();
    private readonly List<FakeExecution> _runs = [];
    public Dictionary<string, long> RetentionSeconds { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Unavailable { get; } = new(StringComparer.Ordinal);

    /// <summary>Fail the n-th (1-based) list call of a namespace once, per namespace.</summary>
    public Dictionary<string, int> FailListCall { get; } = new(StringComparer.Ordinal);
    public bool RepeatNextListToken { get; set; }
    public bool RejectNextListToken { get; set; }
    public int EmptyPagesBeforeData { get; set; }
    public int ListCalls, HistoryCalls, DescribeCalls, NamespaceCalls;
    private readonly Dictionary<string, int> _listCallsByNs = new(StringComparer.Ordinal);
    public List<string> Queries { get; } = [];

    /// <summary>The page token of every list call ("" for the first page).</summary>
    public List<string> ListTokens { get; } = [];

    /// <summary>Clock time of every RPC attempt per namespace (backoff assertions).</summary>
    public List<(string Namespace, DateTime At)> Attempts { get; } = [];

    /// <summary>Runs inside every list call (after fault checks), e.g. to fail the DB mid-pass.</summary>
    public Action<ListWorkflowExecutionsRequest>? OnList { get; set; }

    /// <summary>Temporal deleted the run (retention): gone from Visibility and history.</summary>
    public void Remove(FakeExecution run)
    {
        lock (_gate) _runs.Remove(run);
    }

    public FakeExecution Add(string ns, string runId, HistoryBuilder history, string? status = null, DateTime? closeTime = null,
        string? parentWorkflowId = null, string? parentRunId = null)
    {
        var started = history.Events[0].EventTime.ToDateTime();
        var last = history.Events[^1];
        var run = new FakeExecution
        {
            Namespace = ns, WorkflowId = history.WorkflowId, RunId = runId, WorkflowType = history.WorkflowType,
            StartTime = started, Events = [.. history.Events], ParentWorkflowId = parentWorkflowId, ParentRunId = parentRunId,
            Status = status ?? StatusOf(last),
            CloseTime = closeTime ?? (StatusOf(last) == "Running" ? null : last.EventTime.ToDateTime()),
        };
        lock (_gate) _runs.Add(run);
        return run;
    }

    /// <summary>Appends closing events to a running execution (the run closes now).</summary>
    public void Close(FakeExecution run, HistoryBuilder history)
    {
        lock (_gate)
        {
            run.Events = [.. history.Events];
            run.Status = StatusOf(history.Events[^1]);
            run.CloseTime = history.Events[^1].EventTime.ToDateTime();
        }
    }

    public IReadOnlyList<FakeExecution> Runs { get { lock (_gate) return _runs.ToArray(); } }

    public static string StatusOf(HistoryEvent e) => e.EventType switch
    {
        EventType.WorkflowExecutionCompleted => "Completed",
        EventType.WorkflowExecutionFailed => "Failed",
        EventType.WorkflowExecutionTimedOut => "TimedOut",
        EventType.WorkflowExecutionCanceled => "Canceled",
        EventType.WorkflowExecutionTerminated => "Terminated",
        EventType.WorkflowExecutionContinuedAsNew => "ContinuedAsNew",
        _ => "Running",
    };

    private static readonly Regex Lower = new("StartTime >= \"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex Upper = new("StartTime < \"([^\"]+)\"", RegexOptions.CultureInvariant);

    private void Check(string ns)
    {
        Attempts.Add((ns, clock.UtcNow));
        if (Unavailable.Contains(ns)) throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unavailable, "rpc_unavailable");
    }

    public Task<ListWorkflowExecutionsResponse> ListAsync(ListWorkflowExecutionsRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            ListCalls++;
            Queries.Add(request.Query);
            ListTokens.Add(request.NextPageToken.ToStringUtf8());
            Check(request.Namespace);
            var n = _listCallsByNs[request.Namespace] = _listCallsByNs.GetValueOrDefault(request.Namespace) + 1;
            if (FailListCall.TryGetValue(request.Namespace, out var failAt) && failAt == n)
            {
                FailListCall.Remove(request.Namespace);
                throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unavailable, "rpc_unavailable");
            }
            if (!request.NextPageToken.IsEmpty && RejectNextListToken)
            {
                RejectNextListToken = false;
                throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.InvalidArgument, "invalid_argument");
            }
            if (!request.NextPageToken.IsEmpty && RepeatNextListToken)
            {
                RepeatNextListToken = false;
                return Task.FromResult(new ListWorkflowExecutionsResponse { NextPageToken = request.NextPageToken });
            }

            OnList?.Invoke(request);
            var token = request.NextPageToken.IsEmpty ? "0:0" : request.NextPageToken.ToStringUtf8();
            var parts = token.Split(':');
            var offset = int.Parse(parts[0]);
            var empties = int.Parse(parts[1]);
            if (empties < EmptyPagesBeforeData)
                return Task.FromResult(new ListWorkflowExecutionsResponse { NextPageToken = ByteString.CopyFromUtf8(offset + ":" + (empties + 1)) });

            var lower = Lower.Match(request.Query) is { Success: true } lm ? DateTime.Parse(lm.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal) : (DateTime?)null;
            var upper = Upper.Match(request.Query) is { Success: true } um ? DateTime.Parse(um.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal) : (DateTime?)null;
            var now = clock.UtcNow;
            var matching = _runs.Where(r => r.Namespace == request.Namespace && r.VisibleAt <= now
                    && (lower is null || r.StartTime >= lower) && (upper is null || r.StartTime < upper))
                .OrderByDescending(r => r.StartTime).ThenBy(r => r.RunId, StringComparer.Ordinal).ToList();
            var page = matching.Skip(offset).Take(request.PageSize).ToList();
            var response = new ListWorkflowExecutionsResponse();
            response.Executions.AddRange(page.Select(Info));
            if (offset + page.Count < matching.Count)
                response.NextPageToken = ByteString.CopyFromUtf8((offset + page.Count) + ":" + empties);
            return Task.FromResult(response);
        }
    }

    private static WorkflowExecutionInfo Info(FakeExecution r)
    {
        var info = new WorkflowExecutionInfo
        {
            Execution = new ApiWorkflowExecution { WorkflowId = r.WorkflowId, RunId = r.RunId },
            Type = new WorkflowType { Name = r.WorkflowType },
            StartTime = Timestamp.FromDateTime(DateTime.SpecifyKind(r.StartTime, DateTimeKind.Utc)),
            Status = System.Enum.Parse<WorkflowExecutionStatus>(r.Status),
        };
        if (r.CloseTime is { } close) info.CloseTime = Timestamp.FromDateTime(DateTime.SpecifyKind(close, DateTimeKind.Utc));
        if (r.ParentWorkflowId is not null) info.ParentExecution = new ApiWorkflowExecution { WorkflowId = r.ParentWorkflowId, RunId = r.ParentRunId ?? "" };
        return info;
    }

    public Func<GetWorkflowExecutionHistoryRequest, int, Exception?>? HistoryFault { get; set; }

    /// <summary>Real-time delay per history call, outside the fake's lock, so concurrency is observable.</summary>
    public TimeSpan HistoryDelay { get; set; }
    private int _inFlight;
    public int MaxInFlight;

    public async Task<GetWorkflowExecutionHistoryResponse> GetHistoryAsync(GetWorkflowExecutionHistoryRequest request, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = Volatile.Read(ref MaxInFlight)) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
        try
        {
            if (HistoryDelay > TimeSpan.Zero) await Task.Delay(HistoryDelay, ct);
            return await GetHistoryLockedAsync(request);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private Task<GetWorkflowExecutionHistoryResponse> GetHistoryLockedAsync(GetWorkflowExecutionHistoryRequest request)
    {
        lock (_gate)
        {
            HistoryCalls++;
            Check(request.Namespace);
            var run = _runs.FirstOrDefault(r => r.Namespace == request.Namespace && r.WorkflowId == request.Execution.WorkflowId && r.RunId == request.Execution.RunId);
            if (run is null || run.HistoryDeleted)
                throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.NotFound, "not_found");
            if (run.Archived) return Task.FromResult(new GetWorkflowExecutionHistoryResponse { Archived = true });
            var offset = request.NextPageToken.IsEmpty ? 0 : int.Parse(request.NextPageToken.ToStringUtf8());
            if (HistoryFault?.Invoke(request, offset) is { } fault) throw fault;
            var size = request.MaximumPageSize <= 0 ? 1000 : request.MaximumPageSize;
            var page = run.Events.Skip(offset).Take(size).ToList();
            var response = new GetWorkflowExecutionHistoryResponse { History = new History() };
            response.History.Events.AddRange(page.Select(e => e.Clone()));
            if (offset + page.Count < run.Events.Count)
                response.NextPageToken = ByteString.CopyFromUtf8((offset + page.Count).ToString());
            return Task.FromResult(response);
        }
    }

    public Task<DescribeWorkflowExecutionResponse> DescribeAsync(DescribeWorkflowExecutionRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            DescribeCalls++;
            Check(request.Namespace);
            var run = _runs.FirstOrDefault(r => r.Namespace == request.Namespace && r.WorkflowId == request.Execution.WorkflowId && r.RunId == request.Execution.RunId);
            if (run is null || run.HistoryDeleted)
                throw new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.NotFound, "not_found");
            return Task.FromResult(new DescribeWorkflowExecutionResponse { WorkflowExecutionInfo = Info(run) });
        }
    }

    public Task<DescribeNamespaceResponse> DescribeNamespaceAsync(DescribeNamespaceRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            NamespaceCalls++;
            Check(request.Namespace);
            return Task.FromResult(new DescribeNamespaceResponse
            {
                NamespaceInfo = new NamespaceInfo { Name = request.Namespace },
                Config = new NamespaceConfig
                {
                    WorkflowExecutionRetentionTtl = Duration.FromTimeSpan(TimeSpan.FromSeconds(RetentionSeconds.GetValueOrDefault(request.Namespace, 7 * 86400))),
                    HistoryArchivalState = ArchivalState.Disabled,
                    VisibilityArchivalState = ArchivalState.Disabled,
                },
            });
        }
    }
}

/// <summary>Reversible stand-in for data protection; <see cref="Fail"/> simulates a missing/wrong key.</summary>
internal sealed class FakeProtector : IWorkflowOutcomePayloadProtector
{
    public bool Fail { get; set; }
    public int Protected;

    public byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext)
    {
        if (Fail) throw new CryptographicException("key unavailable");
        Interlocked.Increment(ref Protected);
        var bytes = plaintext.ToArray();
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5a;
        return [.. Encoding.ASCII.GetBytes("enc1:"), .. bytes];
    }

    public byte[] Unprotect(string purpose, byte[] ciphertext)
    {
        if (Fail) throw new CryptographicException("key unavailable");
        var bytes = ciphertext[5..];
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5a;
        return bytes;
    }
}

/// <summary>Makes every DB command fail while <see cref="Down"/> is set (MySQL outage).</summary>
internal sealed class DbOutage : DbCommandInterceptor
{
    public volatile bool Down = false;

    private void Throw()
    {
        if (Down) throw new InvalidOperationException("database unavailable (simulated)");
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Throw(); return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Throw(); return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken); }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Throw(); return base.ScalarExecutingAsync(command, eventData, result, cancellationToken); }
}

/// <summary>Captures every log line (assertions on structured, payload-free logging).</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Category, string Message)> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? "" : " | " + exception)));
    }
}

/// <summary>
/// The observer wired exactly as AddWorkflowOutcomeObserver registers it, over a SQLite
/// in-memory orchestrator DB, a fake Temporal and a manual clock.
/// </summary>
internal sealed class ObserverHarness : IAsyncDisposable
{
    public const string Coordinator = "coordinator";

    public SqliteConnection Connection { get; }
    public OutcomeClock Clock { get; }
    public FakeTemporal Temporal { get; }
    public FakeProtector Protector { get; } = new();
    public DbOutage Outage { get; } = new();
    public WorkflowOutcomeOptions Options { get; }
    public CapturedLogs Logs { get; } = new();

    /// <summary>Replaces the fake RPC seam (the real-Temporal test uses the production one).</summary>
    public IWorkflowOutcomeRpc? RpcOverride { get; set; }
    public ServiceProvider Services { get; private set; } = null!;
    public WorkflowOutcomeReconciler Reconciler { get; private set; } = null!;

    private ObserverHarness(SqliteConnection connection, OutcomeClock clock, WorkflowOutcomeOptions options)
    {
        Connection = connection;
        Clock = clock;
        Temporal = new FakeTemporal(clock);
        Options = options;
    }

    public static WorkflowOutcomeOptions DefaultOptions(params string[] namespaces) => new()
    {
        Enabled = true,
        Namespaces = string.Join(',', namespaces.Length == 0 ? ["namespace-a", "namespace-b", "namespace-c"] : namespaces),
        OwnersFile = OutcomeFixtures.OwnershipPath,
        ClassifiersFile = OutcomeFixtures.ClassifiersPath,
        KeyringDir = Path.Combine(Path.GetTempPath(), "wo-keys-unused"),
        CoordinatorTokenKey = "FLEET_WORKFLOW_OUTCOMES_COORDINATOR_TOKEN",
        CoordinatorBaseUrl = "http://orchestrator:8080",
    };

    public static async Task<ObserverHarness> CreateAsync(WorkflowOutcomeOptions? options = null, DateTimeOffset? now = null,
        Action<ObserverHarness>? configure = null, bool initialize = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        // The MySQL model pins a binary collation on the identity hash; SQLite needs it registered.
        connection.CreateCollation("ascii_bin", (a, b) => string.CompareOrdinal(a, b));
        var harness = new ObserverHarness(connection, new OutcomeClock(now ?? new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)),
            options ?? DefaultOptions());
        configure?.Invoke(harness);
        harness.Build();
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
            foreach (var (name, active) in OutcomeFixtures.Projects)
                db.ProjectContexts.Add(new ProjectContext { Name = name, IsActive = active });
            await db.SaveChangesAsync();
        }
        if (initialize) Assert.True(await harness.Reconciler.InitializeAsync(default));
        return harness;
    }

    private void Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(Logs));
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(Connection).AddInterceptors(Outage));
        services.AddSingleton<IOptions<WorkflowOutcomeOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CTO_AGENT"] = Coordinator }).Build());
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IWorkflowOutcomeRpc>(RpcOverride ?? Temporal);
        services.AddSingleton<IWorkflowOutcomePayloadProtector>(Protector);
        services.AddWorkflowOutcomeObserver();
        Services = services.BuildServiceProvider();
        Reconciler = Services.GetRequiredService<WorkflowOutcomeReconciler>();
    }

    /// <summary>A fresh reconciler (process restart) over the same DB and Temporal.</summary>
    public async Task RestartAsync()
    {
        await Services.DisposeAsync();
        Build();
        Assert.True(await Reconciler.InitializeAsync(default));
    }

    public WorkflowOutcomeStore Store => Services.GetRequiredService<WorkflowOutcomeStore>();

    /// <summary>Runs cycles, advancing the clock by <paramref name="step"/> after each.</summary>
    public async Task CyclesAsync(int count, TimeSpan? step = null)
    {
        for (var i = 0; i < count; i++)
        {
            await Reconciler.RunCycleAsync(default);
            Clock.Advance(step ?? WorkflowOutcomeReconciler.Tick);
        }
    }

    public async Task<T> DbAsync<T>(Func<OrchestratorDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
    }

    public Task<List<WorkflowOutcomeRun>> RunsAsync(string? ns = null) =>
        DbAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().Where(r => ns == null || r.Namespace == ns).ToListAsync());

    public async Task<WorkflowOutcomeRun> RunAsync(string ns, string workflowId, string runId) =>
        (await RunsAsync(ns)).Single(r => r.WorkflowId == workflowId && r.RunId == runId);

    public Task<List<WorkflowOutcomeHealth>> HealthAsync() => DbAsync(db => db.WorkflowOutcomeHealth.AsNoTracking().ToListAsync());

    public Task<WorkflowOutcomeCheckpoint?> CheckpointAsync(string key) =>
        DbAsync(db => db.WorkflowOutcomeCheckpoints.AsNoTracking().SingleOrDefaultAsync(c => c.WindowKey == key));

    public Task<List<WorkflowOutcomeObservation>> ObservationsAsync(Guid runRowId) =>
        DbAsync(db => db.WorkflowOutcomeObservations.AsNoTracking().Where(o => o.RunRowId == runRowId).ToListAsync());

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
