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
/// <param name="attachments">
/// The byte store, when attachments are configured. Null leaves every attachment path inert, which
/// is what an install without an attachment root gets.
/// </param>
public sealed class GarbageCollector(
    string connectionString,
    ConversationStoreOptions options,
    ILogger logger,
    AttachmentStore? attachments = null)
{
    /// <summary>What one pass removed.</summary>
    public sealed record SweepResult
    {
        public required int EphemeralEvents { get; init; }
        public required int DurableEvents { get; init; }
        public required int FloorsAdvanced { get; init; }
        public required int OutboxRows { get; init; }
        public required int DeliveryClaims { get; init; }

        /// <summary>Attachment rows removed with the durable events that referenced them.</summary>
        public int PrunedAttachments { get; init; }

        /// <summary>
        /// Attachment rows collected because they will never become a transcript entry: expired
        /// reservations, sealed-but-abandoned uploads, and failed verifications.
        /// </summary>
        public int StrandedAttachments { get; init; }

        /// <summary>Files on the volume with no row at all.</summary>
        public int OrphanFiles { get; init; }
    }

    public async Task<SweepResult> SweepOnceAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        var ephemeral = await PruneEphemeralAsync(connection, ct);
        var (durable, floors, prunedAttachments) = await PruneDurableAsync(connection, ct);
        var outbox = await PrunePublishedOutboxAsync(connection, ct);
        var claims = await PruneClaimsAsync(connection, ct);
        var strandedAttachments = await SweepStrandedAttachmentsAsync(connection, ct);
        var orphans = await SweepOrphanFilesAsync(connection, ct);

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
        Count("attachment_row", prunedAttachments + strandedAttachments);

        if (orphans > 0) ConversationMetrics.AttachmentOrphanFiles.Add(orphans);

        return new SweepResult
        {
            EphemeralEvents = ephemeral,
            DurableEvents = durable,
            FloorsAdvanced = floors,
            OutboxRows = outbox,
            DeliveryClaims = claims,
            PrunedAttachments = prunedAttachments,
            StrandedAttachments = strandedAttachments,
            OrphanFiles = orphans,
        };
    }

    /// <summary>
    /// Collect the three attachment rows that will never become a transcript entry (#308 D3, AC-24).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three classes, each on the window that belongs to it:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>reserved</c> past the <b>upload</b> window — a reservation whose PUT never
    ///   arrived.</item>
    ///   <item><c>sealed</c> past the <b>submit</b> window — the upload succeeded and the send was
    ///   abandoned.</item>
    ///   <item><c>failed</c> — verification refused the bytes; the row is kept briefly only so a
    ///   retry sees a consistent answer.</item>
    /// </list>
    /// <para>
    /// ⚠️ The first two anchor on DIFFERENT columns, and that is the point: <c>created_at</c> for the
    /// upload window and <c>sealed_at</c> for the submit window. Sharing one anchor would leave a
    /// client that sealed at minute 14 exactly one minute to send its message.
    /// </para>
    /// <para>
    /// <c>bound</c> is never collected here. A bound row's lifetime is its event's, and it is removed
    /// by the floor-advancing prune above — one horizon per artifact (MUST NOT 13).
    /// </para>
    /// </remarks>
    private async Task<int> SweepStrandedAttachmentsAsync(
        MySqlConnection connection, CancellationToken ct)
    {
        var doomed = new List<string>();

        doomed.AddRange(await SelectStrandedAsync(
            connection,
            "state = 'reserved' AND created_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @window SECOND)",
            (long)Fleet.Protocol.ProtocolLimits.AttachmentUploadWindow.TotalSeconds, ct));

        doomed.AddRange(await SelectStrandedAsync(
            connection,
            "state = 'sealed' AND sealed_at IS NOT NULL "
            + "AND sealed_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @window SECOND)",
            (long)Fleet.Protocol.ProtocolLimits.AttachmentSubmitWindow.TotalSeconds, ct));

        doomed.AddRange(await SelectStrandedAsync(
            connection,
            "state = 'failed' AND created_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @window SECOND)",
            (long)Fleet.Protocol.ProtocolLimits.AttachmentUploadWindow.TotalSeconds, ct));

        if (doomed.Count == 0) return 0;

        var names = doomed
            .Select((_, index) => $"@s{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            .ToList();

        int removed;

        await using (var drop = new MySqlCommand(
            $"DELETE FROM conversation_attachments WHERE id IN ({string.Join(", ", names)})",
            connection))
        {
            for (var i = 0; i < doomed.Count; i++) drop.Parameters.AddWithValue(names[i], doomed[i]);
            removed = await drop.ExecuteNonQueryAsync(ct);
        }

        // Rows first, files after — for the same reason the durable prune does it in that order.
        if (attachments is not null)
        {
            foreach (var id in doomed) attachments.Delete(id);
        }

        return removed;
    }

    private async Task<List<string>> SelectStrandedAsync(
        MySqlConnection connection, string predicate, long windowSeconds, CancellationToken ct)
    {
        var ids = new List<string>();

        await using var command = new MySqlCommand(
            $"SELECT id FROM conversation_attachments WHERE {predicate} LIMIT @batch", connection);

        command.Parameters.AddWithValue("@window", windowSeconds);
        command.Parameters.AddWithValue("@batch", options.OutboxBatchSize);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// Delete files on the volume that no row knows about (#308 D3, AC-24).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the safety net for the one window the design deliberately leaves open: rows are
    /// deleted inside a transaction and files after it commits, so a crash between the two leaves a
    /// file with no row. Deleting inside the transaction is not an option — a filesystem delete
    /// cannot be rolled back (MUST NOT 14).
    /// </para>
    /// <para>
    /// Ids are checked against the database in one query rather than one per file, because the
    /// alternative is a round trip per file on a volume that grows.
    /// </para>
    /// </remarks>
    private async Task<int> SweepOrphanFilesAsync(MySqlConnection connection, CancellationToken ct)
    {
        if (attachments is null) return 0;

        var onDisk = attachments.EnumerateIds();
        if (onDisk.Count == 0) return 0;

        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var batch in onDisk.Chunk(options.OutboxBatchSize))
        {
            var names = batch
                .Select((_, index) => $"@f{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                .ToList();

            await using var command = new MySqlCommand(
                $"SELECT id FROM conversation_attachments WHERE id IN ({string.Join(", ", names)})",
                connection);

            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue(names[i], batch[i]);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) known.Add(reader.GetString(0));
        }

        var removed = 0;

        foreach (var id in onDisk.Where(id => !known.Contains(id)))
        {
            attachments.Delete(id);
            removed++;
        }

        if (removed > 0)
            logger.LogInformation("garbage collection removed {Count} orphan attachment file(s)", removed);

        return removed;
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
    private async Task<(int Deleted, int Floors, int Attachments)> PruneDurableAsync(
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
        var prunedAttachments = 0;

        // Collected across every conversation and deleted AFTER the loop, so a file delete never
        // happens inside an open transaction.
        var filesToDelete = new List<string>();

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

                // The attachment rows go FIRST, and in this same transaction (#308 D3, MUST NOT 13).
                //
                // The only reference to an attachment lives on the event about to be pruned, so
                // "pruned beneath the floor while still referenced" cannot occur — but only if the
                // row goes with the event rather than on a horizon of its own. Two retention knobs
                // for one artifact is how a transcript entry and its image come to disagree.
                //
                // Ids are read before the delete because after it there is nothing left to name the
                // files by.
                var doomed = new List<string>();

                await using (var select = new MySqlCommand(
                    """
                    SELECT a.id
                      FROM conversation_attachments a
                      JOIN conversation_events e
                        ON e.conversation_id = a.conversation_id AND e.seq = a.event_seq
                     WHERE a.conversation_id = @id
                       AND a.event_seq IS NOT NULL
                       AND e.retention_class = 'durable'
                       AND e.emitted_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND)
                    """, connection, transaction))
                {
                    select.Parameters.AddWithValue("@id", conversationId);
                    select.Parameters.AddWithValue("@seconds", horizon);

                    await using var reader = await select.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) doomed.Add(reader.GetString(0));
                }

                if (doomed.Count > 0)
                {
                    var names = doomed
                        .Select((_, index) => $"@d{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                        .ToList();

                    await using var drop = new MySqlCommand(
                        $"DELETE FROM conversation_attachments WHERE id IN ({string.Join(", ", names)})",
                        connection, transaction);

                    for (var i = 0; i < doomed.Count; i++)
                        drop.Parameters.AddWithValue(names[i], doomed[i]);

                    prunedAttachments += await drop.ExecuteNonQueryAsync(ct);
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

                // Only after the commit. A file delete cannot be rolled back, so doing it inside
                // would destroy bytes a failed transaction still claims exist.
                filesToDelete.AddRange(doomed);
            }
            catch (Exception e)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                logger.LogWarning(
                    "garbage collection could not prune a conversation: {Error}", e.GetType().Name);
            }
        }

        // A crash between the commits above and this leaves orphan files, which SweepOrphanFilesAsync
        // collects. That is the intended safety net, not an oversight.
        if (attachments is not null)
        {
            foreach (var id in filesToDelete) attachments.Delete(id);
        }

        return (deleted, floors, prunedAttachments);
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
