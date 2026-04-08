namespace Fleet.Temporal.Configuration;

public sealed class FleetMemoryOptions
{
    public const string Section = "FleetMemory";

    /// <summary>Base URL of the fleet-memory MCP server (e.g. http://fleet-memory:3100).</summary>
    public string Url { get; set; } = "http://fleet-memory:3100";
}
