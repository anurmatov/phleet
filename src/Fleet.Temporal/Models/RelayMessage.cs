using System.Text.Json.Serialization;

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
///
/// <para>
/// <c>ExecutionId</c> names the one durable delegation a directive belongs to. Task ids are
/// reused by a later attempt of the same step, so the agent keeps this with the task and applies a
/// cancel that carries an execution id only to the task that carries the same one. It is written
/// only when set: a directive from the in-process path, or from a durable record that has none,
/// is the same bytes it always was, and an agent that does not know the field ignores it.
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
    string? Repo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExecutionId = null);

public static class RelayMessageType
{
    public const string Directive = "directive";
    public const string Response = "response";
    public const string PartialResponse = "partial-response";
    public const string WorkflowSignal = "workflow-signal";
}
