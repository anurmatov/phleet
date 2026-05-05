namespace Fleet.Temporal.Configuration;

/// <summary>
/// Mutable singleton that exposes the CTO agent name, updated live from peer-config.
///
/// The value comes from the <c>FLEET_CTO_AGENT</c> key delivered by
/// <see cref="Services.PeerConfigHostedService"/> into <see cref="FleetWorkflowConfig"/>.
/// This service is a thin injectable wrapper around that static config so MCP tool handlers
/// can receive the value via DI and tests can substitute it without touching the static singleton.
///
/// <c>GetCtoAgent()</c> reads the current value on every call — no startup-time snapshot,
/// no <c>IOptions&lt;T&gt;</c> (which would be startup-only). Safe to call from any thread.
/// </summary>
public class CtoAgentConfigService
{
    /// <summary>
    /// Returns the current CTO agent name. Returns <see cref="string.Empty"/> when
    /// <see cref="FleetWorkflowConfig"/> has not been initialized yet (e.g. in unit tests).
    /// </summary>
    public virtual string GetCtoAgent()
    {
        try
        {
            return FleetWorkflowConfig.Instance.CtoAgent;
        }
        catch (InvalidOperationException)
        {
            // FleetWorkflowConfig not yet initialized — treat as unset.
            return string.Empty;
        }
    }
}
