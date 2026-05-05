namespace Fleet.Temporal.Mcp;

/// <summary>
/// Thin abstraction over Temporal workflow dispatch used by MCP tools.
/// Decouples tool handlers from the Temporal SDK to allow unit-testing without
/// a live Temporal server.
/// </summary>
public interface IWorkflowDispatcher
{
    /// <summary>
    /// Fires a <c>FireAndForgetTaskWorkflow</c> in the <c>fleet</c> namespace targeting
    /// <paramref name="targetAgent"/> with the given <paramref name="taskDescription"/>.
    /// Returns the started workflow ID.
    /// </summary>
    Task<string> FireAndForgetAsync(
        string targetAgent,
        string taskDescription,
        CancellationToken ct = default);
}
