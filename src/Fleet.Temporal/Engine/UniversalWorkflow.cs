namespace Fleet.Temporal.Engine;

using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Converters;
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
///   {{input.*}}   — workflow start arguments (JsonElement)
///   {{vars.*}}    — step outputs keyed by output_var
///   {{config.*}}  — FleetWorkflowOptions serialized as JsonElement
/// </summary>
[Workflow(Dynamic = true)]
public class UniversalWorkflow
{
    private readonly Dictionary<string, object?> _variables = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _signalWaiters = new();
    private TemplateEngine _template = null!;
    private bool _skipRemaining;

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

        // 5. Execute the step tree
        await ExecuteStepAsync(definition.Root);

        return _variables.GetValueOrDefault("_result");
    }

    /// <summary>Dynamic signal handler — routes all signals to waiting TCS instances.</summary>
    [WorkflowSignal(Dynamic = true)]
    public Task HandleSignalAsync(string signalName, IRawValue[] args)
    {
        var payload = args.Length > 0
            ? Workflow.PayloadConverter.ToValue<JsonElement>(args[0])
            : default;

        if (_signalWaiters.TryGetValue(signalName, out var tcs))
            tcs.TrySetResult(payload);

        return Task.CompletedTask;
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
                _                                                      => throw new InvalidOperationException(
                                                                              $"Unknown step type: {step.GetType().Name}")
            };
        }
        catch (Exception) when (step.IgnoreFailure)
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
            await Workflow.WhenAllAsync(step.Steps.Select(s => ExecuteStepAsync(s)));
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

            await Workflow.WhenAllAsync(tasks.Select(t => t()));

            // Collect results into vars.{step.Name}
            if (step.Name != null)
                SetVar(step.Name, results);

            return results;
        }

        return null;
    }

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
        var target = _template.ResolveString(step.Target);
        var instruction = await ResolveInstructionAsync(step);
        var taskId = $"{Workflow.Info.WorkflowId}/{step.Name ?? "delegate"}";
        var timeout = TimeSpan.FromMinutes(step.TimeoutMinutes);

        // DelegateToAgentActivity.[Activity] — default name strips "Async" suffix → "DelegateToAgent"
        var result = await Workflow.ExecuteActivityAsync<AgentTaskResult>(
            "DelegateToAgent",
            new object?[] { target, instruction, taskId, step.RetryOnIncomplete, step.MaxIncompleteRetries },
            new ActivityOptions
            {
                StartToCloseTimeout = timeout + TimeSpan.FromMinutes(2),
                HeartbeatTimeout = TimeSpan.FromMinutes(2),
                RetryPolicy = new() { MaximumAttempts = 1 }
            });

        if (step.OutputVar != null)
            SetVar(step.OutputVar, result.Text);

        return result.Text;
    }

    private async Task<object?> ExecuteDelegateWithEscalationAsync(DelegateWithEscalationStep step)
    {
        var escalationTarget = _template.ResolveString("{{config.EscalationTarget}}");
        // Keep a mutable local for retry-with-updated-instruction
        DelegateWithEscalationStep current = step;

        while (true)
        {
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

                // 2. Register signal waiter BEFORE sending notification so we don't miss
                //    a signal that arrives while the notification activity is in-flight.
                var tcs = new TaskCompletionSource<JsonElement>();
                _signalWaiters["escalation-decision"] = tcs;

                // 3. Best-effort notification to escalation target
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

                // 4. Wait indefinitely for escalation-decision signal
                await Workflow.WaitConditionAsync(() => tcs.Task.IsCompleted);
                var decision = tcs.Task.Result;
                _signalWaiters.Remove("escalation-decision");

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
            await Workflow.WaitConditionAsync(() => tcs.Task.IsCompleted);
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

                var received = await Workflow.WaitConditionAsync(
                    () => tcs.Task.IsCompleted, waitSlice);

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
                if (step.AutoCompleteOnTimeout)
                {
                    var timeoutPayload = JsonSerializer.SerializeToElement(new { Decision = "timeout" });
                    if (step.OutputVar != null) SetVar(step.OutputVar, timeoutPayload);
                    return timeoutPayload;
                }
                throw new TimeoutException(
                    $"Signal '{signalName}' not received within {totalTimeout}");
            }
        }

        var payload = tcs.Task.Result;
        _signalWaiters.Remove(signalName);

        if (step.OutputVar != null) SetVar(step.OutputVar, payload);
        return payload;
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
