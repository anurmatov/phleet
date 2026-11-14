using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations;

/// <summary>
/// <see cref="IChatHistoryReadStore"/> over the conversation event store.
/// </summary>
/// <remarks>
/// <para>
/// No schema change and no second copy: the transcript is read where it lives. The scan is bounded
/// by construction — an agent's own conversations (normally one per person, and there is one
/// person), the four message kinds, and the durable retention the deployment set — so a substring
/// match over the JSON text is adequate, and a full-text index is not worth a migration yet.
/// </para>
/// <para>
/// ⚠️ No text, query or id in any log line. A failure is logged by its exception type alone.
/// </para>
/// </remarks>
public sealed class MySqlChatHistoryReadStore : IChatHistoryReadStore
{
    /// <summary>The channel first-party clients open conversations on.</summary>
    public const string ClientChannelId = "client";

    private const string TextExpression = "JSON_UNQUOTE(JSON_EXTRACT(e.payload_json, '$.text'))";

    private readonly string _connectionString;
    private readonly ILogger _logger;

    public MySqlChatHistoryReadStore(string connectionString, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A conversation connection string is required.", nameof(connectionString));

        _connectionString = connectionString;
        _logger = logger;
    }

    private static string KindList { get; } =
        string.Join(", ", ReplyTarget.Kinds.Order(StringComparer.Ordinal).Select(kind => $"'{kind}'"));

    public async Task<ChatHistoryPage> SearchAsync(string agentName, ChatHistoryQuery query, CancellationToken ct = default)
    {
        // The scope is in the statement: the agent's own conversations on the client channel. Every
        // optional filter is an extra AND; none can widen it.
        var sql = new System.Text.StringBuilder(
            $"""
             SELECT e.event_id, e.conversation_id, e.kind, e.emitted_at, {TextExpression}
               FROM conversation_events e
               JOIN conversations c ON c.id = e.conversation_id
              WHERE c.agent_name = @agent
                AND c.channel_id = @channel
                AND e.kind IN ({KindList})
             """);

        if (query.FromUser is true) sql.Append(" AND e.kind = @userKind");
        if (query.FromUser is false) sql.Append(" AND e.kind <> @userKind");
        if (query.Since is not null) sql.Append(" AND e.emitted_at >= @since");
        if (query.Until is not null) sql.Append(" AND e.emitted_at < @until");
        if (query.Before is not null)
            sql.Append(" AND (e.emitted_at < @beforeAt OR (e.emitted_at = @beforeAt AND e.event_id < @beforeId))");
        if (query.Contains is not null)
            sql.Append($" AND LOWER({TextExpression}) LIKE @pattern ESCAPE '!'");

        sql.Append(" ORDER BY e.emitted_at DESC, e.event_id DESC LIMIT @take");

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = new MySqlCommand(sql.ToString(), connection);

            command.Parameters.AddWithValue("@agent", agentName);
            command.Parameters.AddWithValue("@channel", ClientChannelId);
            command.Parameters.AddWithValue("@userKind", ConversationEventKind.SubmissionText);
            if (query.Since is { } since) command.Parameters.AddWithValue("@since", since.UtcDateTime);
            if (query.Until is { } until) command.Parameters.AddWithValue("@until", until.UtcDateTime);
            if (query.Before is { } before)
            {
                command.Parameters.AddWithValue("@beforeAt", before.At.UtcDateTime);
                command.Parameters.AddWithValue("@beforeId", before.EventId);
            }
            if (query.Contains is { } contains)
                command.Parameters.AddWithValue("@pattern", "%" + EscapeLike(contains.ToLowerInvariant()) + "%");

            // One more than asked for, to know whether there is a next page without counting.
            command.Parameters.AddWithValue("@take", query.Limit + 1);

            var items = new List<ChatHistoryMessage>(query.Limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(Read(reader));

            if (items.Count <= query.Limit) return new ChatHistoryPage(items, null);

            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            return new ChatHistoryPage(items, new ChatHistoryPosition(last.At, last.EventId));
        }
        catch (MySqlException e)
        {
            _logger.LogWarning("chat history search could not be read: {Error}", e.GetType().Name);
            throw new JournalStoreUnavailableException(null, e);
        }
    }

    public async Task<ChatHistoryMessage?> GetAsync(string agentName, string eventId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = new MySqlCommand(
                $"""
                 SELECT e.event_id, e.conversation_id, e.kind, e.emitted_at, {TextExpression}
                   FROM conversation_events e
                   JOIN conversations c ON c.id = e.conversation_id
                  WHERE e.event_id = @event
                    AND c.agent_name = @agent
                    AND c.channel_id = @channel
                    AND e.kind IN ({KindList})
                 """, connection);

            command.Parameters.AddWithValue("@event", eventId);
            command.Parameters.AddWithValue("@agent", agentName);
            command.Parameters.AddWithValue("@channel", ClientChannelId);

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Read(reader) : null;
        }
        catch (MySqlException e)
        {
            _logger.LogWarning("chat history message could not be read: {Error}", e.GetType().Name);
            throw new JournalStoreUnavailableException(null, e);
        }
    }

    private static ChatHistoryMessage Read(MySqlDataReader reader) => new()
    {
        EventId = reader.GetString(0),
        ConversationId = reader.GetString(1),
        Kind = reader.GetString(2),
        At = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
        Text = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
    };

    /// <summary>The query as a literal: `%`, `_` and the escape character itself match only themselves.</summary>
    internal static string EscapeLike(string value) =>
        value.Replace("!", "!!", StringComparison.Ordinal)
             .Replace("%", "!%", StringComparison.Ordinal)
             .Replace("_", "!_", StringComparison.Ordinal);
}
