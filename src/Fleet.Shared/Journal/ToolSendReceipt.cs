using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleet.Shared.Journal;

/// <summary>
/// What <c>fleet-telegram</c> reports after a Telegram MCP tool call got at least one message
/// accepted (#394). The agent that made the call consumes it, decides whether the send answered a
/// human, and journals it with its own ingest token — <c>fleet-telegram</c> itself never talks to
/// Comms.
/// </summary>
/// <remarks>
/// Wire shape (JSON, camelCase, <c>v: 1</c>). <see cref="RequestedAt"/> is taken before the first Bot
/// API call of the tool call. <see cref="Messages"/> are in send order.
/// </remarks>
public sealed record ToolSendReceipt
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ToolSendReceipts.Version;

    /// <summary>The calling agent's name, lower-cased (the <c>?agent=</c> attribution).</summary>
    public required string Agent { get; init; }

    /// <summary>The MCP tool that sent (<c>send_message</c>, <c>send_to_ceo</c>).</summary>
    public required string Tool { get; init; }

    /// <summary>UTC, taken before the first Bot API call.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>The id of the bot that sent. The agent drops a receipt that is not its own bot's.</summary>
    public required long BotId { get; init; }

    public required ToolSendChat Chat { get; init; }

    public required IReadOnlyList<ToolSendMessage> Messages { get; init; }
}

/// <param name="Type">The Bot API chat type string (<c>private</c>, <c>group</c>, <c>supergroup</c>, …).</param>
public sealed record ToolSendChat(long Id, string? Type, string? Title);

/// <param name="TextFormat">
/// <c>html</c> when sent with HTML parse mode, <c>plain</c> without one, <c>rich</c> for
/// <c>sendRichMessage</c> (the text is the Markdown source) — see <see cref="ToolSendTextFormats"/>.
/// </param>
public sealed record ToolSendMessage(
    long MessageId,
    DateTime Date,
    long? ReplyToMessageId,
    string? Text,
    string TextFormat);

public static class ToolSendTextFormats
{
    public const string Html = "html";
    public const string Plain = "plain";
    public const string Rich = "rich";

    public static bool IsKnown(string? value) => value is Html or Plain or Rich;
}

/// <summary>Broker names, limits and the JSON codec for <see cref="ToolSendReceipt"/>.</summary>
public static class ToolSendReceipts
{
    public const int Version = 1;

    /// <summary>Direct exchange. Routing key: the agent name, lower-cased.</summary>
    public const string Exchange = "fleet.journal.tool-sends";

    /// <summary>One durable queue per journaling agent: <c>fleet.journal.tool-sends.&lt;name&gt;</c>.</summary>
    public const string QueuePrefix = Exchange + ".";

    /// <summary>A receipt larger than this is refused by the consumer.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Queue arguments: 24 h TTL, 10,000 messages, oldest dropped on overflow.</summary>
    public const int QueueMessageTtlMs = 24 * 60 * 60 * 1000;
    public const int QueueMaxLength = 10_000;
    public const string QueueOverflow = "drop-head";

    public static string RoutingKey(string agentName) => agentName.Trim().ToLowerInvariant();

    public static string QueueName(string agentName) => QueuePrefix + RoutingKey(agentName);

    public static IDictionary<string, object?> QueueArguments() => new Dictionary<string, object?>
    {
        ["x-message-ttl"] = QueueMessageTtlMs,
        ["x-max-length"] = QueueMaxLength,
        ["x-overflow"] = QueueOverflow,
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize(ToolSendReceipt receipt) => JsonSerializer.SerializeToUtf8Bytes(receipt, Json);

    /// <summary>
    /// Parses a receipt body. Never throws. <paramref name="reason"/> is a fixed code on failure:
    /// <c>too_large</c>, <c>malformed</c>, <c>version</c>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out ToolSendReceipt? receipt, out string? reason)
    {
        receipt = null;
        reason = null;

        if (body.Length > MaxBytes)
        {
            reason = "too_large";
            return false;
        }

        try
        {
            // Version first, on its own, so a v2 body is refused as a version mismatch rather than
            // as whatever its new shape happens to fail on.
            using (var doc = JsonDocument.Parse(body.ToArray()))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("v", out var v)
                    || v.ValueKind != JsonValueKind.Number)
                {
                    reason = "malformed";
                    return false;
                }

                if (!v.TryGetInt32(out var version) || version != Version)
                {
                    reason = "version";
                    return false;
                }
            }

            var parsed = JsonSerializer.Deserialize<ToolSendReceipt>(body, Json);
            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.Agent)
                || string.IsNullOrWhiteSpace(parsed.Tool)
                || parsed.Chat is null
                || parsed.Messages is null
                || parsed.Messages.Count == 0
                || parsed.Messages.Any(m => m is null || !ToolSendTextFormats.IsKnown(m.TextFormat)))
            {
                reason = "malformed";
                return false;
            }

            receipt = parsed;
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            reason = "malformed";
            return false;
        }
    }
}
