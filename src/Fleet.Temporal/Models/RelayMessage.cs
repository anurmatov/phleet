namespace Fleet.Temporal.Models;

/// <summary>
/// Mirrors Fleet.Agent's RelayMessage — used for RabbitMQ serialization.
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
    string? SignalName = null);

public static class RelayMessageType
{
    public const string Directive = "directive";
    public const string Response = "response";
    public const string PartialResponse = "partial-response";
    public const string WorkflowSignal = "workflow-signal";
}
