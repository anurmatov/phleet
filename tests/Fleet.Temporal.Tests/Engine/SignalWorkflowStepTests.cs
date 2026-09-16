using System.Text.Json;
using Fleet.Temporal;
using Fleet.Temporal.Engine;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// The JSON contract and load-time validation for #280's two definition-visible additions:
/// the <c>signal_workflow</c> step and <c>wait_for_signal</c>'s <c>bindTo</c>/<c>bindField</c>.
///
/// Definitions are authored by hand and by agents, so the deserialization contract is the real
/// interface here — a field that silently fails to bind produces a park that looks configured and
/// correlates nothing.
/// </summary>
public sealed class SignalWorkflowStepTests
{
    // Production (LoadWorkflowDefinitionActivity) uses JsonSerializerDefaults.Web. Every JSON test
    // must use the same options or it tests a contract nothing ships.
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    // ── signal_workflow ──────────────────────────────────────────────────────

    [Fact]
    public void SignalWorkflowStep_DeserializesFromTypeDiscriminator()
    {
        var json = """
            {"type":"signal_workflow","workflowId":"{{input.WaiterWorkflowId}}",
             "signalName":"blocker-resolved",
             "payload":{"decision":"approved","blockerRef":"{{workflow.id}}"}}
            """;

        var step = Assert.IsType<SignalWorkflowStep>(
            JsonSerializer.Deserialize<StepDefinition>(json, WebOptions));

        Assert.Equal("{{input.WaiterWorkflowId}}", step.WorkflowId);
        Assert.Equal("blocker-resolved", step.SignalName);
        Assert.Equal("approved", step.Payload!["decision"]!.ToString());
    }

    /// <summary>
    /// The one step type whose <c>ignoreFailure</c> defaults to TRUE.
    ///
    /// A wakeup is a courtesy to a peer that may legitimately have closed. If the default were
    /// false, a gate owner resolving normally would FAIL because the workflow it was being polite
    /// to had already finished — turning a best-effort notification into a new way to break the
    /// gate (#280 MUST NOT 18). The mutation for this is flipping the default; the closed-waiter
    /// test in the run suite is what goes red.
    /// </summary>
    [Fact]
    public void SignalWorkflowStep_IgnoreFailure_DefaultsToTrue()
    {
        var json = """{"type":"signal_workflow","workflowId":"w","signalName":"s"}""";

        var step = Assert.IsType<SignalWorkflowStep>(
            JsonSerializer.Deserialize<StepDefinition>(json, WebOptions));

        Assert.True(step.IgnoreFailure);
    }

    [Fact]
    public void SignalWorkflowStep_IgnoreFailure_CanBeTurnedOffExplicitly()
    {
        var json = """{"type":"signal_workflow","workflowId":"w","signalName":"s","ignoreFailure":false}""";

        var step = Assert.IsType<SignalWorkflowStep>(
            JsonSerializer.Deserialize<StepDefinition>(json, WebOptions));

        Assert.False(step.IgnoreFailure);
    }

    /// <summary>Every other step type keeps the opt-in default — the override is not global.</summary>
    [Fact]
    public void OtherSteps_KeepIgnoreFailureFalseByDefault()
    {
        var json = """{"type":"noop"}""";

        var step = JsonSerializer.Deserialize<StepDefinition>(json, WebOptions);

        Assert.False(step!.IgnoreFailure);
    }

    // ── bindTo / bindField ───────────────────────────────────────────────────

    [Fact]
    public void WaitForSignal_BindFields_Deserialize()
    {
        var json = """
            {"type":"wait_for_signal","signalName":"blocker-resolved","outputVar":"resume",
             "bindTo":"{{vars.blocker_ref}}","bindField":"blockerRef"}
            """;

        var step = Assert.IsType<WaitForSignalStep>(
            JsonSerializer.Deserialize<StepDefinition>(json, WebOptions));

        Assert.Equal("{{vars.blocker_ref}}", step.BindTo);
        Assert.Equal("blockerRef", step.BindField);
    }

    /// <summary>
    /// The three-state distinction the whole correlation design rests on: a definition that omits
    /// <c>bindTo</c> must be indistinguishable from a pre-#280 one, which means <c>null</c> — not
    /// an empty string, which would mean "bound to an unknown target" and score every wakeup as a
    /// mismatch (#280 D-6).
    /// </summary>
    [Fact]
    public void WaitForSignal_OmittedBindTo_IsNullNotEmpty()
    {
        var json = """{"type":"wait_for_signal","signalName":"x","outputVar":"out"}""";

        var step = Assert.IsType<WaitForSignalStep>(
            JsonSerializer.Deserialize<StepDefinition>(json, WebOptions));

        Assert.Null(step.BindTo);
        Assert.Null(step.BindField);
    }

    // ── T16: load-time validation ────────────────────────────────────────────

    /// <summary>
    /// T16. <c>bindTo</c> with no <c>outputVar</c> has nowhere to write its verdict, so it fails at
    /// definition LOAD — once, naming the step — rather than at execution, where the surrounding
    /// step's <c>ignoreFailure</c> could swallow it and leave the park silently uncorrelated.
    /// </summary>
    [Fact]
    public void Validate_BindToWithoutOutputVar_FailsNamingTheStep()
    {
        var root = new SequenceStep
        {
            Steps =
            [
                new NoopStep(),
                new WaitForSignalStep
                {
                    Name = "park_on_gate",
                    SignalName = "blocker-resolved",
                    BindTo = "{{vars.blocker_ref}}",
                    // no OutputVar
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => WorkflowDefinitionValidator.Validate(root, "ExampleWorkflow"));

        Assert.Contains("park_on_gate", ex.Message);
        Assert.Contains("outputVar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExampleWorkflow", ex.Message);
    }

    [Fact]
    public void Validate_BindToWithOutputVar_Passes()
    {
        var root = new WaitForSignalStep
        {
            SignalName = "blocker-resolved",
            BindTo = "{{vars.blocker_ref}}",
            OutputVar = "resume",
        };

        WorkflowDefinitionValidator.Validate(root, "ExampleWorkflow");
    }

    [Fact]
    public void Validate_NoBindTo_PassesWithoutOutputVar()
    {
        // Every pre-#280 definition looks like this. Validation must not newly reject them.
        var root = new WaitForSignalStep { SignalName = "human-review" };

        WorkflowDefinitionValidator.Validate(root, "ExampleWorkflow");
    }

    // ── approver-only gates ──────────────────────────────────────────────────

    /// <summary>
    /// A definition may not send an approver-only gate signal, and it is refused at LOAD when the
    /// name is written literally.
    ///
    /// The MCP signal tool already refuses these from agent callers. <c>signal_workflow</c> is a
    /// second way to reach the same signals, and definitions are agent-authored — so without this
    /// the new step would be a way to approve your own work by writing it into a workflow.
    /// </summary>
    [Theory]
    [InlineData("merge-approval")]
    [InlineData("doc-review")]
    [InlineData("design-approval")]
    [InlineData("advisory-review")]
    [InlineData("MERGE-APPROVAL")]   // case is not a bypass
    [InlineData("  doc-review  ")]   // nor is whitespace
    public void Validate_SignalWorkflowSendingAnApproverGate_FailsToLoad(string signalName)
    {
        var root = new SequenceStep
        {
            Steps =
            [
                new SignalWorkflowStep
                {
                    Name = "sneaky_approval",
                    WorkflowId = "{{input.WaiterWorkflowId}}",
                    SignalName = signalName,
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => WorkflowDefinitionValidator.Validate(root, "ExampleWorkflow"));

        Assert.Contains("sneaky_approval", ex.Message);
        Assert.Contains("approver-only", ex.Message);
    }

    /// <summary>
    /// The signals agents ARE expected to send stay sendable. A guard that also blocked these would
    /// break escalation and human-review routing, which are operational decisions rather than
    /// approvals.
    /// </summary>
    [Theory]
    [InlineData("human-review")]
    [InlineData("escalation-decision")]
    [InlineData("blocker-resolved")]
    public void Validate_SignalWorkflowSendingANonGateSignal_Passes(string signalName)
    {
        var root = new SignalWorkflowStep
        {
            Name = "wake_parked_waiter",
            WorkflowId = "{{input.WaiterWorkflowId}}",
            SignalName = signalName,
        };

        WorkflowDefinitionValidator.Validate(root, "ExampleWorkflow");
    }

    /// <summary>
    /// The reserved list has exactly one owner. Two copies of a security list is a list that
    /// drifts, and the drift is silent until someone approves their own work through the half that
    /// was not updated — so the engine and the MCP tool read the same names.
    /// </summary>
    [Fact]
    public void TheReservedList_IsTheFourApproverGates()
    {
        Assert.Equal(
            ["advisory-review", "design-approval", "doc-review", "merge-approval"],
            CeoGateSignals.All.OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.False(CeoGateSignals.IsReserved("human-review"));
        Assert.False(CeoGateSignals.IsReserved("escalation-decision"));
        Assert.False(CeoGateSignals.IsReserved(null));
    }

    /// <summary>
    /// The walker has to reach nested steps or validation is decorative. This pins each composite
    /// shape: a step type that holds children and is missing from the walk hides them silently.
    /// </summary>
    [Theory]
    [InlineData("sequence")]
    [InlineData("parallel_steps")]
    [InlineData("parallel_foreach")]
    [InlineData("loop")]
    [InlineData("branch_case")]
    [InlineData("branch_default")]
    public void Validate_ReachesNestedSteps(string shape)
    {
        var offender = new WaitForSignalStep
        {
            Name = "nested_park",
            SignalName = "blocker-resolved",
            BindTo = "{{vars.blocker_ref}}",
        };

        StepDefinition root = shape switch
        {
            "sequence"          => new SequenceStep { Steps = [offender] },
            "parallel_steps"    => new ParallelStep { Steps = [offender] },
            "parallel_foreach"  => new ParallelStep { ForEach = "{{vars.items}}", Step = offender },
            "loop"              => new LoopStep { Steps = [offender] },
            "branch_case"       => new BranchStep { On = "{{vars.x}}", Cases = new() { ["a"] = offender } },
            _                   => new BranchStep { On = "{{vars.x}}", Cases = new(), Default = offender },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => WorkflowDefinitionValidator.Validate(root, "ExampleWorkflow"));

        Assert.Contains("nested_park", ex.Message);
    }
}
