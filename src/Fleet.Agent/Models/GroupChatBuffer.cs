namespace Fleet.Agent.Models;

/// <summary>
/// Thread-safe bounded ring buffer for recent group chat messages.
/// </summary>
public sealed class GroupChatBuffer
{
    private const int Capacity = 50;

    private readonly Lock _lock = new();
    private readonly LinkedList<BufferEntry> _entries = new();
    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;

    public void Add(string sender, string text, string? replyTo, DateTimeOffset timestamp,
        long telegramMessageId = 0, long? replyToTelegramMessageId = null)
    {
        lock (_lock)
        {
            _entries.AddLast(new BufferEntry(sender, text, replyTo, timestamp,
                TelegramMessageId: telegramMessageId, ReplyToTelegramMessageId: replyToTelegramMessageId));
            while (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
    }

    public string FormatContext()
    {
        lock (_lock)
        {
            var messages = _entries.Where(e => e.EntryType != "tool_use").ToList();
            if (messages.Count == 0)
                return "";

            return string.Join('\n', messages.Select(e =>
            {
                var prefix = e.ReplyTo is not null ? $"{e.Sender} → {e.ReplyTo}" : e.Sender;
                return $"{prefix}: {e.Text}";
            }));
        }
    }

    public string FormatNewMessages()
    {
        lock (_lock)
        {
            var newEntries = _entries.Where(e => e.Timestamp > _lastChecked && e.EntryType != "tool_use").ToList();
            if (newEntries.Count == 0)
                return "";

            return string.Join('\n', newEntries.Select(e =>
            {
                var prefix = e.ReplyTo is not null ? $"{e.Sender} → {e.ReplyTo}" : e.Sender;
                return $"{prefix}: {e.Text}";
            }));
        }
    }

    public bool HasMessagesSinceLastCheck()
    {
        lock (_lock)
        {
            return _entries.Any(e => e.Timestamp > _lastChecked && e.EntryType != "tool_use");
        }
    }

    public void MarkChecked()
    {
        lock (_lock)
        {
            _lastChecked = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Adds a tool-use event to the buffer (not included in normal conversation context).
    /// </summary>
    public void AddToolUse(string toolName, string description, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            _entries.AddLast(new BufferEntry(toolName, description, null, timestamp, "tool_use"));
            while (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
    }

    /// <summary>
    /// Returns the last N tool_use entries formatted for session-start context.
    /// </summary>
    public string FormatRecentToolUse(int max = 20)
    {
        lock (_lock)
        {
            var toolEntries = _entries
                .Where(e => e.EntryType == "tool_use")
                .TakeLast(max)
                .ToList();

            if (toolEntries.Count == 0)
                return "";

            return "Recent actions before last restart:\n" + string.Join('\n', toolEntries.Select(e =>
                $"  [{e.Timestamp:HH:mm:ss}] {e.Sender}: {e.Text}"));
        }
    }

    /// <summary>
    /// Looks up a buffered message by its Telegram message ID, scanning from most-recent backward.
    /// Returns false when telegramMessageId &lt;= 0, when no matching entry exists,
    /// or when the matched entry is a tool_use entry.
    /// </summary>
    public bool TryGetByMessageId(long telegramMessageId, out string sender, out string text)
    {
        sender = "";
        text = "";

        if (telegramMessageId <= 0)
            return false;

        lock (_lock)
        {
            var node = _entries.Last;
            while (node is not null)
            {
                var entry = node.Value;
                if (entry.TelegramMessageId == telegramMessageId)
                {
                    if (entry.EntryType == "tool_use")
                        return false;

                    sender = entry.Sender;
                    text = entry.Text;
                    return true;
                }
                node = node.Previous;
            }
        }

        return false;
    }

    public List<SerializedEntry> GetEntries()
    {
        lock (_lock)
        {
            return _entries.Select(e => new SerializedEntry(
                e.Sender, e.Text, e.ReplyTo, e.Timestamp, e.EntryType,
                e.TelegramMessageId, e.ReplyToTelegramMessageId)).ToList();
        }
    }

    public void LoadEntries(IEnumerable<SerializedEntry> entries)
    {
        lock (_lock)
        {
            foreach (var e in entries)
            {
                _entries.AddLast(new BufferEntry(e.Sender, e.Text, e.ReplyTo, e.Timestamp,
                    e.EntryType ?? "message",
                    TelegramMessageId: e.TelegramMessageId,
                    ReplyToTelegramMessageId: e.ReplyToTelegramMessageId));
                while (_entries.Count > Capacity)
                    _entries.RemoveFirst();
            }
        }
    }

    private sealed record BufferEntry(
        string Sender,
        string Text,
        string? ReplyTo,
        DateTimeOffset Timestamp,
        string EntryType = "message",
        long TelegramMessageId = 0,
        long? ReplyToTelegramMessageId = null);
}

public record SerializedEntry(
    string Sender,
    string Text,
    string? ReplyTo,
    DateTimeOffset Timestamp,
    string? EntryType = null,
    long TelegramMessageId = 0,
    long? ReplyToTelegramMessageId = null);
