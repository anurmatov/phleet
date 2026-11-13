using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Application;

/// <summary>A new workflow definition. Everything but the description is required.</summary>
public sealed record NewWorkflowDefinition(
    string? Name, string? Namespace, string? TaskQueue, string? Definition, string? Description, string? CreatedBy);

/// <summary>
/// A partial update of a workflow definition. A null member keeps the stored value. An adapter
/// that does not let its caller move a definition between namespaces or task queues leaves those
/// two null.
/// </summary>
public sealed record WorkflowDefinitionPatch(
    string? Definition = null,
    string? Description = null,
    string? Namespace = null,
    string? TaskQueue = null,
    string? Reason = null,
    string? CreatedBy = null);

/// <summary>
/// The one implementation of the workflow-definition writes. The
/// <c>/api/workflow-definitions</c> routes and the definition tools are adapters over it.
/// </summary>
/// <remarks>
/// <c>Version</c> counts definitions, not saves: it goes up, and the previous step tree is kept as a
/// history row, only when a new step tree is stored. A description or routing change keeps the
/// version, so a version number always names one step tree.
/// </remarks>
public sealed class WorkflowDefinitionWriter(IServiceScopeFactory scopes)
{
    public async Task<WriteOutcome<WorkflowDefinition>> CreateAsync(NewWorkflowDefinition request, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        if (string.IsNullOrWhiteSpace(request.Name)) return WriteFaults.Invalid("name_required", "Name is required");
        if (string.IsNullOrWhiteSpace(request.Namespace)) return WriteFaults.Invalid("namespace_required", "Namespace is required");
        if (string.IsNullOrWhiteSpace(request.TaskQueue)) return WriteFaults.Invalid("task_queue_required", "TaskQueue is required");
        if (string.IsNullOrWhiteSpace(request.Definition)) return WriteFaults.Invalid("definition_required", "Definition is required");

        if (await db.WorkflowDefinitions.AnyAsync(d => d.Name == request.Name, ct))
            return WriteFaults.Conflict("workflow_definition_exists", $"Workflow definition '{request.Name}' already exists");

        var now = DateTime.UtcNow;
        var definition = new WorkflowDefinition
        {
            Name = request.Name,
            Namespace = request.Namespace,
            TaskQueue = request.TaskQueue,
            Description = request.Description,
            Definition = request.Definition,
            Version = 1,
            IsActive = true,
            CreatedBy = request.CreatedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WorkflowDefinitions.Add(definition);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The store refused it: a name taken between the check and the save, or a step tree the
            // JSON column will not hold.
            return WriteFaults.Conflict("workflow_definition_rejected",
                $"Failed to create workflow definition '{request.Name}': {ex.InnerException?.Message ?? ex.Message}");
        }

        return WriteOutcome<WorkflowDefinition>.Ok(definition);
    }

    public async Task<WriteOutcome<WorkflowDefinition>> UpdateAsync(
        string name, WorkflowDefinitionPatch patch, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var definition = await db.WorkflowDefinitions.FirstOrDefaultAsync(d => d.Name == name, ct);
        if (definition is null)
            return WriteFaults.NotFound("workflow_definition_not_found", $"Workflow definition '{name}' not found");

        if (patch.Definition is not null)
        {
            // The step tree being replaced goes to history under the version it had.
            db.WorkflowDefinitionVersions.Add(new WorkflowDefinitionVersion
            {
                WorkflowDefinitionId = definition.Id,
                Version = definition.Version,
                Definition = definition.Definition,
                Reason = patch.Reason,
                CreatedBy = patch.CreatedBy,
                CreatedAt = DateTime.UtcNow,
            });
            definition.Definition = patch.Definition;
            definition.Version += 1;
        }

        definition.Namespace = patch.Namespace ?? definition.Namespace;
        definition.TaskQueue = patch.TaskQueue ?? definition.TaskQueue;
        definition.Description = patch.Description ?? definition.Description;
        definition.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return WriteOutcome<WorkflowDefinition>.Ok(definition);
    }

    public async Task<WriteOutcome<WorkflowDefinition>> SetActiveAsync(string name, bool isActive, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var definition = await db.WorkflowDefinitions.FirstOrDefaultAsync(d => d.Name == name, ct);
        if (definition is null)
            return WriteFaults.NotFound("workflow_definition_not_found", $"Workflow definition '{name}' not found");

        definition.IsActive = isActive;
        definition.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return WriteOutcome<WorkflowDefinition>.Ok(definition);
    }
}
