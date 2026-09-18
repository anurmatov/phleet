using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations;

/// <summary>One row claimed from an outbox, ready to publish.</summary>
public sealed record OutboxMessage
{
    public required ulong Id { get; init; }
    public required string ConversationId { get; init; }

    /// <summary>The identity consumers deduplicate on: <c>eventId</c>, or the command's message id.</summary>
    public required string DedupeId { get; init; }

    /// <summary>Routing key. The conversation id for events; the agent name for commands.</summary>
    public required string RoutingKey { get; init; }

    public string? Kind { get; init; }
    public string? PayloadJson { get; init; }
    public required ushort Attempts { get; init; }
}

/// <summary>
/// Publishes one batch and reports, per message, whether the BROKER CONFIRMED it.
/// </summary>
/// <remarks>
/// Abstracted so the outbox's own semantics — claiming, confirm ordering, backoff — are exercised
/// without a broker, and so the broker adapter stays a thin thing with no bookkeeping of its own.
/// An implementation MUST NOT report success before the broker confirms.
/// </remarks>
public interface IOutboxTransport
{
    /// <summary>The exchange this transport publishes to. Used only for logging.</summary>
    string Exchange { get; }

    /// <summary>
    /// Publishes every message and returns the ids the broker CONFIRMED.
    /// </summary>
    /// <remarks>
    /// Anything omitted from the result is retried. Returning an id that was not confirmed is the
    /// one thing this interface cannot detect and the one thing that loses a message.
    /// </remarks>
    Task<IReadOnlySet<ulong>> PublishAsync(IReadOnlyList<OutboxMessage> batch, CancellationToken ct);
}

/// <summary>
/// Drains one outbox table after commit.
/// </summary>
/// <remarks>
/// <para>
/// Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, which gives multiple publishers without a
/// distributed lock. Per-conversation order holds because the appender is single-writer per
/// conversation and <c>id</c> is monotonic.
/// </para>
/// <para>
/// <c>published_at</c> is written ONLY after the broker confirms. Marking a row published before the
/// confirm is how a message is lost while the row says it was delivered — and it is invisible,
/// because the outbox then has nothing left to retry.
/// </para>
/// </remarks>
public sealed class OutboxPublisher(
    string connectionString,
    string table,
    IOutboxTransport transport,
    ConversationStoreOptions options,
    ILogger logger)
{
    /// <summary>The event outbox: committed events, routed by conversation.</summary>
    public const string EventTable = "event_outbox";

    /// <summary>The command outbox: client submissions, steers and cancels, routed to the agent.</summary>
    public const string CommandTable = "command_outbox";

    /// <summary>
    /// Claims up to one batch, publishes it, and records the outcome.
    /// </summary>
    /// <returns>How many rows the broker confirmed.</returns>
    public async Task<int> DrainOnceAsync(string routingKey, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // The claim, the publish and the outcome are ONE transaction, and the row locks are held
        // across the broker round-trip. That is what makes `FOR UPDATE SKIP LOCKED` give multiple
        // publishers without a distributed lock: the lock is the exclusion.
        //
        // An earlier revision of this method committed the claim before publishing, on the argument
        // that holding a write transaction across a network call is a hazard. It is — but releasing
        // early removes the exclusion entirely, and a concurrent publisher then re-selects the very
        // same still-pending rows. The test `Two_publishers_never_claim_the_same_row` caught exactly
        // that: both claimed all eight. If the held transaction ever becomes a problem under load,
        // the answer is a visibility lease written INSIDE the claim, not an early commit.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var batch = await ClaimAsync(connection, transaction, routingKey, ct);

        if (batch.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        IReadOnlySet<ulong> confirmed;

        try
        {
            confirmed = await transport.PublishAsync(batch, ct);
        }
        catch (Exception e)
        {
            // The broker is unreachable. Rows stay pending, the pending-age metric rises, nothing is
            // marked published, and the client is unaffected because its submission is already
            // durable and will dispatch on recovery.
            logger.LogWarning(
                "outbox publish to {Exchange} failed for {Count} row(s): {Error}",
                transport.Exchange, batch.Count, e.GetType().Name);

            await BackoffAsync(connection, transaction, batch, ct);
            await transaction.CommitAsync(ct);
            return 0;
        }

        var unconfirmed = batch.Where(m => !confirmed.Contains(m.Id)).ToList();

        if (confirmed.Count > 0)
        {
            await MarkPublishedAsync(connection, transaction, confirmed, ct);

            ConversationMetrics.OutboxPublished.Add(
                confirmed.Count, new KeyValuePair<string, object?>("outbox", table));
        }

        if (unconfirmed.Count > 0)
        {
            // A confirm lost after publishing leaves the row pending, so it is republished and the
            // consumer deduplicates on the dedupe id. Republishing is the safe direction; marking it
            // published is not.
            logger.LogInformation(
                "{Count} outbox row(s) to {Exchange} were not confirmed and will be retried",
                unconfirmed.Count, transport.Exchange);

            ConversationMetrics.OutboxUnconfirmed.Add(
                unconfirmed.Count, new KeyValuePair<string, object?>("outbox", table));

            await BackoffAsync(connection, transaction, unconfirmed, ct);
        }

        await transaction.CommitAsync(ct);
        return confirmed.Count;
    }

    private async Task<List<OutboxMessage>> ClaimAsync(
        MySqlConnection connection, MySqlTransaction transaction, string routingKey, CancellationToken ct)
    {
        var isCommand = table == CommandTable;

        var sql = isCommand
            ? """
              SELECT id, conversation_id, message_id, kind, payload_json, attempts
                FROM command_outbox
               WHERE state = 'pending' AND next_attempt_at <= UTC_TIMESTAMP(6)
               ORDER BY id LIMIT @batch
                 FOR UPDATE SKIP LOCKED
              """
            : """
              SELECT id, conversation_id, event_id, NULL, NULL, attempts
                FROM event_outbox
               WHERE state = 'pending' AND next_attempt_at <= UTC_TIMESTAMP(6)
               ORDER BY id LIMIT @batch
                 FOR UPDATE SKIP LOCKED
              """;

        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@batch", options.OutboxBatchSize);

        var batch = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var conversationId = reader.GetString(1);

            batch.Add(new OutboxMessage
            {
                Id = reader.GetUInt64(0),
                ConversationId = conversationId,
                DedupeId = reader.GetString(2),
                Kind = reader.IsDBNull(3) ? null : reader.GetString(3),
                PayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                Attempts = reader.GetUInt16(5),

                // Commands route to one agent; events route per conversation.
                RoutingKey = isCommand ? routingKey : conversationId,
            });
        }

        return batch;
    }

    private async Task MarkPublishedAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        IReadOnlySet<ulong> confirmed, CancellationToken ct)
    {
        var ids = string.Join(", ", Enumerable.Range(0, confirmed.Count).Select(i => $"@p{i}"));

        await using var command = new MySqlCommand(
            $"""
            UPDATE {table}
               SET state = 'published', published_at = UTC_TIMESTAMP(6)
             WHERE id IN ({ids})
            """, connection, transaction);

        var index = 0;
        foreach (var id in confirmed) command.Parameters.AddWithValue($"@p{index++}", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Exponential backoff, capped so a long outage does not push the next attempt past the point
    /// anyone is still watching.
    /// </summary>
    private async Task BackoffAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        IReadOnlyList<OutboxMessage> rows, CancellationToken ct)
    {
        var ids = string.Join(", ", Enumerable.Range(0, rows.Count).Select(i => $"@p{i}"));

        await using var command = new MySqlCommand(
            $"""
            UPDATE {table}
               SET attempts = attempts + 1,
                   next_attempt_at = UTC_TIMESTAMP(6)
                     + INTERVAL POW(2, LEAST(attempts, 6)) SECOND
             WHERE id IN ({ids})
            """, connection, transaction);

        for (var i = 0; i < rows.Count; i++)
            command.Parameters.AddWithValue($"@p{i}", rows[i].Id);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Pending depth and the age of the oldest pending row, for the outbox metrics.</summary>
    public async Task<(long Depth, TimeSpan OldestAge)> MeasureBacklogAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new MySqlCommand(
            $"""
            SELECT COUNT(*),
                   COALESCE(TIMESTAMPDIFF(SECOND, MIN(next_attempt_at), UTC_TIMESTAMP(6)), 0)
              FROM {table} WHERE state = 'pending'
            """, connection);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (0, TimeSpan.Zero);

        return (reader.GetInt64(0), TimeSpan.FromSeconds(Math.Max(0, reader.GetInt64(1))));
    }
}
