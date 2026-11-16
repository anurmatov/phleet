namespace Fleet.Agent.Models;

/// <summary>
/// Thread-safe bounded ring buffer for the recent messages of one chat: what people wrote and what
/// the agent answered. Tool calls are not messages and are never stored. In memory only.
/// </summary>
public sealed class GroupChatBuffer
{
    private const int Capacity = 50;

    private readonly Lock _lock = new();
    private readonly LinkedList<BufferedMessage> _entries = new();
    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;

    /// <summary>Telegram chat ID. 0 when not populated (a buffer made outside the per-chat store).</summary>
    public long ChatId { get; set; }

    /// <summary>Chat.Title for group chats. Null for DM buffers.</summary>
    public string? ChatTitle { get; set; }

    /// <summary>
    /// Pre-formatted label segment for DM channel anchors:
    /// <c>"user=@alice"</c>, <c>"name=\"Alice\""</c>, or null when neither is available.
    /// </summary>
    public string? ChatLabel { get; set; }

    /// <summary>
    /// Channel anchor supplied by the conversation registry for a reserved-band (non-Telegram)
    /// conversation. When set, <see cref="RenderHeader"/> returns it verbatim.
    ///
    /// This exists because reserved keys are positive, so they would otherwise render as a
    /// Telegram DM anchor. Positive keys are deliberate — a negative band would have tripped the
    /// <c>chatId &lt; 0</c> "this is a group" heuristics elsewhere in the runtime — and this
    /// override is the one place that has to know the difference.
    /// </summary>
    public string? ChannelAnchorOverride { get; set; }

    /// <summary>
    /// Returns the <c>[channel: ...]</c> header line for prompt injection,
    /// or <c>null</c> when <see cref="ChatId"/> is 0 (legacy / relay).
    /// Groups (chatId &lt; 0): <c>[channel: group chat_id=N title="X"]</c>
    /// DMs (chatId &gt; 0): <c>[channel: dm chat_id=N user=@X]</c> / <c>name="X"</c> / bare id.
    /// A non-Telegram conversation returns <see cref="ChannelAnchorOverride"/> verbatim.
    /// </summary>
    public string? RenderHeader()
    {
        if (ChannelAnchorOverride is { Length: > 0 }) return ChannelAnchorOverride;
        if (ChatId == 0) return null;
        if (ChatId < 0)
        {
            return ChatTitle is { Length: > 0 }
                ? $"[channel: group chat_id={ChatId} title=\"{ChatTitle.Replace("\"", "\\\"")}\"]"
                : $"[channel: group chat_id={ChatId}]";
        }
        // DM
        return ChatLabel is { Length: > 0 }
            ? $"[channel: dm chat_id={ChatId} {ChatLabel}]"
            : $"[channel: dm chat_id={ChatId}]";
    }

    public void Add(string sender, string text, string? replyTo, DateTimeOffset timestamp,
        long telegramMessageId = 0, long? replyToTelegramMessageId = null)
    {
        lock (_lock)
        {
            _entries.AddLast(new BufferedMessage(sender, text, replyTo, timestamp,
                TelegramMessageId: telegramMessageId, ReplyToTelegramMessageId: replyToTelegramMessageId));
            while (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
    }

    public string FormatContext()
    {
        lock (_lock)
        {
            return string.Join('\n', _entries.Select(FormatEntry));
        }
    }

    public string FormatNewMessages()
    {
        lock (_lock)
        {
            var newEntries = _entries.Where(e => e.Timestamp > _lastChecked).ToList();
            if (newEntries.Count == 0)
                return "";

            return string.Join('\n', newEntries.Select(FormatEntry));
        }
    }

    private static string FormatEntry(BufferedMessage e)
    {
        var idPrefix = e.TelegramMessageId > 0 ? $"[telegram_message_id: {e.TelegramMessageId}] " : "";
        var replyIdTag = e.ReplyToTelegramMessageId is > 0 ? $"[reply_to_message_id: {e.ReplyToTelegramMessageId}] " : "";
        return $"{idPrefix}{replyIdTag}{e.Sender}: {e.Text}";
    }

    public bool HasMessagesSinceLastCheck()
    {
        lock (_lock)
        {
            return _entries.Any(e => e.Timestamp > _lastChecked);
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
    /// Advances the checked-up-to watermark to an explicit <paramref name="boundary"/>.
    /// Used by the debounced-batch path so the watermark matches the snapshot taken
    /// before dispatch, not the wall clock at commit time.
    /// </summary>
    public void MarkChecked(DateTimeOffset boundary)
    {
        lock (_lock)
        {
            if (boundary > _lastChecked)
                _lastChecked = boundary;
        }
    }

    /// <summary>A snapshot of the buffered messages, oldest first.</summary>
    public IReadOnlyList<BufferedMessage> GetEntries()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }
}

/// <summary>One message of a chat as <see cref="GroupChatBuffer"/> holds it.</summary>
public sealed record BufferedMessage(
    string Sender,
    string Text,
    string? ReplyTo,
    DateTimeOffset Timestamp,
    long TelegramMessageId = 0,
    long? ReplyToTelegramMessageId = null);
