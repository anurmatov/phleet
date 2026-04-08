namespace Fleet.Agent.Models;

/// <summary>
/// Thread-safe container for running tasks within a single chat.
/// </summary>
public sealed class ChatTaskState
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, RunningTask> _tasks = [];
    private int _nextId;

    public int Count { get { lock (_lock) return _tasks.Count; } }

    public RunningTask Add(string description, CancellationTokenSource cts, bool isSessionTask, long userId = 0, string? bridgeTaskId = null)
    {
        lock (_lock)
        {
            var id = ++_nextId;
            var task = new RunningTask
            {
                Id = id,
                Description = description,
                StartedAt = DateTimeOffset.UtcNow,
                Cts = cts,
                IsSessionTask = isSessionTask,
                UserId = userId,
                BridgeTaskId = bridgeTaskId,
            };
            _tasks[id] = task;
            return task;
        }
    }

    public void Remove(int id)
    {
        lock (_lock) _tasks.Remove(id);
    }

    public RunningTask? Get(int id)
    {
        lock (_lock) return _tasks.GetValueOrDefault(id);
    }

    public List<RunningTask> Snapshot()
    {
        lock (_lock) return [.. _tasks.Values.OrderBy(t => t.Id)];
    }
}
