using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class PreviewAgentProvisionTool(ContainerProvisioningService provisioning)
{
    [McpServerTool(Name = "preview_agent_provision")]
    [Description("Shadow-mode preview of what Docker container spec would be generated for an agent from DB config, and a diff against the actual running container. No changes are made.")]
    public async Task<string> PreviewAsync(
        [Description("Agent short name (e.g. my-agent)")] string agent_name)
    {
        var preview = await provisioning.PreviewAsync(agent_name);

        var sb = new StringBuilder();
        sb.AppendLine($"## Provision Preview: {preview.AgentName} ({preview.ContainerName})");
        sb.AppendLine();

        sb.AppendLine("### Desired Spec (from DB)");
        sb.AppendLine($"- Image: {preview.Desired.Image}");
        sb.AppendLine($"- Memory: {ContainerProvisioningService.FormatBytes(preview.Desired.MemoryBytes)}");
        sb.AppendLine($"- Networks: {string.Join(", ", preview.Desired.Networks)}");
        sb.AppendLine("- Env:");
        foreach (var e in preview.Desired.Env)
            sb.AppendLine($"  - {e}");
        sb.AppendLine("- Binds:");
        foreach (var b in preview.Desired.Binds)
            sb.AppendLine($"  - {b}");

        sb.AppendLine();

        if (preview.Actual is null)
        {
            sb.AppendLine("### Actual Container");
            sb.AppendLine("Container not found or not running.");
        }
        else
        {
            var resolvedNote = preview.ResolvedContainerName != preview.ContainerName
                ? $" (found as '{preview.ResolvedContainerName}')"
                : "";
            sb.AppendLine($"### Actual Container (inspected){resolvedNote}");
            sb.AppendLine($"- Image: {preview.Actual.Image}");
            sb.AppendLine($"- Memory: {ContainerProvisioningService.FormatBytes(preview.Actual.MemoryBytes)}");
            sb.AppendLine($"- Networks: {string.Join(", ", preview.Actual.Networks)}");
            sb.AppendLine("- Env keys:");
            foreach (var e in preview.Actual.Env.Select(ContainerProvisioningService.ParseEnvKey).Order())
                sb.AppendLine($"  - {e}");
            sb.AppendLine("- Binds:");
            foreach (var b in preview.Actual.Binds)
                sb.AppendLine($"  - {b}");
        }

        sb.AppendLine();
        sb.AppendLine("### Diffs");
        if (preview.Diffs.Count == 0)
            sb.AppendLine("No diffs — desired spec matches actual container.");
        else
            foreach (var d in preview.Diffs)
                sb.AppendLine($"- {d}");

        return sb.ToString();
    }
}
