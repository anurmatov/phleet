namespace Fleet.Orchestrator.Services;

/// <summary>Describes a known Temporal workflow type with its input schema and default namespace.</summary>
public sealed record WorkflowTypeEntry(
    string Name,
    string Description,
    string Namespace,
    string TaskQueue,
    string? InputSchema = null);

/// <summary>
/// Static registry of known C# workflow types with hand-authored JSON input schemas.
/// Merged at query time with DB-defined UWE workflow types from the workflow_definitions table.
/// </summary>
public static class WorkflowTypeRegistry
{
    /// <summary>
    /// Hardcoded C# workflow types. InputSchema is a JSON Schema string, or null for UWE types
    /// (unknown schema → raw JSON editor in UI), or an empty-properties schema for no-input workflows.
    /// </summary>
    public static readonly IReadOnlyList<WorkflowTypeEntry> HardcodedTypes =
    [
        new WorkflowTypeEntry(
            Name: "ConsensusReviewWorkflow",
            Description: "Multi-agent parallel review. Fans out to reviewer agents, fast-paths on unanimous approval, synthesizes divergent reviews.",
            Namespace: "fleet",
            TaskQueue: "fleet",
            InputSchema: """
                {
                  "type": "object",
                  "properties": {
                    "Subject": { "type": "string", "description": "Human-readable description of what is being reviewed. Required." },
                    "ReviewPrompt": { "type": "string", "description": "Base prompt given to all reviewer agents. Required." },
                    "ReviewerAgents": { "type": "array", "items": { "type": "string" }, "description": "Agent names. Required." },
                    "AgentPerspectives": { "type": "object", "additionalProperties": { "type": "string" }, "description": "Optional per-agent perspective text keyed by agent name." },
                    "Synthesizer": { "type": "string", "description": "Agent that synthesizes divergent reviews. Required." }
                  },
                  "required": ["Subject", "ReviewPrompt", "ReviewerAgents", "Synthesizer"]
                }
                """),

        new WorkflowTypeEntry(
            Name: "AuthTokenRefreshWorkflow",
            Description: "Centralized OAuth token refresh. Runs on a 30-minute schedule. Start manually with ForceRefresh=true after placing fresh credentials.",
            Namespace: "fleet",
            TaskQueue: "fleet",
            InputSchema: """
                {
                  "type": "object",
                  "properties": {
                    "Providers": { "type": "array", "items": { "type": "string" }, "description": "Providers to refresh. Defaults to [\"claude\"]. Supported: \"claude\", \"codex\"." },
                    "ForceRefresh": { "type": "boolean", "description": "Skip expiry threshold and refresh immediately. Use after placing fresh credentials." }
                  }
                }
                """),

    ];
}
