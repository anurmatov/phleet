namespace Fleet.Orchestrator.Services;

/// <summary>Exposes RabbitMQ connection health for consumers like the MCP health tool.</summary>
public interface IRabbitMqStatus
{
    bool IsConnected { get; }
}
