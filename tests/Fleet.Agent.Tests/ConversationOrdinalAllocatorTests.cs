using System.Collections.Concurrent;
using Fleet.Agent.Services;
using Fleet.Protocol;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="ConversationOrdinalAllocator"/> — #303 AC12.
/// </summary>
/// <remarks>
/// The store fences on epoch and ordinal: a lower epoch is stale, a higher one resets the
/// conversation's <c>last_ordinal</c> to 0, and an equal epoch with an ordinal that is not
/// <i>strictly</i> greater throws. Every property below is one of that fence's preconditions, so a
/// failure here is a fence trip in production rather than a style problem.
/// </remarks>
public sealed class ConversationOrdinalAllocatorTests
{
    /// <summary>
    /// The first ordinal is 1, not 0.
    /// </summary>
    /// <remarks>
    /// The store resets <c>last_ordinal</c> to 0 on epoch adoption and requires strictly greater, so
    /// a first ordinal of 0 is refused by the very first append the process ever makes — and the
    /// symptom is a conversation that accepts a disposition and then silently stops.
    /// </remarks>
    [Fact]
    public void TheFirstOrdinalForAConversationIsOne()
    {
        var allocator = new ConversationOrdinalAllocator();

        Assert.Equal(1UL, allocator.Next("conversation-a"));
    }

    [Fact]
    public void OrdinalsAreStrictlyIncreasingWithinAConversation()
    {
        var allocator = new ConversationOrdinalAllocator();

        Assert.Equal(1UL, allocator.Next("c"));
        Assert.Equal(2UL, allocator.Next("c"));
        Assert.Equal(3UL, allocator.Next("c"));
    }

    /// <summary>
    /// Conversations do not share a counter. A shared one would make a busy conversation push a
    /// quiet one's ordinals forward, which is harmless at the store but destroys the property that
    /// a gap means something.
    /// </summary>
    [Fact]
    public void ConversationsAreIsolatedFromEachOther()
    {
        var allocator = new ConversationOrdinalAllocator();

        Assert.Equal(1UL, allocator.Next("a"));
        Assert.Equal(1UL, allocator.Next("b"));
        Assert.Equal(2UL, allocator.Next("a"));
        Assert.Equal(2UL, allocator.Next("b"));
    }

    /// <summary>
    /// The epoch is minted once and never moves. A per-call epoch would make every append look like
    /// a restart to the store, which resets <c>last_ordinal</c> — and a fence that resets on every
    /// write is not a fence.
    /// </summary>
    [Fact]
    public void TheEpochIsConstantForTheProcessAndIsAValidUlid()
    {
        var allocator = new ConversationOrdinalAllocator();
        var first = allocator.Epoch;

        allocator.Next("a");
        allocator.Next("b");

        Assert.Equal(first, allocator.Epoch);
        Assert.True(Ulid.IsValid(first), $"the epoch must be a ULID the store can compare: {first}");
    }

    /// <summary>
    /// Two allocators — two processes — mint different epochs, and the later one sorts higher.
    /// </summary>
    /// <remarks>
    /// This is the property that makes a restart safe rather than a stale-append storm: the store
    /// compares epochs with <c>string.CompareOrdinal</c> and adopts the higher one.
    /// </remarks>
    [Fact]
    public void ALaterProcessMintsAnEpochThatSortsAfterAnEarlierOne()
    {
        var earlier = new ConversationOrdinalAllocator();
        Thread.Sleep(2);
        var later = new ConversationOrdinalAllocator();

        Assert.NotEqual(earlier.Epoch, later.Epoch);
        Assert.True(
            string.CompareOrdinal(later.Epoch, earlier.Epoch) > 0,
            "a restart must mint an epoch the store reads as newer");
    }

    /// <summary>
    /// AC12: concurrent allocation for ONE conversation yields a strictly increasing sequence with
    /// no duplicates.
    /// </summary>
    /// <remarks>
    /// The mutation this fails on is replacing the atomic <c>AddOrUpdate</c> with a read followed by
    /// a write. Two threads then read the same value and both write value + 1, the store sees the
    /// same ordinal twice, and the second append trips <c>OutOfOrderAppendException</c> — a fence
    /// trip whose only cause was us.
    /// </remarks>
    [Fact]
    public void ConcurrentAllocationForOneConversationProducesNoDuplicates()
    {
        const int threads = 16;
        const int perThread = 500;

        var allocator = new ConversationOrdinalAllocator();
        var seen = new ConcurrentBag<ulong>();

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                seen.Add(allocator.Next("busy"));
        });

        var ordered = seen.OrderBy(x => x).ToList();

        Assert.Equal(threads * perThread, ordered.Count);
        Assert.Equal(ordered.Count, ordered.Distinct().Count());
        Assert.Equal(1UL, ordered[0]);
        Assert.Equal((ulong)ordered.Count, ordered[^1]);
    }

    [Fact]
    public void AnEmptyConversationIdIsRefusedRatherThanSharedWithEveryOtherConversation() =>
        Assert.Throws<ArgumentException>(() => new ConversationOrdinalAllocator().Next(""));
}
