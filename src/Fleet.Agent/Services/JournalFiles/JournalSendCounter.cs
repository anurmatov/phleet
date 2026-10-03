using System.Collections.Concurrent;
namespace Fleet.Agent.Services.JournalFiles;
/// <summary>Local acceptance counter only. No exported metric or file identifiers.</summary>
public sealed class JournalSendCounter
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    public long Count(string outcome) => _counts.GetValueOrDefault(outcome);
    public void Record(string outcome) => _counts.AddOrUpdate(outcome, 1, (_, n) => n + 1);
}
