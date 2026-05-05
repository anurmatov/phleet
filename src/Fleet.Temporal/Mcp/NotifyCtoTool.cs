using System.ComponentModel;
using System.Text.Json;
using Fleet.Temporal.Configuration;
using ModelContextProtocol.Server;

namespace Fleet.Temporal.Mcp;

[McpServerToolType]
public sealed class NotifyCtoTool(
    CtoAgentConfigService ctoConfig,
    IWorkflowDispatcher dispatcher,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "notify_cto")]
    [Description(
        "Escalate an operational concern to the CTO agent — a recurring bug, unexpected tool error, " +
        "memory inconsistency, or a request to update your instructions or tools. " +
        "The CTO triages the notification, synthesizes a structured summary with a recommended decision, " +
        "and presents it to the CEO before acting. " +
        "Do NOT post operational concerns in group chats. Do NOT sit on them. " +
        "Returns {\"ok\":true,\"workflow_id\":\"...\"} on success or {\"ok\":false,\"error\":\"...\"} on failure.")]
    public async Task<string> NotifyAsync(
        [Description("Notification message (1–2000 chars)")] string message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(message) || message.Length < 1)
            return Error($"notify_cto: message must be at least 1 character");

        if (message.Length > 2000)
            return Error($"notify_cto: message too long ({message.Length} chars, max 2000)");

        var ctoAgent = ctoConfig.GetCtoAgent();
        if (string.IsNullOrWhiteSpace(ctoAgent))
            return Error("notify_cto: FLEET_CTO_AGENT is not configured");

        var sender = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault()
                     ?? "an agent";

        if (string.Equals(sender, ctoAgent, StringComparison.OrdinalIgnoreCase))
            return Error("notify_cto: self-notification not allowed");

        try
        {
            var directive = $"[notification from {sender}] {message}\n\n" +
                "ACTION: do NOT forward verbatim. Triage as follows:\n" +
                "1. Analyze the notification — what is the sender asking for, why, what's the impact, urgency, risks.\n" +
                "2. DM the CEO via the send_to_ceo MCP tool with a structured summary:\n" +
                "   • who: {sender}\n" +
                "   • what they want: <one sentence>\n" +
                "   • why it matters / impact: <one sentence>\n" +
                "   • your recommendation: approve / reject / counter-proposal\n" +
                "   • reasoning: <one or two sentences>\n" +
                "3. Wait for the CEO's reply (approve / reject / direction). Then act on the decision and report back.";
            var workflowId = await dispatcher.FireAndForgetAsync(
                ctoAgent,
                directive,
                ct);

            return JsonSerializer.Serialize(new { ok = true, workflow_id = workflowId });
        }
        catch (Exception ex)
        {
            return Error($"notify_cto: failed to dispatch workflow — {ex.Message}");
        }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message });
}
