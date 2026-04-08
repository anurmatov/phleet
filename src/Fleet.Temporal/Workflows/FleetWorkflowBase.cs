using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;

namespace Fleet.Temporal.Workflows;

/// <summary>
/// Base class for Fleet Temporal workflows that delegate tasks to agents.
///
/// Provides failure escalation via <see cref="ExecuteWithEscalationAsync"/>:
/// when an agent reports a failed result, the workflow pauses, notifies the
/// <see cref="EscalationTarget"/>, and waits for an "escalation-decision" signal
/// before continuing.
///
/// Signal payload options (see <see cref="EscalationDecision"/>):
///   retry    — retry the step, optionally with updated instructions
///   skip     — stop executing further steps, return what was collected so far
///   continue — proceed despite the failure
/// </summary>
public abstract class FleetWorkflowBase
{
    internal static readonly ActivityOptions NotifyOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(10),
        HeartbeatTimeout = TimeSpan.FromSeconds(60),
        CancellationType = ActivityCancellationType.WaitCancellationCompleted,
    };

    private EscalationSignalPayload? _escalationDecision;

    /// <summary>
    /// Agent name to notify on step failure. Reads from <see cref="FleetWorkflowConfig"/>.
    /// Override in subclasses to route escalations to a different agent.
    /// </summary>
    protected virtual string EscalationTarget => FleetWorkflowConfig.Instance.EscalationTarget;

    /// <summary>
    /// Signal providing an escalation decision for a failed step.
    /// </summary>
    [WorkflowSignal("escalation-decision")]
    public Task OnEscalationDecisionAsync(EscalationSignalPayload payload)
    {
        _escalationDecision = payload;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Executes a DelegateToAgentActivity call with automatic failure escalation.
    ///
    /// If the agent reports a failure:
    ///   1. Notifies <see cref="EscalationTarget"/> in the group chat with failure details.
    ///   2. Pauses until an "escalation-decision" signal is received.
    ///   3. On "retry": re-sends the task (with optional updated instruction) and loops.
    ///   4. On "skip": sets <paramref name="shouldSkipRemaining"/> to true and returns the failed result.
    ///   5. On "continue": returns the failed result and continues workflow execution.
    /// </summary>
    /// <param name="agentName">Agent to delegate to.</param>
    /// <param name="instruction">Task instruction.</param>
    /// <param name="taskId">Stable, unique task ID for Temporal retry safety.</param>
    /// <param name="activityOptions">Temporal activity options (timeout, heartbeat, etc.).</param>
    /// <param name="stepLabel">Human-readable step name shown in escalation notifications.</param>
    /// <param name="shouldSkipRemaining">
    /// Set to true when "skip" is signalled — callers should stop executing further steps.
    /// </param>
    protected async Task<AgentTaskResult> ExecuteWithEscalationAsync(
        string agentName,
        string instruction,
        string taskId,
        ActivityOptions activityOptions,
        string stepLabel,
        Action<bool> shouldSkipRemaining)
    {
        var workflowId = Workflow.Info.WorkflowId;
        var currentInstruction = instruction;
        var retryCount = 0;

        while (true)
        {
            var result = await Workflow.ExecuteActivityAsync(
                (DelegateToAgentActivity a) => a.DelegateToAgentAsync(agentName, currentInstruction, taskId),
                activityOptions);

            if (!result.IsFailed)
                return result;

            // Step failed — notify escalation target and wait for decision
            retryCount++;
            var failureNotification =
                $"[workflow escalation] step failed in workflow {workflowId}.\n" +
                $"step: {stepLabel}\n" +
                $"agent: {agentName}\n" +
                $"taskId: {taskId}\n" +
                $"attempt: {retryCount}\n" +
                $"failure:\n{result.Text}\n\n" +
                $"send signal 'escalation-decision' to workflow {workflowId} with one of:\n" +
                $"  retry    — retry this step (optionally include UpdatedInstruction)\n" +
                $"  skip     — stop here, surface collected results\n" +
                $"  continue — proceed despite this failure";

            _escalationDecision = null;

            // Best-effort notification — do not fail the workflow if this fails
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (DelegateToAgentActivity a) => a.DelegateToAgentAsync(
                        EscalationTarget,
                        failureNotification,
                        $"{taskId}/escalation-notify-{retryCount}"),
                    NotifyOptions);
            }
            catch
            {
                // Notification failure is non-fatal — still pause for the signal
            }

            // Expose current phase so the dashboard shows only the escalation signal button
            try { Workflow.UpsertTypedSearchAttributes(Temporalio.Common.SearchAttributeKey.CreateKeyword("Phase").ValueSet("escalation")); }
            catch { /* non-fatal */ }

            // Block until escalation decision arrives
            await Workflow.WaitConditionAsync(() => _escalationDecision is not null);

            var decision = _escalationDecision!;
            var decisionValue = decision.Decision.ToLowerInvariant().Trim();

            if (decisionValue == EscalationDecision.Skip)
            {
                shouldSkipRemaining(true);
                return result;
            }

            if (decisionValue == EscalationDecision.Continue)
            {
                return result;
            }

            // Default: retry — use updated instruction if provided
            if (!string.IsNullOrWhiteSpace(decision.UpdatedInstruction))
                currentInstruction = decision.UpdatedInstruction;
        }
    }
}
