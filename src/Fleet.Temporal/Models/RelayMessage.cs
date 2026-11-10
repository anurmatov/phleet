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
    string? Repo = null);

public static class RelayMessageType
{
    public const string Directive = "directive";
    public const string Response = "response";
    public const string PartialResponse = "partial-response";
    public const string WorkflowSignal = "workflow-signal";
}
