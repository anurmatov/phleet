namespace Fleet.Temporal.Engine;

using System.Text.Json.Serialization;

/// <summary>
/// Base step definition. All step types derive from this.
/// JSON discriminator: "type" field maps to step type string.
/// Engine dispatches on the concrete C# type via pattern matching.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SequenceStep), "sequence")]
[JsonDerivedType(typeof(ParallelStep), "parallel")]
[JsonDerivedType(typeof(DelegateStep), "delegate")]
[JsonDerivedType(typeof(DelegateWithEscalationStep), "delegate_with_escalation")]
[JsonDerivedType(typeof(WaitForSignalStep), "wait_for_signal")]
[JsonDerivedType(typeof(ChildWorkflowStep), "child_workflow")]
[JsonDerivedType(typeof(FireAndForgetStep), "fire_and_forget")]
[JsonDerivedType(typeof(LoopStep), "loop")]
[JsonDerivedType(typeof(BranchStep), "branch")]
[JsonDerivedType(typeof(BreakStep), "break")]
[JsonDerivedType(typeof(ContinueStep), "continue")]
[JsonDerivedType(typeof(NoopStep), "noop")]
[JsonDerivedType(typeof(SetVariableStep), "set_variable")]
[JsonDerivedType(typeof(SetAttributeStep), "set_attribute")]
[JsonDerivedType(typeof(HttpRequestStep), "http_request")]
[JsonDerivedType(typeof(CrossNamespaceStartStep), "cross_namespace_start")]
public abstract record StepDefinition
{
    /// <summary>Human-readable step name; also used as key when storing step output in vars.</summary>
    public string? Name { get; init; }

    /// <summary>Variable name to store step output in vars scope. Always a literal string, never a template.</summary>
    public string? OutputVar { get; init; }

    /// <summary>If true, exceptions from this step are swallowed and execution continues.</summary>
    public bool IgnoreFailure { get; init; }
}

// --- Control flow (used by loop/branch) ---

/// <summary>Sentinel values returned by branch/loop steps to control iteration flow.</summary>
public enum ControlFlow { Break, Continue }

// --- Composition steps ---

public sealed record SequenceStep : StepDefinition
{
    public required StepDefinition[] Steps { get; init; }
}

public sealed record ParallelStep : StepDefinition
{
    /// <summary>Static parallel branches (mutually exclusive with ForEach).</summary>
    public StepDefinition[]? Steps { get; init; }

    /// <summary>Dynamic parallel: expression resolving to an array of items.</summary>
    public string? ForEach { get; init; }

    /// <summary>Variable name for the current item in a ForEach iteration.</summary>
    public string? ItemVar { get; init; }

    /// <summary>Step template executed per ForEach item.</summary>
    public StepDefinition? Step { get; init; }
}

public sealed record LoopStep : StepDefinition
{
    public required StepDefinition[] Steps { get; init; }
    public int MaxIterations { get; init; } = 5;
}

public sealed record BranchStep : StepDefinition
{
    /// <summary>Expression to evaluate (e.g. "{{vars.review_result | extract: 'VERDICT'}}").</summary>
    public required string On { get; init; }

    /// <summary>Case value → step definition. Use {"type":"break"} or {"type":"continue"} for loop control.</summary>
    public required Dictionary<string, StepDefinition> Cases { get; init; }

    /// <summary>Executed when no case matches.</summary>
    public StepDefinition? Default { get; init; }
}

// --- Loop control + utility steps ---

/// <summary>Exits the enclosing loop immediately.</summary>
public sealed record BreakStep : StepDefinition { }

/// <summary>Skips remaining steps in the current loop iteration.</summary>
public sealed record ContinueStep : StepDefinition { }

/// <summary>Explicit no-op — does nothing and returns null. Clearer than {"type":"sequence","steps":[]}.</summary>
public sealed record NoopStep : StepDefinition { }

// --- Agent delegation steps ---

public record DelegateStep : StepDefinition
{
    /// <summary>Target agent name (supports {{template}}).</summary>
    public required string Target { get; init; }

    /// <summary>Inline instruction text (supports {{template}}).</summary>
    public string? Instruction { get; init; }

    public int TimeoutMinutes { get; init; } = 30;
    public bool RetryOnIncomplete { get; init; } = true;
    public int MaxIncompleteRetries { get; init; } = 3;
}

public sealed record DelegateWithEscalationStep : DelegateStep
{
    // Inherits all DelegateStep properties.
    // Engine wraps execution with the full escalation pattern:
    //   delegate → on failure: notify EscalationTarget → wait for "escalation-decision" signal
    //   → branch: "retry" loops, "skip" sets _skipRemaining, "continue" returns null.
}

// --- Signal steps ---

public sealed record WaitForSignalStep : StepDefinition
{
    public required string SignalName { get; init; }

    /// <summary>Total wait timeout in minutes. Null = wait indefinitely.</summary>
    public int? TimeoutMinutes { get; init; }

    /// <summary>How often to send reminder notifications (minutes). Null = no reminders.</summary>
    public int? ReminderIntervalMinutes { get; init; }

    public int MaxReminders { get; init; } = 3;

    /// <summary>Phase value set as a search attribute while waiting.</summary>
    public string? Phase { get; init; }

    /// <summary>If true, returns {Decision:"timeout"} instead of throwing on timeout.</summary>
    public bool AutoCompleteOnTimeout { get; init; }

    /// <summary>Notification step executed before waiting and on each reminder tick.</summary>
    public DelegateStep? NotifyStep { get; init; }
}

// --- Child workflow steps ---

public record ChildWorkflowStep : StepDefinition
{
    /// <summary>Workflow type name (supports {{template}}).</summary>
    public required string WorkflowType { get; init; }

    /// <summary>Arguments passed to the child workflow as a JSON object.</summary>
    public Dictionary<string, object?>? Args { get; init; }

    public string? Namespace { get; init; }
    public string? TaskQueue { get; init; }
}

public sealed record FireAndForgetStep : ChildWorkflowStep
{
    // Same as ChildWorkflowStep but uses ParentClosePolicy.Abandon.
}

public sealed record CrossNamespaceStartStep : StepDefinition
{
    public required string WorkflowType { get; init; }
    public required string Namespace { get; init; }
    public required string TaskQueue { get; init; }

    /// <summary>Explicit workflow ID. Auto-generated if null.</summary>
    public string? WorkflowId { get; init; }

    public Dictionary<string, object?>? Args { get; init; }
}

// --- Variable + Attribute + HTTP steps ---

/// <summary>
/// Evaluates template expressions and stores results in workflow variables.
/// Deterministic and instant — no delegation, no side effects.
/// </summary>
public sealed record SetVariableStep : StepDefinition
{
    /// <summary>
    /// Map of variable name → template expression.
    /// Each expression is resolved via the template engine and stored in _variables.
    /// Example: {"pr_url": "{{vars.impl_result | extract: 'PR_URL: (https://[^\\s]+)'}}"}
    /// </summary>
    public required Dictionary<string, string> Vars { get; init; }
}

public sealed record SetAttributeStep : StepDefinition
{
    /// <summary>
    /// Key-value pairs to upsert. Values support {{template}}.
    /// Type resolved from SearchAttributeTypeRegistry at runtime; unknown attributes are skipped.
    /// </summary>
    public required Dictionary<string, string> Attributes { get; init; }
}

public sealed record HttpRequestStep : StepDefinition
{
    /// <summary>Target URL. Supports {{template}} interpolation.</summary>
    public required string Url { get; init; }

    /// <summary>HTTP method (default "GET").</summary>
    public string Method { get; init; } = "GET";

    /// <summary>Request headers. Values support {{template}} interpolation.</summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>Request body. String values support {{template}} interpolation; objects are serialized as JSON.</summary>
    public object? Body { get; init; }

    /// <summary>Request timeout in seconds (default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Acceptable HTTP status codes (default [200]). Step fails if response code is not in this list.</summary>
    public int[]? ExpectedStatusCodes { get; init; }
}
