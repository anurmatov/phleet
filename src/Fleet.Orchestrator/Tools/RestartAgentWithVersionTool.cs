using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>
/// The MCP adapter over <see cref="AgentLifecycle.RestartWithInstructionVersionAsync"/>, which the
/// REST restart-with-version route calls too.
/// </summary>
[McpServerToolType]
public sealed class RestartAgentWithVersionTool(AgentLifecycle lifecycle)
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
        var outcome = await lifecycle.RestartWithInstructionVersionAsync(agent_name, instruction_name, version_number, pin);
        if (outcome.Fault is { } fault)
            return fault.Code switch
            {
                "agent_not_found" => $"Agent '{agent_name}' not found in DB.",
                "instruction_not_found" => $"Instruction '{instruction_name}' not found.",
                "instruction_version_not_found" =>
                    $"Version {version_number} does not exist for instruction '{instruction_name}'. Available: "
                    + string.Join(", ", ((List<int>)fault.Detail!).Select(v => $"v{v}")),
                _ => fault.Message,
            };

        var done = outcome.Value!;
        var sb = new StringBuilder();
        if (done.Pinned)
        {
            sb.AppendLine($"Pinned instruction '{instruction_name}' to v{version_number} in DB.");
            sb.AppendLine();
            sb.AppendLine(done.Reprovision.Success
                ? $"✓ {done.Reprovision.Message}"
                : $"✗ reprovision failed: {done.Reprovision.Message}");
        }
        else
        {
            sb.AppendLine($"Reprovisioning '{agent_name}' with instruction '{instruction_name}' pinned to v{version_number} (temporary — DB unchanged, current is v{done.PreviousCurrentVersion}).");
            sb.AppendLine();
            sb.AppendLine(done.Reprovision.Success
                ? $"✓ {done.Reprovision.Message}"
                : $"✗ reprovision failed: {done.Reprovision.Message}");
            if (done.Reprovision.Success)
                sb.AppendLine($"Note: next normal reprovision will revert to current DB version (v{done.PreviousCurrentVersion}).");
        }

        return sb.ToString().TrimEnd();
    }
}
