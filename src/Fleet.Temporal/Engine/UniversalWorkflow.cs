namespace Fleet.Temporal.Engine;

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Workflows;

/// <summary>
/// Dynamic workflow handler that interprets step definitions loaded from the orchestrator DB.
/// Registered as <c>[Workflow(Dynamic = true)]</c> — catches all workflow type names that do NOT
/// have a statically-registered typed workflow class on the same worker. Typed handlers always
/// take priority, so existing workflows are completely unaffected.
///
/// Workflow return value: set <c>output_var: "_result"</c> on the final step. The engine
/// returns <c>_variables["_result"]</c> from <see cref="RunAsync"/>.
///
/// Variable scopes available in template expressions:
///   {{input.*}}    — workflow start arguments (JsonElement)
///   {{vars.*}}     — step outputs keyed by output_var
///   {{config.*}}   — FleetWorkflowOptions serialized as JsonElement
///   {{workflow.*}} — this execution's own identity: id, runId, type (#280 D-8). Read-only:
///                    set_variable writes into vars, so a definition cannot shadow it and
///                    redirect the wakeups it sends (MUST NOT 27).
/// </summary>
[Workflow(Dynamic = true)]
public class UniversalWorkflow
{
    /// <summary>
    /// Cap on buffered signals per execution. Unbounded workflow state is a memory and
    /// history-size hazard; eviction is oldest-first, counted and logged, never silent (#280 D-2).
    /// </summary>
    internal const int MaxBufferedSignals = 16;

    /// <summary>
    /// Patch id gating the buffer FAST PATH — consuming a buffered entry, which changes the
    /// command sequence. Buffering itself emits no command and is replay-neutral (#280 D-3).
    /// </summary>
    internal const string SignalBufferPatchId = "uwe-signal-buffer-v1";

    /// <summary>
    /// Error type stamped on the refusal to emit an approver-only gate signal.
    ///
    /// It exists so the refusal can be excluded from the <c>ignoreFailure</c> catch by TYPE rather
    /// than by message matching. An authorization check a definition can switch off with one flag
    /// is not an authorization check.
    /// </summary>
    internal const string ReservedSignalErrorType = "ReservedSignalName";

    /// <summary>
    /// Error type stamped on a deliberate <c>fail</c> step (#424). Like the refusal, it is excluded
    /// from the <c>ignoreFailure</c> catch by TYPE, never by message, so no ancestor in the same run
    /// can turn a deliberate failure into success.
    /// </summary>
    internal const string ExplicitFailErrorType = "ExplicitFail";

    /// <summary>Value of a delegate's <c>statusVar</c> before the attempt returns a status (#424).</summary>
    internal const string UnknownStatus = "unknown";

    /// <summary>
    /// Keyword search attribute naming the gate visit a parked <c>visitVar</c> wait is serving, or
    /// <c>""</c> when none is (#436). The orchestrator compares it with a delegated decision's
    /// <c>VisitId</c> before sending; the wait's guard re-checks at consumption.
    /// </summary>
    internal const string GateVisitAttribute = "GateVisit";

    /// <summary>
    /// The guard's discard warning, as a message template (#436). Operators search for the leading
    /// text. Only the signal name and the wait's own visit id are logged — never the payload, which
    /// carries the decision's evidence.
    /// </summary>
    internal const string DelegatedDiscardLogTemplate =
        "delegated signal discarded: stale visit or artifact (signal={Signal} visit={Visit})";

    /// <summary>A signal that arrived with no waiter registered, plus its arrival ordinal.</summary>
    private readonly record struct BufferedSignal(JsonElement Payload, long Seq);

    /// <summary>
    /// A wait's <c>delegatedGuard</c> with its expectations rendered (#436), resolved ONCE at wait
    /// entry like <c>bindTo</c>, so nothing that runs during the wait can move what it accepts.
    /// </summary>
    private sealed record ConsumptionGuard(
        string? Marker,
        IReadOnlyList<KeyValuePair<string, string>> Expected,
        string SignalName,
        string? VisitId);

    private readonly Dictionary<string, object?> _variables = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _signalWaiters = new();

    /// <summary>
    /// Signals accepted while no waiter was registered. At most one entry per signal name —
    /// last write wins, and a refresh also refreshes <c>Seq</c>. Entries live until consumed by a
    /// matching wait or until the workflow closes; they NEVER expire on a timer, because a timer
    /// is a second, invisible way to lose a wakeup (#280 D-2).
    /// </summary>
    private readonly Dictionary<string, BufferedSignal> _pendingSignals = new();

    /// <summary>
    /// Arrival ordinal, incremented ONCE per signal arrival before the delivered-vs-buffered
    /// decision. Delivered signals advance it too, so the value is a true arrival ordinal rather
    /// than a buffer-write counter — which is what makes the escalation epoch comparison in D-10
    /// mean anything (MUST NOT 9).
    /// </summary>
    private long _signalArrivalSeq;

    /// <summary>
    /// Entries into waits that set <c>visitVar</c>, across the whole run (#436). One counter for
    /// every such wait, so a visit id is unique within the run whatever its signal name. Workflow
    /// state, not a command: it is rebuilt identically on replay because step order is
    /// deterministic.
    /// </summary>
    private int _gateVisitCount;

    private TemplateEngine _template = null!;
    private bool _skipRemaining;

    /// <summary>
    /// Cached <see cref="Workflow.Patched"/> result, evaluated once in <see cref="RunAsync"/>.
    /// Never evaluated from the signal handler, where the marker command would land at an
    /// unpredictable point in history (#280 D-3, MUST NOT 13).
    /// </summary>
    private bool _signalBufferEnabled;

    [WorkflowRun]
    public async Task<object?> RunAsync(IRawValue[] args)
    {
        // 1. Parse input into variables
        var input = args.Length > 0
            ? Workflow.PayloadConverter.ToValue<JsonElement>(args[0])
            : default;
        _variables["input"] = input;

        // 2. Initialize vars scope before template engine (template engine holds a reference)
        _variables["vars"] = new Dictionary<string, object?>();

        // The workflow's own identity as a template scope (#280 D-8). All three values are
        // replay-stable. A definition needs this to hand a gated run the id to signal back, and
        // before this scope existed the only alternative was an agent remembering it.
        _variables["workflow"] = new Dictionary<string, object?>
        {
            ["id"]    = Workflow.Info.WorkflowId,
            ["runId"] = Workflow.Info.RunId,
            ["type"]  = Workflow.Info.WorkflowType,
        };

        _template = new TemplateEngine(_variables);

        // 3. Load workflow definition from DB (determinism-safe — replayed from history)
        var definition = await Workflow.ExecuteActivityAsync<WorkflowDefinitionModel>(
            "LoadWorkflowDefinition",
            [Workflow.Info.WorkflowType],
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        // 4. Load config (determinism-safe)
        _variables["config"] = await Workflow.ExecuteActivityAsync<JsonElement>(
            "LoadWorkflowConfig",
            Array.Empty<object?>(),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        // 5. Decide once, before any step runs, whether the buffer fast path is available.
        //    An execution that started before this shipped replays with false and keeps the old
        //    command sequence exactly; a new execution gets the fix (#280 D-3).
        _signalBufferEnabled = Workflow.Patched(SignalBufferPatchId);

        // 6. Execute the step tree
        await ExecuteStepAsync(definition.Root);

        return _variables.GetValueOrDefault("_result");
    }

    /// <summary>
    /// Dynamic signal handler — routes all signals to waiting TCS instances, and buffers the rest.
    ///
    /// Before #280 this method had no <c>else</c>: a signal arriving while no waiter was
    /// registered was accepted by Temporal, recorded in history, and then silently discarded. For
    /// a driver workflow that window is most of its life, so the wakeup it was waiting for looked
    /// delivered and simply never happened.
    ///
    /// Two losses are closed here. The missing <c>else</c>, and the discarded
    /// <see cref="TaskCompletionSource{TResult}.TrySetResult"/> return value — <c>false</c> means
    /// the waiter had already completed in this same workflow task, so that payload was dropped
    /// too. Both now fall through to the buffer.
    ///
    /// Buffering emits no workflow command, so it is replay-neutral and runs unconditionally;
    /// only CONSUMING an entry is patch-gated (#280 D-2, D-3).
    /// </summary>
    [WorkflowSignal(Dynamic = true)]
    public Task HandleSignalAsync(string signalName, IRawValue[] args)
    {
        // Incremented for EVERY arrival, before the deliver-vs-buffer decision, so the ordinal
        // orders arrivals rather than buffer writes (#280 D-2, MUST NOT 9).
        var seq = ++_signalArrivalSeq;

        var payload = args.Length > 0
            ? Workflow.PayloadConverter.ToValue<JsonElement>(args[0])
            : default;

        if (_signalWaiters.TryGetValue(signalName, out var tcs) && tcs.TrySetResult(payload))
            return Task.CompletedTask;

        BufferSignal(signalName, payload, seq);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True for a failure no <c>ignoreFailure</c> may swallow: the approver-only signal REFUSAL,
    /// and a deliberate <c>fail</c> step (#424). Matched on the error type, not the message, so
    /// rewording a message cannot quietly make it suppressible.
    /// </summary>
    private static bool IsUnsuppressible(Exception ex) =>
        ex is ApplicationFailureException { ErrorType: ReservedSignalErrorType or ExplicitFailErrorType };

    private void BufferSignal(string signalName, JsonElement payload, long seq)
    {
        // Last write wins for a repeated signal name, and refreshes Seq with it. Eviction is
        // oldest-first by arrival ordinal — deterministic, unlike dictionary enumeration order.
        if (!_pendingSignals.ContainsKey(signalName) && _pendingSignals.Count >= MaxBufferedSignals)
        {
            var oldest = _pendingSignals.OrderBy(e => e.Value.Seq).First();
            _pendingSignals.Remove(oldest.Key);
            Workflow.Logger.LogWarning(
                "Signal buffer full ({Max}) — evicted oldest buffered signal '{Evicted}' (seq {Seq}) to make room for '{Incoming}'",
                MaxBufferedSignals, oldest.Key, oldest.Value.Seq, signalName);
        }

        _pendingSignals[signalName] = new BufferedSignal(payload, seq);
    }

    /// <summary>
    /// Takes a buffered signal if the fast path is enabled and an entry exists that arrived
    /// strictly after <paramref name="afterSeq"/>.
    ///
    /// Strictly greater, never <c>&gt;=</c>: a snapshot taken after an arrival has
    /// <c>epoch == thatEntry.Seq</c>, so <c>&gt;=</c> would accept the arrival that immediately
    /// preceded it (#280 D-2, MUST NOT 9).
    ///
    /// A declined entry is LEFT IN THE BUFFER, never deleted. Deleting it here would be a silent
    /// discard — the exact defect this whole mechanism exists to remove (MUST NOT 10).
    /// </summary>
    private bool TryTakeBufferedSignal(string signalName, long afterSeq, out JsonElement payload)
    {
        payload = default;
        if (!_signalBufferEnabled) return false;
        if (!_pendingSignals.TryGetValue(signalName, out var buffered)) return false;
        if (buffered.Seq <= afterSeq) return false;

        _pendingSignals.Remove(signalName);
        payload = buffered.Payload;
        return true;
    }

    // =========================================================================
    // Step dispatch — uses C# pattern matching on concrete record types.
    // DelegateWithEscalationStep MUST appear before DelegateStep (subclass ordering).
    // =========================================================================

    private async Task<object?> ExecuteStepAsync(StepDefinition step)
    {
        if (_skipRemaining) return null;

        try
        {
            return step switch
            {
                SequenceStep s                                         => await ExecuteSequenceAsync(s),
                ParallelStep s                                         => await ExecuteParallelAsync(s),
                DelegateWithEscalationStep s                           => await ExecuteDelegateWithEscalationAsync(s),
                DelegateStep s                                         => await ExecuteDelegateAsync(s),
                WaitForSignalStep s                                    => await ExecuteWaitForSignalAsync(s),
                FireAndForgetStep s                                    => await ExecuteFireAndForgetAsync(s),
                ChildWorkflowStep s                                    => await ExecuteChildWorkflowAsync(s),
                LoopStep s                                             => await ExecuteLoopAsync(s),
                BranchStep s                                           => await ExecuteBranchAsync(s),
                BreakStep                                              => ControlFlow.Break,    // added in PR #816
                ContinueStep                                           => ControlFlow.Continue, // added in PR #816
                NoopStep                                               => null,                 // explicit no-op
                SetVariableStep s                                      => ExecuteSetVariable(s),
                SetAttributeStep s                                     => await ExecuteSetAttributeAsync(s),
                HttpRequestStep s                                      => await ExecuteHttpRequestAsync(s),
                CrossNamespaceStartStep s                              => await ExecuteCrossNamespaceStartAsync(s),
                SleepStep s                                            => await ExecuteSleepAsync(s),
                SignalWorkflowStep s                                   => await ExecuteSignalWorkflowAsync(s),
                FailStep s                                             => await ExecuteFailAsync(s),
                _                                                      => throw new InvalidOperationException(
                                                                              $"Unknown step type: {step.GetType().Name}")
            };
        }
        // ignoreFailure suppresses FAILURES, not refusals or deliberate fails. A step that was
        // denied permission to do something must not be silenced by the definition that asked for
        // it — otherwise the approver-only guard below would be one JSON flag away from being
        // switched off (#280). A `fail` step is the definition saying the run must fail; an
        // ancestor's ignoreFailure must not undo that (#424).
        catch (Exception ex) when (step.IgnoreFailure && !IsUnsuppressible(ex))
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Composition
    // -------------------------------------------------------------------------

    private async Task<object?> ExecuteSequenceAsync(SequenceStep step)
    {
        object? last = null;
        foreach (var child in step.Steps)
        {
            last = await ExecuteStepAsync(child);
            if (last is ControlFlow) return last; // propagate break/continue upward
        }
        return last;
    }

    private async Task<object?> ExecuteParallelAsync(ParallelStep step)
    {
        if (step.Steps is { Length: > 0 })
        {
            // Static parallel: all branches run concurrently
            await WhenAllBranchesAsync(step.Steps.Select(s => (Task)ExecuteStepAsync(s)).ToList());
            return null;
        }

        if (step.ForEach != null && step.Step != null && step.ItemVar != null)
        {
            // Dynamic parallel: fan out over a resolved array
            var items = _template.Resolve(step.ForEach);
            var itemList = FlattenToStringList(items);

            var results = new List<object?>(itemList.Count);
            // Initialize slots so results.Add is safe across cooperative tasks
            for (int i = 0; i < itemList.Count; i++) results.Add(null);

            // Each branch binds its item into TemplateEngine.IterationOverlay (AsyncLocal),
            // which is scoped to each branch's execution context. This prevents cross-iteration
            // overwrites when tasks interleave at activity await boundaries. (#686)
            var tasks = itemList.Select((item, idx) => (Func<Task>)(async () =>
            {
                _template.IterationOverlay.Value = new Dictionary<string, object?> { [step.ItemVar!] = item };
                var result = await ExecuteStepAsync(step.Step!);
                results[idx] = result;
            }));

            await WhenAllBranchesAsync(tasks.Select(t => t()).ToList());

            // Collect results into vars.{step.Name}
            if (step.Name != null)
                SetVar(step.Name, results);

            return results;
        }

        return null;
    }

    /// <summary>
    /// Awaits every branch, as <c>Workflow.WhenAllAsync</c> always did.
    /// Branches already started run to completion; none is cancelled.
    ///
    /// WhenAll rethrows the FIRST faulted branch in branch order. If an earlier branch failed
    /// ordinarily and a later one hit a <c>fail</c> step or the refusal, the parallel step's own
    /// <c>ignoreFailure</c> would see only the ordinary failure and swallow the run's unsuppressible
    /// one (#424). Every branch has finished when WhenAll throws, so the first unsuppressible
    /// failure in branch order is picked instead — deterministic, and no command changes.
    /// </summary>
    private static async Task WhenAllBranchesAsync(IReadOnlyList<Task> branches)
    {
        try
        {
            await Workflow.WhenAllAsync(branches);
        }
        catch (Exception) when (FirstUnsuppressible(branches) is { } unsuppressible)
        {
            ExceptionDispatchInfo.Capture(unsuppressible).Throw();
        }
    }

    private static Exception? FirstUnsuppressible(IReadOnlyList<Task> branches) =>
        branches
            .SelectMany(b => b.Exception?.InnerExceptions ?? Enumerable.Empty<Exception>())
            .FirstOrDefault(IsUnsuppressible);

    private async Task<object?> ExecuteLoopAsync(LoopStep step)
    {
        object? lastResult = null;
        for (int i = 0; i < step.MaxIterations; i++)
        {
            if (step.Name != null)
                SetVar($"{step.Name}_iteration", i);

            foreach (var child in step.Steps)
            {
                lastResult = await ExecuteStepAsync(child);
                if (lastResult is ControlFlow cf)
                {
                    if (cf == ControlFlow.Break) return null; // exit loop, don't propagate sentinel
                    if (cf == ControlFlow.Continue) break;    // skip rest of this iteration
                }
                if (_skipRemaining) return null;
            }

            if (_skipRemaining) return null;
        }
        return lastResult;
    }

    private async Task<object?> ExecuteBranchAsync(BranchStep step)
    {
        var value = _template.ResolveString(step.On);
        var matchedStep = step.Cases.TryGetValue(value, out var c) ? c : step.Default;

        if (matchedStep == null) return null;
        var result = await ExecuteStepAsync(matchedStep);
        if (step.OutputVar != null && result is not ControlFlow)
            SetVar(step.OutputVar, result);
        return result;
    }

    // -------------------------------------------------------------------------
    // Agent delegation
    // -------------------------------------------------------------------------

    private async Task<object?> ExecuteDelegateAsync(DelegateStep step)
    {
        // Reset first, before anything that can throw, so an attempt that never returns a status
        // reads "unknown" rather than a previous attempt's value (#424). Only a workflow variable
        // is written; the command sequence is unchanged.
        if (step.StatusVar != null)
            SetVar(step.StatusVar, UnknownStatus);

        var target = _template.ResolveString(step.Target);
        var instruction = await ResolveInstructionAsync(step);
        var taskId = $"{Workflow.Info.WorkflowId}/{step.Name ?? "delegate"}";
        var timeout = TimeSpan.FromMinutes(step.TimeoutMinutes);
        var repo = step.Repo is null ? null : _template.ResolveString(step.Repo);

        // The repo is appended ONLY when it resolved to something (#347 D9). A step without it —
        // every definition written before the field existed, and the escalation notification
        // below — schedules exactly the five arguments it always did, so recorded histories
        // replay and the activity input stays byte-identical. The parameters are positional, so
        // agentBudgetSeconds goes in as its default (0) to reach the repo slot.
        var args = string.IsNullOrWhiteSpace(repo)
            ? new object?[] { target, instruction, taskId, step.RetryOnIncomplete, step.MaxIncompleteRetries }
            : new object?[] { target, instruction, taskId, step.RetryOnIncomplete, step.MaxIncompleteRetries, 0, repo };

        var result = await Workflow.ExecuteActivityAsync<AgentTaskResult>(
            DelegateToAgentActivity.ActivityName,
            args,
            new ActivityOptions
            {
                StartToCloseTimeout = timeout + TimeSpan.FromMinutes(2),
                HeartbeatTimeout = TimeSpan.FromMinutes(2),
                RetryPolicy = new() { MaximumAttempts = 1 }
            });

        if (step.OutputVar != null)
            SetVar(step.OutputVar, ResolveOutputVar(result));

        if (step.StatusVar != null)
            SetVar(step.StatusVar, NormalizeStatus(result.Status));

        return result.Text;
    }

    /// <summary>
    /// The status a definition sees in <c>statusVar</c>: the relay token trimmed and lower-cased,
    /// with null or blank as <c>unknown</c>. No mapping and no allow-list — an unrecognised token
    /// is stored as-is, because the definition must see what the agent actually reported (#424).
    /// </summary>
    internal static string NormalizeStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? UnknownStatus : status.Trim().ToLowerInvariant();

    /// <summary>
    /// Returns the value to store in outputVar from a delegate result.
    /// When the agent answered IDLE, Text is empty and the status prefix is all the
    /// caller has — store "[status: idle]" so templates using
    /// <c>{{vars.x | default: '...'}}</c> don't fire their fallback on a normal IDLE.
    /// </summary>
    internal static string ResolveOutputVar(AgentTaskResult result) =>
        string.IsNullOrEmpty(result.Text) && result.Status == "idle"
            ? "[status: idle]"
            : result.Text;

    private async Task<object?> ExecuteDelegateWithEscalationAsync(DelegateWithEscalationStep step)
    {
        var escalationTarget = _template.ResolveString("{{config.EscalationTarget}}");
        // Keep a mutable local for retry-with-updated-instruction
        DelegateWithEscalationStep current = step;

        while (true)
        {
            // Epoch snapshot for the escalation buffer check (#280 D-10), taken BEFORE the attempt
            // and once PER ITERATION. Both halves are load-bearing (MUST NOT 8):
            //
            //  - before the attempt, because the operator who resolves an escalation is almost
            //    always reacting to the step already being in trouble, i.e. while the delegate is
            //    still running. Snapshotting inside the catch would score that reply as stale and
            //    decline the very signal it exists to accept.
            //  - per iteration, because a `retry` decision starts a fresh window; snapshotting
            //    once per step would let a leftover reply from the previous round resolve the next
            //    escalation.
            var attemptEpoch = _signalArrivalSeq;

            try
            {
                return await ExecuteDelegateAsync(current);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var stepLabel = current.Name ?? "delegate_with_escalation";
                var targetName = _template.ResolveString(current.Target);

                // 1. Set Phase search attribute
                try { UpsertPhaseAttribute("escalation"); }
                catch { /* non-fatal */ }

                // 2. A reply that arrived during this attempt is already here. Consume it and skip
                //    both the notification and the wait — this wait is INDEFINITE (there is no
                //    timeout branch below), so without this a dropped escalation-decision hangs
                //    the workflow forever rather than timing out (#280 D-10, F18).
                JsonElement decision;
                if (TryTakeBufferedSignal("escalation-decision", attemptEpoch, out var early))
                {
                    Workflow.Logger.LogInformation(
                        "escalation-decision for step '{Step}' arrived during the attempt — consuming without notifying",
                        stepLabel);
                    decision = early;
                }
                else
                {
                    // 3. Register signal waiter BEFORE sending notification so we don't miss
                    //    a signal that arrives while the notification activity is in-flight.
                    //    This ordering, not the epoch, is what covers a reply sent while the
                    //    notification activity is still in flight (#280 F20).
                    var tcs = new TaskCompletionSource<JsonElement>();
                    _signalWaiters["escalation-decision"] = tcs;

                    // 4. Best-effort notification to escalation target. It carries no Repo: it
                    //    is about the workflow, not the repository, and stays a five-argument
                    //    delegation. The retried attempt keeps the step's own Repo (`with` copies it).
                    var notifyStep = new DelegateStep
                    {
                        Name = $"{stepLabel}_escalation_notify",
                        Target = escalationTarget,
                        Instruction =
                            $"[workflow escalation] step failed in workflow {Workflow.Info.WorkflowId}.\n" +
                            $"step: {stepLabel}\nagent: {targetName}\nerror: {ex.Message}\n\n" +
                            $"send signal 'escalation-decision' with JSON payload:\n" +
                            $"  {{\"Decision\": \"retry\"}}   — retry the step\n" +
                            $"  {{\"Decision\": \"retry\", \"UpdatedInstruction\": \"...\"}}   — retry with new instruction\n" +
                            $"  {{\"Decision\": \"skip\"}}    — stop here\n" +
                            $"  {{\"Decision\": \"continue\"}} — proceed despite failure",
                        TimeoutMinutes = 10,
                        IgnoreFailure = true
                    };

                    try { await ExecuteDelegateAsync(notifyStep); }
                    catch { /* notification failure is non-fatal */ }

                    // 5. Wait indefinitely for escalation-decision signal
                    await Workflow.WaitConditionAsync(() => tcs.Task.IsCompleted);
                    decision = tcs.Task.Result;
                    _signalWaiters.Remove("escalation-decision");
                }

                // 6. Decision handling — unchanged, and deliberately shared by both paths above.
                //    #280 fixes delivery, not escalation policy: the `continue` default for an
                //    unknown decision stays exactly as it was (MUST NOT 22).
                var decisionStr = decision.TryGetProperty("Decision", out var d)
                    ? d.GetString()?.ToLowerInvariant() : "continue";
                var updatedInstruction = decision.TryGetProperty("UpdatedInstruction", out var ui)
                    ? ui.GetString() : null;

                switch (decisionStr)
                {
                    case "retry":
                        if (!string.IsNullOrWhiteSpace(updatedInstruction))
                            current = current with { Instruction = updatedInstruction };
                        continue; // retry the loop

                    case "skip":
                        _skipRemaining = true;
                        if (step.OutputVar != null) SetVar(step.OutputVar, null);
                        return null;

                    default: // "continue" or anything else
                        if (step.OutputVar != null) SetVar(step.OutputVar, null);
                        return null;
                }
            }
        }
    }

    private Task<string> ResolveInstructionAsync(DelegateStep step)
    {
        if (!string.IsNullOrEmpty(step.Instruction))
            return Task.FromResult(_template.ResolveString(step.Instruction));

        throw new InvalidOperationException(
            $"DelegateStep '{step.Name}' must have an Instruction");
    }

    // -------------------------------------------------------------------------
    // Signal waiting
    // -------------------------------------------------------------------------

    private async Task<object?> ExecuteWaitForSignalAsync(WaitForSignalStep step)
    {
        var signalName = _template.ResolveString(step.SignalName);

        // Gate visit (#436), minted FIRST: before the buffer fast path, so a buffered payload is
        // judged against the visit that consumes it, and before the guard renders, so
        // {{vars.<visitVar>}} sees it. A variable write, not a command. No visitVar → no counter
        // step, no variable and no command, exactly as before the field existed.
        string? visitId = null;
        if (step.VisitVar != null)
        {
            visitId = $"{signalName}:{++_gateVisitCount}";
            SetVar(step.VisitVar, visitId);
        }

        // Resolved ONCE, here at wait entry, so a later change to the variable it reads cannot
        // move the target mid-wait (#280 D-6).
        var bindTo = step.BindTo is null ? null : _template.ResolveString(step.BindTo);

        // Rendered ONCE, for the same reason (#436). Null for an unguarded wait, which then takes
        // none of the guarded branches below.
        var guard = step.DelegatedGuard is null ? null : RenderGuard(step.DelegatedGuard, signalName, visitId);

        // Buffer fast path, at the head: a wakeup that arrived while this workflow was mid-tick
        // is already here, so there is nothing to park on. Deliberately BEFORE the Phase upsert
        // and the notification — a park that never happens must not announce itself as parked or
        // ask a human to act on something already resolved (#280 D-2, F1/F2).
        //
        // A guarded wait judges the entry first (#436). The entry is removed either way: a stale
        // delegated approval left in the buffer would only be judged again by the next visit. A
        // discarded entry falls through to the normal park.
        if (TryTakeBufferedSignal(signalName, afterSeq: 0, out var bufferedPayload)
            && (guard is null || GuardAccepts(guard, bufferedPayload)))
        {
            Workflow.Logger.LogInformation(
                "Signal '{Signal}' was already buffered — resuming without parking", signalName);
            WriteBindMatch(step, bindTo, bufferedPayload, engineTimeout: false);
            if (step.OutputVar != null) SetVar(step.OutputVar, bufferedPayload);
            return bufferedPayload;
        }

        // The visit is announced only by a wait that actually parks, and before Phase, so an
        // observer that sees the gate phase also sees which visit it is (#436).
        if (visitId != null)
        {
            try { UpsertGateVisitAttribute(visitId); }
            catch { /* non-fatal */ }
        }

        if (step.Phase != null)
        {
            try { UpsertPhaseAttribute(_template.ResolveString(step.Phase)); }
            catch { /* non-fatal */ }
        }

        // Register signal waiter
        var tcs = new TaskCompletionSource<JsonElement>();
        _signalWaiters[signalName] = tcs;

        // Initial notification before waiting
        if (step.NotifyStep != null)
        {
            try { await ExecuteDelegateAsync(step.NotifyStep); }
            catch (Exception) when (step.NotifyStep.IgnoreFailure)
            {
                // Initial notification failure is non-fatal when ignoreFailure is set
            }
        }

        if (step.TimeoutMinutes == null)
        {
            // Wait indefinitely
            if (guard is null)
                await Workflow.WaitConditionAsync(() => tcs.Task.IsCompleted);
            else
                (_, tcs) = await WaitGuardedAsync(signalName, tcs, guard, waitSlice: null);
        }
        else
        {
            var totalTimeout = TimeSpan.FromMinutes(step.TimeoutMinutes.Value);
            var reminderInterval = step.ReminderIntervalMinutes.HasValue
                ? TimeSpan.FromMinutes(step.ReminderIntervalMinutes.Value)
                : totalTimeout; // no reminders if interval not configured

            var elapsed = TimeSpan.Zero;
            var remindersSent = 0;

            while (elapsed < totalTimeout)
            {
                var waitSlice = TimeSpan.FromTicks(
                    Math.Min(reminderInterval.Ticks, (totalTimeout - elapsed).Ticks));

                bool received;
                if (guard is null)
                {
                    received = await Workflow.WaitConditionAsync(
                        () => tcs.Task.IsCompleted, waitSlice);
                }
                else
                {
                    // A discard re-waits inside THIS slice, so elapsed and remindersSent below are
                    // untouched by it: no reminder fires early and the total never extends (#436).
                    (received, tcs) = await WaitGuardedAsync(signalName, tcs, guard, waitSlice);
                }

                if (received) break;

                elapsed += waitSlice;

                if (elapsed < totalTimeout
                    && remindersSent < step.MaxReminders
                    && step.NotifyStep != null)
                {
                    try { await ExecuteDelegateAsync(step.NotifyStep); }
                    catch { /* reminder failure is non-fatal */ }
                    remindersSent++;
                }
            }

            if (!tcs.Task.IsCompleted)
            {
                _signalWaiters.Remove(signalName);
                if (visitId != null) ClearGateVisitAttribute();
                if (step.AutoCompleteOnTimeout)
                {
                    var timeoutPayload = JsonSerializer.SerializeToElement(new { Decision = "timeout" });
                    // The engine generated this payload, so it carries no correlation field and
                    // cannot be stale or misaddressed. It is scored `match` WITHOUT comparison —
                    // comparing it would score the park's own timeout as a mismatch, degrade it to
                    // a resume, and re-park forever instead of terminating (#280 D-6 rule 1, F7).
                    WriteBindMatch(step, bindTo, timeoutPayload, engineTimeout: true);
                    if (step.OutputVar != null) SetVar(step.OutputVar, timeoutPayload);
                    return timeoutPayload;
                }
                throw new TimeoutException(
                    $"Signal '{signalName}' not received within {totalTimeout}");
            }
        }

        var payload = tcs.Task.Result;
        _signalWaiters.Remove(signalName);
        if (visitId != null) ClearGateVisitAttribute();

        WriteBindMatch(step, bindTo, payload, engineTimeout: false);
        if (step.OutputVar != null) SetVar(step.OutputVar, payload);
        return payload;
    }

    // -------------------------------------------------------------------------
    // Delegated-approval guard (#436)
    // -------------------------------------------------------------------------

    private ConsumptionGuard RenderGuard(DelegatedGuard guard, string signalName, string? visitId)
    {
        var expected = new List<KeyValuePair<string, string>>();
        foreach (var (field, template) in guard.Require ?? [])
            expected.Add(new(field, _template.ResolveString(template)));
        return new ConsumptionGuard(guard.Marker, expected, signalName, visitId);
    }

    /// <summary>
    /// Waits on a guarded wait's waiter until a payload the guard accepts completes it, or the
    /// slice runs out (#436). Returns the waiter that is current afterwards: a discard replaces it.
    ///
    /// <para>
    /// On a discard, three steps with no <c>await</c> between them, so no signal can slip past:
    /// (a) a fresh waiter replaces the completed one in <c>_signalWaiters</c>; (b) the buffer is
    /// drained — a signal that arrived after the stale one found the waiter already completed and
    /// went there — and each drained entry is judged in turn; (c) the wait resumes against the
    /// slice's ORIGINAL end, recorded when the slice started. A remainder of zero or less is the
    /// slice timing out. An indefinite wait (<paramref name="waitSlice"/> null) loops without
    /// timers.
    /// </para>
    ///
    /// <para>
    /// "Received" is read from the waiter, not from the wait's return value: a payload that lands in
    /// the same workflow task as the slice's timer completes the waiter even when the timer wins,
    /// and it must be judged here — returning it unjudged would let the caller accept it.
    /// </para>
    /// </summary>
    private async Task<(bool Received, TaskCompletionSource<JsonElement> Waiter)> WaitGuardedAsync(
        string signalName,
        TaskCompletionSource<JsonElement> waiter,
        ConsumptionGuard guard,
        TimeSpan? waitSlice)
    {
        DateTime? sliceEnd = waitSlice is { } slice ? Workflow.UtcNow + slice : null;
        var remaining = waitSlice;

        while (true)
        {
            var current = waiter;
            if (remaining is { } wait)
                await Workflow.WaitConditionAsync(() => current.Task.IsCompleted, wait);
            else
                await Workflow.WaitConditionAsync(() => current.Task.IsCompleted);

            if (!current.Task.IsCompleted) return (false, waiter);
            if (GuardAccepts(guard, current.Task.Result)) return (true, waiter);

            // (a) Re-register synchronously.
            waiter = new TaskCompletionSource<JsonElement>();
            _signalWaiters[signalName] = waiter;

            // (b) Drain. Every taken entry is removed, accepted or not.
            while (TryTakeBufferedSignal(signalName, afterSeq: 0, out var buffered))
            {
                if (!GuardAccepts(guard, buffered)) continue;
                waiter.TrySetResult(buffered);
                return (true, waiter);
            }

            // (c) Same slice, original end.
            if (sliceEnd is { } end)
            {
                var left = end - Workflow.UtcNow;
                if (left <= TimeSpan.Zero) return (false, waiter);
                remaining = left;
            }
        }
    }

    /// <summary>
    /// The guard's verdict on one payload (#436); logs the discard. A payload that is not an
    /// object, or carries no marker property (name compared ignoring case, any value), is the human
    /// path and is always accepted. A marked payload must match every rendered expectation.
    /// </summary>
    private static bool GuardAccepts(ConsumptionGuard guard, JsonElement payload)
    {
        if (!IsDelegated(guard.Marker, payload)) return true;

        // An empty requirement list proves nothing about the visit; validation refuses it, and a
        // marked payload is discarded here rather than waved through.
        if (guard.Expected.Count > 0
            && guard.Expected.All(e => RequirementMet(payload, e.Key, e.Value)))
            return true;

        Workflow.Logger.LogWarning(DelegatedDiscardLogTemplate, guard.SignalName, guard.VisitId);
        return false;
    }

    private static bool IsDelegated(string? marker, JsonElement payload) =>
        marker is not null
        && payload.ValueKind == JsonValueKind.Object
        && payload.EnumerateObject().Any(p => string.Equals(p.Name, marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One expectation: the property (exact name first, then ignoring case, as in
    /// <see cref="CompareBind"/>) is a JSON string ordinal-equal to <paramref name="expected"/>, and
    /// <paramref name="expected"/> is not blank — a blank rendering (no review published yet) can
    /// never be matched, whatever the payload says.
    /// </summary>
    private static bool RequirementMet(JsonElement payload, string field, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;

        if (!payload.TryGetProperty(field, out var value))
        {
            value = payload.EnumerateObject()
                .Where(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value)
                .FirstOrDefault();
        }

        return value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes <c>{outputVar}_bindMatch</c> — the correlation verdict for a resolved wait (#280 D-6).
    ///
    /// The payload is never modified. A relayed signal arrives as a JSON <em>string</em>
    /// (<c>TemporalRelayListener</c> signals with the raw message text), and a
    /// <see cref="JsonElement"/> is immutable in any case, so any design that stamps a field onto
    /// the payload breaks on that path (MUST NOT 3).
    ///
    /// Mismatch is the fail-safe direction: it still resumes the waiting workflow, it only
    /// withholds permission to TERMINATE on a decision that may belong to something else. So every
    /// unparseable or unexpected shape lands there rather than throwing (MUST NOT 5, 15).
    /// </summary>
    private void WriteBindMatch(WaitForSignalStep step, string? bindTo, JsonElement payload, bool engineTimeout)
    {
        // Rule 2: the field was omitted, so this definition opted out of correlation entirely and
        // its vars scope must stay exactly as it was before #280 (MUST NOT 4).
        if (step.BindTo is null) return;

        // Validation refuses this combination at definition load; belt-and-braces at runtime,
        // because there is nowhere to write the verdict.
        if (step.OutputVar is null) return;

        string verdict;
        if (engineTimeout)
        {
            verdict = "match";                                   // rule 1
        }
        else if (string.IsNullOrWhiteSpace(bindTo))
        {
            verdict = "mismatch";                                // rule 3 — an unbound park
        }
        else
        {
            var field = string.IsNullOrWhiteSpace(step.BindField) ? "blockerRef" : step.BindField!;
            verdict = CompareBind(bindTo!, field, payload);      // rule 4
        }

        SetVar($"{step.OutputVar}_bindMatch", verdict);
    }

    private string CompareBind(string expected, string field, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            Workflow.Logger.LogInformation(
                "Signal payload is {Kind}, not an object — cannot read '{Field}', scoring mismatch",
                payload.ValueKind, field);
            return "mismatch";
        }

        // Exact first, then case-insensitive, matching how the template engine navigates payloads:
        // a gate owner writes camelCase, while a hand-sent signal may well be PascalCase.
        if (!payload.TryGetProperty(field, out var value))
        {
            value = payload.EnumerateObject()
                .Where(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value)
                .FirstOrDefault();
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            Workflow.Logger.LogInformation(
                "Signal payload has no string '{Field}' — scoring mismatch", field);
            return "mismatch";
        }

        return string.Equals(value.GetString(), expected, StringComparison.Ordinal)
            ? "match"
            : "mismatch";
    }

    // -------------------------------------------------------------------------
    // Signalling another workflow
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a signal to another workflow by id (#280 D-4).
    ///
    /// An empty target is a SKIP, not a failure: a gated run started without a waiter id resolves
    /// the template to empty and must then behave exactly as it did before this step existed
    /// (MUST NOT 18, F15).
    /// </summary>
    private async Task<object?> ExecuteSignalWorkflowAsync(SignalWorkflowStep step)
    {
        var signalName = _template.ResolveString(step.SignalName);

        // Authorization, checked here and not only at definition load, because the signal name can
        // be a template: a definition passing load with "{{vars.gate}}" could still resolve to
        // merge-approval at runtime. Definitions are agent-authored, so without this the new step
        // would be a way to approve your own work by writing it into a workflow (#280).
        //
        // Deliberately BEFORE the empty-target skip: a definition that tries to send an
        // approver-only gate is wrong whether or not it currently has somewhere to send it, and
        // finding that out only once a waiter id happens to be present is the kind of latent
        // authorization hole that ships.
        if (CeoGateSignals.IsReserved(signalName))
        {
            throw new ApplicationFailureException(
                $"signal_workflow step '{step.Name ?? "(unnamed)"}' attempted to send '{signalName}', " +
                $"which is an approver-only gate. These signals resolve a human approval and may only " +
                $"be sent from the dashboard: {CeoGateSignals.Joined}. " +
                "This is refused regardless of ignoreFailure.",
                errorType: ReservedSignalErrorType,
                nonRetryable: true);
        }

        var workflowId = _template.ResolveString(step.WorkflowId);
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            Workflow.Logger.LogInformation(
                "signal_workflow: no target workflow id resolved — skipping signal '{Signal}'",
                step.SignalName);
            return null;
        }

        var payload = ResolveArgs(step.Payload);

        var handle = Workflow.GetExternalWorkflowHandle(workflowId);
        await handle.SignalAsync(signalName, payload is not null ? [payload] : []);

        Workflow.Logger.LogInformation(
            "signal_workflow: sent '{Signal}' to {WorkflowId}", signalName, workflowId);
        return null;
    }

    // -------------------------------------------------------------------------
    // Child workflows
    // -------------------------------------------------------------------------

    private async Task<object?> ExecuteChildWorkflowAsync(ChildWorkflowStep step)
    {
        var workflowType = _template.ResolveString(step.WorkflowType);
        var args = ResolveArgs(step.Args);
        var ns = step.Namespace ?? Workflow.Info.Namespace;
        var tq = step.TaskQueue ?? Workflow.Info.TaskQueue;

        var result = await Workflow.ExecuteChildWorkflowAsync<object?>(
            workflowType,
            args is not null ? [args] : [],
            new ChildWorkflowOptions
            {
                TaskQueue = tq,
            });

        if (step.OutputVar != null) SetVar(step.OutputVar, result);
        return result;
    }

    private async Task<object?> ExecuteFireAndForgetAsync(FireAndForgetStep step)
    {
        var workflowType = _template.ResolveString(step.WorkflowType);
        var args = ResolveArgs(step.Args);
        var ns = step.Namespace ?? Workflow.Info.Namespace;
        var tq = step.TaskQueue ?? Workflow.Info.TaskQueue;

        var handle = await Workflow.StartChildWorkflowAsync(
            workflowType,
            args is not null ? [args] : [],
            new ChildWorkflowOptions
            {
                TaskQueue = tq,
                ParentClosePolicy = ParentClosePolicy.Abandon
            });

        // Don't await the result — fire and forget
        if (step.OutputVar != null) SetVar(step.OutputVar, handle.Id);
        return handle.Id;
    }

    private async Task<object?> ExecuteCrossNamespaceStartAsync(CrossNamespaceStartStep step)
    {
        var workflowType = _template.ResolveString(step.WorkflowType);
        var workflowId = step.WorkflowId != null
            ? _template.ResolveString(step.WorkflowId)
            : null;
        var args = ResolveArgs(step.Args);

        // Delegate to the existing StartCrossNamespaceWorkflowActivity.
        // Arg order matches StartAsync(targetNamespace, workflowType, workflowId, taskQueue, input). (#685)
        var result = await Workflow.ExecuteActivityAsync<string>(
            "StartCrossNamespaceWorkflow",
            new object?[] { step.Namespace, workflowType, workflowId ?? Guid.NewGuid().ToString(), step.TaskQueue, args },
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        if (step.OutputVar != null) SetVar(step.OutputVar, result);
        return result;
    }

    // -------------------------------------------------------------------------
    // Variable assignment
    // -------------------------------------------------------------------------

    private object? ExecuteSetVariable(SetVariableStep step)
    {
        foreach (var (varName, expr) in step.Vars)
            SetVar(varName, _template.ResolveString(expr));
        return null;
    }

    // Attribute + MCP
    // -------------------------------------------------------------------------

    private Task<object?> ExecuteSetAttributeAsync(SetAttributeStep step)
    {
        var updates = new List<SearchAttributeUpdate>();

        foreach (var (name, valueExpr) in step.Attributes)
        {
            var resolved = _template.ResolveString(valueExpr);
            var attrType = SearchAttributeTypeRegistry.GetType(name);

            if (attrType == null)
            {
                Workflow.Logger.LogWarning("SetAttributeStep: unknown search attribute '{Name}' — skipped", name);
                continue;
            }

            try
            {
                updates.Add(attrType switch
                {
                    SearchAttributeTypeRegistry.AttributeType.Int
                        => SearchAttributeKey.CreateLong(name).ValueSet(long.Parse(resolved)),

                    SearchAttributeTypeRegistry.AttributeType.Keyword
                        => SearchAttributeKey.CreateKeyword(name).ValueSet(resolved),

                    SearchAttributeTypeRegistry.AttributeType.DateTime
                        => SearchAttributeKey.CreateDateTimeOffset(name)
                               .ValueSet(DateTimeOffset.Parse(resolved)),

                    _ => throw new InvalidOperationException($"Unhandled attribute type: {attrType}")
                });
            }
            catch (Exception ex)
            {
                Workflow.Logger.LogWarning(ex, "SetAttributeStep: failed to build update for '{Name}' — skipped", name);
            }
        }

        if (updates.Count > 0)
            Workflow.UpsertTypedSearchAttributes([.. updates]);

        return Task.FromResult<object?>(null);
    }

    private async Task<object?> ExecuteHttpRequestAsync(HttpRequestStep step)
    {
        var url = _template.ResolveString(step.Url);
        var method = step.Method;

        // Resolve headers
        Dictionary<string, string>? headers = null;
        if (step.Headers is { Count: > 0 })
        {
            headers = new Dictionary<string, string>(step.Headers.Count);
            foreach (var (k, v) in step.Headers)
                headers[k] = _template.ResolveString(v);
        }

        // Resolve body
        string? body = null;
        if (step.Body != null)
        {
            body = step.Body switch
            {
                string s => _template.ResolveString(s),
                JsonElement je when je.ValueKind == JsonValueKind.String
                    => _template.ResolveString(je.GetString()),
                _ => JsonSerializer.Serialize(step.Body)
            };
        }

        var input = new HttpRequestInput(
            url,
            method,
            headers,
            body,
            step.TimeoutSeconds,
            step.ExpectedStatusCodes ?? [200]);

        var result = await Workflow.ExecuteActivityAsync<string>(
            "HttpRequest",
            [input],
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(step.TimeoutSeconds + 5)
            });

        if (step.OutputVar != null) SetVar(step.OutputVar, result);
        return result;
    }

    private async Task<object?> ExecuteSleepAsync(SleepStep step)
    {
        const long MinSeconds = 1;
        const long MaxSeconds = 2_592_000; // 30 days

        if (step.Seconds is not { } seconds || seconds < MinSeconds || seconds > MaxSeconds)
        {
            // Non-retryable: the step definition is invalid and retrying will not fix it.
            // InvalidOperationException would cause indefinite Temporal task retries (non_retryable=false).
            throw new ApplicationFailureException(
                $"sleep step '{step.Name ?? "(unnamed)"}': 'seconds' must be an integer in " +
                $"[{MinSeconds}..{MaxSeconds}] (30 days), got {step.Seconds?.ToString() ?? "null"}. " +
                "Clamping is not applied — fix the step definition. " +
                "Hint: check for unit errors (e.g. milliseconds passed where seconds are expected).",
                nonRetryable: true);
        }

        await Workflow.DelayAsync(TimeSpan.FromSeconds(seconds));
        return null;
    }

    /// <summary>
    /// Fails the run on purpose (#424). Schedules nothing: no activity, timer or command. The
    /// <c>ExplicitFail</c> type is what makes every ancestor's <c>ignoreFailure</c> rethrow it, so
    /// the run closes Failed. Workflow.Logger is replay-safe, so the line is logged once.
    /// </summary>
    private Task<object?> ExecuteFailAsync(FailStep step)
    {
        var message = _template.ResolveString(step.Message);
        var stepName = step.Name ?? "(unnamed)";

        Workflow.Logger.LogWarning(
            "UWE explicit fail in {WorkflowId} at step {Step}: {Message}",
            Workflow.Info.WorkflowId, stepName, message);

        throw new ApplicationFailureException(message, errorType: ExplicitFailErrorType, nonRetryable: true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Stores a value in the vars scope (accessible as {{vars.key}}).
    /// Special case: "_result" is stored at the top-level _variables so RunAsync can return it.
    /// </summary>
    private void SetVar(string key, object? value)
    {
        if (key == "_result")
        {
            _variables["_result"] = value;
            return;
        }
        var vars = (Dictionary<string, object?>)_variables["vars"]!;
        vars[key] = value;
    }

    private void UpsertPhaseAttribute(string phase)
    {
        Workflow.UpsertTypedSearchAttributes(
            SearchAttributeKey.CreateKeyword("Phase").ValueSet(phase));
    }

    private static void UpsertGateVisitAttribute(string visitId) =>
        Workflow.UpsertTypedSearchAttributes(
            SearchAttributeKey.CreateKeyword(GateVisitAttribute).ValueSet(visitId));

    /// <summary>
    /// Clears <c>GateVisit</c> to <c>""</c> when a parked visit ends (#436) — accepted, timed out
    /// with auto-complete, or timed out and thrown — so a decision addressed to it is refused
    /// before it is sent. Non-fatal, like the Phase upsert.
    /// </summary>
    private static void ClearGateVisitAttribute()
    {
        try { UpsertGateVisitAttribute(""); }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Resolves a Dict&lt;string, object?&gt; of args through the template engine,
    /// returning null if args is null or empty.
    /// </summary>
    private Dictionary<string, object?>? ResolveArgs(Dictionary<string, object?>? args)
    {
        if (args is null or { Count: 0 }) return null;

        var resolved = new Dictionary<string, object?>(args.Count);
        foreach (var (k, v) in args)
        {
            // String values from JSON deserialization arrive as JsonElement, not C# string.
            // Both cases need template resolution. (#687)
            var resolvedValue = v switch
            {
                string s => _template.Resolve(s),
                JsonElement je when je.ValueKind == JsonValueKind.String
                    => _template.Resolve(je.GetString()),
                _ => v,
            };
            // Omit null and empty-string values — compiled C# workflows crash trying to
            // deserialize "" into non-string nullable types (Dictionary, arrays, etc.). (#830)
            if (resolvedValue is null || (resolvedValue is string rs && rs.Length == 0))
                continue;
            resolved[k] = resolvedValue;
        }
        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>
    /// Converts a resolved object (JsonElement array, List, comma-separated string)
    /// into a flat list of strings for ForEach iteration.
    /// </summary>
    private static List<string> FlattenToStringList(object? items)
    {
        var list = new List<string>();
        switch (items)
        {
            case JsonElement el when el.ValueKind == JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    list.Add(item.ToString());
                break;

            case System.Collections.IEnumerable enumerable when items is not string:
                foreach (var item in enumerable)
                    if (item is not null) list.Add(item.ToString()!);
                break;

            case string s:
                list.AddRange(s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
                break;

            case null:
                break;

            default:
                list.Add(items.ToString()!);
                break;
        }
        return list;
    }
}
