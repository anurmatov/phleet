using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ListInstructionVersionsTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "list_instruction_versions")]
    [Description("List all versions of a named instruction with version number, created_at, created_by, reason, and a content preview.")]
    public async Task<string> ListInstructionVersionsAsync(
        [Description("Instruction name (e.g. 'base', 'co-cto', 'developer')")] string instruction_name)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var instruction = await db.Instructions
            .Include(i => i.Versions.OrderByDescending(v => v.VersionNumber))
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Name == instruction_name);

        if (instruction is null)
            return $"Instruction '{instruction_name}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Instruction: {instruction.Name}");
        sb.AppendLine($"Current version: {instruction.CurrentVersion}");
        sb.AppendLine($"Total versions: {instruction.Versions.Count}");
        sb.AppendLine();

        foreach (var v in instruction.Versions)
        {
            var preview = v.Content.Length > 100
                ? v.Content[..100].Replace('\n', ' ') + "..."
                : v.Content.Replace('\n', ' ');
            sb.AppendLine($"### v{v.VersionNumber}{(v.VersionNumber == instruction.CurrentVersion ? " (current)" : "")}");
            sb.AppendLine($"- Created: {v.CreatedAt:yyyy-MM-dd HH:mm:ss}Z");
            if (v.CreatedBy is not null) sb.AppendLine($"- By: {v.CreatedBy}");
            if (v.Reason   is not null) sb.AppendLine($"- Reason: {v.Reason}");
            sb.AppendLine($"- Preview: {preview}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
