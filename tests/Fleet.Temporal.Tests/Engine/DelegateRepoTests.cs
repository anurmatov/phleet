using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Fleet.Temporal.Workflows.Fleet;
using Temporalio.Activities;
using Temporalio.Api.Common.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #347 D9 — the UWE <c>delegate.repo</c> field and what it must NOT change.
///
/// <para>
/// The repo reaches the agent as a trailing activity argument, appended only when a step sets
/// <c>repo</c> and it resolves non-empty. Everything else — every definition written before the
/// field existed — must schedule the same five arguments, byte for byte, and every history those
/// definitions already recorded must replay.
/// </para>
///
/// <para>
/// The baseline is <c>Fixtures/uwe-delegate-5arg-history.json</c>: <see cref="DelegateHistoryScenario"/>
/// run on the engine BEFORE this change (commit 67ed2b9, the scenario file copied unchanged into
/// that tree) on a time-skipping test server. Only replay-irrelevant text was edited afterwards: the
/// worker identity (now a placeholder host) and the recording directory in one failure stack trace.
/// It must never be regenerated from post-change code — it would then stop being a
/// pre-change history and every assertion here would compare the engine with itself.
/// </para>
/// </summary>
public sealed class DelegateRepoTests
{
    private static WorkflowHistory Fixture() =>
        WorkflowHistory.FromJson(
            DelegateHistoryScenario.WorkflowId,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DelegateHistoryScenario.FixturePath)));

    /// <summary>
    /// The fixture really is what it claims to be: four delegations — plain step, failing
    /// escalation attempt, escalation notification, retried attempt — of exactly five payloads
    /// each, and a recorded definition with no <c>repo</c> key anywhere.
    /// </summary>
    [Fact]
    public void TheFixture_IsAPreRepoFiveArgumentHistory()
    {
        var history = Fixture();

        var inputs = DelegateHistoryScenario.DelegateInputs(history);
        Assert.Equal(4, inputs.Count);
        Assert.All(inputs, p => Assert.Equal(5, p.Payloads_.Count));

        var definitionJson = history.Events
            .Where(e => e.ActivityTaskCompletedEventAttributes is not null)
            .Select(e => e.ActivityTaskCompletedEventAttributes.Result.Payloads_[0].Data.ToStringUtf8())
            .First(json => json.Contains("\"delegate_with_escalation\"", StringComparison.Ordinal));
        Assert.DoesNotContain("repo", definitionJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replay safety. A workflow in flight when the bridge deploys replays its recorded history
    /// against the new engine; a divergent command sequence is a non-determinism failure that
    /// wedges it. The recorded definition deserializes with <c>Repo = null</c>, so every
    /// delegation is scheduled exactly as recorded.
    /// </summary>
    [Fact]
    public async Task APreRepoHistory_ReplaysCleanlyAgainstTheCurrentEngine()
    {
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());

        await replayer.ReplayWorkflowAsync(Fixture());
    }

    /// <summary>
    /// Replay alone cannot prove the INPUT is unchanged — Temporal does not compare activity
    /// arguments on replay. So the same scenario runs live on the current engine and every
    /// scheduled <c>DelegateToAgent</c> input is compared with the recorded one, payload bytes and
    /// metadata included.
    /// </summary>
    [Fact]
    public async Task ADelegateWithoutRepo_SchedulesTheSameFiveArgumentInputAsBefore()
    {
        var history = await DelegateHistoryScenario.RunAsync(
            DelegateHistoryScenario.Definition(), DelegateHistoryScenario.Input());

        Assert.Equal(
            DelegateHistoryScenario.DelegateInputs(Fixture()),
            DelegateHistoryScenario.DelegateInputs(history));
    }

    /// <summary>
    /// A definition that adopts <c>"repo": "{{input.Repo}}"</c> but is started without one must
    /// behave exactly like a definition without the field.
    /// </summary>
    [Fact]
    public async Task ARepoTemplateThatResolvesBlank_AppendsNothing()
    {
        var history = await DelegateHistoryScenario.RunAsync(
            RepoDefinition("{{input.Repo}}"), DelegateHistoryScenario.Input());

        Assert.Equal(
            DelegateHistoryScenario.DelegateInputs(Fixture()),
            DelegateHistoryScenario.DelegateInputs(history));
    }

    /// <summary>
    /// With a repo: the step's own attempts — including the escalation retry, which is a
    /// <c>with</c> copy of the step — carry <c>0</c> (the default budget, needed only to reach the
    /// positional slot) and the repo. The engine-built escalation notification carries neither.
    /// The first five payloads of every delegation are unchanged.
    /// </summary>
    [Fact]
    public async Task ARepoStep_AppendsTheRepoToItsAttempts_ButNotToTheEscalationNotification()
    {
        var history = await DelegateHistoryScenario.RunAsync(
            RepoDefinition("{{input.Repo}}"),
            DelegateHistoryScenario.Input(new { Task = "the thing", Repo = "org/app" }));

        var expected = DelegateHistoryScenario.DelegateInputs(Fixture());
        var actual = DelegateHistoryScenario.DelegateInputs(history);

        Assert.Equal(4, actual.Count);
        Assert.Equal([7, 7, 5, 7], actual.Select(p => p.Payloads_.Count).ToArray());

        for (var i = 0; i < actual.Count; i++)
            Assert.Equal(expected[i].Payloads_, actual[i].Payloads_.Take(5));

        foreach (var attempt in new[] { actual[0], actual[1], actual[3] })
        {
            Assert.Equal(0, Decode<int>(attempt.Payloads_[5]));
            Assert.Equal("org/app", Decode<string>(attempt.Payloads_[6]));
        }
    }

    /// <summary>
    /// The typed callers schedule by name with their argument list spelled out, because an
    /// expression-tree call would have had the compiler append the new <c>repo</c> default. This
    /// pins the escalation-channel workflow to the six arguments it always sent.
    /// </summary>
    [Fact]
    public async Task NotifyCtoWorkflow_StillSchedulesItsSixArguments()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"notify-cto-{Guid.NewGuid():N}";
        var workflowId = $"notify-cto-{Guid.NewGuid():N}";

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<NotifyCtoWorkflow>()
                .AddActivity(ActivityDefinition.Create(
                    DelegateToAgentActivity.ActivityName,
                    typeof(AgentTaskResult),
                    [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int), typeof(int), typeof(string)],
                    3,
                    _ => new AgentTaskResult("ok", "completed"))));

        await worker.ExecuteAsync(() =>
            env.Client.ExecuteWorkflowAsync(
                (NotifyCtoWorkflow wf) => wf.RunAsync(new NotifyCtoWorkflowInput("agent-a", "heads up")),
                new WorkflowOptions(workflowId, taskQueue)));

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        var input = Assert.Single(DelegateHistoryScenario.DelegateInputs(history));

        Assert.Equal(6, input.Payloads_.Count);
        Assert.Equal("agent-a", Decode<string>(input.Payloads_[0]));
        Assert.Equal("heads up", Decode<string>(input.Payloads_[1]));
        Assert.Equal($"{workflowId}/notify", Decode<string>(input.Payloads_[2]));
        Assert.True(Decode<bool>(input.Payloads_[3]));
        Assert.Equal(3, Decode<int>(input.Payloads_[4]));
        Assert.Equal(0, Decode<int>(input.Payloads_[5]));
    }

    private static WorkflowDefinitionModel RepoDefinition(string repo)
    {
        var baseline = (SequenceStep)DelegateHistoryScenario.Definition().Root;
        return DelegateHistoryScenario.Definition(
            (DelegateStep)baseline.Steps[0] with { Repo = repo },
            (DelegateWithEscalationStep)baseline.Steps[1] with { Repo = repo });
    }

    private static T Decode<T>(Payload payload) =>
        DataConverter.Default.PayloadConverter.ToValue<T>(payload);
}
