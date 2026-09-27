namespace Fleet.Conversations.Contracts;

/// <summary>
/// The durable conversation journal: one row per platform message, one observer row per runtime
/// that saw it.
/// </summary>
/// <remarks>
/// <para>
/// Text only in this slice. Attachments are metadata stored as <c>not_archived</c>; no bytes and no
/// object reference exist yet.
/// </para>
/// <para>
/// Both methods throw <see cref="JournalStoreUnavailableException"/> when the database cannot
/// answer or its schema is behind this binary, and write nothing in that case.
/// </para>
/// </remarks>
public interface IJournalStore
{
    /// <summary>
    /// Records one message as seen by <paramref name="observer"/>, idempotently.
    /// </summary>
    /// <param name="observer">The token subject. Never taken from the request body.</param>
    Task<JournalIngestResult> IngestAsync(JournalRecord record, string observer, CancellationToken ct = default);

    /// <summary>Schema version and per-observer counts. Never message text.</summary>
    Task<JournalStoreStatus> GetStatusAsync(CancellationToken ct = default);
}
