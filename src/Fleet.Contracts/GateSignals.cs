namespace Fleet.Contracts;

/// <summary>
/// The names of the workflow signals that carry a decision. The temporal bridge reserves some of
/// them, the orchestrator offers them as dashboard buttons and scopes epic grants by them, and a
/// name that differs between the two is a gate one side does not protect.
/// </summary>
public static class GateSignal
{
    public const string MergeApproval = "merge-approval";
    public const string DocReview = "doc-review";
    public const string DesignApproval = "design-approval";
    public const string AdvisoryReview = "advisory-review";
    public const string AcceptanceDecision = "acceptance-decision";
    public const string RequestInput = "request-input";
    public const string RequestApproval = "request-approval";

    /// <summary>Operational routing, not an approval: agents are expected to send these two.</summary>
    public const string HumanReview = "human-review";
    public const string EscalationDecision = "escalation-decision";
}

/// <summary>
/// The approval gates that only a human may resolve.
///
/// <para>
/// These signal names carry an approval decision that the dashboard — which is auth-gated and
/// identifies the human behind it — is the only sanctioned source of. Everything else about a
/// workflow can be automated; these cannot, because the whole point of the gate is that something
/// outside the automation said yes.
/// </para>
///
/// <para>
/// The list is one list because it has two enforcement points in the temporal bridge, and two
/// copies of a security list is a list that drifts: the MCP signal tool refuses them from agent
/// callers, and the workflow engine refuses to let a definition emit one through
/// <c>signal_workflow</c>. A definition is authored by an agent, so without the second check the
/// step would be a way to approve your own work by writing it into a workflow (#280).
/// </para>
///
/// <para>
/// Deliberately NOT here: <see cref="GateSignal.HumanReview"/> and
/// <see cref="GateSignal.EscalationDecision"/>. Those are operational routing decisions rather than
/// approvals, and agents are expected to send them.
/// </para>
/// </summary>
public static class CeoGateSignals
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        GateSignal.MergeApproval,
        GateSignal.DocReview,
        GateSignal.DesignApproval,
        GateSignal.AdvisoryReview,
        GateSignal.AcceptanceDecision,
        GateSignal.RequestInput,
        GateSignal.RequestApproval,
    };

    /// <summary>The reserved names, for messages and documentation.</summary>
    public static IReadOnlyCollection<string> All => Names;

    /// <summary>
    /// True when this signal name is an approver-only gate. Case-insensitive, and whitespace is
    /// trimmed, because a check that <c>" Merge-Approval "</c> slips past is not a check.
    /// </summary>
    public static bool IsReserved(string? signalName) =>
        signalName is not null && Names.Contains(signalName.Trim());

    /// <summary>Comma-separated list for error messages.</summary>
    public static string Joined => string.Join(", ", Names);
}
