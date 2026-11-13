using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class WorkflowDefinitionTools(IServiceScopeFactory scopeFactory, WorkflowDefinitionWriter writer)
{
    [McpServerTool(Name = "list_workflow_definitions")]
    [Description("List UWE workflow definitions stored in the orchestrator DB.")]
    public async Task<string> ListWorkflowDefinitionsAsync(
        [Description("Include inactive definitions in the list. Default: false (active only).")] bool includeInactive = false)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var query = db.WorkflowDefinitions.AsQueryable();
        if (!includeInactive) query = query.Where(d => d.IsActive);

        var defs = await query
            .OrderBy(d => d.Name)
            .AsNoTracking()
            .Select(d => new { d.Name, d.Namespace, d.TaskQueue, d.Version, d.IsActive, d.UpdatedAt })
            .ToListAsync();

        if (defs.Count == 0)
            return includeInactive ? "No workflow definitions found." : "No active workflow definitions found.";

        var sb = new StringBuilder();
        sb.AppendLine("| Name | Namespace | TaskQueue | Version | Active | UpdatedAt |");
        sb.AppendLine("|------|-----------|-----------|---------|--------|-----------|");
        foreach (var d in defs)
            sb.AppendLine($"| {d.Name} | {d.Namespace} | {d.TaskQueue} | {d.Version} | {d.IsActive} | {d.UpdatedAt:yyyy-MM-dd HH:mm}Z |");
        return sb.ToString();
    }

    [McpServerTool(Name = "get_workflow_definition")]
    [Description("Get a UWE workflow definition by name, including its full JSON step tree.")]
    public async Task<string> GetWorkflowDefinitionAsync(
        [Description("Workflow definition name (e.g. 'MyWorkflow')")] string name)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var def = await db.WorkflowDefinitions
            .Include(d => d.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Name == name);

        if (def is null)
            return $"Workflow definition '{name}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"## {def.Name}");
        sb.AppendLine($"- **Namespace**: {def.Namespace}");
        sb.AppendLine($"- **TaskQueue**: {def.TaskQueue}");
        sb.AppendLine($"- **Version**: {def.Version}");
        sb.AppendLine($"- **Active**: {def.IsActive}");
        if (def.Description is not null) sb.AppendLine($"- **Description**: {def.Description}");
        sb.AppendLine($"- **Created**: {def.CreatedAt:yyyy-MM-dd HH:mm}Z");
        sb.AppendLine($"- **Updated**: {def.UpdatedAt:yyyy-MM-dd HH:mm}Z");
        if (def.CreatedBy is not null) sb.AppendLine($"- **CreatedBy**: {def.CreatedBy}");
        sb.AppendLine();
        sb.AppendLine("### Definition (JSON step tree)");
        sb.AppendLine("```json");
        sb.AppendLine(def.Definition);
        sb.AppendLine("```");
        return sb.ToString();
    }

    [McpServerTool(Name = "create_workflow_definition")]
    [Description("Create a new UWE workflow definition in the orchestrator DB.")]
    public async Task<string> CreateWorkflowDefinitionAsync(
        [Description("Workflow type name (must match the Temporal workflow type, e.g. 'MyWorkflow')")] string name,
        [Description("Temporal namespace (e.g. 'fleet')")] string workflow_namespace,
        [Description("Temporal task queue (e.g. 'fleet')")] string task_queue,
        [Description("JSON string containing the root StepDefinition step tree")] string definition,
        [Description("Optional human-readable description of what this workflow does")] string? description = null)
    {
        var outcome = await writer.CreateAsync(new NewWorkflowDefinition(
            name, workflow_namespace, task_queue, definition, description, CreatedBy: null));

        return outcome.Fault is { } fault
            ? fault.Code == "workflow_definition_rejected" ? fault.Message : $"Failed to create workflow definition '{name}': {fault.Message}"
            : $"Created workflow definition '{name}' v1.";
    }

    [McpServerTool(Name = "update_workflow_definition")]
    [Description("Update a UWE workflow definition. A new step tree becomes a new version and the previous one is kept in history; a description-only update keeps the version. Namespace and TaskQueue are not updatable.")]
    public async Task<string> UpdateWorkflowDefinitionAsync(
        [Description("Workflow definition name to update")] string name,
        [Description("New JSON step tree. If omitted, only description is updated.")] string? definition = null,
        [Description("New description. If omitted, description is unchanged.")] string? description = null)
    {
        // Namespace and task queue are left out on purpose: this tool does not move a definition.
        var outcome = await writer.UpdateAsync(name, new WorkflowDefinitionPatch(Definition: definition, Description: description));

        if (outcome.Fault is { } fault)
            return fault.Code == "workflow_definition_not_found" ? $"Workflow definition '{name}' not found." : fault.Message;

        return definition is not null
            ? $"Updated workflow definition '{name}' to v{outcome.Value!.Version}."
            : $"Updated workflow definition '{name}' (description only; still v{outcome.Value!.Version}).";
    }
}
