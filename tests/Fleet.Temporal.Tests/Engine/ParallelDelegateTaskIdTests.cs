using System.Collections.Concurrent;
using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// A delegation's task id is what the bridge, the agent and the agent's reply are matched by, and
/// it was <c>{workflowId}/{step}</c> for every branch of a parallel step. These pin the branch
/// discriminator that makes it unique, and what it must NOT change: ids outside a branch, and the
/// replay of a run that fanned out before the change.
///
/// <para>
/// The baseline is <c>Fixtures/uwe-parallel-delegate-history.json</c>:
/// <see cref="ParallelDelegateHistoryScenario"/> run on the engine BEFORE the change (commit
/// cb6153f2) on a time-skipping test server. Only the worker identity was edited afterwards (now a
/// placeholder host). It must never be regenerated from post-change code: it would stop being a
/// pre-change history and the replay test would compare the engine with itself.
/// </para>
/// </summary>
public sealed class ParallelDelegateTaskIdTests
{
    private const string Wf = ParallelDelegateHistoryScenario.WorkflowId;

    private static WorkflowHistory Fixture() =>
        WorkflowHistory.FromJson(
            Wf,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ParallelDelegateHistoryScenario.FixturePath)));

    /// <summary>
    /// The fixture really is a pre-change history: every branch of a step shares one id, and the
    /// patch marker is absent.
    /// </summary>
    [Fact]
    public void TheFixture_IsAPreChangeHistory_WithOneIdPerStep()
    {
        var history = Fixture();

        Assert.Equal(
            [
                $"{Wf}/prepare",
                $"{Wf}/review", $"{Wf}/review", $"{Wf}/review",
                $"{Wf}/delegate",
                $"{Wf}/inner", $"{Wf}/inner",
            ],
            ParallelDelegateHistoryScenario.DelegateTaskIds(history));
        Assert.DoesNotContain(Markers(history), m => m.Contains(UniversalWorkflow.BranchTaskIdPatchId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Replay safety. A run that fanned out before the bridge deploys replays its recorded history
    /// against the new engine. The patch is absent from it, so it keeps its old ids and the engine
    /// emits the same commands in the same order — the position tracking adds none.
    /// </summary>
    [Fact]
    public async Task APreChangeFanOutHistory_ReplaysCleanlyAgainstTheCurrentEngine()
    {
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());

        await replayer.ReplayWorkflowAsync(Fixture());
    }

    /// <summary>
    /// The same scenario on the current engine: one id per branch, nested positions outermost
    /// first, and the delegate outside every branch exactly as it was. The new history carries the
    /// marker and replays.
    /// </summary>
    [Fact]
    public async Task EveryBranch_GetsItsOwnTaskId_AndADelegateOutsideOneKeepsItsId()
    {
        var history = await ParallelDelegateHistoryScenario.RunAsync();

        var ids = ParallelDelegateHistoryScenario.DelegateTaskIds(history);
        Assert.Equal(
            [
                $"{Wf}/prepare",
                $"{Wf}/review~0", $"{Wf}/review~1", $"{Wf}/review~2",
                $"{Wf}/delegate~0",
                $"{Wf}/inner~1.0", $"{Wf}/inner~1.1",
            ],
            ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        Assert.Single(Markers(history), m => m.Contains(UniversalWorkflow.BranchTaskIdPatchId, StringComparison.Ordinal));

        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

    /// <summary>
    /// A run that never delegates from a branch does not consult the patch, so it records no new
    /// marker: its history is the one it always had.
    /// </summary>
    [Fact]
    public async Task ARunWithoutABranchDelegation_RecordsNoBranchMarker()
    {
        var history = await DelegateHistoryScenario.RunAsync(
            DelegateHistoryScenario.Definition(), DelegateHistoryScenario.Input());

        Assert.DoesNotContain(Markers(history), m => m.Contains(UniversalWorkflow.BranchTaskIdPatchId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The defect itself, through the real <see cref="TaskCompletionRegistry"/>. Three branches
    /// delegate the same step; the replies are delivered last branch first. With one id per step
    /// only one waiter could exist and the other branches never returned; with one id per branch
    /// each gets the reply addressed to it.
    /// </summary>
    [Fact]
    public async Task AFanOut_DeliversEachAgentsReplyToItsOwnBranch()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = "fan-out-" + Guid.NewGuid().ToString("N");
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var activities = new RegistryActivities(FanOut(), registry);

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(queue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "FanOutExample",
                new object?[] { JsonSerializer.SerializeToElement(new { Agents = new[] { "agent-a", "agent-b", "agent-c" } }) },
                new WorkflowOptions(id: queue, taskQueue: queue));

            // All three branches are waiting at once: three registrations, not one overwritten twice.
            await activities.WaitForRegistrationsAsync(3);
            Assert.Equal(3, registry.PendingCount);

            foreach (var (taskId, target) in activities.Registered.Reverse())
                Assert.True(registry.TryComplete(taskId, new AgentTaskResult($"reply from {target}", "completed")));

            await handle.GetResultAsync<object?>();
        });

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["agent-a"] = "reply from agent-a",
                ["agent-b"] = "reply from agent-b",
                ["agent-c"] = "reply from agent-c",
            },
            activities.Returned.ToDictionary(r => r.Key, r => r.Value));
    }

    private static WorkflowDefinitionModel FanOut() => new()
    {
        Name = "FanOutExample",
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep
        {
            Steps =
            [
                new ParallelStep
                {
                    Name = "reviews",
                    ForEach = "{{input.Agents}}",
                    ItemVar = "agent",
                    Step = new DelegateStep { Name = "review", Target = "{{vars.agent}}", Instruction = "Review." },
                },
                new SetVariableStep { Vars = new() { ["_result"] = "done" } },
            ],
        },
    };

    private static List<string> Markers(WorkflowHistory history) =>
        history.Events.Where(e => e.EventType == EventType.MarkerRecorded)
            .SelectMany(e => e.MarkerRecordedEventAttributes.Details.Values)
            .SelectMany(p => p.Payloads_)
            .Select(p => p.Data.ToStringUtf8())
            .ToList();

    /// <summary>
    /// A delegate stub that waits the way the real activity does: it registers its task id and
    /// returns whatever completes that registration.
    /// </summary>
    private sealed class RegistryActivities(WorkflowDefinitionModel definition, TaskCompletionRegistry registry)
    {
        private readonly ConcurrentQueue<(string TaskId, string Target)> _registered = new();
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyCollection<(string TaskId, string Target)> Registered => _registered;

        /// <summary>What each target's own delegation returned.</summary>
        public ConcurrentDictionary<string, string> Returned { get; } = new();

        public async Task WaitForRegistrationsAsync(int count)
        {
            for (var i = 0; i < count; i++)
                Assert.True(await _signal.WaitAsync(TimeSpan.FromSeconds(30)), "a branch never delegated");
        }

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
            var pending = registry.Register(taskId);
            _registered.Enqueue((taskId, target));
            _signal.Release();
            var result = await pending.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Returned[target] = result.Text;
            return result;
        }
    }
}
