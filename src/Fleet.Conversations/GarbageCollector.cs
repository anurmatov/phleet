using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations;

/// <summary>
/// Prunes expired rows, and advances the retained floor when — and only when — it removes durable
/// history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Retention here is a garbage-collection horizon, not a deletion feature.</b> Nothing in this
/// class serves a user's request to erase anything, and no copy shipped with it may describe it as
/// one.
/// </para>
/// <para>
/// <b>The two kinds of prune are different events, and conflating them is the bug this class is
/// shaped to avoid.</b> Pruning an <i>ephemeral</i> row leaves no gap: its absence at a seq is not
/// a hole in history, it is the retention class doing what it says. Pruning a <i>durable</i> row
/// does leave a gap, and the reader has to be told — so the floor moves with it, in the same
/// transaction as the delete.
/// </para>
/// </remarks>
public sealed class GarbageCollector(
    string connectionString,
    ConversationStoreOptions options,
    ILogger logger)
{
    /// <summary>What one pass removed.</summary>
    public sealed record SweepResult
    {
        public required int EphemeralEvents { get; init; }
        public required int DurableEvents { get; init; }
        public required int FloorsAdvanced { get; init; }
        public required int OutboxRows { get; init; }
        public required int DeliveryClaims { get; init; }
    }

    public async Task<SweepResult> SweepOnceAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        var ephemeral = await PruneEphemeralAsync(connection, ct);
        var (durable, floors) = await PruneDurableAsync(connection, ct);
        var outbox = await PrunePublishedOutboxAsync(connection, ct);
        var claims = await PruneClaimsAsync(connection, ct);

        if (ephemeral + durable + outbox + claims > 0)
        {
            logger.LogInformation(
                "garbage collection removed {Ephemeral} ephemeral, {Durable} durable, {Outbox} outbox "
                + "and {Claims} claim row(s)",
                ephemeral, durable, outbox, claims);
        }

        Count("ephemeral_event", ephemeral);
        Count("durable_event", durable);
        Count("outbox_row", outbox);
        Count("delivery_claim", claims);

        return new SweepResult
        {
            EphemeralEvents = ephemeral,
            DurableEvents = durable,
            FloorsAdvanced = floors,
            OutboxRows = outbox,
            DeliveryClaims = claims,
        };
    }

    /// <summary>
    /// Prune expired ephemeral events. The floor is deliberately NOT touched.
    /// </summary>
    /// <remarks>
    /// A pruned ephemeral row between two surviving durable rows is a hole in the seq sequence and
    /// is <b>not</b> a gap. Emitting one would tell a client it had lost history it was never
    /// promised, and every reconnect would report a loss that did not happen.
    /// </remarks>
    private async Task<int> PruneEphemeralAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            """
            DELETE FROM conversation_events
             WHERE retention_class = 'ephemeral'
               AND emitted_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND)
             LIMIT @batch
            """, connection);

        command.Parameters.AddWithValue("@seconds", (long)options.EphemeralEventRetention.TotalSeconds);
        command.Parameters.AddWithValue("@batch", options.OutboxBatchSize);

        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Prune expired durable events and advance <c>retained_floor_seq</c> — in ONE transaction, per
    /// conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>The delete and the floor move are the same transaction, and that is the whole
    /// mechanism.</b> Advancing the floor separately opens a window in which the rows are gone and
    /// the floor still says they are there: a reader in that window gets a short page with no gap
    /// announcement and concludes it has the complete suffix. That is a silent loss, and it is
    /// exactly what a catch-up cursor cannot detect.
    /// </para>
    /// <para>
    /// The floor becomes the lowest SURVIVING durable seq rather than the highest deleted one plus
    /// one, because an ephemeral row may sit between them and the floor has to name a position a
    /// reader can actually read from.
    /// </para>
    /// </remarks>
    private async Task<(int Deleted, int Floors)> PruneDurableAsync(
        MySqlConnection connection, CancellationToken ct)
    {
        var horizon = (long)options.DurableEventRetention.TotalSeconds;
        var candidates = new List<string>();

        await using (var select = new MySqlCommand(
            """
            SELECT DISTINCT conversation_id
              FROM conversation_events
             WHERE retention_class = 'durable'
               AND emitted_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND)
             LIMIT @batch
            """, connection))
        {
            select.Parameters.AddWithValue("@seconds", horizon);
            select.Parameters.AddWithValue("@batch", options.OutboxBatchSize);

            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                candidates.Add(reader.GetString(0));
        }

        var deleted = 0;
        var floors = 0;

        foreach (var conversationId in candidates)
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);

            try
            {
                // The conversation row lock, so a concurrent append cannot land between the delete
                // and the floor move.
                await using (var locked = new MySqlCommand(
                    "SELECT id FROM conversations WHERE id = @id FOR UPDATE", connection, transaction))
                {
                    locked.Parameters.AddWithValue("@id", conversationId);
                    await locked.ExecuteScalarAsync(ct);
                }

                int removed;

                await using (var prune = new MySqlCommand(
                    """
                    DELETE FROM conversation_events
                     WHERE conversation_id = @id
                       AND retention_class = 'durable'
                       AND emitted_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND)
                    """, connection, transaction))
                {
                    prune.Parameters.AddWithValue("@id", conversationId);
                    prune.Parameters.AddWithValue("@seconds", horizon);
                    removed = await prune.ExecuteNonQueryAsync(ct);
                }

                if (removed == 0)
                {
                    await transaction.RollbackAsync(ct);
                    continue;
                }

                await using (var advance = new MySqlCommand(
                    """
                    UPDATE conversations c
                       SET c.retained_floor_seq = GREATEST(
                             c.retained_floor_seq,
                             COALESCE((SELECT MIN(e.seq) FROM conversation_events e
                                        WHERE e.conversation_id = c.id
                                          AND e.retention_class = 'durable'),
                                      c.next_seq))
                     WHERE c.id = @id
                    """, connection, transaction))
                {
                    advance.Parameters.AddWithValue("@id", conversationId);
                    await advance.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                deleted += removed;
                floors++;
            }
            catch (Exception e)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                logger.LogWarning(
                    "garbage collection could not prune a conversation: {Error}", e.GetType().Name);
            }
        }

        return (deleted, floors);
    }

    /// <summary>
    /// Prune outbox rows that have been PUBLISHED for longer than the retention.
    /// </summary>
    /// <remarks>
    /// Published only. A pending row is work that has not happened yet, and deleting one would drop
    /// a command or an event with nothing left to notice.
    /// </remarks>
    private async Task<int> PrunePublishedOutboxAsync(MySqlConnection connection, CancellationToken ct)
    {
        var removed = 0;

        foreach (var table in new[] { OutboxPublisher.EventTable, OutboxPublisher.CommandTable })
        {
            await using var command = new MySqlCommand(
                $"""
                 DELETE FROM {table}
                  WHERE state = 'published'
                    AND published_at IS NOT NULL
                    AND published_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND)
                  LIMIT @batch
                 """, connection);

            command.Parameters.AddWithValue("@seconds", (long)options.OutboxRetention.TotalSeconds);
            command.Parameters.AddWithValue("@batch", options.OutboxBatchSize);

            removed += await command.ExecuteNonQueryAsync(ct);
        }

        return removed;
    }

    /// <summary>
    /// Prune delivery claims past their retention.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This retention must exceed the broker's redelivery horizon, and also the longest a
    /// submission can legitimately wait behind a running turn.</b> A <c>done</c> claim within its
    /// retention is the ONLY thing standing between a redelivered command and a duplicate turn —
    /// the attempt state machine refuses a second start of a running attempt, not a first one — so
    /// collecting a claim early removes the guard rather than tightening it.
    /// </remarks>
    private async Task<int> PruneClaimsAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            """
            DELETE FROM delivery_claims
             WHERE claimed_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND)
             LIMIT @batch
            """, connection);

        command.Parameters.AddWithValue(
            "@seconds", (long)options.DeliveryClaimRetention.TotalSeconds);
        command.Parameters.AddWithValue("@batch", options.OutboxBatchSize);

        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void Count(string kind, int removed)
    {
        if (removed > 0)
            ConversationMetrics.Collected.Add(removed, new KeyValuePair<string, object?>("kind", kind));
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
