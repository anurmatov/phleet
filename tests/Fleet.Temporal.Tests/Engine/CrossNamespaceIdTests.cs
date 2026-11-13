using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// <c>cross_namespace_start</c> without a <c>workflowId</c> used to take its id from
/// <c>Guid.NewGuid()</c> inside workflow code. It now comes from the workflow's seeded random
/// source, behind a patch.
///
/// <para>
/// The patch is needed although replay never compares that id (it is an activity argument): the
/// seeded source is also what names a human-input child, and a child workflow id IS compared. A
/// run recorded before the change did not draw from it for the cross-namespace id, so drawing
/// from it on replay hands the child a different id and the replay fails. The fixture is exactly
/// that run, recorded on the engine before the change (commit 4fd3130a); removing the patch guard
/// makes its replay fail with a child-workflow-id mismatch.
/// </para>
/// </summary>
public sealed class CrossNamespaceIdTests
{
    private static WorkflowHistory Fixture() =>
        WorkflowHistory.FromJson(
            CrossNamespaceIdHistoryScenario.WorkflowId,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, CrossNamespaceIdHistoryScenario.FixturePath)));

    private static WorkflowReplayer Replayer() => new(
        new WorkflowReplayerOptions
        {
            Namespace = CrossNamespaceIdHistoryScenario.Namespace,
            TaskQueue = CrossNamespaceIdHistoryScenario.TaskQueue,
        }.AddWorkflow<UniversalWorkflow>());

    [Fact]
    public void TheFixture_IsAPreChangeHistory_WithAGeneratedIdAndAHumanInputChild()
    {
        var history = Fixture();

        Assert.True(Guid.TryParse(Assert.Single(CrossNamespaceIdHistoryScenario.StartedIds(history)), out _));
        Assert.StartsWith("human-input-", Assert.Single(CrossNamespaceIdHistoryScenario.ChildIds(history)));
        Assert.DoesNotContain(Markers(history), m => m.Contains(UniversalWorkflow.CrossNamespaceIdPatchId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task APreChangeHistory_ReplaysCleanly_BecauseItsChildIdIsStillDrawnFirst()
    {
        await Replayer().ReplayWorkflowAsync(Fixture());
    }

    /// <summary>The same scenario on the current engine records the marker, and replays.</summary>
    [Fact]
    public async Task AGeneratedId_IsTakenBehindThePatch_AndTheNewHistoryReplays()
    {
        var history = await CrossNamespaceIdHistoryScenario.RunAsync();

        Assert.True(Guid.TryParse(Assert.Single(CrossNamespaceIdHistoryScenario.StartedIds(history)), out _));
        Assert.Single(Markers(history), m => m.Contains(UniversalWorkflow.CrossNamespaceIdPatchId, StringComparison.Ordinal));

        await Replayer().ReplayWorkflowAsync(history);
    }

    /// <summary>
    /// A step that names its id generates nothing, so it does not consult the patch: no marker,
    /// and the id is passed through as written.
    /// </summary>
    [Fact]
    public async Task AnExplicitWorkflowId_IsPassedThrough_AndRecordsNoMarker()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = "cross-ns-" + Guid.NewGuid().ToString("N");
        var definition = new WorkflowDefinitionModel
        {
            Name = "ExplicitIdExample",
            Namespace = "default",
            TaskQueue = "test",
            Root = new CrossNamespaceStartStep
            {
                WorkflowType = "other-workflow",
                Namespace = "other",
                TaskQueue = "other",
                WorkflowId = "chosen-{{input.Suffix}}",
                OutputVar = "_result",
            },
        };
        var activities = new CrossNamespaceIdActivities(definition);

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(queue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                definition.Name,
                new object?[] { JsonSerializer.SerializeToElement(new { Suffix = "one" }) },
                new WorkflowOptions(id: queue, taskQueue: queue));
            Assert.Equal("chosen-one", await handle.GetResultAsync<string>());
        });

        var history = await env.Client.GetWorkflowHandle(queue).FetchHistoryAsync();
        Assert.Equal(["chosen-one"], CrossNamespaceIdHistoryScenario.StartedIds(history));
        Assert.DoesNotContain(Markers(history), m => m.Contains(UniversalWorkflow.CrossNamespaceIdPatchId, StringComparison.Ordinal));
    }

    private static List<string> Markers(WorkflowHistory history) =>
        history.Events.Where(e => e.EventType == EventType.MarkerRecorded)
            .SelectMany(e => e.MarkerRecordedEventAttributes.Details.Values)
            .SelectMany(p => p.Payloads_)
            .Select(p => p.Data.ToStringUtf8())
            .ToList();
}
