namespace Fleet.Temporal.Activities;

using Fleet.Temporal.Configuration;
using Microsoft.Extensions.Options;
using Temporalio.Activities;

/// <summary>
/// Exposes <see cref="TemporalBridgeOptions.AgentTimeoutSeconds"/> — the deployment's answer to
/// "how long may an agent take" — to workflow code.
///
/// Workflow code cannot read <c>IOptions</c>: configuration is host state, so a replay after a
/// config change would compute a different StartToCloseTimeout than the one the original
/// execution ran with. Reading it through an activity records the value in workflow history, so
/// the number is decided once, at the start of the execution, and survives replay.
/// </summary>
public sealed class LoadAgentBudgetActivity(IOptions<TemporalBridgeOptions> options)
{
    private readonly TemporalBridgeOptions _options = options.Value;

    [Activity("LoadAgentBudgetSeconds")]
    public int Load() => _options.AgentTimeoutSeconds;
}
