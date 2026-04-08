namespace Fleet.Orchestrator.Configuration;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    public string Host { get; set; } = "";

    /// <summary>Topic exchange for orchestrator messages (heartbeats, registrations).</summary>
    public string Exchange { get; set; } = "fleet.orchestrator";
}
