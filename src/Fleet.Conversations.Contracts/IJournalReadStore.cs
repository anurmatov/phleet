namespace Fleet.Conversations.Contracts;

/// <summary>
/// The journal, read (#394): what the three read tools on the journal listener ask of the store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope is applied inside every query, on every page.</b> A message the reader may not see is
/// indistinguishable from one that does not exist: null, not found, or simply absent from a page —
/// never an error of its own. A cursor only ever moves the position; it cannot widen what a page
/// may contain.
/// </para>
/// <para>
/// Keyset pagination only, on immutable keys, and each page is ONE read-committed query: a page
/// shows the store as it stood when that page was read, and no cross-page snapshot is promised.
/// </para>
/// <para>
/// Every method throws <see cref="JournalStoreUnavailableException"/> when the database cannot
/// answer or its schema is behind this binary. No method ever returns part of a page.
/// </para>
/// </remarks>
public interface IJournalReadStore
{
    /// <summary>Visible messages, newest first: <c>(sent_at, id)</c> descending.</summary>
    Task<JournalSearchPage> SearchAsync(JournalReader reader, JournalSearchQuery query, CancellationToken ct = default);

    /// <summary>One visible message by journal id; null when it is absent or not visible.</summary>
    Task<JournalReadMessage?> GetMessageAsync(JournalReader reader, string messageId, CancellationToken ct = default);

    /// <summary>
    /// Visible messages carrying this platform chat and message id. One conversation per bot can
    /// share a chat id, so more than one can match.
    /// </summary>
    Task<JournalMessageLookup> FindByTelegramAsync(
        JournalReader reader, long telegramChatId, long telegramMessageId, CancellationToken ct = default);

    /// <summary>
    /// One page of a conversation in <c>(order_key, id)</c> order. Null when the conversation does
    /// not exist, holds no message the reader may see, or the anchor is not a visible message of it.
    /// </summary>
    Task<JournalConversationPage?> ReadConversationAsync(
        JournalReader reader, JournalReplayQuery query, CancellationToken ct = default);
}
