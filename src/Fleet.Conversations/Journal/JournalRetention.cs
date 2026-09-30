using System.Globalization;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fleet.Conversations.Journal;

/// <summary>What an operator purge selects. Exactly one of the three scopes is set.</summary>
public sealed record JournalPurgeSelector
{
    public string? MessageId { get; init; }
    public string? ConversationId { get; init; }
    public long? TelegramChatId { get; init; }

    /// <summary>Only messages sent before this instant, when set.</summary>
    public DateTimeOffset? Before { get; init; }
}

/// <summary>
/// Rows a purge deleted, or would delete. <see cref="Objects"/> counts the archived media objects it
/// retired; their bytes stay in the bucket until the delete grace passes.
/// </summary>
public sealed record JournalPurgeCounts(
    long Messages, long Observers, long Attachments, long Conversations, long Objects = 0);

/// <summary>
/// The journal's retention sweep and the operator purge.
/// </summary>
/// <remarks>
/// <para>
/// A platform delete or edit never reaches a bot, so the journal keeps the original until the
/// retention horizon or an operator purge. Backups keep purged rows until the backup rotation.
/// </para>
/// <para>
/// Observers and attachments go with their message through <c>ON DELETE CASCADE</c>. A conversation
/// is deleted only once it holds no message; the foreign key from <c>journal_messages</c> has no
/// cascade, so a conversation that gained a message in between refuses the delete rather than
/// orphaning it.
/// </para>
/// </remarks>
public sealed class JournalRetention(
    string connectionString,
    TimeSpan retention,
    int batchSize,
    ILogger logger,
    JournalRuntimeStats? stats = null,
    TimeProvider? time = null,
    IJournalObjectStore? objects = null)
{
    /// <summary>Upper bound on batches per sweep, so one tick cannot run unbounded.</summary>
    private const int MaxBatchesPerSweep = 1000;

    /// <summary>
    /// How long a retired object's bytes are kept after its message is gone. A backup taken before
    /// the mark must still restore to a whole set, and a restore that re-creates a message whose
    /// object was already deleted is a hole nothing can repair afterwards.
    /// </summary>
    public static readonly TimeSpan DeleteGrace = TimeSpan.FromHours(72);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Objects this sweep retired: their messages are gone, so their bytes go after the delete
    /// grace. Zero on a deployment without media.
    /// </summary>
    public sealed record SweepResult(int Messages, int Conversations, int Objects = 0);

    /// <summary>
    /// Deletes messages sent before <c>now − retention</c>, in batches, then conversations left with
    /// no message.
    /// </summary>
    public async Task<SweepResult> SweepOnceAsync(CancellationToken ct = default)
    {
        var cutoff = (_time.GetUtcNow() - retention).UtcDateTime;
        var messages = 0;
        var conversations = 0;

        // Keys retired by THIS sweep. Nothing here deletes them — the grace window is the point —
        // and the object sweeper is what eventually removes the bytes.
        var retiredKeys = new List<string>();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        for (var i = 0; i < MaxBatchesPerSweep; i++)
        {
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            var batch = new List<string>();
            await using (var select = new MySqlCommand(
                "SELECT id FROM journal_messages WHERE sent_at < @cutoff ORDER BY sent_at, id LIMIT @batch FOR UPDATE",
                connection, transaction))
            {
                select.Parameters.AddWithValue("@cutoff", cutoff);
                select.Parameters.AddWithValue("@batch", batchSize);
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) batch.Add(reader.GetString(0));
            }
            if (batch.Count == 0) break;
            var names = batch.Select((_, index) => $"@m{index}").ToArray();
            var selection = string.Join(",", names);

            if (objects is not null)
            {
                var doomed = new List<(string Id, string Key)>();
                await using (var select = BatchCommand(
                    $"SELECT DISTINCT o.id, o.object_key FROM journal_objects o "
                    + $"JOIN journal_attachments a ON a.object_id = o.id WHERE a.message_id IN ({selection}) ORDER BY o.id"))
                {
                    await using var reader = await select.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) doomed.Add((reader.GetString(0), reader.GetString(1)));
                }
                foreach (var (id, key) in doomed)
                {
                    // Lock before checking references, as on the commit and object-sweep paths.
                    await using (var held = new MySqlCommand(
                        "SELECT id FROM journal_objects WHERE id = @id FOR UPDATE", connection, transaction))
                    {
                        held.Parameters.AddWithValue("@id", id);
                        await held.ExecuteScalarAsync(ct);
                    }
                    await using var retire = BatchCommand(
                        "UPDATE journal_objects SET state = 'deleting', committed_sha256 = NULL, "
                        + "delete_after = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @grace SECOND), updated_at = UTC_TIMESTAMP(6) "
                        + "WHERE id = @id AND state = 'committed' AND NOT EXISTS "
                        + $"(SELECT 1 FROM journal_attachments WHERE object_id = @id AND message_id NOT IN ({selection}))");
                    retire.Parameters.AddWithValue("@id", id);
                    retire.Parameters.AddWithValue("@grace", (int)DeleteGrace.TotalSeconds);
                    if (await retire.ExecuteNonQueryAsync(ct) == 1) retiredKeys.Add(key);
                }
            }
            await using var command = BatchCommand($"DELETE FROM journal_messages WHERE id IN ({selection})");
            messages += await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            if (batch.Count < batchSize) break;

            MySqlCommand BatchCommand(string sql)
            {
                var command = new MySqlCommand(sql, connection, transaction);
                for (var index = 0; index < batch.Count; index++) command.Parameters.AddWithValue(names[index], batch[index]);
                return command;
            }
        }

        for (var i = 0; i < MaxBatchesPerSweep; i++)
        {
            await using var command = new MySqlCommand(
                """
                DELETE FROM journal_conversations
                 WHERE NOT EXISTS (SELECT 1 FROM journal_messages m
                                    WHERE m.conversation_id = journal_conversations.id)
                 LIMIT @batch
                """, connection);
            command.Parameters.AddWithValue("@batch", batchSize);

            var removed = await command.ExecuteNonQueryAsync(ct);
            conversations += removed;
            if (removed < batchSize) break;
        }

        if (messages + conversations > 0)
        {
            logger.LogInformation(
                "journal retention removed {Messages} message(s) and {Conversations} empty conversation(s)",
                messages, conversations);
        }

        stats?.RecordSweep(messages, conversations);
        return new SweepResult(messages, conversations, retiredKeys.Count);
    }

    /// <summary>Counts a sweep that failed. The next tick retries it.</summary>
    public void RecordFailure() => stats?.RecordSweepFailure();

    /// <summary>
    /// Deletes what <paramref name="selector"/> names, in ONE transaction, and returns the counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without <paramref name="confirm"/> the same statements run and the transaction is rolled back,
    /// so the counts a dry run prints are exactly what a confirmed run deletes, from the same code
    /// path, and no row changes.
    /// </para>
    /// <para>
    /// Conversations in the selector's scope that are left with no message are deleted too. On any
    /// database error the transaction rolls back and nothing is deleted.
    /// </para>
    /// <para>
    /// The archived objects the purged messages held are retired in the SAME transaction, before the
    /// messages go, under the same surviving-reference guard the retention sweep uses. Without that
    /// the delete cascades the attachment rows away, nothing connects an object to anything again,
    /// and its bytes sit in the bucket for the life of the archive with no row to schedule them.
    /// </para>
    /// </remarks>
    public static async Task<JournalPurgeCounts> PurgeAsync(
        string connectionString, JournalPurgeSelector selector, bool confirm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var scopes = (selector.MessageId is null ? 0 : 1)
            + (selector.ConversationId is null ? 0 : 1)
            + (selector.TelegramChatId is null ? 0 : 1);

        if (scopes != 1)
            throw new ArgumentException("exactly one of message, conversation or telegram chat is required", nameof(selector));

        // The conversations in scope, and the messages within them that match.
        var scope = selector switch
        {
            { MessageId: not null } =>
                "c.id IN (SELECT conversation_id FROM journal_messages WHERE id = @message)",
            { ConversationId: not null } => "c.id = @conversation",
            _ => "c.telegram_chat_id = @chat",
        };

        var match = selector switch
        {
            { MessageId: not null } => "m.id = @message",
            { ConversationId: not null } => "m.conversation_id = @conversation",
            _ => "m.conversation_id IN (SELECT id FROM journal_conversations WHERE telegram_chat_id = @chat)",
        };

        if (selector.Before is not null) match += " AND m.sent_at < @before";

        // The same predicate against a message aliased `m2`. The surviving-reference guard below
        // needs it: a subquery only sees the aliases in its OWN FROM, so reusing `{match}` there
        // would reference an `m` that subquery never introduced.
        var matchOn = match.Replace("m.", "m2.", StringComparison.Ordinal);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            // The scoped conversations first, locked, so an ingest cannot add to one mid-purge.
            var conversationIds = new List<string>();
            await using (var lockScope = Bind(new MySqlCommand(
                $"SELECT c.id FROM journal_conversations c WHERE {scope} FOR UPDATE", connection, transaction)))
            {
                await using var reader = await lockScope.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) conversationIds.Add(reader.GetString(0));
            }

            var observers = await CountAsync(
                $"SELECT COUNT(*) FROM journal_message_observers o JOIN journal_messages m ON m.id = o.message_id WHERE {match}");
            var attachments = await CountAsync(
                $"SELECT COUNT(*) FROM journal_attachments a JOIN journal_messages m ON m.id = a.message_id WHERE {match}");

            // ⚠️ BEFORE the delete, and inside the transaction. The attachment rows go by cascade a
            //    few lines below, and after that nothing connects an object to any message — the
            //    sweeper never sees it and the bucket keeps the bytes for the life of the archive.
            //
            //    The same guard retention uses: retire only what no message OUTSIDE the purge
            //    references. Dedup points every attachment carrying one digest at a single committed
            //    object, so purging one message must not retire bytes another message still serves.
            //    The DELETE that follows takes row locks on exactly the rows these subqueries read,
            //    so "still referenced" is decided against the state the delete will produce.
            //
            //    Unlike a re-send, a purge cannot fail on a row already in `deleting`: the object is
            //    scheduled either way. What must not happen is a purge restarting a delete the
            //    retention sweep already put in motion — hence `state = 'committed'` and no re-stamp.
            //
            //    ⚠️ The two subqueries alias their message tables `m` and `m2` on purpose, and the
            //    second predicate is `matchOn`, not `match`. A subquery sees the outer statement's
            //    tables too, so reusing `match` inside `NOT EXISTS` referenced an `m` that subquery
            //    never joined — and instead of a wrong answer, MySQL and MariaDB both resolved it
            //    against the UPDATE target and failed the purge outright with `Unknown column
            //    'm.conversation_id'`. Loud is the good outcome here: silently binding the guard to
            //    the object's own row is what would have made it always true.
            long objects = 0;
            await using (var retire = new MySqlCommand(
                $"""
                UPDATE journal_objects
                   SET state = 'deleting',
                       delete_after = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @grace SECOND),
                       committed_sha256 = NULL,
                       updated_at = UTC_TIMESTAMP(6)
                 WHERE state = 'committed'
                   AND EXISTS (SELECT 1 FROM journal_attachments a
                                JOIN journal_messages m ON m.id = a.message_id
                               WHERE a.object_id = journal_objects.id AND {match})
                   AND NOT EXISTS (SELECT 1 FROM journal_attachments a2
                                    JOIN journal_messages m2 ON m2.id = a2.message_id
                                   WHERE a2.object_id = journal_objects.id
                                     AND NOT ({matchOn}))
                """, connection, transaction))
            {
                retire.Parameters.AddWithValue("@grace", (int)DeleteGrace.TotalSeconds);
                if (selector.MessageId is not null)
                    retire.Parameters.AddWithValue("@message", selector.MessageId);
                if (selector.ConversationId is not null)
                    retire.Parameters.AddWithValue("@conversation", selector.ConversationId);
                if (selector.TelegramChatId is not null)
                    retire.Parameters.AddWithValue("@chat", selector.TelegramChatId);
                if (selector.Before is not null)
                    retire.Parameters.AddWithValue("@before", selector.Before.Value.UtcDateTime);
                objects = await retire.ExecuteNonQueryAsync(ct);
            }

            long messages;
            await using (var deleteMessages = Bind(new MySqlCommand(
                $"DELETE m FROM journal_messages m WHERE {match}", connection, transaction)))
            {
                messages = await deleteMessages.ExecuteNonQueryAsync(ct);
            }

            long conversations = 0;
            foreach (var id in conversationIds)
            {
                await using var deleteConversation = new MySqlCommand(
                    """
                    DELETE FROM journal_conversations
                     WHERE id = @id
                       AND NOT EXISTS (SELECT 1 FROM journal_messages m
                                        WHERE m.conversation_id = journal_conversations.id)
                    """, connection, transaction);
                deleteConversation.Parameters.AddWithValue("@id", id);
                conversations += await deleteConversation.ExecuteNonQueryAsync(ct);
            }

            if (confirm) await transaction.CommitAsync(ct);
            else await transaction.RollbackAsync(ct);

            return new JournalPurgeCounts(messages, observers, attachments, conversations, objects);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        async Task<long> CountAsync(string sql)
        {
            await using var command = Bind(new MySqlCommand(sql, connection, transaction));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        MySqlCommand Bind(MySqlCommand command)
        {
            if (selector.MessageId is not null) command.Parameters.AddWithValue("@message", selector.MessageId);
            if (selector.ConversationId is not null) command.Parameters.AddWithValue("@conversation", selector.ConversationId);
            if (selector.TelegramChatId is not null) command.Parameters.AddWithValue("@chat", selector.TelegramChatId);
            if (selector.Before is not null) command.Parameters.AddWithValue("@before", selector.Before.Value.UtcDateTime);
            return command;
        }
    }
}
