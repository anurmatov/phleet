namespace Fleet.Temporal.Mcp;

/// <summary>
/// Forwards a delegated gate approval (#436) to the orchestrator, which alone decides it under an
/// active epic grant and alone sends the signal. The bridge never sends a delegated approval
/// itself, so a refusal, a transport failure or a timeout here means nothing was sent.
/// </summary>
public interface IEpicGrantDecisionForwarder
{
    /// <summary>
    /// One <c>POST api/epic-grants/{grantId}/decisions</c>. One attempt, no retry. Never throws for
    /// transport, timeout, configuration or response-shape problems — those come back as
    /// <see cref="EpicGrantDecisionResult.Error"/>.
    /// </summary>
    Task<EpicGrantDecisionResult> ForwardAsync(
        string grantId,
        EpicGrantDecisionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The forwarded body. Serialized camelCase, in this order:
/// <c>namespace, workflowId, gate, decision, visitId, artifactRef, evidence, caller</c>.
/// <see cref="Gate"/> is the canonical lowercase gate name.
/// </summary>
public sealed record EpicGrantDecisionRequest(
    string Namespace,
    string WorkflowId,
    string Gate,
    string Decision,
    string VisitId,
    string ArtifactRef,
    string Evidence,
    string Caller);

/// <summary>
/// What the orchestrator answered, or why there is no answer.
///
/// <para>When <see cref="Error"/> is null the orchestrator returned a JSON object carrying a string
/// <c>result</c> (<c>sent</c>, <c>refused</c> or <c>send_failed</c>), with its <c>reason</c> and
/// <c>decisionId</c> when present. When <see cref="Error"/> is set there was no usable answer:
/// unconfigured base address, transport failure, timeout, a non-JSON body or a JSON body without a
/// decision result.</para>
/// </summary>
public sealed record EpicGrantDecisionResult
{
    public string? Result { get; init; }
    public string? Reason { get; init; }
    public long? DecisionId { get; init; }
    public int? StatusCode { get; init; }
    public string? Error { get; init; }

    public static EpicGrantDecisionResult Failed(string error, int? statusCode = null) =>
        new() { Error = error, StatusCode = statusCode };
}
