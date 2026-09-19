using System.Collections.Concurrent;
using Fleet.Protocol;

namespace Fleet.Agent.Services;

/// <summary>
/// The ONE thing in this process that produces an appender epoch or an ordinal (#303 D7a).
/// </summary>
/// <remarks>
/// <para>
/// Both halves of the south seam write to the same conversation — the consumer on
/// <c>/submissions:disposition</c>, <c>/deliveries:complete</c> and <c>/turns:start</c>, and the
/// consumer again on <c>/events:append</c> and <c>/turns:commit</c> for what the adapter handed it.
/// A per-component counter would give the store two independent sequences that collide on the very
/// first append, and the store's fence would report it as an out-of-order defect — which it would
/// be, ours.
/// </para>
/// <para>
/// <b>The ordinal is allocated at the SEND SITE</b>, immediately before the call that carries it,
/// and never when an event is created. The bus drains terminal outboxes before the shared progress
/// channel, so a terminal legitimately reaches the adapter ahead of progress that was published
/// earlier. Allocating at creation would give that terminal the higher number, transmit it first,
/// advance the store's <c>last_ordinal</c>, and the progress behind it would then trip
/// <c>OutOfOrderAppendException</c> — a fence trip caused by nothing but our own ordering. Allocated
/// at the send site the ordinal describes what actually happened, and progress arriving after a
/// terminal falls into the store's documented after-terminal drop with a <c>null</c> seq instead.
/// </para>
/// <para>
/// The epoch is a ULID from <see cref="Fleet.Protocol.Ulid"/> — the SAME implementation the store
/// compares against. A second ULID encoding in this assembly could round-trip perfectly and still
/// disagree on sort order, which is the only property the fence relies on.
/// </para>
/// </remarks>
public sealed class ConversationOrdinalAllocator
{
    private readonly ConcurrentDictionary<string, long> _ordinals = new(StringComparer.Ordinal);

    /// <summary>One ULID, minted at construction, constant for the process lifetime.</summary>
    /// <remarks>
    /// A restart mints a new, higher epoch; the store resets that conversation's
    /// <c>last_ordinal</c> to 0 itself on adoption. Nothing here ever resets, reuses or rewinds.
    /// </remarks>
    public string Epoch { get; } = Ulid.NewUlid();

    /// <summary>
    /// The next ordinal for a conversation. Thread-safe, strictly increasing, and <b>1 for the
    /// first call</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One atomic <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey,Func{TKey,TValue},Func{TKey,TValue,TValue})"/>,
    /// not a read followed by a write. Two threads reading the same value and both writing
    /// value + 1 would hand the store the same ordinal twice; the second append then trips the
    /// fence, and the fence's whole meaning is that a trip indicates a real defect.
    /// </para>
    /// <para>
    /// First value is 1, not 0: the store resets <c>last_ordinal</c> to 0 on epoch adoption and
    /// requires the incoming ordinal to be <i>strictly</i> greater.
    /// </para>
    /// </remarks>
    public ulong Next(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        return (ulong)_ordinals.AddOrUpdate(conversationId, 1L, static (_, current) => current + 1L);
    }
}
