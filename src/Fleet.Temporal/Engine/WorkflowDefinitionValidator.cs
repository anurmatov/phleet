namespace Fleet.Temporal.Engine;

/// <summary>
/// Post-deserialization checks on a step tree, run once at definition LOAD time (#280 D-6, M2).
///
/// <para>
/// Load time is the only useful place for these. A definition is loaded once per execution, by an
/// activity, before any step runs — so a bad definition fails immediately, with the step named, and
/// the failure is attributable to the definition rather than to whatever step happened to run
/// first. The same check at step-execution time would fire only when the workflow reached that
/// branch, possibly days in, and could be swallowed by <c>ignoreFailure</c> on the surrounding
/// step.
/// </para>
///
/// <para>
/// The engine had no validation pass before this; this is it. Keep it to invariants that are
/// decidable from the tree alone — anything needing runtime state belongs in the step executor.
/// </para>
/// </summary>
public static class WorkflowDefinitionValidator
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> naming the offending step if the tree is
    /// unloadable. Returns normally otherwise.
    /// </summary>
    public static void Validate(StepDefinition root, string workflowTypeName)
    {
        foreach (var step in Walk(root))
        {
            // bindTo with no outputVar has nowhere to write its verdict. Silently discarding it
            // would leave every park effectively unbound — correlation configured, never applied,
            // and no signal that anything was wrong (#280 D-6).
            if (step is WaitForSignalStep { BindTo: not null, OutputVar: null } wait)
            {
                throw new InvalidOperationException(
                    $"Workflow definition '{workflowTypeName}' is invalid: wait_for_signal step " +
                    $"'{wait.Name ?? wait.SignalName}' sets 'bindTo' but has no 'outputVar'. " +
                    "The correlation result is written to '{outputVar}_bindMatch', so outputVar is " +
                    "required whenever bindTo is present.");
            }

            // An approver-only gate written as a literal is refused before the definition can ever
            // run. The engine checks again at execution — a templated signal name is unknowable
            // here — but catching the literal case at load means the answer arrives when someone
            // publishes the definition rather than at 3am when the step is first reached.
            if (step is SignalWorkflowStep signal && CeoGateSignals.IsReserved(signal.SignalName))
            {
                throw new InvalidOperationException(
                    $"Workflow definition '{workflowTypeName}' is invalid: signal_workflow step " +
                    $"'{signal.Name ?? signal.SignalName}' sends '{signal.SignalName}', which is an " +
                    $"approver-only gate. These signals resolve a human approval and may only be sent " +
                    $"from the dashboard: {CeoGateSignals.Joined}.");
            }
        }
    }

    /// <summary>
    /// Depth-first walk over every step in the tree, including the steps nested inside composite
    /// steps and the notify step of a wait.
    ///
    /// New composite step types must be added here. A step type that holds children and is not
    /// listed simply hides them from validation — silently, which is why the list is exhaustive
    /// rather than best-effort.
    /// </summary>
    public static IEnumerable<StepDefinition> Walk(StepDefinition? step)
    {
        if (step is null) yield break;

        yield return step;

        switch (step)
        {
            case SequenceStep s:
                foreach (var child in s.Steps)
                    foreach (var d in Walk(child)) yield return d;
                break;

            case ParallelStep s:
                foreach (var child in s.Steps ?? [])
                    foreach (var d in Walk(child)) yield return d;
                foreach (var d in Walk(s.Step)) yield return d;
                break;

            case LoopStep s:
                foreach (var child in s.Steps)
                    foreach (var d in Walk(child)) yield return d;
                break;

            case BranchStep s:
                foreach (var (_, child) in s.Cases)
                    foreach (var d in Walk(child)) yield return d;
                foreach (var d in Walk(s.Default)) yield return d;
                break;

            case WaitForSignalStep s:
                foreach (var d in Walk(s.NotifyStep)) yield return d;
                break;
        }
    }
}
