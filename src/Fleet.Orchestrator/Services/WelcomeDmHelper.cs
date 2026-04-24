using Fleet.Orchestrator.Data;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Encapsulates the guard logic and execution path for the first-provision welcome DM
/// sent by the co-cto agent to the CEO Telegram user.
///
/// Dependencies for the two side-effecting operations (DB save, workflow start) are
/// accepted as delegates so the class stays testable without mocking sealed services.
/// </summary>
public static class WelcomeDmHelper
{
    /// <summary>
    /// Returns true when all conditions for sending the welcome are met:
    /// - The agent is being provisioned (provision=true, the default)
    /// - WelcomeSentAt has not been set yet (primary idempotency gate)
    /// - The CEO Telegram user ID is configured
    /// </summary>
    public static bool ShouldTrigger(Agent agent, bool provision, long? ceoUserId) =>
        provision && agent.WelcomeSentAt is null && ceoUserId.HasValue;

    /// <summary>
    /// Returns the directive text sent to the CTO agent instructing it to compose and
    /// send the welcome DM. The agent queries live runtime state (MCP servers, workflow
    /// types) at send time — no static lists are embedded here.
    /// </summary>
    public static string BuildWelcomeDirective(string agentName, long ceoUserId) =>
        $"You have just been provisioned as the CTO of a new phleet fleet. " +
        $"Send a welcome DM to Telegram user ID {ceoUserId}. " +
        $"In the message: " +
        $"(1) introduce yourself and explain what phleet is in 1-2 sentences, " +
        $"(2) list your available MCP servers with a one-line description each — read from your .mcp.json, " +
        $"(3) list the registered Temporal workflow types by calling temporal_list_workflow_types, " +
        $"(4) mention that you can create and manage other agents on demand, " +
        $"(5) close with an open invitation to ask you anything. " +
        $"Tone: warm and human, not a corporate bot. A touch of personality or light humor is welcome — you're introducing yourself to a teammate, not filing a status report. Keep it practical and skip the formal framing. " +
        $"Do not mention agent names, internal URLs, chat IDs, credentials, or any private fleet deployment details.";

    /// <summary>
    /// Persists WelcomeSentAt, then fires TaskDelegationWorkflow as a background Task.Run.
    ///
    /// Ordering guarantee: DB save completes (or fails) before Task.Run is started.
    /// If save fails: logs a warning and returns without starting the workflow (no double-send risk).
    /// If workflow start fails inside Task.Run: logs a warning — caller already returned its response.
    ///
    /// <paramref name="saveWelcomeSentAt"/> must persist agent.WelcomeSentAt to DB.
    /// <paramref name="startWorkflow"/> must call StartWorkflowAsync with the welcome input.
    /// </summary>
    public static async Task TriggerAsync(
        Agent agent,
        Func<Task> saveWelcomeSentAt,
        Func<Task> startWorkflow,
        ILogger logger)
    {
        bool saved = false;
        try
        {
            agent.WelcomeSentAt = DateTime.UtcNow;
            await saveWelcomeSentAt();
            saved = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Welcome DM skipped: could not persist WelcomeSentAt: {Message}", ex.Message);
        }

        if (!saved)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await startWorkflow();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Welcome DM workflow start failed: {Message}", ex.Message);
            }
        }, CancellationToken.None);
    }
}
