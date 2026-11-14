using System.Collections.Concurrent;
using System.Text.Json;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.GroupChat;

/// <summary>
/// The per-chat message buffers and their file, <c>.fleet/chat-history.json</c> under the agent's
/// work directory. The file is read once, the first time any buffer is asked for, and rewritten
/// on every <see cref="Save"/>. Client (non-Telegram) conversations are kept in memory only.
/// </summary>
internal sealed class GroupChatHistoryStore(string historyPath, ILogger logger)
{
    private readonly ConcurrentDictionary<long, GroupChatBuffer> _groupBuffers = new();
    private readonly string _historyPath = historyPath;
    private readonly ILogger _logger = logger;
    private bool _historyLoaded;

    internal GroupChatBuffer GetGroupBuffer(long chatId)
    {
        if (!_historyLoaded)
            LoadBuffersFromDisk();

        var buffer = _groupBuffers.GetOrAdd(chatId, id => new GroupChatBuffer { ChatId = id });
        // Patch buffers deserialized before ChatId was introduced (ChatId == 0 on load)
        if (buffer.ChatId == 0) buffer.ChatId = chatId;
        return buffer;
    }

    /// <summary>
    /// The buffer for a chat that already has one in memory. It neither creates a buffer nor reads
    /// the file: the proactive sweep must not make every allowed group look like it has history.
    /// </summary>
    internal bool TryGetExisting(long chatId, out GroupChatBuffer buffer) =>
        _groupBuffers.TryGetValue(chatId, out buffer!);

    private void LoadBuffersFromDisk()
    {
        _historyLoaded = true;

        if (!File.Exists(_historyPath))
            return;

        try
        {
            var json = File.ReadAllText(_historyPath);

            // Try the current format: Dictionary<long, PersistedBuffer> (LastChecked + Entries).
            // Old files use Dictionary<long, List<SerializedEntry>> — values are JSON arrays,
            // which fail to deserialize as PersistedBuffer objects, so we catch and fall through.
            Dictionary<long, PersistedBuffer>? newData = null;
            try
            {
                newData = JsonSerializer.Deserialize<Dictionary<long, PersistedBuffer>>(json);
            }
            catch (JsonException)
            {
                // Old format — fall through to legacy path
            }

            if (newData is not null)
            {
                foreach (var (chatId, persisted) in newData)
                {
                    var buffer = _groupBuffers.GetOrAdd(chatId, id => new GroupChatBuffer { ChatId = id });
                    if (buffer.ChatId == 0) buffer.ChatId = chatId;
                    buffer.LoadEntries(persisted.Entries);
                    buffer.LoadState(persisted.LastChecked);
                }
                _logger.LogInformation("Loaded chat history from disk ({Count} chats)", newData.Count);
            }
            else
            {
                // Legacy format: Dictionary<long, List<SerializedEntry>> — no LastChecked stored.
                // Default to UtcNow so existing messages are not treated as unread after upgrade.
                var legacyData = JsonSerializer.Deserialize<Dictionary<long, List<SerializedEntry>>>(json);
                if (legacyData is null) return;

                var now = DateTimeOffset.UtcNow;
                foreach (var (chatId, entries) in legacyData)
                {
                    var buffer = _groupBuffers.GetOrAdd(chatId, id => new GroupChatBuffer { ChatId = id });
                    if (buffer.ChatId == 0) buffer.ChatId = chatId;
                    buffer.LoadEntries(entries);
                    buffer.LoadState(now);
                }
                _logger.LogInformation("Loaded chat history from disk (legacy format, {Count} chats)", legacyData.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load chat history from {Path}", _historyPath);
        }
    }

    /// <summary>Writes every Telegram chat's buffer to disk. Never throws: a failed write is logged.</summary>
    internal void Save()
    {
        try
        {
            var data = new Dictionary<long, PersistedBuffer>();
            foreach (var (chatId, buffer) in _groupBuffers)
            {
                // Client (non-Telegram) conversations are process-lifetime only and never reach
                // disk. This filter is mandatory rather than belt-and-braces: BufferBotResponse
                // calls SaveBuffers() on EVERY completion, so without it client text would be
                // persisted through the completion path regardless of what the intake does.
                // It also avoids accumulating orphaned on-disk buffers keyed to reserved runtime
                // keys that will never be reused after a restart.
                if (ConversationRegistry.IsReservedKey(chatId))
                    continue;

                // Normalize MinValue (never-checked buffer) to UtcNow so that on next
                // load the existing entries are not treated as unread.
                var lastChecked = buffer.GetLastChecked();
                if (lastChecked == DateTimeOffset.MinValue)
                    lastChecked = DateTimeOffset.UtcNow;
                data[chatId] = new PersistedBuffer(lastChecked, buffer.GetEntries());
            }

            var dir = Path.GetDirectoryName(_historyPath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save chat history to {Path}", _historyPath);
        }
    }

    /// <summary>
    /// On-disk shape for a single group's chat history (current format).
    /// Replaces the legacy <c>List&lt;SerializedEntry&gt;</c> value so that
    /// <see cref="GroupChatBuffer._lastChecked"/> survives agent restarts.
    /// </summary>
    private sealed record PersistedBuffer(DateTimeOffset LastChecked, List<SerializedEntry> Entries);
}
