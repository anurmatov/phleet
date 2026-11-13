using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Services;

/// <summary>What Temporal said about one call on behalf of a pending activity.</summary>
public enum ActivityCallOutcome
{
    /// <summary>Accepted.</summary>
    Ok,

    /// <summary>A heartbeat was accepted and the workflow has asked for the activity to be cancelled.</summary>
    CancelRequested,

    /// <summary>
    /// Temporal no longer has this activity attempt: it timed out, was completed already, or its
    /// workflow closed. Nothing more can be done for it.
    /// </summary>
    Gone,

    /// <summary>Temporal could not be asked, or could not answer. The same call may work later.</summary>
    Unavailable,
}

/// <summary>
/// The four things a bridge does to an activity it promised to complete later. An interface so the
/// service that decides WHEN can be tested without a Temporal server.
/// </summary>
public interface IAsyncActivityGateway
{
    Task<ActivityCallOutcome> HeartbeatAsync(PendingDelegation delegation, CancellationToken ct);

    Task<ActivityCallOutcome> CompleteAsync(PendingDelegation delegation, AgentTaskResult result, CancellationToken ct);

    Task<ActivityCallOutcome> FailAsync(PendingDelegation delegation, Exception failure, CancellationToken ct);

    Task<ActivityCallOutcome> ReportCancellationAsync(PendingDelegation delegation, CancellationToken ct);
}

/// <summary>
/// Completes, fails and heartbeats activities by the task token recorded when they started, on the
/// same per-namespace clients (and so the same data converter) the workers use.
/// </summary>
public sealed class TemporalAsyncActivityGateway(
    ITemporalClientFactory clients, ILogger<TemporalAsyncActivityGateway> logger) : IAsyncActivityGateway
{
    /// <summary>
    /// One call's deadline. A pass visits every pending delegation in turn, so a Temporal that has
    /// stopped answering must not hold the pass for longer than the next heartbeat is due.
    /// </summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    public Task<ActivityCallOutcome> HeartbeatAsync(PendingDelegation delegation, CancellationToken ct) =>
        CallAsync(delegation, "heartbeat", ct, (handle, rpc) => handle.HeartbeatAsync(new AsyncActivityHeartbeatOptions
        {
            Details = [$"waiting for agent {delegation.Agent} response (taskId={delegation.CurrentTaskId})"],
            Rpc = rpc,
        }));

    public Task<ActivityCallOutcome> CompleteAsync(PendingDelegation delegation, AgentTaskResult result, CancellationToken ct) =>
        CallAsync(delegation, "complete", ct, (handle, rpc) => handle.CompleteAsync(result, new AsyncActivityCompleteOptions { Rpc = rpc }));

    public Task<ActivityCallOutcome> FailAsync(PendingDelegation delegation, Exception failure, CancellationToken ct) =>
        CallAsync(delegation, "fail", ct, (handle, rpc) => handle.FailAsync(failure, new AsyncActivityFailOptions { Rpc = rpc }));

    public Task<ActivityCallOutcome> ReportCancellationAsync(PendingDelegation delegation, CancellationToken ct) =>
        CallAsync(delegation, "report-cancellation", ct, (handle, rpc) =>
            handle.ReportCancellationAsync(new AsyncActivityReportCancellationOptions { Rpc = rpc }));

    private async Task<ActivityCallOutcome> CallAsync(
        PendingDelegation delegation, string what, CancellationToken ct, Func<AsyncActivityHandle, RpcOptions, Task> call)
    {
        try
        {
            var client = await clients.GetClientAsync(delegation.Namespace);
            var handle = client.GetAsyncActivityHandle(Convert.FromBase64String(delegation.TaskToken));
            await call(handle, new RpcOptions { Timeout = CallTimeout, CancellationToken = ct });
            return ActivityCallOutcome.Ok;
        }
        catch (AsyncActivityCanceledException)
        {
            return ActivityCallOutcome.CancelRequested;
        }
        catch (RpcException ex) when (ex.Code is RpcException.StatusCode.NotFound or RpcException.StatusCode.InvalidArgument)
        {
            return ActivityCallOutcome.Gone;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Identifiers only: the exception text of an RPC failure carries no payload.
            logger.LogWarning(
                "Temporal {Call} for taskId={TaskId} (workflow {WorkflowId}) could not be made: {Error}",
                what, delegation.CurrentTaskId, delegation.WorkflowId, ex.GetType().Name);
            return ActivityCallOutcome.Unavailable;
        }
    }
}
