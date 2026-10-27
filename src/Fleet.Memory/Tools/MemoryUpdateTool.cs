using System.ComponentModel;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryUpdateTool(
    MemoryService memoryService,
    AclCacheService aclCache,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "memory_update")]
    [Description("Update an existing memory's title, content, tags, or project. The memory will be automatically re-indexed for search.")]
    public async Task<string> UpdateAsync(
        [Description("The memory ID to update")] string id,
        [Description("New title (optional, pass empty to keep current)")] string? title = null,
        [Description("New content (optional, pass empty to keep current)")] string? content = null,
        [Description("New comma-separated tags (optional, pass empty to keep current)")] string? tags = null,
        [Description("New project name (optional, pass empty to keep current)")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "memory_update: missing required parameter 'id'.\nHint: pass the memory ID (full UUID or first 8 characters) from memory_search or memory_list.";

        var context = httpContextAccessor?.HttpContext;
        var verified = VerifiedMemoryRequest.Get(context);
        if (VerifiedMemoryRequest.IsRequired(context) && verified is null)
            return "memory_update: verified request authorization unavailable.";
        var agentName = verified?.Identity.AgentName ?? context?.Request.Query["agent"].FirstOrDefault();
        // ACL gate: agent must be allowed to read (and therefore update) the target memory.
        if (verified is not null || aclCache.IsAclEnabled)
        {
            if (verified is null && !aclCache.IsAvailable)
                return "memory_update: ACL cache unavailable — orchestrator unreachable. Try again shortly.";

            if (string.IsNullOrWhiteSpace(agentName))
                return "memory_update: agent identity required — missing '?agent=' query parameter.";

            MemoryDocument? probe;
            try { probe = await memoryService.GetAsync(id); }
            catch (AmbiguousMemoryIdException ex) { return $"memory_update: {ex.Message}"; }
            var existingProject = probe?.Project ?? "";
            var (allowed, denyReason) = (verified is null ? aclCache.CanRead(agentName!, existingProject) : aclCache.CanRead(verified.Snapshot, agentName!, existingProject));
            if (!allowed)
                return "memory_update: access denied.";

            // If the caller is changing the project, they must also be in the target project's allow-list.
            var newProject = string.IsNullOrEmpty(project) ? null : project;
            if (newProject is not null)
            {
                var (targetAllowed, targetReason) = (verified is null ? aclCache.CanRead(agentName!, newProject) : aclCache.CanRead(verified.Snapshot, agentName!, newProject));
                if (!targetAllowed)
                    return $"memory_update: access denied — cannot move memory to project '{newProject}': {targetReason}.";
            }

            if (probe is null)
                return $"Memory not found with ID: {id}";
        }

        try
        {
            List<string>? tagList = tags is not null
                ? tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
                : null;

            // Pass null for unchanged fields
            var actualTitle = string.IsNullOrEmpty(title) ? null : title;
            var actualContent = string.IsNullOrEmpty(content) ? null : content;
            var actualProject = string.IsNullOrEmpty(project) ? null : project;

            var (doc, indexingWarning, size) = await memoryService.UpdateAsync(id, actualTitle, actualContent, tagList, actualProject);

            var result = $"Updated memory '{doc.Title}' (id: {doc.Id})";
            if (indexingWarning is not null)
                result += $"\n{indexingWarning}";

            // #346: nothing when the guidance is off, so the output stays byte-identical.
            return result + MemorySizeGuidance.RenderMcpLines(size);
        }
        catch (FileNotFoundException)
        {
            return $"Memory not found with ID: {id}";
        }
        catch (AmbiguousMemoryIdException ex)
        {
            return $"memory_update: {ex.Message}";
        }
        catch (InvalidDataException ex)
        {
            return $"error_serialization_validation: {ex.Message}";
        }
        catch (IOException ex)
        {
            return $"error_write_failed: {ex.Message}";
        }
    }
}
