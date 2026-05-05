using System.Collections.Concurrent;

namespace Fleet.Telegram.Services;

/// <summary>
/// In-memory per-chat ring buffer storing the last <see cref="MaxPerChat"/> messages.
/// Used by <c>get_message</c> and <c>get_recent_messages</c> MCP tools to resolve a
/// Telegram message_id into its text content (e.g. after a reaction event).
/// Populated by fleet-agent via POST /internal/messages/record.
/// </summary>
public sealed class MessageStore
{
    private const int MaxPerChat = 100;

    private readonly ConcurrentDictionary<long, ChatBuffer> _chats = new();

    public void Record(long chatId, long messageId, string text, long senderUserId, string senderUsername, DateTimeOffset timestamp)
    {
        _chats.GetOrAdd(chatId, _ => new ChatBuffer())
              .Add(messageId, text, senderUserId, senderUsername, timestamp);
    }

    /// <summary>Returns the stored record for the given chat + message ID, or null when outside the cache window.</summary>
    public MessageRecord? Get(long chatId, long messageId)
    {
        if (!_chats.TryGetValue(chatId, out var buf)) return null;
        return buf.Get(messageId);
    }

    /// <summary>Returns the most recent <paramref name="limit"/> messages for a chat (oldest first).</summary>
    public IReadOnlyList<MessageRecord> GetRecent(long chatId, int limit)
    {
        if (!_chats.TryGetValue(chatId, out var buf)) return [];
        return buf.GetRecent(limit);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class ChatBuffer
    {
        private readonly Lock _lock = new();
        // Ordered list for GetRecent; dictionary for O(1) Get by id.
        private readonly LinkedList<MessageRecord> _list = new();
        private readonly Dictionary<long, MessageRecord> _index = new();

        public void Add(long messageId, string text, long senderUserId, string senderUsername, DateTimeOffset timestamp)
        {
            var record = new MessageRecord(messageId, text, senderUserId, senderUsername, timestamp);
            lock (_lock)
            {
                if (_index.ContainsKey(messageId))
                    return; // already recorded (dedup)

                _list.AddLast(record);
                _index[messageId] = record;

                while (_list.Count > MaxPerChat)
                {
                    var oldest = _list.First!.Value;
                    _list.RemoveFirst();
                    _index.Remove(oldest.MessageId);
                }
            }
        }

        public MessageRecord? Get(long messageId)
        {
            lock (_lock)
            {
                return _index.TryGetValue(messageId, out var r) ? r : null;
            }
        }

        public IReadOnlyList<MessageRecord> GetRecent(int limit)
        {
            lock (_lock)
            {
                return _list.TakeLast(limit).ToList();
            }
        }
    }
}

public sealed record MessageRecord(
    long MessageId,
    string Text,
    long SenderUserId,
    string SenderUsername,
    DateTimeOffset Timestamp);
