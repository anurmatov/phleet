namespace Fleet.Temporal.Configuration;

/// <summary>
/// Operational configuration for fleet-namespace workflows, bound to the <c>FleetWorkflows</c>
/// section in appsettings.json. No defaults — all agent names and repos must be supplied
/// explicitly by the caller when starting workflows. See src/Fleet.Temporal/appsettings.json.
/// </summary>
public sealed class FleetWorkflowOptions
{
    public const string Section = "FleetWorkflows";

    /// <summary>Agent name for escalation notifications (e.g. workflow failures, timeout alerts).</summary>
    public string EscalationTarget { get; set; } = "";

    /// <summary>
    /// Short name of the user-defined CTO/co-cto agent. Resolved at workflow runtime as
    /// <c>{{config.CtoAgent}}</c> so seed-shipped workflow definitions can target the user's
    /// agent without hardcoding a name.
    /// </summary>
    public string CtoAgent
    {
        get => _ctoAgent;
        set => _ctoAgent = value;
    }

    // volatile so runtime updates from PeerConfigHostedService are immediately visible
    // to Temporal activity threads without holding a lock on the hot read path.
    private volatile string _ctoAgent = "";
}
