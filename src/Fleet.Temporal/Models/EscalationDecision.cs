using System.Text.Json.Serialization;

namespace Fleet.Temporal.Models;

/// <summary>
/// Constants for the escalation decision signal sent by the escalation target when a workflow step fails.
/// </summary>
public static class EscalationDecision
{
    /// <summary>Retry the failed step, optionally with updated instructions.</summary>
    public const string Retry = "retry";

    /// <summary>Skip remaining steps and surface whatever results were collected so far.</summary>
    public const string Skip = "skip";

    /// <summary>Proceed anyway despite the failure — treat the failed step as if it completed.</summary>
    public const string Continue = "continue";
}

/// <summary>
/// Signal payload sent by the escalation target when making an escalation decision.
/// </summary>
/// <param name="Decision">One of <see cref="EscalationDecision"/> constants.</param>
/// <param name="UpdatedInstruction">
/// Optional updated instruction to pass to the failed agent when retrying.
/// Ignored for Skip and Continue decisions.
/// </param>
public sealed record EscalationSignalPayload(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("updatedInstruction")] string? UpdatedInstruction = null);
