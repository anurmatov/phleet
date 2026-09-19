using System.Collections.Concurrent;
using System.Threading.Channels;
using Fleet.Conversations.Contracts;

namespace Fleet.Agent.Services;

/// <summary>Which south call a handed-off event becomes.</summary>
public enum SouthOutboundAction
{
    /// <summary><c>POST /turns:start</c>, using the event's own <c>identity.turnId</c> (D4).</summary>
    StartTurn,

    /// <summary><c>POST /events:append</c>.</summary>
    Append,

    /// <summary><c>POST /turns:commit</c>. Terminal kinds only (D6).</summary>
    Commit,
}

/// <summary>One translated event, waiting for the consumer's in-order sender.</summary>
/// <remarks>
/// Carries no ordinal. The ordinal is taken at the send site (D7a), so this record describes what
/// is to be sent and not what position it will occupy.
/// </remarks>
public sealed record SouthOutboundItem
{
    public required string ConversationId { get; init; }
    public required SouthOutboundAction Action { get; init; }
    public required EventDescriptor Event { get; init; }
    public required EventRetentionClass RetentionClass { get; init; }
    public string? SubmissionId { get; init; }
    public string? TurnId { get; init; }

    public bool IsTerminal => Action is SouthOutboundAction.Commit;
}

/// <summary>
/// The bounded per-conversation hand-off between the adapter and the consumer's sender (D10).
/// </summary>
/// <remarks>
/// <para>
/// <b>The terminal slot is a capacity reservation, not an overtaking lane.</b> It guarantees a
/// terminal always has somewhere to go when progress has filled the queue; it does not license the
/// sender to transmit it past work already queued for the same conversation. Reads are strictly
/// FIFO, which — together with send-site ordinal allocation — is what makes the store's fence
/// meaningful: an <c>OutOfOrderAppendException</c> can then only mean a real defect, never a benign
/// reordering of our own making.
/// </para>
/// <para>
/// One channel per conversation, not one shared channel, for the same reason the event bus splits:
/// a chatty conversation must not be able to starve a quiet one. Conversations are independent and
/// the consumer may drain them concurrently.
/// </para>
/// <para>
/// Enqueue is <b>non-blocking</b> and never throws. A refusal is a return value the caller counts;
/// the adapter runs on the pump's loop under a five-second timeout, and a queue that blocked there
/// would be a hung adapter.
/// </para>
/// </remarks>
public sealed class ConversationSouthHandoff
{
    /// <summary>Queued items a conversation may hold before progress is refused.</summary>
    public const int Capacity = 256;

    /// <summary>
    /// Extra slots reserved for terminals. A conversation runs one turn at a time, so needing more
    /// than a handful is a defect rather than load.
    /// </summary>
    public const int TerminalReserve = 4;

    private readonly ConcurrentDictionary<string, ConversationQueue> _queues = new(StringComparer.Ordinal);

    /// <summary>
    /// Enqueue an item for its conversation. Returns false when the queue refused it.
    /// </summary>
    public bool TryEnqueue(SouthOutboundItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Queue(item.ConversationId).TryEnqueue(item);
    }

    /// <summary>The conversation's FIFO reader. Created on first use.</summary>
    public ChannelReader<SouthOutboundItem> Reader(string conversationId) => Queue(conversationId).Reader;

    /// <summary>Items currently waiting for a conversation. Test and diagnostic surface.</summary>
    public int Depth(string conversationId) =>
        _queues.TryGetValue(conversationId, out var queue) ? queue.Depth : 0;

    /// <summary>Complete every writer so a drain loop can finish on shutdown.</summary>
    public void CompleteAll()
    {
        foreach (var queue in _queues.Values)
            queue.Complete();
    }

    private ConversationQueue Queue(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        return _queues.GetOrAdd(conversationId, static _ => new ConversationQueue());
    }

    private sealed class ConversationQueue
    {
        // Unbounded underneath with an explicit depth cap on top, rather than a bounded channel.
        // A bounded channel cannot admit a terminal once it is full without evicting something, and
        // eviction is exactly what the reservation exists to avoid — the terminal has to go behind
        // the progress already queued, not in front of it.
        private readonly Channel<SouthOutboundItem> _channel =
            Channel.CreateUnbounded<SouthOutboundItem>(new UnboundedChannelOptions { SingleReader = true });

        private int _depth;

        public ChannelReader<SouthOutboundItem> Reader => _channel.Reader;

        public int Depth => Volatile.Read(ref _depth);

        public bool TryEnqueue(SouthOutboundItem item)
        {
            var limit = item.IsTerminal ? Capacity + TerminalReserve : Capacity;

            // Reserve the slot before writing, so two concurrent producers cannot both observe room
            // for the last one. Released again when the write is refused.
            if (Interlocked.Increment(ref _depth) > limit)
            {
                Interlocked.Decrement(ref _depth);
                return false;
            }

            if (_channel.Writer.TryWrite(item))
                return true;

            Interlocked.Decrement(ref _depth);
            return false;
        }

        /// <summary>Called by the reader once an item has been transmitted.</summary>
        public void Release() => Interlocked.Decrement(ref _depth);

        public void Complete() => _channel.Writer.TryComplete();
    }

    /// <summary>Release a transmitted item's slot back to its conversation.</summary>
    public void Release(string conversationId)
    {
        if (_queues.TryGetValue(conversationId, out var queue))
            queue.Release();
    }
}
