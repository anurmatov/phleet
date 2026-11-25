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

            if (step is FireAndForgetStep { ExecutionTimeoutMinutes: not null })
                throw new InvalidOperationException($"Workflow '{workflowTypeName}' step '{step.Name}' cannot set executionTimeoutMinutes on fire_and_forget.");
            if (step is ChildWorkflowStep { ExecutionTimeoutMinutes: <= 0 })
                throw new InvalidOperationException($"Workflow '{workflowTypeName}' step '{step.Name}' requires positive executionTimeoutMinutes.");

            if (step is WaitForSignalStep gateWait)
                ValidateGateVisit(gateWait, workflowTypeName);

            if (step is ParallelStep parallel)
                ValidateConcurrentWaits(parallel, workflowTypeName);

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

            // statusVar is a variable NAME, like outputVar. A blank name or a template has no
            // meaning, and the definition would silently read nothing back (#424).
            if (step is DelegateStep { StatusVar: { } statusVar } delegateStep
                && (string.IsNullOrWhiteSpace(statusVar) || statusVar.Contains("{{", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Workflow definition '{workflowTypeName}' is invalid: delegate step " +
                    $"'{delegateStep.Name ?? delegateStep.Target}' has statusVar '{statusVar}'. " +
                    "statusVar must be a literal, non-blank variable name, never a template.");
            }

            if (step is FailStep fail)
            {
                if (string.IsNullOrWhiteSpace(fail.Message))
                {
                    throw new InvalidOperationException(
                        $"Workflow definition '{workflowTypeName}' is invalid: fail step " +
                        $"'{fail.Name ?? "(unnamed)"}' has no 'message'. A fail step must say why the run failed.");
                }

                // The fail step's own ignoreFailure is a no-op — nothing can swallow it — so the
                // flag can only mislead whoever reads the definition.
                if (fail.IgnoreFailure)
                {
                    throw new InvalidOperationException(
                        $"Workflow definition '{workflowTypeName}' is invalid: fail step " +
                        $"'{fail.Name ?? "(unnamed)"}' sets 'ignoreFailure', which has no effect: " +
                        "a fail step cannot be suppressed by any ignoreFailure in the same run.");
                }
            }
        }

        ValidateNoRedelivery(root, workflowTypeName, ignoringAncestor: null, isNotifyStep: false);
    }

    /// <summary>
    /// <c>noRedelivery</c> is a promise about one directive: the bridge attempts to publish it at
    /// most once, and a cancellation at the step is never swallowed. A definition that asks for
    /// the promise where the engine cannot keep it is refused here, with the step named:
    ///
    /// <list type="bullet">
    /// <item><c>delegate_with_escalation</c>: an escalation <c>retry</c> runs the step again, with
    /// the same or a replaced instruction.</item>
    /// <item>a wait's <c>notifyStep</c>: it is sent again on every reminder, and the wait handles
    /// its failures itself.</item>
    /// <item>continuation retries left on: an incomplete answer would be continued with a second
    /// directive. Both fields have to say so, because both default to on.</item>
    /// <item>any ancestor with <c>ignoreFailure</c>: the step's own flag is not allowed to swallow
    /// a cancellation, and an ancestor's would swallow it one level up.</item>
    /// </list>
    ///
    /// Walked with the ancestors in hand, which <see cref="Walk"/> does not keep.
    /// </summary>
    private static void ValidateNoRedelivery(
        StepDefinition? step, string workflowTypeName, StepDefinition? ignoringAncestor, bool isNotifyStep)
    {
        if (step is null) return;

        if (step is DelegateStep { NoRedelivery: true } once)
        {
            var label = once.Name ?? once.Target;
            var prefix = $"Workflow definition '{workflowTypeName}' is invalid: delegate step '{label}' sets 'noRedelivery'";

            if (once is DelegateWithEscalationStep)
            {
                throw new InvalidOperationException(
                    $"{prefix} on a delegate_with_escalation. An escalation 'retry' sends the step's " +
                    "directive again, so noRedelivery is allowed only on a plain delegate step.");
            }

            if (isNotifyStep)
            {
                throw new InvalidOperationException(
                    $"{prefix} as the notifyStep of a wait_for_signal. A notifyStep is sent again on every " +
                    "reminder, so noRedelivery is allowed only on a plain delegate step.");
            }

            if (once.RetryOnIncomplete || once.MaxIncompleteRetries != 0)
            {
                throw new InvalidOperationException(
                    $"{prefix} but leaves continuation retries on (retryOnIncomplete: " +
                    $"{(once.RetryOnIncomplete ? "true" : "false")}, maxIncompleteRetries: {once.MaxIncompleteRetries}). " +
                    "A continuation is a second directive, so noRedelivery requires " +
                    "\"retryOnIncomplete\": false and \"maxIncompleteRetries\": 0.");
            }

            if (ignoringAncestor is not null)
            {
                throw new InvalidOperationException(
                    $"{prefix} under {StepTypeName(ignoringAncestor)} step '{ignoringAncestor.Name ?? "(unnamed)"}', " +
                    "which sets 'ignoreFailure'. A cancellation at a noRedelivery step must end the run, and " +
                    "an ancestor's ignoreFailure would swallow it. Remove ignoreFailure from every step above it.");
            }
        }

        // The nearest one is the one named: it is the first that would swallow.
        var above = step.IgnoreFailure ? step : ignoringAncestor;

        switch (step)
        {
            case SequenceStep s:
                foreach (var child in s.Steps) ValidateNoRedelivery(child, workflowTypeName, above, false);
                break;

            case ParallelStep s:
                foreach (var child in s.Steps ?? []) ValidateNoRedelivery(child, workflowTypeName, above, false);
                ValidateNoRedelivery(s.Step, workflowTypeName, above, false);
                break;

            case LoopStep s:
                foreach (var child in s.Steps) ValidateNoRedelivery(child, workflowTypeName, above, false);
                break;

            case BranchStep s:
                foreach (var (_, child) in s.Cases) ValidateNoRedelivery(child, workflowTypeName, above, false);
                ValidateNoRedelivery(s.Default, workflowTypeName, above, false);
                break;

            case WaitForSignalStep s:
                ValidateNoRedelivery(s.NotifyStep, workflowTypeName, above, isNotifyStep: true);
                break;
        }
    }

    /// <summary>The JSON <c>type</c> of a step that can hold others, for a message.</summary>
    private static string StepTypeName(StepDefinition step) => step switch
    {
        SequenceStep => "sequence",
        ParallelStep => "parallel",
        LoopStep => "loop",
        BranchStep => "branch",
        WaitForSignalStep => "wait_for_signal",
        _ => "enclosing",
    };

    /// <summary>
    /// #436 — <c>visitVar</c> and <c>delegatedGuard</c>. The guard is what stops a stale or
    /// misaddressed delegated approval at consumption, so a guard that cannot tie a payload to the
    /// current visit is refused here rather than run as a check that checks nothing. The one guard
    /// allowed without a visit is the refusing one, which accepts no delegated payload at all
    /// (<see cref="ValidateRefusingGuard"/>).
    /// </summary>
    private static void ValidateGateVisit(WaitForSignalStep wait, string workflowTypeName)
    {
        var label = wait.Name ?? wait.SignalName;

        // A variable NAME, like outputVar and statusVar: the visit id must land somewhere the guard
        // and later steps can read by a fixed name.
        if (wait.VisitVar is { } visitVar
            && (string.IsNullOrWhiteSpace(visitVar) || visitVar.Contains("{{", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Workflow definition '{workflowTypeName}' is invalid: wait_for_signal step " +
                $"'{label}' has visitVar '{visitVar}'. visitVar must be a literal, non-blank " +
                "variable name, never a template.");
        }

        if (wait.DelegatedGuard is not { } guard) return;

        // A refusing guard discards every delegated payload, so it has nothing to compare and no
        // visit to compare with. It is checked on its own and leaves every rule below as it was.
        if (guard.Refuse)
        {
            ValidateRefusingGuard(wait, guard, label, workflowTypeName);
            return;
        }

        if (wait.VisitVar is null)
        {
            throw new InvalidOperationException(
                $"Workflow definition '{workflowTypeName}' is invalid: wait_for_signal step " +
                $"'{label}' sets 'delegatedGuard' but has no 'visitVar'. The guard ties a delegated " +
                "approval to the current gate visit, so visitVar is required whenever delegatedGuard is present.");
        }

        if (string.IsNullOrWhiteSpace(guard.Marker))
        {
            throw new InvalidOperationException(
                $"Workflow definition '{workflowTypeName}' is invalid: wait_for_signal step " +
                $"'{label}' has a delegatedGuard with a blank 'marker'. The marker names the payload " +
                "property that identifies a delegated approval (e.g. \"GrantId\").");
        }

        if (guard.Require is null || !guard.Require.ContainsKey("VisitId"))
        {
            throw new InvalidOperationException(
                $"Workflow definition '{workflowTypeName}' is invalid: wait_for_signal step " +
                $"'{label}' has a delegatedGuard whose 'require' has no 'VisitId'. A delegated " +
                "approval must name the gate visit it was decided for, e.g. " +
                "\"VisitId\": \"{{vars.<visitVar>}}\".");
        }
    }

    /// <summary>
    /// <c>delegatedGuard.refuse</c>: the wait accepts no delegated payload. It needs the marker
    /// that identifies one, and nothing else of a gate visit: a <c>require</c> would be a
    /// comparison nothing is accepted by, and a <c>visitVar</c> would publish the wait as an open
    /// gate visit that no decision can be made at.
    /// </summary>
    private static void ValidateRefusingGuard(
        WaitForSignalStep wait, DelegatedGuard guard, string label, string workflowTypeName)
    {
        var prefix = $"Workflow definition '{workflowTypeName}' is invalid: wait_for_signal step '{label}' " +
                     "has a delegatedGuard with 'refuse'";

        if (string.IsNullOrWhiteSpace(guard.Marker))
        {
            throw new InvalidOperationException(
                $"{prefix} and a blank 'marker'. The marker names the payload property that identifies " +
                "a delegated approval (e.g. \"GrantId\"); without it nothing would be refused.");
        }

        if (guard.Require is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"{prefix} and 'require' entries. A refusing guard discards every delegated payload, " +
                "so it compares nothing: remove 'require', or remove 'refuse' to guard a gate visit.");
        }

        if (wait.VisitVar is not null)
        {
            throw new InvalidOperationException(
                $"{prefix} and a 'visitVar'. A refusing wait is not a gate visit: it must not mint or " +
                "publish one. Remove 'visitVar', or remove 'refuse' to guard a gate visit.");
        }
    }

    /// <summary>
    /// Two branches of one <c>parallel</c> must not wait on the same signal name. The engine keeps
    /// ONE waiter per name: the branch that registers second takes it, a signal wakes only that
    /// branch, and the other is left parked until its own timeout — or for ever if it has none.
    ///
    /// <para>
    /// Only what the tree proves is refused: LITERAL signal names. In a static parallel, the same
    /// literal in two different branches; in a <c>forEach</c>, any literal at all, because every
    /// item's branch runs the same step and so waits on the same name. Waits that follow one
    /// another inside a single branch are fine and are not compared.
    /// </para>
    ///
    /// <para>
    /// A templated name is not judged here — whether <c>approve-{{vars.item}}</c> collides depends
    /// on the items — and neither is <c>delegate_with_escalation</c>, whose
    /// <c>escalation-decision</c> wait exists only if the step fails. The engine logs a warning
    /// when either does collide at run time.
    /// </para>
    /// </summary>
    private static void ValidateConcurrentWaits(ParallelStep parallel, string workflowTypeName)
    {
        var label = parallel.Name ?? "(unnamed)";

        if (parallel.Steps is not { Length: > 0 })
        {
            // forEach: one step template, run once per item.
            if (parallel.ForEach is null || parallel.Step is null) return;
            if (LiteralWaits(parallel.Step).FirstOrDefault() is not { } shared) return;

            throw new InvalidOperationException(
                $"Workflow definition '{workflowTypeName}' is invalid: parallel step '{label}' fans out " +
                $"wait_for_signal step '{shared.Name ?? shared.SignalName}', which waits on the literal " +
                $"signal '{shared.SignalName}' in every branch. {OneWaiterPerName} Put the item in the " +
                $"signal name, e.g. \"{shared.SignalName}-{{{{vars.{parallel.ItemVar ?? "item"}}}}}\".");
        }

        var seen = new Dictionary<string, WaitForSignalStep>(StringComparer.Ordinal);
        foreach (var branch in parallel.Steps)
        {
            // Collected per branch and merged afterwards, so two waits in the SAME branch never
            // meet each other here.
            var inBranch = new Dictionary<string, WaitForSignalStep>(StringComparer.Ordinal);
            foreach (var wait in LiteralWaits(branch))
                inBranch.TryAdd(wait.SignalName, wait);

            foreach (var (signalName, wait) in inBranch)
            {
                if (seen.TryGetValue(signalName, out var other))
                {
                    throw new InvalidOperationException(
                        $"Workflow definition '{workflowTypeName}' is invalid: parallel step '{label}' has two " +
                        $"branches that wait on signal '{signalName}' at the same time (wait_for_signal steps " +
                        $"'{other.Name ?? other.SignalName}' and '{wait.Name ?? wait.SignalName}'). " +
                        $"{OneWaiterPerName} Give each branch its own signal name.");
                }

                seen[signalName] = wait;
            }
        }
    }

    private const string OneWaiterPerName =
        "The engine keeps one waiter per signal name, so a signal would wake only one of them and the " +
        "others would stay parked until their own timeout, or for ever.";

    private static IEnumerable<WaitForSignalStep> LiteralWaits(StepDefinition step) =>
        Walk(step).OfType<WaitForSignalStep>()
            .Where(w => !string.IsNullOrEmpty(w.SignalName) && !w.SignalName.Contains("{{", StringComparison.Ordinal));

    /// <summary>
    /// Depth-first walk over every step in the tree, including the steps nested inside composite
    /// steps and the notify step of a wait.
    ///
    /// New composite step types must be added here, and to <see cref="ValidateNoRedelivery"/>,
    /// which walks the same tree with the ancestors in hand. A step type that holds children and
    /// is not listed simply hides them from validation — silently, which is why the list is
    /// exhaustive rather than best-effort.
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
