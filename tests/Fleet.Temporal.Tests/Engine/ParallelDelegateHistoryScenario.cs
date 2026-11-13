using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// One UWE run that delegates from inside every kind of parallel branch the engine has: a
/// <c>forEach</c> fan-out of one named delegate, a static <c>parallel</c> with an unnamed delegate,
/// and a <c>forEach</c> nested in that static parallel.
///
/// <para>
/// The committed fixture <c>Fixtures/uwe-parallel-delegate-history.json</c> is this scenario's
/// history as recorded by the engine BEFORE delegations in a branch got a branch discriminator
/// (commit cb6153f2), so every branch of a step carries the same task id in it. It is what a run
/// in flight across that deploy looks like, and it must keep replaying.
/// </para>
///
/// <para>
/// Everything here must keep compiling against that pre-change engine, which is how the fixture
/// was produced, so it mentions nothing the change added. The ids are fixed rather than random
/// because the task ids embed the workflow id.
/// </para>
/// </summary>
internal static class ParallelDelegateHistoryScenario
{
    public const string WorkflowType = "example-parallel-workflow";
    public const string WorkflowId = "example-parallel-delegate-history";
    public const string TaskQueue = "example-parallel-delegate-history";
    public const string ActivityName = "DelegateToAgent";
    public const string FixturePath = "Fixtures/uwe-parallel-delegate-history.json";

    public static WorkflowDefinitionModel Definition() => new()
    {
        Name = WorkflowType,
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep
        {
            Steps =
            [
                // Outside any branch: its id must never change.
                new DelegateStep { Name = "prepare", Target = "agent-a", Instruction = "Prepare." },
                new ParallelStep
                {
                    Name = "reviews",
                    ForEach = "{{input.Agents}}",
                    ItemVar = "agent",
                    Step = new DelegateStep
                    {
                        Name = "review",
                        Target = "{{vars.agent}}",
                        Instruction = "Review as {{vars.agent}}.",
                    },
                },
                new ParallelStep
                {
                    Steps =
                    [
                        new DelegateStep { Target = "agent-a", Instruction = "Summarise." },
                        new ParallelStep
                        {
                            ForEach = "{{input.Pair}}",
                            ItemVar = "member",
                            Step = new DelegateStep
                            {
                                Name = "inner",
                                Target = "{{vars.member}}",
                                Instruction = "Check as {{vars.member}}.",
                            },
                        },
                    ],
                },
                new SetVariableStep { Vars = new() { ["_result"] = "done" } },
            ],
        },
    };

    public static JsonElement Input() => JsonSerializer.SerializeToElement(new
    {
        Agents = new[] { "agent-a", "agent-b", "agent-c" },
        Pair = new[] { "agent-b", "agent-c" },
    });

    /// <summary>Runs the scenario on a fresh time-skipping server and returns its full history.</summary>
    public static async Task<WorkflowHistory> RunAsync()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        var activities = new ParallelDelegateHistoryActivities(Definition());
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                WorkflowType,
                new object?[] { Input() },
                new WorkflowOptions(id: WorkflowId, taskQueue: TaskQueue));

            await handle.GetResultAsync();
        });

        return await env.Client.GetWorkflowHandle(WorkflowId).FetchHistoryAsync();
    }

    /// <summary>The task id of every <c>DelegateToAgent</c> the history scheduled, in order.</summary>
    public static IReadOnlyList<string> DelegateTaskIds(WorkflowHistory history) =>
        history.Events
            .Where(e => e.ActivityTaskScheduledEventAttributes?.ActivityType?.Name == ActivityName)
            .Select(e => DataConverter.Default.PayloadConverter.ToValue<string>(
                e.ActivityTaskScheduledEventAttributes.Input.Payloads_[2]))
            .ToList();
}

/// <summary>Stubs for <see cref="ParallelDelegateHistoryScenario"/>: every delegation succeeds.</summary>
internal sealed class ParallelDelegateHistoryActivities(WorkflowDefinitionModel definition)
{
    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

    [Activity(ParallelDelegateHistoryScenario.ActivityName)]
    public AgentTaskResult Delegate(
        string target,
        string instruction,
        string taskId,
        bool retryOnIncomplete,
        int maxIncompleteRetries,
        int agentBudgetSeconds = 0,
        string? repo = null) =>
        new($"{target} done", "completed");
}
