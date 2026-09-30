using MySqlConnector;

namespace Fleet.Conversations.Journal;

/// <summary>A row lock held until the PUT completes or its scope rolls back.</summary>
public sealed class LockedJournalObject(
    MySqlConnection connection, MySqlTransaction transaction, JournalObjectRow? row, TimeProvider time)
    : IAsyncDisposable
{
    public JournalObjectRow? Row { get; } = row;

    public async Task<bool> CompleteAsync(string owner, bool aborted, CancellationToken ct)
    {
        if (Row is null) return false;
        await using var command = new MySqlCommand(
            "UPDATE journal_objects SET state = @state, updated_at = @now "
            + "WHERE id = @id AND owner = @owner AND state = 'uploading'", connection, transaction);
        command.Parameters.AddWithValue("@state", aborted ? "aborted" : "uploaded");
        command.Parameters.AddWithValue("@now", time.GetUtcNow().UtcDateTime);
        command.Parameters.AddWithValue("@id", Row.Id);
        command.Parameters.AddWithValue("@owner", owner);
        var changed = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return changed == 1;
    }

    public async ValueTask DisposeAsync()
    {
        try { await transaction.DisposeAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}
