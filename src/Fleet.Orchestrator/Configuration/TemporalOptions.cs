namespace Fleet.Orchestrator.Configuration;

public sealed class TemporalOptions
{
    public const string Section = "Temporal";

    /// <summary>Temporal server address (host:port). Empty = poller disabled.</summary>
    public string Address { get; set; } = "";

    /// <summary>Namespaces to query for running workflows.</summary>
    public List<string> Namespaces { get; set; } = [];

    /// <summary>How often to poll Temporal for running workflows (seconds).</summary>
    public int PollIntervalSeconds { get; set; } = 15;
}
