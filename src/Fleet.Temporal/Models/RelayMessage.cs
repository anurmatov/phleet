namespace Fleet.Temporal.Models;

/// <summary>
/// Mirrors Fleet.Agent's RelayMessage — used for RabbitMQ serialization.
///
/// <para>
/// <c>Repo</c> (#347) is the <c>owner/name</c> a workflow delegation is about, set from the UWE
/// step's <c>repo</c> field. It travels as a structured field so the agent can route project
/// context by it without ever parsing directive text. It is trailing and optional, so a payload
/// written before it existed deserializes with <c>Repo = null</c>.
/// </para>
/// </summary>
/// <para>
/// <c>Deadline</c> (#492 X4) is set only on a managed workflow directive: the delegating activity's own
/// StartToClose deadline, so the agent keeps the reply session for as long as the activity can accept it.
/// </para>
public sealed record RelayMessage(
    long ChatId,
    string Sender,
    string Text,
    DateTimeOffset Timestamp,
    string Type = RelayMessageType.Directive,
    string? CorrelationId = null,
    string? TaskId = null,
    string? WorkflowId = null,
    string? SignalName = null,
    string? Repo = null,
    Guid? Epoch = null,
    string? Sandbox = null,
    DateTimeOffset? Deadline = null);

public static class RelayMessageType
{
    public const string Directive = "directive";
    public const string Response = "response";
    public const string PartialResponse = "partial-response";
    public const string WorkflowSignal = "workflow-signal";
    /// <summary>Typed workflow directive to a managed stock agent over its epoch control lane (#492).</summary>
    public const string WorkflowDirective = "workflow-directive";
}
