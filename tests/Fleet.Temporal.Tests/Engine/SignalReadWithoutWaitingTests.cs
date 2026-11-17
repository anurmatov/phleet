using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// Two things a definition may rely on in a <c>wait_for_signal</c> that ends as a timeout
/// (<c>autoCompleteOnTimeout</c>), with no notice, phase or visit:
///
/// <para>
/// With <c>timeoutMinutes: 0</c> it is a read without waiting. A signal that was buffered is
/// returned; otherwise <c>{"Decision":"timeout"}</c> is, in the same workflow task, and the step
/// emits no timer and no other command.
/// </para>
///
/// <para>
/// With a positive timeout it starts one timer of that length, exactly as a <c>sleep</c> of the
/// same length does, and a signal that arrives meanwhile ends it early.
/// </para>
///
/// The PR definition's merge hold reads late feedback this way; these pin the engine's side.
/// </summary>
public sealed class SignalReadWithoutWaitingTests
{
    private const string Signal = "example-signal";

    [Fact]
    public void AZeroTimeoutThatEndsAsATimeout_IsAValidDefinition()
    {
        var root = JsonSerializer.Deserialize<StepDefinition>("""
            {"type": "sequence", "steps": [
              {"type": "wait_for_signal", "name": "read", "signalName": "example-signal",
               "timeoutMinutes": 0, "autoCompleteOnTimeout": true, "outputVar": "late"}]}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        WorkflowDefinitionValidator.Validate(root, "example-workflow");

        var read = Assert.IsType<WaitForSignalStep>(Assert.Single(Assert.IsType<SequenceStep>(root).Steps));
        Assert.Equal(0, read.TimeoutMinutes);
        Assert.True(read.AutoCompleteOnTimeout);
    }

    [Fact]
    public async Task ZeroTimeout_NothingBuffered_ReturnsTimeoutAtOnce_WithNoTimerAndNoCommand()
    {
        var run = await RunAsync(
            [
                Tick("before"),
                Read(timeoutMinutes: 0),
                Result("{{vars.late.Decision}}"),
                Tick("after"),
            ]);

        Assert.Equal("timeout", run.Result);
        Assert.DoesNotContain(run.History.Events, e => e.EventType is EventType.TimerStarted or EventType.WorkflowExecutionSignaled);
        // Between the answer of the step before the read and the scheduling of the step after it
        // there is one workflow task and nothing else: the read ended inside it.
        Assert.Equal(
            [EventType.WorkflowTaskScheduled, EventType.WorkflowTaskStarted, EventType.WorkflowTaskCompleted],
            Between(run.History, "before", "after"));
    }

    [Fact]
    public async Task ZeroTimeout_BufferedSignal_IsReturnedAtOnce_AndIsGoneForTheNextRead()
    {
        var run = await RunAsync(
            [
                Tick("before"),
                Read(timeoutMinutes: 0, outputVar: "first"),
                Read(timeoutMinutes: 0, outputVar: "second"),
                Result("{{vars.first.Decision}}/{{vars.first.Comment}}/{{vars.second.Decision}}"),
                Tick("after"),
            ],
            async (handle, activities) =>
            {
                // While the first step is out no wait is registered: the signal is buffered.
                await activities.Started("before");
                await handle.SignalAsync(Signal, [new { Decision = "changes_requested", Comment = "late" }]);
                activities.Release("before");
            });

        Assert.Equal("changes_requested/late/timeout", run.Result);
        Assert.DoesNotContain(run.History.Events, e => e.EventType == EventType.TimerStarted);
        Assert.Equal(
            [EventType.WorkflowTaskScheduled, EventType.WorkflowTaskStarted, EventType.WorkflowTaskCompleted],
            Between(run.History, "before", "after").Where(t => t != EventType.WorkflowExecutionSignaled));
    }

    [Fact]
    public async Task FiveMinuteRead_StartsOneTimerOf300Seconds_AsASleepOfThatLengthDoes()
    {
        var read = await RunAsync([Read(timeoutMinutes: 5), Result("{{vars.late.Decision}}")]);
        var sleep = await RunAsync([new SleepStep { Name = "pause", Seconds = 300 }, Result("slept")]);

        Assert.Equal("timeout", read.Result);
        Assert.Equal([300L], TimerSeconds(read.History));
        Assert.Equal([300L], TimerSeconds(sleep.History));
        Assert.Single(read.History.Events, e => e.EventType == EventType.TimerFired);
        Assert.DoesNotContain(read.History.Events, e => e.EventType == EventType.TimerCanceled);
        // The same events as the sleep, in the same order: a timer and what the server adds around it.
        Assert.Equal(Shape(sleep.History), Shape(read.History));
    }

    [Fact]
    public async Task FiveMinuteRead_ASignalEndsItEarly_AndTheTimerIsCancelled()
    {
        var run = await RunAsync(
            [Read(timeoutMinutes: 5), Result("{{vars.late.Decision}}")],
            async (handle, _) =>
            {
                await WaitForAsync(handle, history => history.Events.Any(e => e.EventType == EventType.TimerStarted));
                await handle.SignalAsync(Signal, [new { Decision = "changes_requested" }]);
            });

        Assert.Equal("changes_requested", run.Result);
        Assert.Equal([300L], TimerSeconds(run.History));
        Assert.Single(run.History.Events, e => e.EventType == EventType.TimerCanceled);
        Assert.DoesNotContain(run.History.Events, e => e.EventType == EventType.TimerFired);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private sealed record Run(string? Result, WorkflowHistory History);

    private static WaitForSignalStep Read(int timeoutMinutes, string outputVar = "late") => new()
    {
        Name = "read",
        SignalName = Signal,
        TimeoutMinutes = timeoutMinutes,
        AutoCompleteOnTimeout = true,
        OutputVar = outputVar,
    };

    private static DelegateStep Tick(string name) => new() { Name = name, Target = "example-agent", Instruction = "work" };

    private static SetVariableStep Result(string template) => new() { Vars = new() { ["_result"] = template } };

    private static async Task<Run> RunAsync(
        StepDefinition[] steps, Func<WorkflowHandle, ReadActivities, Task>? drive = null)
    {
        var definition = new WorkflowDefinitionModel
        {
            Name = "example-workflow",
            Namespace = "default",
            TaskQueue = "test",
            Root = new SequenceStep { Steps = steps },
        };
        WorkflowDefinitionValidator.Validate(definition.Root, definition.Name);

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"read-{Guid.NewGuid():N}";
        var workflowId = $"read-{Guid.NewGuid():N}";
        var activities = new ReadActivities(definition, held: drive is not null);

        using var worker = new TemporalWorker(env.Client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<UniversalWorkflow>().AddAllActivities(activities));

        string? result = null;
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "example-workflow", Array.Empty<object?>(), new WorkflowOptions(id: workflowId, taskQueue: taskQueue));
            if (drive is not null) await drive(handle, activities);
            activities.ReleaseAll();
            result = await handle.GetResultAsync<string?>().WaitAsync(TimeSpan.FromSeconds(60));
        });

        return new Run(result, await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync());
    }

    private static async Task WaitForAsync(WorkflowHandle handle, Func<WorkflowHistory, bool> reached)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!reached(await handle.FetchHistoryAsync()))
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("the run did not reach the expected point");
            await Task.Delay(20);
        }
    }

    private static List<long> TimerSeconds(WorkflowHistory history) =>
        history.Events.Where(e => e.EventType == EventType.TimerStarted)
            .Select(e => e.TimerStartedEventAttributes.StartToFireTimeout.Seconds).ToList();

    /// <summary>The run's events after the two it always starts with (the definition and config loads).</summary>
    private static List<EventType> Shape(WorkflowHistory history)
    {
        var loads = history.Events.Where(e => e.EventType == EventType.ActivityTaskCompleted).Select(e => e.EventId).Take(2).ToList();
        return history.Events.Where(e => e.EventId > loads[^1]).Select(e => e.EventType).ToList();
    }

    /// <summary>The event types after the named delegation's answer and before the next one is scheduled.</summary>
    private static List<EventType> Between(WorkflowHistory history, string answered, string scheduled)
    {
        long Scheduled(string step) => history.Events.Single(e =>
            e.EventType == EventType.ActivityTaskScheduled
            && e.ActivityTaskScheduledEventAttributes.ActivityType.Name == DelegateToAgentActivity.ActivityName
            && DataConverter.Default.PayloadConverter
                .ToValue<string>(e.ActivityTaskScheduledEventAttributes.Input.Payloads_[2]).EndsWith("/" + step, StringComparison.Ordinal)).EventId;

        var from = history.Events.Single(e => e.EventType == EventType.ActivityTaskCompleted
            && e.ActivityTaskCompletedEventAttributes.ScheduledEventId == Scheduled(answered)).EventId;
        var to = Scheduled(scheduled);
        return history.Events.Where(e => e.EventId > from && e.EventId < to).Select(e => e.EventType).ToList();
    }

    /// <summary>
    /// The engine's two loads and a delegation that, when <paramref name="held"/>, does not answer
    /// until its step is released.
    /// </summary>
    private sealed class ReadActivities(WorkflowDefinitionModel definition, bool held)
    {
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _released = [];
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started(string step) => Gate(_started, step).Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Release(string step) => Gate(_released, step).TrySetResult();

        public void ReleaseAll() => _all.TrySetResult();

        [Activity("LoadWorkflowDefinition")]
        public WorkflowDefinitionModel LoadDefinition(string _) => definition;

        [Activity("LoadWorkflowConfig")]
        public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

        [Activity("DelegateToAgent")]
        public async Task<AgentTaskResult> DelegateAsync(
            string target, string instruction, string taskId, bool retryOnIncomplete, int maxRetries)
        {
            var step = taskId.Split('/')[^1];
            Gate(_started, step).TrySetResult();
            if (held)
                await Task.WhenAny(Gate(_released, step).Task, _all.Task).WaitAsync(TimeSpan.FromSeconds(30));
            return new AgentTaskResult("done", "completed");
        }

        private static TaskCompletionSource Gate(Dictionary<string, TaskCompletionSource> gates, string step)
        {
            lock (gates)
            {
                if (!gates.TryGetValue(step, out var gate))
                    gates[step] = gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return gate;
            }
        }
    }
}
