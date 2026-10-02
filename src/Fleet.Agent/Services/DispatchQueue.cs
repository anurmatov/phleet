using System.Diagnostics.CodeAnalysis;
using Fleet.Agent.Models;

namespace Fleet.Agent.Services;

/// <summary>Two FIFO lanes; promotion and capacity decisions share one lock.</summary>
public sealed class DispatchQueue(QueueLaneCounter? counter = null)
{
    public const int MaxQueueDepth = 20;
    public const int MaxPriorityQueueDepth = 10;
    private readonly object _gate = new();
    private readonly List<QueuedMessage> _priority = [];
    private readonly List<QueuedMessage> _routine = [];
    private readonly Dictionary<long, QueuedMessage> _pending = [];
    private long _seq;
    private int _consecutivePriority;
    private readonly QueueLaneCounter _counter = counter ?? new();
    public int Count { get { lock (_gate) return _priority.Count + _routine.Count; } }
    public bool IsEmpty => Count == 0;
    public int PriorityCount { get { lock (_gate) return _priority.Count; } }
    public IReadOnlyList<QueuedMessage> Snapshot() { lock (_gate) return [.. _priority, .. _routine]; }
    public bool HasPending(long chat) { lock (_gate) return _pending.ContainsKey(chat); }
    private static bool Human(TaskSource source) => source is TaskSource.UserMessage or TaskSource.NewCommand;
    private static bool Primary(QueuedMessagePart part) => part.Priority == TaskPriority.PrimaryHuman && Human(part.Source);

    public bool TryAppend(long chat, QueuedMessagePart part)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(chat, out var pending)) return false;
            pending.QueueDispatchLock.Wait();
            try
            {
                if (!pending.CanMerge(part)) return false;
                if (Primary(part)) PromoteLocked(chat, fresh: false);
                return pending.TryAppendPart(part);
            }
            finally { pending.QueueDispatchLock.Release(); }
        }
    }

    private bool PromoteLocked(long chat, bool fresh)
    {
        var earlier = _routine.Where(e => e.ChatId == chat && Human(e.Source)).OrderBy(e => e.Seq).ToArray();
        if (_priority.Count + earlier.Length + (fresh ? 1 : 0) > MaxPriorityQueueDepth)
        {
            _counter.Increment(earlier.Length > 0 ? "promotion_refused_full" : "priority_overflow_to_routine");
            return false;
        }
        foreach (var entry in earlier)
        {
            _routine.Remove(entry);
            entry.Priority = TaskPriority.PrimaryHuman;
            entry.Seq = ++_seq;
            _priority.Add(entry);
            _counter.Increment("priority_promoted");
        }
        return true;
    }

    public bool TryEnqueue(long chat, QueuedMessagePart part, [NotNullWhen(true)] out QueuedMessage? entry, out int position)
    {
        lock (_gate)
        {
            var lane = Primary(part) && PromoteLocked(chat, fresh: true) ? _priority : _routine;
            var cap = ReferenceEquals(lane, _priority) ? MaxPriorityQueueDepth : MaxQueueDepth;
            if (lane.Count >= cap) { entry = null; position = 0; return false; }
            entry = new QueuedMessage(chat, part)
            {
                Seq = ++_seq,
                Priority = ReferenceEquals(lane, _priority) ? TaskPriority.PrimaryHuman : TaskPriority.Routine,
            };
            lane.Add(entry);
            if (ReferenceEquals(lane, _priority)) _counter.Increment("priority_enqueued");
            if (part.Source == TaskSource.UserMessage) _pending[chat] = entry;
            position = lane.Count + (ReferenceEquals(lane, _routine) ? _priority.Count : 0);
            return true;
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out QueuedMessage? entry)
    {
        lock (_gate)
        {
            var lane = _priority.Count > 0 && (_routine.Count == 0 || _consecutivePriority < 3) ? _priority : _routine;
            if (lane.Count == 0) { entry = null; return false; }
            if (ReferenceEquals(lane, _routine) && _priority.Count > 0 && _consecutivePriority >= 3)
                _counter.Increment("starvation_guard_dispatch");
            _consecutivePriority = ReferenceEquals(lane, _priority) && _routine.Count > 0 ? _consecutivePriority + 1 : 0;
            entry = lane[0]; lane.RemoveAt(0);
            entry.QueueDispatchLock.Wait();
            try { entry.Claimed = true; }
            finally { entry.QueueDispatchLock.Release(); }
            return true;
        }
    }

    public void RemovePendingIfCurrent(QueuedMessage entry)
    {
        lock (_gate)
            if (_pending.TryGetValue(entry.ChatId, out var current) && ReferenceEquals(current, entry)) _pending.Remove(entry.ChatId);
    }

    public IReadOnlyList<QueuedMessage> RemoveByTaskId(string taskId, Action? scanned = null)
    {
        // Hooks run without the lock; a concurrent append stays attached to its original entry.
        foreach (var item in Snapshot()) scanned?.Invoke();
        lock (_gate)
        {
            var removed = _priority.Concat(_routine).Where(e => e.ContainsTaskId(taskId)).ToArray();
            foreach (var item in removed) { _priority.Remove(item); _routine.Remove(item); }
            RebuildPendingLocked();
            return removed;
        }
    }

    public IReadOnlyList<QueuedMessage> Clear()
    {
        lock (_gate)
        {
            var removed = _priority.Concat(_routine).ToArray();
            _priority.Clear(); _routine.Clear(); _pending.Clear(); _consecutivePriority = 0;
            return removed;
        }
    }

    private void RebuildPendingLocked()
    {
        _pending.Clear();
        foreach (var item in _priority.Concat(_routine).OrderBy(e => e.Seq))
            if (item.Source == TaskSource.UserMessage && !item.Claimed) _pending[item.ChatId] = item;
    }
}
