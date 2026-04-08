using System.ComponentModel;
using Fleet.Memory.Services;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryDeleteTool(MemoryService memoryService)
{
    [McpServerTool(Name = "memory_delete")]
    [Description("Delete a memory. By default, moves it to _archived/ (soft delete). Use permanent=true to permanently remove it.")]
    public async Task<string> DeleteAsync(
        [Description("The memory ID to delete")] string id,
        [Description("If true, permanently delete instead of archiving (default: false)")] bool permanent = false)
    {
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
