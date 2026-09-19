namespace Fleet.Temporal.Configuration;

/// <summary>
/// The single place that decides how an agent's own response budget relates to the Temporal
/// activity container it runs inside.
///
/// Two clocks are involved and they must be ordered. The activity enforces its own budget and
/// reports it; Temporal enforces <c>StartToCloseTimeout</c> and kills the attempt without
/// explanation. If the Temporal timer is the shorter of the two, a healthy agent is killed
/// mid-turn and the activity never gets to say why — which is exactly what
/// <c>ConsensusReviewWorkflow</c> did with a 15-minute StartToClose over a 90-minute budget.
///
/// Both numbers therefore derive from one value here, and
/// <see cref="Activities.DelegateToAgentActivity"/> re-checks the ordering at activity start so
/// a caller that schedules its own options cannot reintroduce the inversion.
/// </summary>
public static class AgentDelegationBudget
{
    /// <summary>
    /// Headroom between the agent's budget and the activity's StartToCloseTimeout. Covers the
    /// publish, the response hop and the activity's own timeout/reporting path, so the activity
    /// always loses its own race first and can report elapsed time.
    /// </summary>
    public static readonly TimeSpan StartToCloseMargin = TimeSpan.FromMinutes(2);

    /// <summary>The StartToCloseTimeout an activity given <paramref name="budget"/> must be scheduled with.</summary>
    public static TimeSpan StartToCloseFor(TimeSpan budget) => budget + StartToCloseMargin;
}
