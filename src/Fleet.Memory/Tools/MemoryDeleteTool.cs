using System.ComponentModel;
using Fleet.Memory.Services;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryDeleteTool(
    MemoryService memoryService,
    AclCacheService aclCache,
    IHttpContextAccessor httpContextAccessor,
    ILogger<MemoryDeleteTool> logger)
{
    [McpServerTool(Name = "memory_delete")]
    [Description("Delete a memory. By default, moves it to _archived/ (soft delete). Use permanent=true to permanently remove it.")]
    public async Task<string> DeleteAsync(
        [Description("The memory ID to delete")] string id,
        [Description("If true, permanently delete instead of archiving (default: false)")] bool permanent = false)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "memory_delete: missing required parameter 'id'.\nHint: pass the memory ID (full UUID or first 8 characters) from memory_search or memory_list.";

        var agentName = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault();

        if (aclCache.IsAclEnabled && !aclCache.IsAvailable)
            return "memory_delete: ACL cache unavailable — orchestrator unreachable. Try again shortly.";

        if (aclCache.IsAclEnabled && string.IsNullOrWhiteSpace(agentName))
            return "memory_delete: agent identity required — missing '?agent=' query parameter.";

        if (aclCache.IsAclEnabled)
        {
            // Fetch the memory to determine its project, then ACL-check before deleting.
            // Uniform denial (no existence leak) — same pattern as memory_get.
            var probe = await memoryService.GetAsync(id);
            var project = probe?.Project ?? "";
            var (allowed, denyReason) = aclCache.CanRead(agentName!, project);
            if (!allowed)
            {
                logger.LogInformation(
                    "memory_delete: denied agent '{Agent}' on id '{Id}': {Reason}",
                    agentName, id, denyReason);
                return "memory_delete: access denied.";
            }
            if (probe is null)
                return $"Memory not found with ID: {id}";
        }

        try
        {
            await memoryService.DeleteAsync(id, permanent);
            var action = permanent ? "permanently deleted" : "archived";
            return $"Memory {id} has been {action}.";
        }
        catch (FileNotFoundException)
        {
            return $"Memory not found with ID: {id}";
        }
    }
}
