using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class SystemHealthTool(AgentRegistry registry, IRabbitMqStatus rabbitMqStatus)
{
    [McpServerTool(Name = "system_health")]
    [Description("Get overall fleet health: agent counts by status, RabbitMQ connectivity, and a per-agent summary.")]
    public string GetSystemHealth()
    {
        var agents = registry.GetAll();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents)
        {
            var s = a.EffectiveStatus;
            counts[s] = counts.TryGetValue(s, out var v) ? v + 1 : 1;
        }

        var healthy = (counts.GetValueOrDefault("active") + counts.GetValueOrDefault("idle") + counts.GetValueOrDefault("busy"));
        var stale   = counts.GetValueOrDefault("stale");
        var dead    = counts.GetValueOrDefault("dead");
        var total   = agents.Count;

        var overallStatus = dead > 0 ? "DEGRADED" : stale > 0 ? "WARNING" : total == 0 ? "NO_AGENTS" : "HEALTHY";

        var sb = new StringBuilder();
        sb.AppendLine($"## Fleet System Health — {overallStatus}");
        sb.AppendLine();
        sb.AppendLine($"**RabbitMQ**: {(rabbitMqStatus.IsConnected ? "connected" : "disconnected")}");
        sb.AppendLine($"**Agents total**: {total}");
        sb.AppendLine($"**Healthy (active/idle/busy)**: {healthy}");
        if (stale > 0) sb.AppendLine($"**Stale (60–90s silent)**: {stale}");
        if (dead  > 0) sb.AppendLine($"**Dead (>90s silent)**: {dead}");
        sb.AppendLine();

        if (total > 0)
        {
            sb.AppendLine("### Agent Summary");
            foreach (var a in agents)
            {
                var age = (int)(DateTimeOffset.UtcNow - a.LastSeen).TotalSeconds;
                var marker = a.EffectiveStatus is "dead" ? "💀" : a.EffectiveStatus is "stale" ? "⚠️" : "✓";
                sb.AppendLine($"- {marker} **{a.AgentName}**: {a.EffectiveStatus} (last seen {age}s ago)");
            }
        }

        return sb.ToString();
    }
}
