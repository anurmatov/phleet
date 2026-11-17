using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// One UWE run through every place a <c>delegatedGuard</c> WITH a visit judges a payload: the
/// buffer fast path (a stale delegated payload discarded at entry, a current one accepted at
/// entry), the live wait (a stale one discarded, then the drain accepting the payload behind it,
/// delegated in a timed wait and human in an indefinite one), and a guarded wait nobody signals
/// (one reminder, then the auto-complete timeout).
///
/// <para>
/// It exists for the refusing guard (<c>delegatedGuard.refuse</c>), which is added to the same
/// code. A guard without that field must emit exactly the commands it emitted before. The
/// committed fixture <c>Fixtures/uwe-guarded-gate-history.json</c> is this scenario's history as
/// recorded by the engine BEFORE that change, on a time-skipping test server. Only
/// replay-irrelevant text was edited afterwards: the worker identity (a placeholder host).
/// </para>
///
/// <para>
/// Everything here must keep compiling against that pre-change engine, because that is how the
/// fixture was produced, so it never mentions the new field. Ids are fixed so the task ids the
/// notifications embed are stable. Every signal is sent while an activity is held open, and no
/// payload is discarded while a timer is the only thing pending, so no timer's length depends on
/// when a signal happened to arrive.
/// </para>
/// </summary>
internal static class GuardedGateHistoryScenario
{
    public const string WorkflowType = "example-workflow";
    public const string WorkflowId = "example-guarded-gate-history";
    public const string TaskQueue = "example-guarded-gate-history";
    public const string FixturePath = "Fixtures/uwe-guarded-gate-history.json";

    /// <summary>The steps the stubs hold open until the scenario has sent its signals.</summary>
    public static readonly string[] Held = ["tick_a", "notify_a", "notify_b", "tick_c"];

    public static WorkflowDefinitionModel Definition() => new()
    {
        Name = WorkflowType,
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep
        {
            Steps =
            [
                new SetVariableStep { Vars = new() { ["review_ref"] = "ref-1" } },

                // Held open: a delegated payload for another visit is sent now and buffered.
                new DelegateStep { Name = "tick_a", Target = "agent1", Instruction = "work", OutputVar = "tick_a" },

                // The buffered one is discarded at entry and the wait parks. While its notice is
                // held open, a second stale one completes the waiter and the current one is
                // buffered behind it: a live discard, then the drain accepts.
                Guarded("gate_a", "gate-a", "visit_a", "a") with
                {
                    TimeoutMinutes = 60,
                    ReminderIntervalMinutes = 30,
                    MaxReminders = 2,
                    AutoCompleteOnTimeout = true,
                    NotifyStep = new DelegateStep { Name = "notify_a", Target = "agent1", Instruction = "decide a" },
                },

                // Indefinite: a stale delegated payload, then a human decision behind it.
                Guarded("gate_b", "gate-b", "visit_b", "b") with
                {
                    NotifyStep = new DelegateStep { Name = "notify_b", Target = "agent1", Instruction = "decide b" },
                },

                // Held open: the current delegated payload for the NEXT guarded wait is buffered.
                new DelegateStep { Name = "tick_c", Target = "agent1", Instruction = "work", OutputVar = "tick_c" },

                // Buffer fast path, accepted: never parks, never publishes its visit.
                Guarded("gate_c", "gate-c", "visit_c", "c") with
                {
                    TimeoutMinutes = 60,
                    MaxReminders = 0,
                    AutoCompleteOnTimeout = true,
                },

                // Never signalled: one reminder at 30 minutes, then the auto-complete timeout at 60.
                Guarded("gate_d", "gate-d", "visit_d", "d") with
                {
                    TimeoutMinutes = 60,
                    ReminderIntervalMinutes = 30,
                    MaxReminders = 3,
                    AutoCompleteOnTimeout = true,
                    NotifyStep = new DelegateStep { Name = "notify_d", Target = "agent1", Instruction = "decide d" },
                },

                new SetVariableStep
                {
                    Vars = new()
                    {
                        ["_result"] = "{{vars.a.Evidence}}|{{vars.b.Decision}}|{{vars.c.Evidence}}|{{vars.d.Decision}}|{{vars.visit_d}}",
                    },
                },
            ],
        },
    };

    /// <summary>The result the scenario's definition returns, on either engine.</summary>
    public const string ExpectedResult = "current-a|approved|current-c|timeout|gate-d:4";

    private static WaitForSignalStep Guarded(string name, string signal, string visitVar, string outputVar) => new()
    {
        Name = name,
        SignalName = signal,
        Phase = "waiting-" + outputVar,
        OutputVar = outputVar,
        VisitVar = visitVar,
        DelegatedGuard = new DelegatedGuard
        {
            Marker = "GrantId",
            Require = new()
            {
                ["VisitId"] = "{{vars." + visitVar + "}}",
                ["ArtifactRef"] = "{{vars.review_ref}}",
            },
        },
    };

    /// <summary>Runs <paramref name="definition"/> on a fresh time-skipping server and returns its history.</summary>
    public static async Task<WorkflowHistory> RunAsync(WorkflowDefinitionModel definition)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(
            new AddSearchAttributesRequest
            {
                Namespace = env.Client.Options.Namespace,
                SearchAttributes =
                {
                    ["Phase"] = IndexedValueType.Keyword,
                    ["GateVisit"] = IndexedValueType.Keyword,
                },
            });

        var activities = new GateVisitGuardActivities(definition, Held);
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        string? result = null;
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                WorkflowType,
                Array.Empty<object?>(),
                new WorkflowOptions(id: WorkflowId, taskQueue: TaskQueue));

            await activities.Started("tick_a").WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync("gate-a", [Delegated("gate-a:9", "ref-1", "other-visit")]);
            activities.Release("tick_a");

            await activities.Started("notify_a").WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync("gate-a", [Delegated("gate-a:1", "ref-0", "other-artifact")]);
            await handle.SignalAsync("gate-a", [Delegated("gate-a:1", "ref-1", "current-a")]);
            activities.Release("notify_a");

            await activities.Started("notify_b").WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync("gate-b", [Delegated("gate-b:1", "ref-1", "other-visit")]);
            await handle.SignalAsync("gate-b", [JsonSerializer.SerializeToElement(new { Decision = "approved" })]);
            activities.Release("notify_b");

            await activities.Started("tick_c").WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync("gate-c", [Delegated("gate-c:3", "ref-1", "current-c")]);
            activities.Release("tick_c");

            result = await handle.GetResultAsync<string>();
        });

        Assert.Equal(ExpectedResult, result);
        return await env.Client.GetWorkflowHandle(WorkflowId).FetchHistoryAsync();
    }

    private static JsonElement Delegated(string visitId, string artifactRef, string evidence) =>
        JsonSerializer.SerializeToElement(new
        {
            Decision = "approved",
            GrantId = "grant-1",
            VisitId = visitId,
            ArtifactRef = artifactRef,
            Evidence = evidence,
        });
}
