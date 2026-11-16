using System.Collections.Concurrent;
using System.Text.Json;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Which run waits for a run: the <c>WaiterWorkflowId</c> of its start input.
/// </summary>
/// <remarks>
/// A driver that starts a run through the start tool is not its Temporal parent, so the list of
/// runs says nothing of the two belonging together. The started run's input names the driver, and
/// a run's input never changes, so it is read once per run (the first history event alone) and
/// remembered. A read that fails is not remembered, and no other is tried for a minute: a
/// Temporal that is slow to answer costs the poller one bounded wait, never one per run, and the
/// list of runs goes out as it always did, without the link.
/// MUST NOT log or keep anything else of the input: it holds the task's text.
/// </remarks>
public sealed class WorkflowWaiterIndex(ILogger<WorkflowWaiterIndex> logger)
{
    /// <summary>How long one read may take.</summary>
    internal static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(3);

    /// <summary>How long no read is tried after one failed.</summary>
    internal static readonly TimeSpan Backoff = TimeSpan.FromMinutes(1);

    /// <summary>Runs remembered before the oldest half is dropped. A running fleet holds dozens.</summary>
    internal const int MaxRemembered = 4096;

    /// <summary>The longest waiter id kept. A workflow id is far shorter.</summary>
    internal const int MaxIdLength = 512;

    // Value "" is "read, and it names no waiter".
    private readonly ConcurrentDictionary<string, string> _known = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private long _noReadBeforeTicks;

    /// <summary>The waiter's workflow id, or null when the run names none or could not be read yet.</summary>
    public async Task<string?> WaiterOfAsync(
        ITemporalClient client, string @namespace, string workflowId, string runId, CancellationToken ct)
    {
        if (runId.Length == 0) return null;
        var key = @namespace + "\n" + runId;
        if (_known.TryGetValue(key, out var known)) return known.Length == 0 ? null : known;
        if (Environment.TickCount64 < Interlocked.Read(ref _noReadBeforeTicks)) return null;

        string? waiter;
        try
        {
            var response = await client.WorkflowService.GetWorkflowExecutionHistoryAsync(
                new GetWorkflowExecutionHistoryRequest
                {
                    Namespace = client.Options.Namespace,
                    Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = workflowId, RunId = runId },
                    MaximumPageSize = 1,
                },
                new RpcOptions { CancellationToken = ct, Timeout = ReadBound });
            var started = response?.History?.Events
                .FirstOrDefault(e => e.EventType == EventType.WorkflowExecutionStarted)
                ?.WorkflowExecutionStartedEventAttributes;
            if (started is null) return null;
            waiter = started.Input?.Payloads_ is { Count: > 0 } payloads ? WaiterIn(payloads[0]) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // The run's identity and the kind of failure, never the input.
            logger.LogDebug("Waiter read failed workflow={WorkflowId} run={RunId} error={Error}", workflowId, runId, ex.GetType().Name);
            Interlocked.Exchange(ref _noReadBeforeTicks, Environment.TickCount64 + (long)Backoff.TotalMilliseconds);
            return null;
        }

        Remember(key, waiter ?? "");
        return waiter;
    }

    private void Remember(string key, string value)
    {
        if (!_known.TryAdd(key, value)) return;
        _order.Enqueue(key);
        if (_known.Count <= MaxRemembered) return;
        for (var i = 0; i < MaxRemembered / 2 && _order.TryDequeue(out var old); i++)
            _known.TryRemove(old, out _);
    }

    /// <summary>
    /// The <c>WaiterWorkflowId</c> of a start input, when the input is a JSON object that has one.
    /// Anything else (another encoding, text, a list, a blank or oversized id) names no waiter.
    /// </summary>
    internal static string? WaiterIn(Payload payload)
    {
        if (!payload.Metadata.TryGetValue("encoding", out var encoding)
            || encoding.ToStringUtf8() is not ("json/plain" or "json/protobuf"))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(payload.Data.Memory);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("WaiterWorkflowId", out var value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            var id = value.GetString()?.Trim();
            return string.IsNullOrEmpty(id) || id.Length > MaxIdLength ? null : id;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
