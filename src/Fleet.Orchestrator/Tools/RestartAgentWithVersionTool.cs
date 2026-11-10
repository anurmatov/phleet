using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class RestartAgentWithVersionTool(
    ContainerProvisioningService provisioning,
    IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "restart_agent_with_version")]
    [Description(
        "Restart an agent using a specific instruction version instead of the current latest. " +
        "By default the override is temporary (next normal reprovision uses the current DB version). " +
        "Set pin=true to make the version sticky by updating the DB CurrentVersion.")]
    public async Task<string> RestartAgentWithVersionAsync(
        [Description("Agent short name (e.g. my-agent)")] string agent_name,
        [Description("Instruction name to override (e.g. 'base', 'co-cto', 'developer')")] string instruction_name,
        [Description("Version number to use")] int version_number,
        [Description("If true, pins the version in DB so future reprovisioning also uses it. Default: false (temporary override only).")] bool pin = false)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        // Validate agent exists
        var agent = await db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        // Validate instruction exists and the requested version exists
        var instruction = await db.Instructions
            .Include(i => i.Versions)
            .FirstOrDefaultAsync(i => i.Name == instruction_name);

        if (instruction is null)
            return $"Instruction '{instruction_name}' not found.";

        var version = instruction.Versions.FirstOrDefault(v => v.VersionNumber == version_number);
        if (version is null)
        {
            var available = string.Join(", ", instruction.Versions
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => $"v{v.VersionNumber}"));
            return $"Version {version_number} does not exist for instruction '{instruction_name}'. Available: {available}";
        }

        var sb = new StringBuilder();

        if (pin)
        {
            // Permanently pin: update CurrentVersion in DB so all future reprovisioning uses this version
            instruction.CurrentVersion = version_number;
            await db.SaveChangesAsync();
            sb.AppendLine($"Pinned instruction '{instruction_name}' to v{version_number} in DB.");
            sb.AppendLine();

            // Reprovision normally (no override needed — DB is updated)
            var pinnedResult = await provisioning.ReprovisionAsync(agent_name);
            sb.AppendLine(pinnedResult.Success
                ? $"✓ {pinnedResult.Message}"
                : $"✗ reprovision failed: {pinnedResult.Message}");
        }
        else
        {
            // Temporary override: reprovision with version override dict, DB unchanged
            sb.AppendLine($"Reprovisioning '{agent_name}' with instruction '{instruction_name}' pinned to v{version_number} (temporary — DB unchanged, current is v{instruction.CurrentVersion}).");
            sb.AppendLine();

            var overrides = new Dictionary<string, int> { [instruction_name] = version_number };
            var result = await provisioning.ReprovisionAsync(agent_name, instructionVersionOverrides: overrides);
            sb.AppendLine(result.Success
                ? $"✓ {result.Message}"
                : $"✗ reprovision failed: {result.Message}");

            if (result.Success)
                sb.AppendLine($"Note: next normal reprovision will revert to current DB version (v{instruction.CurrentVersion}).");
        }

        return sb.ToString().TrimEnd();
    }
}
