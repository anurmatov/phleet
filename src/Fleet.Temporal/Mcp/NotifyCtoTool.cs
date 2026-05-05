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
                     ?? "unknown";

        if (string.Equals(sender, ctoAgent, StringComparison.OrdinalIgnoreCase))
            return Error("notify_cto: self-notification not allowed");

        try
        {
            var workflowId = await dispatcher.FireAndForgetAsync(
                ctoAgent,
                $"[notification from {sender}] {message}",
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
