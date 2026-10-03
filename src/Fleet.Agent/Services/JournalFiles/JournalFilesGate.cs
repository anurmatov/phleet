namespace Fleet.Agent.Services.JournalFiles;
/// <summary>One content stream across fetch and send, including time spent waiting.</summary>
public sealed class JournalFilesGate : IDisposable
{
    internal SemaphoreSlim Serial { get; } = new(1, 1);
    public void Dispose() => Serial.Dispose();
}
