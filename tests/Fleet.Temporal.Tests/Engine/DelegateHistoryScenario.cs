using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// One UWE run that schedules <c>DelegateToAgent</c> through every delegate path the engine has:
/// a plain <c>delegate</c> step, a <c>delegate_with_escalation</c> attempt that fails, the
/// engine-built escalation notification, and the retried attempt.
///
/// <para>
/// It exists for #347 (the relay <c>Repo</c> signal), which appends an argument to the activity
/// input ONLY when a step sets <c>repo</c>. The committed fixture
/// <c>Fixtures/uwe-delegate-5arg-history.json</c> is this scenario's history as recorded by the
/// engine BEFORE that change (commit 67ed2b9), so it is a genuine five-argument history with a
/// definition payload that has no <c>repo</c> key at all.
/// </para>
///
/// <para>
/// Everything here must keep compiling against that pre-change engine — it is how the fixture was
/// produced — so it never mentions <c>Repo</c>. Tests that need a repo build their own definition.
/// The ids are fixed rather than random because the task ids and the escalation notification
/// embed the workflow id, and the byte comparison against the fixture needs them equal.
/// </para>
/// </summary>
internal static class DelegateHistoryScenario
{
    public const string WorkflowType = "example-workflow";
    public const string WorkflowId = "example-delegate-history";
    public const string TaskQueue = "example-delegate-history";
    public const string ActivityName = "DelegateToAgent";
    public const string FixturePath = "Fixtures/uwe-delegate-5arg-history.json";

    public static WorkflowDefinitionModel Definition() => Definition(
        new DelegateStep
        {
            Name = "plain_step",
            Target = "agent-a",
            Instruction = "Do {{input.Task}}.",
            OutputVar = "plain",
        },
        new DelegateWithEscalationStep
        {
            Name = "escalating_step",
            Target = "agent-a",
            Instruction = "Then act on: {{vars.plain}}",
            OutputVar = "_result",
        });

    public static WorkflowDefinitionModel Definition(DelegateStep plain, DelegateWithEscalationStep escalating) => new()
    {
        Name = WorkflowType,
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep { Steps = [plain, escalating] },
    };

    public static JsonElement Input(object? input = null) =>
        JsonSerializer.SerializeToElement(input ?? new { Task = "the thing" });

    /// <summary>
    /// Runs the scenario on a fresh time-skipping server and returns its full history. The first
    /// <c>escalating_step</c> attempt fails; the escalation is answered with <c>retry</c> while its
    /// notification is still in flight (the waiter is registered before the notification is sent),
    /// so the command sequence is the same on every run.
    /// </summary>
    public static async Task<WorkflowHistory> RunAsync(WorkflowDefinitionModel definition, JsonElement input)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        // The escalation branch upserts the Phase search attribute; an unregistered attribute
        // fails that workflow task and the branch never gets past its first command.
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(
            new AddSearchAttributesRequest
            {
                Namespace = env.Client.Options.Namespace,
                SearchAttributes = { ["Phase"] = IndexedValueType.Keyword },
            });

        var activities = new DelegateHistoryActivities(definition);
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                WorkflowType,
                new object?[] { input },
                new WorkflowOptions(id: WorkflowId, taskQueue: TaskQueue));

            await activities.EscalationNotified.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync(
                "escalation-decision",
                [JsonSerializer.SerializeToElement(new { Decision = "retry" })]);
            activities.ReleaseNotification();

            await handle.GetResultAsync();
        });

        return await env.Client.GetWorkflowHandle(WorkflowId).FetchHistoryAsync();
    }

    /// <summary>The input of every <c>DelegateToAgent</c> the history scheduled, in order.</summary>
    public static IReadOnlyList<Payloads> DelegateInputs(WorkflowHistory history) =>
        history.Events
            .Where(e => e.ActivityTaskScheduledEventAttributes?.ActivityType?.Name == ActivityName)
            .Select(e => e.ActivityTaskScheduledEventAttributes.Input)
            .ToList();
}

/// <summary>
/// Stubs for <see cref="DelegateHistoryScenario"/>. The delegate stub takes the activity's full
/// optional tail so it accepts both the five-argument input and the seven-argument one a
/// <c>repo</c> step schedules; what the engine actually scheduled is read from the history, not
/// from here.
/// </summary>
internal sealed class DelegateHistoryActivities(WorkflowDefinitionModel definition)
{
    private readonly TaskCompletionSource _releaseNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _escalatingAttempts;

    public TaskCompletionSource EscalationNotified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseNotification() => _releaseNotification.TrySetResult();

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() =>
        JsonSerializer.SerializeToElement(new { EscalationTarget = "example-escalation-target" });

    [Activity(DelegateHistoryScenario.ActivityName)]
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

        if (step.EndsWith("_escalation_notify", StringComparison.Ordinal))
        {
            EscalationNotified.TrySetResult();
            await _releaseNotification.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new AgentTaskResult("notified", "completed");
        }

        if (step == "escalating_step" && Interlocked.Increment(ref _escalatingAttempts) == 1)
            throw new ApplicationFailureException("step failed on purpose", nonRetryable: true);

        return new AgentTaskResult($"{step} done", "completed");
    }
}
