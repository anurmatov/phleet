using Fleet.Temporal.Activities;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
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
    internal const string DeliveryFailedErrorType = "NotificationDeliveryFailed";

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
        AgentTaskResult? result = null;

        try
        {
            // Scheduled by name with the argument list spelled out: an expression-tree call has
            // the compiler append every optional parameter's default, so adding one to the
            // activity (#347 repo) would silently change this workflow's input. These six are
            // exactly what the expression form scheduled.
            result = await Workflow.ExecuteActivityAsync<AgentTaskResult>(
                DelegateToAgentActivity.ActivityName,
                new object?[]
                {
                    input.TargetAgent,
                    input.TaskDescription,
                    taskId,
                    true,  // retryOnIncomplete
                    3,     // maxIncompleteRetries
                    0,     // agentBudgetSeconds (the default)
                },
                DelegateOptions);
        }
        catch (Exception)
        {
            // A caller that tracks delivery (#488's failure notifier) asks for the failure to close
            // the run Failed, so the outcome is visible. The message is fixed; no error text leaks.
            if (input.ReportDeliveryFailure)
                throw DeliveryFailed();

            // ignoreFailure=true — escalation channel must not surface errors to callers.
            Workflow.Logger.LogWarning(
                "NotifyCtoWorkflow: delegation to {Agent} failed — ignoring",
                input.TargetAgent);
        }

        // A returned result can also mean nothing was delivered: "failed", or still "incomplete"
        // after the retries. Only a completed answer confirms delivery; the result text never leaves.
        if (input.ReportDeliveryFailure && result is not { IsCompleted: true })
            throw DeliveryFailed();
    }

    private static ApplicationFailureException DeliveryFailed() =>
        new("notification delivery failed", errorType: DeliveryFailedErrorType, nonRetryable: true);
}

/// <summary>
/// Input for <see cref="NotifyCtoWorkflow"/>. <paramref name="ReportDeliveryFailure"/> defaults to
/// false, so <c>notify_cto</c> and runs started before it existed keep swallowing failures.
/// </summary>
public record NotifyCtoWorkflowInput(string TargetAgent, string TaskDescription, bool ReportDeliveryFailure = false);
