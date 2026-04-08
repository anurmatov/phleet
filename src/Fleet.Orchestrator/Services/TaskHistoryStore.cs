using System.Collections.Concurrent;
using Fleet.Orchestrator.Models;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// In-memory store of recently completed tasks per agent (last 100 per agent).
/// </summary>
public sealed class TaskHistoryStore
{
    private const int MaxPerAgent = 100;
    private readonly ConcurrentDictionary<string, LinkedList<TaskRecord>> _history =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(TaskRecord record)
    {
        var list = _history.GetOrAdd(record.AgentName, _ => new LinkedList<TaskRecord>());
        lock (list)
        {
            list.AddFirst(record);
            while (list.Count > MaxPerAgent)
                list.RemoveLast();
        }
    }

    public IReadOnlyList<TaskRecord> GetHistory(string agentName)
    {
        if (!_history.TryGetValue(agentName, out var list))
            return [];
        lock (list)
            return list.ToList();
    }
}
