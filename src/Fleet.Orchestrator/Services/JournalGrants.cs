using Fleet.Orchestrator.Data;
namespace Fleet.Orchestrator.Services;
/// <summary>The single source for provisioning and live send authorization. No automatic grants.</summary>
public static class JournalGrants
{
    public const string SendGrant = "mcp__fleet-journal-files__send_attachment";
    public static bool IsSendGrant(string name) => name.Trim().All(c => c <= 127)
        && string.Equals(name.Trim(), SendGrant, StringComparison.OrdinalIgnoreCase);
    public static bool HasSendGrant(Agent agent) => agent.Tools.Any(t => t.IsEnabled && IsSendGrant(t.ToolName));
    public static bool HasBot(Agent agent) => agent.EnvRefs.Any(e => e.EnvKeyName.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase)
        && e.EnvKeyName.EndsWith("_BOT_TOKEN", StringComparison.OrdinalIgnoreCase));
    public static bool SendEffective(Agent agent) => HasSendGrant(agent) && agent.JournalEnabled && HasBot(agent);
    public static bool CrossChatEffective(Agent agent) => agent.JournalCrossChatEnabled && SendEffective(agent);
}
