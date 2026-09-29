namespace Fleet.Conversations.Journal;

/// <summary>
/// An object store that knows whether it is currently usable.
/// </summary>
/// <remarks>
/// Separate from <see cref="Fleet.Conversations.Contracts.IJournalObjectStore"/> because
/// "is the bucket answering" is a question with a <b>cached</b> answer, and the store that caches
/// it is the one the host built. A route that asked the raw store on every request would put a
/// bucket round trip in front of every upload — including the ones that would have succeeded — and
/// turn a slow bucket into a slow listener.
/// </remarks>
public interface IJournalStoreHealth
{
    /// <summary>
    /// True when uploads may be accepted right now. Backed by a probe with a short cache, refreshed
    /// by the host's retry loop rather than by the caller.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>The state the status route reports: <c>enabled</c>, <c>degraded</c> or <c>disabled</c>.</summary>
    string MediaState { get; }
}
