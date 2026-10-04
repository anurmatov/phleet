using System.Collections.Concurrent;
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
/// #424 — <c>delegate.statusVar</c>: the delegation's completion status, visible to the definition.
///
/// <para>
/// Drives the production <see cref="UniversalWorkflow"/> in a time-skipping
/// <see cref="WorkflowEnvironment"/> with stub activities, the pattern of <c>SleepStepTests</c>.
/// The status reaches the definition exactly as the relay produced it (trimmed, lower-cased, never
/// mapped), and it is reset to <c>unknown</c> before every attempt, so a thrown attempt can never
/// leave a previous attempt's <c>completed</c> behind.
/// </para>
/// </summary>
public sealed class DelegateStatusVarTests
{
    // ── AC1: the token is stored exactly, after trim + lower-case ────────────

    [Theory]
    [InlineData("completed", "completed")]
    [InlineData("incomplete", "incomplete")]
    [InlineData("failed", "failed")]
    [InlineData("idle", "idle")]
    [InlineData("custom_token", "custom_token")]
    [InlineData(" FAILED ", "failed")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    public async Task TheRelayStatus_IsStoredExactly(string? status, string expected)
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                Delegate("work", statusVar: "work_status"),
                new SetVariableStep { Vars = new() { ["_result"] = "{{vars.work_status}}" } },
            ],
        });
        var activities = new StatusVarActivities(definition);
        activities.Script("work", Returns("agent text", status));

        var (result, _) = await RunAsync(activities);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("completed", "completed")]
    [InlineData(" Incomplete\t", "incomplete")]
    [InlineData("SOMETHING_NEW", "something_new")]
    [InlineData(null, "unknown")]
    [InlineData("   ", "unknown")]
    public void NormalizeStatus_TrimsAndLowerCases_WithoutMapping(string? status, string expected) =>
        Assert.Equal(expected, UniversalWorkflow.NormalizeStatus(status));

    // ── AC2: statusVar changes nothing else ──────────────────────────────────

    /// <summary>
    /// The same definition with and without <c>statusVar</c>: the stored <c>outputVar</c> is
    /// byte-identical (it is the workflow result here) and the scheduled <c>DelegateToAgent</c>
    /// input is identical, for the five-argument form and the seven-argument <c>repo</c> form. The
    /// idle case covers <c>"[status: idle]"</c>.
    /// </summary>
    [Theory]
    [InlineData(null, "agent text", "completed")]
    [InlineData(null, "", "idle")]
    [InlineData("owner/repo", "agent text", "completed")]
    [InlineData("owner/repo", "", "idle")]
    public async Task AddingStatusVar_ChangesNeitherOutputVarNorTheActivityInput(string? repo, string text, string status)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        const string workflowId = "example-statusvar-parity";

        async Task<(Payload Result, Payloads Input)> RunOnce(string? statusVar)
        {
            var definition = Definition(Delegate("work", outputVar: "_result", statusVar: statusVar) with { Repo = repo });
            var activities = new StatusVarActivities(definition);
            activities.Script("work", Returns(text, status));
            var taskQueue = $"statusvar-parity-{Guid.NewGuid():N}";

            using var worker = Worker(env, taskQueue, activities);
            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    "example-workflow", Array.Empty<object?>(), new WorkflowOptions(workflowId, taskQueue));
                await handle.GetResultAsync();
            });

            var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
            var result = history.Events.Single(e => e.EventType == EventType.WorkflowExecutionCompleted)
                .WorkflowExecutionCompletedEventAttributes.Result.Payloads_.Single();
            return (result, Assert.Single(DelegateInputs(history)));
        }

        var without = await RunOnce(statusVar: null);
        var with = await RunOnce(statusVar: "work_status");

        Assert.Equal(without.Result, with.Result);
        Assert.Equal(without.Input, with.Input);
        Assert.Equal(repo is null ? 5 : 7, with.Input.Payloads_.Count);
    }

    // ── AC3: a thrown attempt reads unknown, never a stale value ─────────────

    [Fact]
    public async Task AThrownAttempt_UnderIgnoreFailure_LeavesUnknown()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                new SequenceStep { IgnoreFailure = true, Steps = [Delegate("work", statusVar: "work_status")] },
                new SetVariableStep { Vars = new() { ["_result"] = "{{vars.work_status}}" } },
            ],
        });
        var activities = new StatusVarActivities(definition);
        activities.Script("work", Throws());

        var (result, _) = await RunAsync(activities);

        Assert.Equal("unknown", result);
    }

    /// <summary>
    /// Iteration 1 returns <c>completed</c>, iteration 2 throws. The trail records the value after
    /// each iteration, so a stale <c>completed</c> after iteration 2 cannot hide.
    /// </summary>
    [Fact]
    public async Task ALoopWhoseSecondAttemptThrows_ReadsUnknown_NotTheStaleCompleted()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                new LoopStep
                {
                    MaxIterations = 2,
                    Steps =
                    [
                        new SequenceStep { IgnoreFailure = true, Steps = [Delegate("work", statusVar: "work_status")] },
                        new SetVariableStep { Vars = new() { ["trail"] = "{{vars.trail}}|{{vars.work_status}}" } },
                    ],
                },
                new SetVariableStep { Vars = new() { ["_result"] = "{{vars.trail}}" } },
            ],
        });
        var activities = new StatusVarActivities(definition);
        activities.Script("work", Returns("first", "completed"), Throws());

        var (result, _) = await RunAsync(activities);

        Assert.Equal("|completed|unknown", result);
    }

    // ── AC4: delegate_with_escalation resets per attempt ─────────────────────

    /// <summary>
    /// Attempt 1 throws, the escalation is answered, and the final value is read as the workflow
    /// result. <c>statusVar</c> is <c>_result</c> here because <c>skip</c> stops every later step,
    /// so a following <c>set_variable</c> could never report it. <c>retry</c> → attempt 2's
    /// <c>completed</c>; <c>skip</c> → the thrown attempt's <c>unknown</c>, untouched by the
    /// escalation. One notification and one signal either way, as in <c>EscalationSignalBufferTests</c>.
    /// </summary>
    [Theory]
    [InlineData("retry", "completed")]
    [InlineData("skip", "unknown")]
    public async Task Escalation_ResetsPerAttempt(string decision, string expected)
    {
        var definition = Definition(new DelegateWithEscalationStep
        {
            Name = "flaky_step",
            Target = "agent1",
            Instruction = "do the thing",
            OutputVar = "flaky_out",
            StatusVar = "_result",
        });
        var activities = new StatusVarActivities(definition);
        activities.Script("flaky_step", Throws(), Returns("done", "completed"));

        var (result, history) = await RunAsync(activities, async handle =>
        {
            await activities.EscalationNotified.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await handle.SignalAsync("escalation-decision", [JsonSerializer.SerializeToElement(new { Decision = decision })]);
        });

        Assert.Equal(expected, result);
        Assert.Single(activities.Calls, name => name.EndsWith("_escalation_notify", StringComparison.Ordinal));
        Assert.Single(history.Events, e => e.EventType == EventType.WorkflowExecutionSignaled);
        Assert.Equal(decision == "retry" ? 2 : 1, activities.Calls.Count(name => name == "flaky_step"));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static DelegateStep Delegate(string name, string? outputVar = null, string? statusVar = null) => new()
    {
        Name = name,
        Target = "agent1",
        Instruction = "do the thing",
        OutputVar = outputVar,
        StatusVar = statusVar,
    };

    private static WorkflowDefinitionModel Definition(StepDefinition root) => new()
    {
        Name = "example-workflow",
        Namespace = "default",
        TaskQueue = "test",
        Root = root,
    };

    private static Func<AgentTaskResult> Returns(string text, string? status) => () => new AgentTaskResult(text, status!);

    private static Func<AgentTaskResult> Throws() =>
        () => throw new ApplicationFailureException("delegate failed on purpose", nonRetryable: true);

    private static IReadOnlyList<Payloads> DelegateInputs(WorkflowHistory history) =>
        history.Events
            .Where(e => e.ActivityTaskScheduledEventAttributes?.ActivityType?.Name == "DelegateToAgent")
            .Select(e => e.ActivityTaskScheduledEventAttributes.Input)
            .ToList();

    private static TemporalWorker Worker(WorkflowEnvironment env, string taskQueue, object activities) =>
        new(env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

    private static async Task<(string? Result, WorkflowHistory History)> RunAsync(
        object activities,
        Func<WorkflowHandle, Task>? drive = null)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        // The escalation branch upserts the Phase search attribute.
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(new AddSearchAttributesRequest
        {
            Namespace = env.Client.Options.Namespace,
            SearchAttributes = { ["Phase"] = IndexedValueType.Keyword },
        });

        var taskQueue = $"statusvar-{Guid.NewGuid():N}";
        var workflowId = $"statusvar-{Guid.NewGuid():N}";
        string? result = null;

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "example-workflow",
                Array.Empty<object?>(),
                new WorkflowOptions(workflowId, taskQueue) { RetryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 1 } });
            if (drive is not null) await drive(handle);
            result = await handle.GetResultAsync<string?>().WaitAsync(TimeSpan.FromSeconds(60));
        });

        return (result, await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync());
    }
}

// ---------------------------------------------------------------------------
// Test-only activity stubs
// ---------------------------------------------------------------------------

/// <summary>
/// Stubs the two start-up loads and the agent delegation. Each step name gets a script of
/// outcomes, consumed one per call; the escalation notification always succeeds.
/// </summary>
file sealed class StatusVarActivities(WorkflowDefinitionModel definition)
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<AgentTaskResult>>> _scripts = new();
    private readonly ConcurrentQueue<string> _calls = new();

    public TaskCompletionSource EscalationNotified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Calls => _calls.ToList();

    public void Script(string step, params Func<AgentTaskResult>[] outcomes) =>
        _scripts[step] = new ConcurrentQueue<Func<AgentTaskResult>>(outcomes);

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() =>
        JsonSerializer.SerializeToElement(new { EscalationTarget = "example-escalation-target" });

    [Activity("DelegateToAgent")]
    public AgentTaskResult Delegate(
        string target, string instruction, string taskId, bool retryOnIncomplete, int maxIncompleteRetries,
        int agentBudgetSeconds = 0, string? repo = null)
    {
        var step = taskId.Split('/').LastOrDefault() ?? taskId;
        _calls.Enqueue(step);

        if (step.EndsWith("_escalation_notify", StringComparison.Ordinal))
        {
            EscalationNotified.TrySetResult();
            return new AgentTaskResult("notified", "completed");
        }

        return _scripts.TryGetValue(step, out var script) && script.TryDequeue(out var next)
            ? next()
            : new AgentTaskResult("unscripted", "completed");
    }
}
