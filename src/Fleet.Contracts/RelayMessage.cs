using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Fleet.Contracts;

/// <summary>
/// The one message on the relay exchange: agent to agent, bridge to agent and back, and the
/// orchestrator's notices to a running agent.
/// </summary>
/// <remarks>
/// <para>
/// The fields were added over time and always at the end, so each producer writes a prefix of this
/// list (<see cref="RelayShape"/>) and every consumer reads whatever prefix it is given: a field a
/// producer does not write deserializes as its default here.
/// </para>
/// <para>
/// <c>Repo</c> (#347) is the <c>owner/name</c> a workflow delegation is about. Only the temporal
/// bridge writes it, it is kept for workflow replay, and the agent ignores it (#346).
/// </para>
/// </remarks>
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

/// <summary>The value of <see cref="RelayMessage.Type"/>.</summary>
public static class RelayMessageType
{
    public const string Directive = "directive";
    public const string Response = "response";
    public const string PartialResponse = "partial-response";

    /// <summary>A refreshed OAuth token, broadcast by the temporal bridge on the fanout exchange.</summary>
    public const string TokenUpdate = "token-update";

    public const string StatusCheck = "status-check";
    public const string StatusResponse = "status-response";
    public const string BridgeRequest = "bridge-request";
    public const string BridgeResponse = "bridge-response";

    /// <summary>An agent's signal for a workflow, sent to the temporal bridge.</summary>
    public const string WorkflowSignal = "workflow-signal";

    /// <summary>Published by the orchestrator when AllowedUserIds or AllowedGroupIds change.</summary>
    public const string ConfigUpdate = "config.update";

    /// <summary>Published by an agent to a control-plane agent when an unknown DM requests access.</summary>
    public const string AccessRequest = "access.request";
}

/// <summary>The value of <see cref="RelayMessage.Sender"/> for the senders that are not agents.</summary>
public static class RelaySender
{
    public const string TemporalBridge = "temporal-bridge";
    public const string Orchestrator = "orchestrator";

    /// <summary>fleet-bridge's own messages. A relayed question is <c>bridge:{agent}[:{project}]</c>.</summary>
    public const string Bridge = "bridge";
}

/// <summary>
/// How many of <see cref="RelayMessage"/>'s fields a producer writes, counted from the first.
/// </summary>
/// <remarks>
/// Each value is what that producer has always written. Writing more would be harmless to a current
/// consumer and is still not done: a sample under <c>tests/fixtures/wire</c> holds every producer to
/// its bytes, so a change here is deliberate and shows up as a changed sample.
/// </remarks>
public enum RelayShape
{
    /// <summary>Through <c>Type</c>: the orchestrator's notices and the token broadcast.</summary>
    Envelope = 5,

    /// <summary>Through <c>TaskId</c>: fleet-bridge.</summary>
    Bridge = 7,

    /// <summary>Through <c>SignalName</c>: an agent.</summary>
    Agent = 9,

    /// <summary>Every field, <c>Repo</c> included: a delegation from the temporal bridge.</summary>
    Delegation = 10,
}

/// <summary>Reads and writes a <see cref="RelayMessage"/> as it is on the broker.</summary>
public static class RelayWire
{
    /// <summary>The fields in the order they are declared and written.</summary>
    private static readonly string[] Fields =
    [
        nameof(RelayMessage.ChatId), nameof(RelayMessage.Sender), nameof(RelayMessage.Text),
        nameof(RelayMessage.Timestamp), nameof(RelayMessage.Type), nameof(RelayMessage.CorrelationId),
        nameof(RelayMessage.TaskId), nameof(RelayMessage.WorkflowId), nameof(RelayMessage.SignalName),
        nameof(RelayMessage.Repo),
    ];

    // Declared after Fields: static initializers run in order, and OptionsFor reads it.
    private static readonly Dictionary<RelayShape, JsonSerializerOptions> Writers =
        Enum.GetValues<RelayShape>().ToDictionary(shape => shape, OptionsFor);

    /// <summary>The UTF-8 JSON <paramref name="shape"/>'s producer writes. Nulls are written.</summary>
    public static byte[] Serialize(RelayMessage message, RelayShape shape) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Writers[shape]);

    /// <summary>Any producer's message. Null for a JSON <c>null</c>; throws on anything malformed.</summary>
    public static RelayMessage? Deserialize(string json) => JsonSerializer.Deserialize<RelayMessage>(json);

    private static JsonSerializerOptions OptionsFor(RelayShape shape)
    {
        var written = Fields.Take((int)shape).ToHashSet(StringComparer.Ordinal);

        return new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    info =>
                    {
                        if (info.Type != typeof(RelayMessage)) return;
                        for (var i = info.Properties.Count - 1; i >= 0; i--)
                        {
                            if (!written.Contains(info.Properties[i].Name))
                                info.Properties.RemoveAt(i);
                        }
                    },
                },
            },
        };
    }
}
