using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// One UWE run that takes every exit a <c>wait_for_signal</c> step WITHOUT <c>visitVar</c> or
/// <c>delegatedGuard</c> has: the buffer fast path, a live delivery to a parked finite wait, a
/// live delivery to an indefinite wait, a timeout with <c>autoCompleteOnTimeout</c> after one
/// reminder, and a timeout that throws under <c>ignoreFailure</c>. Every parked wait sets a
/// <c>phase</c>, and three send a notification.
///
/// <para>
/// It exists for #436, which adds gate visits and a consumption guard to the wait. A wait without
/// those fields must emit exactly the commands it emitted before. The committed fixture
/// <c>Fixtures/uwe-gate-wait-history.json</c> is this scenario's history as recorded by the engine
/// BEFORE that change (commit dff99e4) on a time-skipping test server. Only replay-irrelevant text
/// was edited afterwards: the worker identity (a placeholder host).
/// </para>
///
/// <para>
/// Everything here must keep compiling against that pre-change engine, because that is how the
/// fixture was produced, so it never mentions the new fields. Ids are fixed so the task ids the
/// notifications embed are stable. Every signal is sent while an activity is held open, so the
/// command sequence does not depend on how the server groups workflow tasks.
/// </para>
/// </summary>
internal static class GateWaitHistoryScenario
{
    public const string WorkflowType = "example-workflow";
    public const string WorkflowId = "example-gate-wait-history";
    public const string TaskQueue = "example-gate-wait-history";
    public const string FixturePath = "Fixtures/uwe-gate-wait-history.json";

    public static WorkflowDefinitionModel Definition() => new()
    {
        Name = WorkflowType,
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep
        {
            Steps =
            [
                // Held open by the stub: gate-a is sent now, with no waiter registered, and buffered.
                new DelegateStep { Name = "tick", Target = "agent1", Instruction = "work", OutputVar = "tick" },

                // Buffer fast path: consumed at entry, never parks.
                new WaitForSignalStep
                {
                    Name = "gate_buffered",
                    SignalName = "gate-a",
                    Phase = "waiting-a",
                    OutputVar = "a",
                    TimeoutMinutes = 60,
                    MaxReminders = 0,
                    AutoCompleteOnTimeout = true,
                },

                // Parks; gate-b is sent while the initial notification is held open.
                new WaitForSignalStep
                {
                    Name = "gate_live",
                    SignalName = "gate-b",
                    Phase = "waiting-b",
                    OutputVar = "b",
                    TimeoutMinutes = 60,
                    ReminderIntervalMinutes = 30,
                    MaxReminders = 2,
                    AutoCompleteOnTimeout = true,
                    NotifyStep = new DelegateStep { Name = "notify_b", Target = "agent1", Instruction = "decide b" },
                },

                // Indefinite; gate-d is sent while the initial notification is held open.
                new WaitForSignalStep
                {
                    Name = "gate_indefinite",
                    SignalName = "gate-d",
                    Phase = "waiting-d",
                    OutputVar = "d",
                    NotifyStep = new DelegateStep { Name = "notify_d", Target = "agent1", Instruction = "decide d" },
                },

                // Never signalled: one reminder at 30 minutes, then the auto-complete timeout at 60.
                new WaitForSignalStep
                {
                    Name = "gate_timeout",
                    SignalName = "gate-c",
                    Phase = "waiting-c",
                    OutputVar = "c",
                    TimeoutMinutes = 60,
                    ReminderIntervalMinutes = 30,
                    MaxReminders = 3,
                    AutoCompleteOnTimeout = true,
                    NotifyStep = new DelegateStep { Name = "notify_c", Target = "agent1", Instruction = "decide c" },
                },

                // Never signalled: the TimeoutException, swallowed by ignoreFailure.
                new WaitForSignalStep
                {
                    Name = "gate_throw",
                    SignalName = "gate-e",
                    OutputVar = "e",
                    TimeoutMinutes = 30,
                    MaxReminders = 0,
                    IgnoreFailure = true,
                },

                new SetVariableStep
                {
                    Vars = new()
                    {
                        ["_result"] = "{{vars.a.Decision}}|{{vars.b.Decision}}|{{vars.d.Decision}}|{{vars.c.Decision}}|{{vars.e.Decision | default: 'none'}}",
                    },
                },
            ],
        },
    };

    /// <summary>The result the scenario's definition returns, on either engine.</summary>
    public const string ExpectedResult = "approved|approved|approved|timeout|none";

    /// <summary>Runs <paramref name="definition"/> on a fresh time-skipping server and returns its history.</summary>
    public static async Task<WorkflowHistory> RunAsync(WorkflowDefinitionModel definition)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        // A parked wait upserts Phase; an unregistered attribute fails the workflow task.
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

        var activities = new GateWaitHistoryActivities(definition);
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

            await activities.Started("tick").WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync("gate-a", [Approved()]);
            activities.Release("tick");

            await activities.Started("notify_b").WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync("gate-b", [Approved()]);
            activities.Release("notify_b");

            await activities.Started("notify_d").WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync("gate-d", [Approved()]);
            activities.Release("notify_d");

            result = await handle.GetResultAsync<string>();
        });

        Assert.Equal(ExpectedResult, result);
        return await env.Client.GetWorkflowHandle(WorkflowId).FetchHistoryAsync();
    }

    private static JsonElement Approved() =>
        JsonSerializer.SerializeToElement(new { Decision = "approved" });

    /// <summary>
    /// The commands a history records, in order, with ids and times left out: activity type and
    /// input bytes, timer durations, timer cancellations, marker names, search-attribute upserts
    /// (name and value bytes), and the completion result. Workflow-task bookkeeping and signal
    /// arrival events are not commands and are skipped.
    /// </summary>
    public static IReadOnlyList<string> Commands(WorkflowHistory history) =>
        history.Events.Select(Command).OfType<string>().ToList();

    private static string? Command(HistoryEvent e) => e.EventType switch
    {
        EventType.ActivityTaskScheduled =>
            $"activity {e.ActivityTaskScheduledEventAttributes.ActivityType.Name} " +
            string.Join(",", e.ActivityTaskScheduledEventAttributes.Input?.Payloads_.Select(p => p.Data.ToStringUtf8()) ?? []),
        EventType.TimerStarted => $"timer {e.TimerStartedEventAttributes.StartToFireTimeout}",
        EventType.TimerCanceled => "timer-cancel",
        EventType.MarkerRecorded => $"marker {e.MarkerRecordedEventAttributes.MarkerName}",
        EventType.UpsertWorkflowSearchAttributes =>
            "upsert " + string.Join(",",
                e.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}={kv.Value.Data.ToStringUtf8()}")),
        EventType.WorkflowExecutionCompleted =>
            "complete " + string.Join(",",
                e.WorkflowExecutionCompletedEventAttributes.Result?.Payloads_.Select(p => p.Data.ToStringUtf8()) ?? []),
        _ => null,
    };
}

/// <summary>
/// Stubs for <see cref="GateWaitHistoryScenario"/>. <c>tick</c>, <c>notify_b</c> and
/// <c>notify_d</c> are held until the test releases them; every other delegation returns at once.
/// </summary>
internal sealed class GateWaitHistoryActivities(WorkflowDefinitionModel definition)
{
    private static readonly string[] Held = ["tick", "notify_b", "notify_d"];

    private readonly Dictionary<string, (TaskCompletionSource Started, TaskCompletionSource Release)> _holds =
        Held.ToDictionary(
            name => name,
            _ => (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                  new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));

    public Task Started(string step) => _holds[step].Started.Task;

    public void Release(string step) => _holds[step].Release.TrySetResult();

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

    [Activity("DelegateToAgent")]
    public async Task<AgentTaskResult> DelegateAsync(
        string target,
        string instruction,
        string taskId,
        bool retryOnIncomplete,
        int maxIncompleteRetries,
        int agentBudgetSeconds = 0,
        string? repo = null)
    {
        var step = taskId.Split('/').LastOrDefault() ?? taskId;
        if (_holds.TryGetValue(step, out var hold))
        {
            hold.Started.TrySetResult();
            await hold.Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        return new AgentTaskResult($"{step} done", "completed");
    }
}
