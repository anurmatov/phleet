namespace Fleet.Agent.Services;

/// <summary>
/// Shared singleton that tracks live connectivity state for the agent.
/// Written by AgentTransport on startup; read by OrchestratorHeartbeatService
/// to include TelegramConnected in every heartbeat payload.
/// </summary>
public interface IFleetConnectionState
{
    bool TelegramConnected { get; set; }
}

public sealed class FleetConnectionState : IFleetConnectionState
{
    public bool TelegramConnected { get; set; } = false;
}
