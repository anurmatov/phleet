using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// One UWE run that uses both #424 features: a delegate with <c>statusVar</c>, then a
/// <c>branch</c> on that status that reaches a <c>fail</c> step under an <c>ignoreFailure</c>
/// ancestor. The delegate reports <c>" FAILED "</c>, so the definition sees <c>failed</c>, the
/// <c>fail</c> runs, nothing swallows it, and the run closes Failed with <c>ExplicitFail</c>. The
/// delegate after the parent is never scheduled.
///
/// <para>
/// <c>Fixtures/uwe-delegate-statusvar-history.json</c> is this scenario's history, recorded on a
/// time-skipping test server with the engine at commit b8aed95 (#424). Only replay-irrelevant text
/// was edited afterwards: the worker identity (a placeholder host) and the source directory in the
/// failure stack trace. Ids are fixed so the task ids recorded in it are stable.
/// </para>
/// </summary>
internal static class DelegateStatusVarHistoryScenario
{
    public const string WorkflowType = "example-workflow";
    public const string WorkflowId = "example-statusvar-history";
    public const string TaskQueue = "example-statusvar-history";
    public const string FixturePath = "Fixtures/uwe-delegate-statusvar-history.json";

    public static WorkflowDefinitionModel Definition() => new()
    {
        Name = WorkflowType,
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep
        {
            Steps =
            [
                new DelegateStep
                {
                    Name = "implement",
                    Target = "agent1",
                    Instruction = "Do {{input.Task}}.",
                    OutputVar = "impl",
                    StatusVar = "impl_status",
                },
                new SequenceStep
                {
                    Name = "guard",
                    IgnoreFailure = true,
                    Steps =
                    [
                        new BranchStep
                        {
                            On = "{{vars.impl_status}}",
                            Cases = new()
                            {
                                ["failed"] = new FailStep
                                {
                                    Name = "stop_on_failed",
                                    Message = "implement reported {{vars.impl_status}}",
                                },
                            },
                            Default = new NoopStep(),
                        },
                    ],
                },
                new DelegateStep { Name = "post_check", Target = "agent1", Instruction = "Check {{vars.impl}}." },
            ],
        },
    };

    public static JsonElement Input() => JsonSerializer.SerializeToElement(new { Task = "the thing" });

    /// <summary>Runs the scenario on a fresh time-skipping server and returns its full history.</summary>
    public static async Task<WorkflowHistory> RunAsync()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        var activities = new DelegateStatusVarHistoryActivities(Definition());
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
                new WorkflowOptions(id: WorkflowId, taskQueue: TaskQueue)
                {
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                });

            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
        });

        return await env.Client.GetWorkflowHandle(WorkflowId).FetchHistoryAsync();
    }
}

/// <summary>Stubs for <see cref="DelegateStatusVarHistoryScenario"/>.</summary>
internal sealed class DelegateStatusVarHistoryActivities(WorkflowDefinitionModel definition)
{
    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

    [Activity("DelegateToAgent")]
    public AgentTaskResult Delegate(
        string target, string instruction, string taskId, bool retryOnIncomplete, int maxIncompleteRetries,
        int agentBudgetSeconds = 0, string? repo = null) =>
        new("partial work", " FAILED ");
}
