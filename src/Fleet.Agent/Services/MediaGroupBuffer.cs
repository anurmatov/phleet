using System.Collections.Concurrent;
using Fleet.Agent.Models;

namespace Fleet.Agent.Services;

/// <summary>
/// Buffers photos that arrive as part of a Telegram media group (multiple photos sent together).
/// Each photo in a group arrives as a separate Update with the same MediaGroupId.
///
/// Flushing rules:
/// - Debounce: flush 1500 ms after the last received photo (timer resets on each arrival).
/// - Hard cap: if photos keep trickling in for longer than <paramref name="maxTotalMs"/>, force-flush.
/// - Size cap: images beyond <paramref name="maxImages"/> are dropped before calling AddPhotoAsync;
///   the caller is responsible for issuing the user-facing warning before dropping.
/// </summary>
internal sealed class MediaGroupBuffer
{
    private const int DebounceWindowMs = 1500;

    private readonly int _maxTotalMs;
    private readonly ConcurrentDictionary<string, MediaGroupState> _groups = new();

    public MediaGroupBuffer(int maxTotalMs)
    {
        _maxTotalMs = maxTotalMs;
    }

    /// <summary>Returns the number of non-null images already buffered for a group, or 0 if the group is unknown.</summary>
    public int GetImageCount(string groupKey)
    {
        if (_groups.TryGetValue(groupKey, out var state))
        {
            lock (state) return state.Images.Count;
        }
        return 0;
    }

    /// <summary>
    /// Register a new photo for a media group identified by <paramref name="groupKey"/>.
    /// <paramref name="image"/> may be null if the photo was skipped (oversized or download failure).
    /// <paramref name="template"/> carries the message metadata from the first photo; subsequent
    /// arrivals for the same group reuse the first photo's metadata.
    /// <paramref name="flush"/> is called exactly once when the group is complete.
    /// </summary>
    public Task AddPhotoAsync(
        string groupKey,
        MessageImage? image,
        IncomingMessage template,
        Func<IncomingMessage, Task> flush)
    {
        var now = DateTimeOffset.UtcNow;
        var state = _groups.GetOrAdd(groupKey, _ => new MediaGroupState(template, now));

        bool forceFlushNow;
        lock (state)
        {
            if (image is not null)
                state.Images.Add(image);
            forceFlushNow = (now - state.FirstReceivedAt).TotalMilliseconds >= _maxTotalMs;
        }

        if (forceFlushNow)
        {
            CancellationTokenSource? old;
            lock (state) { old = state.DebounceCts; state.DebounceCts = null; }
            old?.Cancel();
            old?.Dispose();
            return FlushGroupAsync(groupKey, flush);
        }

        // Cancel previous debounce; start fresh 1500 ms window.
        CancellationTokenSource? oldCts;
        var newCts = new CancellationTokenSource();
        lock (state)
        {
            oldCts = state.DebounceCts;
            state.DebounceCts = newCts;
        }
        oldCts?.Cancel();
        oldCts?.Dispose();

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DebounceWindowMs, newCts.Token); }
            catch (OperationCanceledException) { return; }
            await FlushGroupAsync(groupKey, flush);
        });

        return Task.CompletedTask;
    }

    private async Task FlushGroupAsync(string groupKey, Func<IncomingMessage, Task> flush)
    {
        if (!_groups.TryRemove(groupKey, out var state))
            return;

        IReadOnlyList<MessageImage> images;
        IncomingMessage template;
        lock (state)
        {
            state.DebounceCts?.Cancel();
            state.DebounceCts?.Dispose();
            images = [.. state.Images];
            template = state.TemplateMessage;
        }

        var msg = template with { Images = images };
        await flush(msg);
    }

    private sealed class MediaGroupState
    {
        public IncomingMessage TemplateMessage { get; }
        public List<MessageImage> Images { get; } = [];
        public CancellationTokenSource? DebounceCts { get; set; }
        public DateTimeOffset FirstReceivedAt { get; }

        public MediaGroupState(IncomingMessage template, DateTimeOffset firstReceivedAt)
        {
            TemplateMessage = template;
            FirstReceivedAt = firstReceivedAt;
        }
    }
}
