using System.Collections.Concurrent;
using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// A generic driver definition, shaped like #280 §5: tick, extract the status and blocker, park on
/// a human gate when blocked, and act on the wakeup through the correlation verdict.
///
/// <para>
/// Public-safe by construction — a nameless "example-agent", a <c>STATUS:</c> line and a
/// <c>BLOCKER:</c> line. The deployment's own driver definition is private and is not reproduced;
/// what is reproduced is its SHAPE, because that is what the engine has to support.
/// </para>
///
/// <para>
/// Two details are load-bearing and worth not "simplifying" if this fixture is ever edited:
/// <list type="number">
/// <item><c>bindTo</c> is present in BOTH the bound and unbound case. That is what makes a park
/// with no blocker id emit <c>mismatch</c> on every wakeup — resume-only, never terminal — rather
/// than falling into the omitted-field path where no verdict is written at all.</item>
/// <item>the parked phase is computed by a branch, not by a filter. With only
/// <c>default:</c>/<c>extract:</c>/<c>json</c>, "non-empty → A else B" cannot be expressed in one
/// <c>set_variable</c>.</item>
/// </list>
/// </para>
/// </summary>
internal static class DriverFixture
{
    public const string SignalName = "blocker-resolved";

    public static WorkflowDefinitionModel Definition() => new()
    {
        Name = "example-driver",
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep
        {
            Steps =
            [
                new LoopStep
                {
                    Name = "driver_loop",
                    MaxIterations = 8,
                    Steps =
                    [
                        new DelegateStep
                        {
                            Name = "tick",
                            Target = "example-agent",
                            Instruction = "Do the next unit of work and report STATUS:.",
                            OutputVar = "tick_result",
                        },
                        new SetVariableStep
                        {
                            Name = "extract_tick_fields",
                            Vars = new()
                            {
                                ["tick_status"]  = "{{vars.tick_result | extract: 'STATUS:\\s*(\\S+)' | default: 'working'}}",
                                ["tick_blocker"] = "{{vars.tick_result | extract: 'BLOCKER:\\s*(\\S+)' | default: ''}}",
                            },
                        },
                        new BranchStep
                        {
                            Name = "on_status",
                            On = "{{vars.tick_status}}",
                            Cases = new()
                            {
                                ["done"] = new SequenceStep
                                {
                                    Steps =
                                    [
                                        new SetVariableStep { Vars = new() { ["_result"] = "DONE" } },
                                        new BreakStep(),
                                    ],
                                },
                                ["blocked"] = BlockedBranch(),
                            },
                            Default = new ContinueStep(),
                        },
                    ],
                },
            ],
        },
    };

    private static StepDefinition BlockedBranch() => new SequenceStep
    {
        Steps =
        [
            new SetVariableStep
            {
                Name = "capture_blocker_ref",
                Vars = new() { ["blocker_ref"] = "{{vars.tick_blocker | default: ''}}" },
            },

            // A bound park and an unbound one differ only in the phase they advertise, so the
            // degraded state is visible in dashboard filtering instead of looking healthy.
            new BranchStep
            {
                Name = "choose_parked_phase",
                On = "{{vars.blocker_ref | default: 'none'}}",
                Cases = new()
                {
                    ["none"] = new SetVariableStep
                    {
                        Vars = new() { ["parked_phase"] = "parked_unbound_blocker" },
                    },
                },
                Default = new SetVariableStep
                {
                    Vars = new() { ["parked_phase"] = "parked_on_human_gate" },
                },
            },

            new WaitForSignalStep
            {
                Name = "park_on_gate",
                SignalName = SignalName,
                BindTo = "{{vars.blocker_ref}}",
                BindField = "blockerRef",
                TimeoutMinutes = 10_080,          // 7 days: a safety net, not a deadline
                ReminderIntervalMinutes = null,   // one notification, one durable timer
                MaxReminders = 0,
                AutoCompleteOnTimeout = true,
                Phase = "{{vars.parked_phase}}",
                OutputVar = "resume",
                NotifyStep = new DelegateStep
                {
                    Name = "park_notify",
                    Target = "example-agent",
                    Instruction = "Parked on a human gate for blocker {{vars.blocker_ref}}.",
                    IgnoreFailure = true,
                },
            },

            // A wakeup that does not correlate resumes and reconciles; it never terminates.
            new BranchStep
            {
                Name = "apply_correlation",
                On = "{{vars.resume_bindMatch}}",
                Cases = new()
                {
                    ["mismatch"] = new SetVariableStep
                    {
                        Vars = new() { ["effective"] = "approved" },
                    },
                },
                Default = new SetVariableStep
                {
                    Vars = new() { ["effective"] = "{{vars.resume.decision | default: 'timeout'}}" },
                },
            },

            new BranchStep
            {
                Name = "on_decision",
                On = "{{vars.effective}}",
                Cases = new()
                {
                    ["approved"] = new ContinueStep(),
                    ["rejected"] = Terminal("REJECTED"),
                    ["cancelled"] = Terminal("CANCELLED"),
                },
                // Unparseable or unknown fails CLOSED to the timeout terminal, never to approval.
                Default = Terminal("TIMEOUT"),
            },
        ],
    };

    private static StepDefinition Terminal(string outcome) => new SequenceStep
    {
        Steps =
        [
            new DelegateStep
            {
                Name = "terminal_notify",
                Target = "example-agent",
                Instruction = $"Driver terminated: {outcome}.",
                IgnoreFailure = true,
            },
            new SetVariableStep { Vars = new() { ["_result"] = outcome } },
            new BreakStep(),
        ],
    };
}

/// <summary>
/// Activity stubs for the driver fixture. <see cref="Ticks"/> is the script: each tick dequeues one
/// response, so a test states the conversation it wants rather than encoding it in flags.
/// </summary>
internal sealed class DriverActivities(WorkflowDefinitionModel definition)
{
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>Responses the tick agent returns, in order. Empty means "STATUS: working".</summary>
    public ConcurrentQueue<string> Ticks { get; } = new();

    public int CountOf(string stepName) => _calls.Count(c => c == stepName);

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() =>
        JsonSerializer.SerializeToElement(new { EscalationTarget = "example-escalation-target" });

    [Activity("DelegateToAgent")]
    public AgentTaskResult Delegate(
        string target, string instruction, string taskId, bool retryOnIncomplete, int maxRetries)
    {
        // DelegateToAgentActivity builds taskId as "{workflowId}/{stepName}".
        var stepName = taskId.Split('/').LastOrDefault() ?? taskId;
        _calls.Enqueue(stepName);

        if (stepName != "tick")
            return new AgentTaskResult("acknowledged", "completed");

        return Ticks.TryDequeue(out var scripted)
            ? new AgentTaskResult(scripted, "completed")
            : new AgentTaskResult("STATUS: working", "completed");
    }
}
