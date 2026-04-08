namespace Fleet.Orchestrator.Services;

/// <summary>
/// Static registry of workflow signal definitions.
/// Maps workflow type name → available signals with predefined button payloads.
/// </summary>
public static class SignalRegistry
{
    public record SignalButton(string Label, string Payload, bool RequiresComment = false);

    /// <summary>
    /// Defines a signal that can be sent to a workflow.
    /// ValidPhases: if non-null, the signal button is only shown when the workflow's Phase
    /// search attribute matches one of the listed values. Null means always show.
    /// </summary>
    public record SignalDef(string Name, string Label, SignalButton[] Buttons, string[]? ValidPhases = null);

    private static readonly Dictionary<string, SignalDef[]> _registry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ExternalAdvisoryWorkflow"] =
            [
                new SignalDef("advisory-review", "advisory review", [
                    new SignalButton("Approve", """{"Decision":"approved"}"""),
                    new SignalButton("Reject",  """{"Decision":"rejected"}""", RequiresComment: true),
                ]),
            ],
            // UWE workflow variants — mirror the compiled workflow signal definitions above
            ["UweDesignWorkflow"] =
            [
                new SignalDef("design-approval", "design approval", [
                    new SignalButton("Approve",         """{"Decision":"approved"}"""),
                    new SignalButton("Request Changes", """{"Decision":"changes_requested"}""", RequiresComment: true),
                    new SignalButton("Reject",          """{"Decision":"rejected"}""",          RequiresComment: true),
                ], ValidPhases: ["design-approval"]),
            ],
            ["UweDocMaintenanceWorkflow"] =
            [
                new SignalDef("doc-review", "doc review", [
                    new SignalButton("Approve",         """{"Decision":"approved"}"""),
                    new SignalButton("Request Changes", """{"Decision":"changes_requested"}""", RequiresComment: true),
                    new SignalButton("Reject",          """{"Decision":"rejected"}""",          RequiresComment: true),
                ], ValidPhases: ["doc-review"]),
            ],
            ["UwePrImplementationWorkflow"] =
            [
                new SignalDef("human-review", "human review", [
                    new SignalButton("Approve",         """{"Decision":"approved"}"""),
                    new SignalButton("Request Changes", """{"Decision":"changes_requested"}""", RequiresComment: true),
                    new SignalButton("Reject",          """{"Decision":"rejected"}"""),
                ], ValidPhases: ["human-review"]),
                new SignalDef("merge-approval", "merge approval", [
                    new SignalButton("Approve Merge",    """{"Decision":"approved"}"""),
                    new SignalButton("Request Changes",  """{"Decision":"changes_requested"}""", RequiresComment: true),
                    new SignalButton("Reject",           """{"Decision":"rejected"}""",          RequiresComment: true),
                ], ValidPhases: ["merge-approval"]),
                new SignalDef("escalation-decision", "escalation", [
                    new SignalButton("Approve",         """{"Decision":"approved"}"""),
                    new SignalButton("Request Changes", """{"Decision":"changes_requested"}""", RequiresComment: true),
                    new SignalButton("Cancel",          """{"Decision":"cancel"}"""),
                ], ValidPhases: ["escalation"]),
            ],
        };

    public static IReadOnlyDictionary<string, SignalDef[]> All => _registry;

    public static SignalDef[]? Get(string workflowType) =>
        _registry.TryGetValue(workflowType, out var sigs) ? sigs : null;
}
