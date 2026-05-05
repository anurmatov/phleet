using Fleet.Temporal.Activities;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;

namespace Fleet.Temporal.Workflows.Fleet;

/// <summary>
/// Compiled workflow for CTO escalation. Delegates a notification to the CTO agent
/// via <see cref="DelegateToAgentActivity"/> and ignores failures so that a broken
/// escalation channel never propagates errors back to the caller.
///
/// Using a compiled workflow (rather than a UWE DB-defined FireAndForgetTaskWorkflow)
/// removes the orchestrator DB as a runtime dependency for escalation — DB down or
/// definition deactivated would otherwise silently drop the escalation.
/// </summary>
[Workflow]
public class NotifyCtoWorkflow
{
    private static readonly ActivityOptions DelegateOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(17), // 15 min task + 2 min buffer
        HeartbeatTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new() { MaximumAttempts = 1 },
    };

    [WorkflowRun]
    public async Task RunAsync(NotifyCtoWorkflowInput input)
    {
        var taskId = $"{Workflow.Info.WorkflowId}/notify";

        try
        {
            await Workflow.ExecuteActivityAsync(
                (DelegateToAgentActivity a) => a.DelegateToAgentAsync(
                    input.TargetAgent,
                    input.TaskDescription,
                    taskId,
                    true,  // retryOnIncomplete
                    3),    // maxIncompleteRetries
                DelegateOptions);
        }
        catch (Exception)
        {
            // ignoreFailure=true — escalation channel must not surface errors to callers.
            Workflow.Logger.LogWarning(
                "NotifyCtoWorkflow: delegation to {Agent} failed — ignoring",
                input.TargetAgent);
        }
    }
}

/// <summary>Input for <see cref="NotifyCtoWorkflow"/>.</summary>
public record NotifyCtoWorkflowInput(string TargetAgent, string TaskDescription);
