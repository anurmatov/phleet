namespace Fleet.Temporal.Engine;

/// <summary>
/// Complete workflow definition loaded from DB by LoadWorkflowDefinitionActivity.
/// Serialized/deserialized as the activity result — replayed from history on replay, never re-fetched.
/// </summary>
public sealed record WorkflowDefinitionModel
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public required string TaskQueue { get; init; }
    public required StepDefinition Root { get; init; }
    public int Version { get; init; }
}
