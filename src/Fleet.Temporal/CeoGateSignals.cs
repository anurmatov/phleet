namespace Fleet.Temporal;

/// <summary>
/// The approval gates that only a human may resolve.
///
/// <para>
/// These four signal names carry an approval decision that the dashboard — which is auth-gated and
/// identifies the human behind it — is the only sanctioned source of. Everything else about a
/// workflow can be automated; these cannot, because the whole point of the gate is that something
/// outside the automation said yes.
/// </para>
///
/// <para>
/// The list lives here rather than inside one caller because it now has TWO enforcement points,
/// and two copies of a security list is a list that drifts: the MCP signal tool refuses them from
/// agent callers, and the workflow engine refuses to let a definition emit one through
/// <c>signal_workflow</c>. A definition is authored by an agent, so without the second check the
/// new step would be a way to approve your own work by writing it into a workflow (#280).
/// </para>
///
/// <para>
/// Deliberately NOT here: <c>human-review</c> and <c>escalation-decision</c>. Those are operational
/// routing decisions rather than approvals, and agents are expected to send them.
/// </para>
/// </summary>
public static class CeoGateSignals
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "merge-approval",
        "doc-review",
        "design-approval",
        "advisory-review",
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
