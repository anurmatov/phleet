namespace Fleet.Contracts;

/// <summary>
/// The body of <c>POST /api/epic-grants/{grantId}/decisions</c>: a delegated gate approval (#436)
/// the temporal bridge forwards to the orchestrator, which alone decides it and alone sends the
/// signal.
/// </summary>
/// <remarks>
/// Serialized camelCase, in this order:
/// <c>namespace, workflowId, gate, decision, visitId, artifactRef, evidence, caller</c>.
/// <see cref="Gate"/> is the canonical lowercase gate name (<see cref="GateSignal"/>). The bridge
/// sets every member; they are nullable because the orchestrator checks what it was sent and
/// refuses a body with one missing instead of failing to bind it.
/// </remarks>
public sealed record EpicGrantDecisionRequest(
    string? Namespace,
    string? WorkflowId,
    string? Gate,
    string? Decision,
    string? VisitId,
    string? ArtifactRef,
    string? Evidence,
    string? Caller);
