namespace Fleet.Orchestrator.Configuration;

public sealed class EpicDriverWatcherOptions
{
    public const string Section = "EpicDriverWatcher";
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 300;
}
