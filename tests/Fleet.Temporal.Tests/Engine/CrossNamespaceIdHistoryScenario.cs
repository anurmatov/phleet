using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// One UWE run that starts a workflow in another namespace WITHOUT naming its id, and then asks
/// for human input — the only step whose child workflow id comes from the workflow's own seeded
/// random source.
///
/// <para>
/// The order is the point. The generated cross-namespace id is an activity argument, which replay
/// does not compare. The human-input child id is recorded and IS compared, and it is drawn from
/// the same random source the cross-namespace id now uses. So a history recorded when the first
/// id came from somewhere else replays only if the engine still leaves that source untouched for
/// it.
/// </para>
///
/// <para>
/// The committed fixture <c>Fixtures/uwe-cross-namespace-id-history.json</c> is this scenario's
/// history as recorded by the engine BEFORE the cross-namespace id became deterministic (commit
/// 4fd3130a), up to the point where the child has started and parked. Everything here must keep
/// compiling against that engine, which is how the fixture was produced.
/// </para>
/// </summary>
internal static class CrossNamespaceIdHistoryScenario
{
    public const string WorkflowType = "ExampleCrossNamespaceParent";
    public const string WorkflowId = "example-cross-namespace-id-history";
    public const string Namespace = "fleet";
    public const string TaskQueue = "fleet";
    public const string ActivityName = "StartCrossNamespaceWorkflow";
    public const string FixturePath = "Fixtures/uwe-cross-namespace-id-history.json";

    public static WorkflowDefinitionModel Definition() => new()
    {
        Name = WorkflowType,
        Namespace = Namespace,
        TaskQueue = TaskQueue,
        Root = new SequenceStep
        {
            Steps =
            [
                new CrossNamespaceStartStep
                {
                    Name = "start_elsewhere",
                    WorkflowType = "other-workflow",
                    Namespace = "other",
                    TaskQueue = "other",
                    OutputVar = "started",
                },
                new ChildWorkflowStep
                {
                    WorkflowType = HumanInputRequestContract.WorkflowType,
                    Args = new()
                    {
                        ["Question"] = "Proceed?",
                        ["Context"] = "literal context",
                        ["AnswerMode"] = "text",
                    },
                    OutputVar = "response",
                },
            ],
        },
    };

    /// <summary>
    /// Runs the scenario on a fresh time-skipping server until the human-input child has started
    /// and parked, and returns the PARENT's history up to there. The parent is left waiting.
    /// </summary>
    public static async Task<WorkflowHistory> RunAsync()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = Namespace });
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(new AddSearchAttributesRequest
        {
            Namespace = Namespace,
            SearchAttributes =
            {
                ["Phase"] = IndexedValueType.Keyword,
                ["GateRef"] = IndexedValueType.Keyword,
                ["GateVisit"] = IndexedValueType.Keyword,
            },
        });

        var activities = new CrossNamespaceIdActivities(Definition());
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        WorkflowHistory? parentHistory = null;
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                WorkflowType,
                Array.Empty<object?>(),
                new WorkflowOptions(id: WorkflowId, taskQueue: TaskQueue));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            WorkflowHandle? child = null;
            while (child is null)
            {
                var history = await handle.FetchHistoryAsync();
                var started = history.Events.FirstOrDefault(e => e.EventType == EventType.ChildWorkflowExecutionStarted);
                if (started is not null)
                    child = env.Client.GetWorkflowHandle(started.ChildWorkflowExecutionStartedEventAttributes.WorkflowExecution.WorkflowId);
                else if (DateTime.UtcNow > deadline) throw new TimeoutException("The human-input child did not start.");
                else await Task.Delay(20);
            }

            // The child parking means the parent's workflow task for "child started" is long done,
            // so the parent's history is quiescent and the same on every run.
            while (!(await child.FetchHistoryAsync()).Events.Any(e => e.EventType == EventType.TimerStarted))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The human-input child did not park.");
                await Task.Delay(20);
            }

            parentHistory = await handle.FetchHistoryAsync();
        });

        return parentHistory!;
    }

    /// <summary>The workflow id every <c>StartCrossNamespaceWorkflow</c> in the history was given.</summary>
    public static IReadOnlyList<string> StartedIds(WorkflowHistory history) =>
        history.Events
            .Where(e => e.ActivityTaskScheduledEventAttributes?.ActivityType?.Name == ActivityName)
            .Select(e => DataConverter.Default.PayloadConverter.ToValue<string>(
                e.ActivityTaskScheduledEventAttributes.Input.Payloads_[2]))
            .ToList();

    /// <summary>The id of every child workflow the history started.</summary>
    public static IReadOnlyList<string> ChildIds(WorkflowHistory history) =>
        history.Events
            .Where(e => e.EventType == EventType.StartChildWorkflowExecutionInitiated)
            .Select(e => e.StartChildWorkflowExecutionInitiatedEventAttributes.WorkflowId)
            .ToList();
}

/// <summary>Stubs for <see cref="CrossNamespaceIdHistoryScenario"/>: nothing is started anywhere.</summary>
internal sealed class CrossNamespaceIdActivities(WorkflowDefinitionModel parent)
{
    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string name) => name == parent.Name
        ? parent
        : new()
        {
            Name = HumanInputRequestContract.WorkflowType,
            Namespace = CrossNamespaceIdHistoryScenario.Namespace,
            TaskQueue = CrossNamespaceIdHistoryScenario.TaskQueue,
            Root = SeedHarness.Parse(HumanInputRequestContract.WorkflowType),
        };

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

    [Activity(CrossNamespaceIdHistoryScenario.ActivityName)]
    public string Start(string targetNamespace, string workflowType, string workflowId, string taskQueue, object? input) =>
        workflowId;
}
