using System.Collections.Concurrent;
namespace Fleet.Agent.Services.MessageCopy;

/// <summary>In-process test counter, not an exported metric.</summary>
public sealed class MessageCopyCounter
{
    private readonly ConcurrentDictionary<string, long> _counts = new();
    public void Record(string outcome) => _counts.AddOrUpdate(outcome, 1, (_, count) => count + 1);
    public long Count(string outcome) => _counts.GetValueOrDefault(outcome);
}
