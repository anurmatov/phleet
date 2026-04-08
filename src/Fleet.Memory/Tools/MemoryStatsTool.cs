using System.ComponentModel;
using System.Text;
using Fleet.Memory.Services;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryStatsTool(MemoryService memoryService)
{
    [McpServerTool(Name = "memory_stats")]
    [Description("Get statistics about stored memories — counts by type, total count, and archived count.")]
    public string GetStats()
    {
        var stats = memoryService.GetStats();

        var sb = new StringBuilder();
        sb.AppendLine("## Memory Statistics");
        sb.AppendLine();

        var total = 0;
        foreach (var (type, count) in stats.OrderByDescending(s => s.Value))
        {
            if (type == "_archived")
                continue;
            sb.AppendLine($"- **{type}**: {count}");
            total += count;
        }

        sb.AppendLine();
        sb.AppendLine($"**Total active**: {total}");
        if (stats.TryGetValue("_archived", out var archived))
            sb.AppendLine($"**Archived**: {archived}");

        return sb.ToString();
    }
}
