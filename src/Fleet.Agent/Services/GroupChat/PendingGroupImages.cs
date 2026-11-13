using System.Collections.Concurrent;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.GroupChat;

/// <summary>
/// Images from group messages that did not trigger a turn themselves (all-messages mode), held
/// per chat until the next check-in forwards them. An entry is dropped five minutes after its
/// last image arrived.
/// </summary>
internal sealed class PendingGroupImages(ILogger logger)
{
    private readonly ConcurrentDictionary<long, PendingImagesEntry> _pendingImages = new();
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Buffer images from a non-direct-trigger all-mode message so they can be forwarded
    /// to the LLM when the debounce check-in fires. Enforces MaxImagesPerGroup cap.
    /// </summary>
    internal void Add(long chatId, IReadOnlyList<MessageImage> images, int maxImages)
    {
        if (images.Count == 0) return;
        var entry = _pendingImages.GetOrAdd(chatId, _ => new PendingImagesEntry());
        var hadOverflow = entry.AddRange(images, maxImages);
        if (hadOverflow)
            _logger.LogWarning("Pending image buffer for group {ChatId} exceeded cap ({Cap}), overflow images dropped", chatId, maxImages);
    }

    internal IReadOnlyList<MessageImage> Drain(long chatId)
    {
        if (!_pendingImages.TryRemove(chatId, out var entry))
            return [];
        if (entry.IsExpired)
        {
            _logger.LogDebug("Pending images for group {ChatId} exceeded TTL, dropping without forwarding", chatId);
            return [];
        }
        return entry.Drain();
    }

    /// <summary>
    /// Returns a snapshot of buffered pending images without removing them.
    /// Excludes TTL-expired entries (they must not be delivered) but does NOT
    /// remove the expired entry — that is deferred to <see cref="Commit"/>.
    /// </summary>
    internal IReadOnlyList<MessageImage> Peek(long chatId)
    {
        if (!_pendingImages.TryGetValue(chatId, out var entry))
            return [];
        if (entry.IsExpired)
        {
            _logger.LogDebug("Pending images for group {ChatId} exceeded TTL, excluding from snapshot (CommitPendingImages will clean up)", chatId);
            return [];
        }
        return entry.Peek();
    }

    /// <summary>
    /// Removes the pending-images entry for <paramref name="chatId"/> when all images
    /// in storage predate <paramref name="boundary"/> (i.e. no new images arrived after
    /// the snapshot). Also removes expired entries so they are not left behind indefinitely.
    /// </summary>
    internal void Commit(long chatId, DateTimeOffset boundary)
    {
        if (!_pendingImages.TryGetValue(chatId, out var entry))
            return;
        if (!entry.ShouldCommit(boundary))
        {
            // New images arrived after our snapshot — leave them for the next cycle.
            _logger.LogDebug("Pending images for group {ChatId}: new images arrived after snapshot boundary, not committing", chatId);
            return;
        }
        _pendingImages.TryRemove(chatId, out _);
    }

    /// <summary>Returns <c>true</c> when there is an entry for <paramref name="chatId"/>.</summary>
    internal bool Has(long chatId) => _pendingImages.ContainsKey(chatId);

    /// <summary>Testing seam: makes the entry for <paramref name="chatId"/> appear older than <paramref name="age"/>.</summary>
    internal void BackdateForTest(long chatId, TimeSpan age)
    {
        if (_pendingImages.TryGetValue(chatId, out var entry))
            entry.BackdateStoredAtForTest(age);
    }

    private sealed class PendingImagesEntry
    {
        /// Images older than this are considered stale and dropped on drain.
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

        private readonly Lock _lock = new();
        private readonly List<MessageImage> _images = [];
        private DateTimeOffset _storedAt = DateTimeOffset.UtcNow;

        /// <summary>
        /// Appends <paramref name="incoming"/> up to <paramref name="cap"/>.
        /// Returns <c>true</c> if any images were dropped due to the cap.
        /// </summary>
        public bool AddRange(IReadOnlyList<MessageImage> incoming, int cap)
        {
            lock (_lock)
            {
                _storedAt = DateTimeOffset.UtcNow;
                var hadOverflow = false;
                foreach (var img in incoming)
                {
                    if (_images.Count >= cap) { hadOverflow = true; break; }
                    _images.Add(img);
                }
                return hadOverflow;
            }
        }

        public IReadOnlyList<MessageImage> Drain()
        {
            lock (_lock)
            {
                if (_images.Count == 0) return [];
                var result = _images.ToArray();
                _images.Clear();
                return result;
            }
        }

        /// <summary>
        /// Returns a copy of the buffered images without removing them.
        /// </summary>
        public IReadOnlyList<MessageImage> Peek()
        {
            lock (_lock)
            {
                return _images.Count == 0 ? [] : [.. _images];
            }
        }

        /// <summary>
        /// Returns <c>true</c> when all images in storage were stored at or before
        /// <paramref name="boundary"/> — meaning no new images arrived after the snapshot
        /// and it is safe to remove the entire entry.
        /// Also returns <c>true</c> when the entry is TTL-expired, so stale entries are
        /// cleaned up whenever a commit runs.
        /// </summary>
        public bool ShouldCommit(DateTimeOffset boundary)
        {
            lock (_lock) return _storedAt <= boundary || IsExpiredLocked;
        }

        public bool IsExpired
        {
            get { lock (_lock) return IsExpiredLocked; }
        }

        private bool IsExpiredLocked => DateTimeOffset.UtcNow - _storedAt > Ttl;

        /// <summary>Testing seam: sets _storedAt to simulate images that arrived in the past.</summary>
        internal void BackdateStoredAtForTest(TimeSpan age)
        {
            lock (_lock) _storedAt = DateTimeOffset.UtcNow - age;
        }
    }
}
