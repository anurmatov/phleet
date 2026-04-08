namespace Fleet.Orchestrator.Models;

/// <summary>
/// A completed task record for an agent, captured when the current task transitions.
/// </summary>
public sealed record TaskRecord(
    string AgentName,
    string TaskText,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt)
{
    public double DurationSeconds => (EndedAt - StartedAt).TotalSeconds;
}
