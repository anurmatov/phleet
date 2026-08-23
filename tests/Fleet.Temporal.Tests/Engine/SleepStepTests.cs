using System.Linq;
using System.Text.Json;
using Fleet.Temporal.Engine;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// Tests for the <see cref="SleepStep"/> step type.
///
/// Three layers:
/// 1. JSON deserialization — verifies [JsonDerivedType] registration and Seconds property,
///    including type-level rejection of strings and fractional numbers (M5).
/// 2. Durable-timer integration — drives production ExecuteSleepAsync inside UniversalWorkflow
///    via stub activities in a time-skipping WorkflowEnvironment; asserts that logical time
///    advances by the declared seconds, catching unit errors (M4).
/// 3. Validation — verifies that invalid Seconds values fail the workflow with a non-retryable
///    ApplicationFailureException via the production ExecuteSleepAsync path.
/// </summary>
public sealed class SleepStepTests
{
    // Production code (LoadWorkflowDefinitionActivity) deserializes step definitions with
    // JsonSerializerDefaults.Web, which enables camelCase property names and case-insensitive
    // matching. All JSON tests must use the same options so they test the actual production contract.
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    // -----------------------------------------------------------------------
    // JSON deserialization — [JsonDerivedType(typeof(SleepStep), "sleep")]
    // -----------------------------------------------------------------------

    [Fact]
    public void SleepStep_Deserializes_FromTypeDiscriminator()
    {
        var json = """{"type":"sleep","seconds":300}""";

        var step = JsonSerializer.Deserialize<StepDefinition>(json, WebOptions);

        var sleep = Assert.IsType<SleepStep>(step);
        Assert.Equal(300L, sleep.Seconds);
    }

    [Fact]
    public void SleepStep_MissingSeconds_DeserializesToNullSeconds()
    {
        var json = """{"type":"sleep"}""";

        var step = JsonSerializer.Deserialize<StepDefinition>(json, WebOptions);

        var sleep = Assert.IsType<SleepStep>(step);
        Assert.Null(sleep.Seconds);
    }

    [Fact]
    public void SleepStep_Serializes_WithTypeDiscriminator()
    {
        var step = new SleepStep { Seconds = 120L };

        var json = JsonSerializer.Serialize<StepDefinition>(step, WebOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("sleep", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(120L, doc.RootElement.GetProperty("seconds").GetInt64());
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(60L)]
    [InlineData(3600L)]
    [InlineData(2_592_000L)] // 30 days — upper ceiling
    public void SleepStep_ValidBoundaries_DeserializeToExpectedSeconds(long seconds)
    {
        var json = $$"""{"type":"sleep","seconds":{{seconds}}}""";

        var step = Assert.IsType<SleepStep>(JsonSerializer.Deserialize<StepDefinition>(json, WebOptions));

        Assert.Equal(seconds, step.Seconds);
    }

    // -----------------------------------------------------------------------
    // JSON type-rejection — M5
    //
    // JsonSerializerDefaults.Web enables AllowReadingFromString, which would
    // silently coerce "seconds":"300" (string) into Seconds=300 and pass the
    // range check.  [JsonNumberHandling(Strict)] on SleepStep.Seconds overrides
    // this for the Seconds property, so type errors surface at definition load
    // time rather than at step execution where ignoreFailure could suppress them.
    //
    // Fractional seconds (1.5) throw JsonException at deserialization because
    // long? cannot represent a fractional value; this behaviour predates the
    // Strict fix but is documented here alongside the string case for clarity.
    // -----------------------------------------------------------------------

    [Fact]
    public void SleepStep_StringSeconds_DeserializationFails()
    {
        // "300" as a JSON string must be rejected even under Web defaults.
        var json = """{"type":"sleep","seconds":"300"}""";

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<StepDefinition>(json, WebOptions));
    }

    [Fact]
    public void SleepStep_FractionalSeconds_DeserializationFails()
    {
        // 1.5 cannot be stored as long? — rejected at load time.
        var json = """{"type":"sleep","seconds":1.5}""";

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<StepDefinition>(json, WebOptions));
    }

    // -----------------------------------------------------------------------
    // Durable-timer integration — M4
    //
    // Drives the actual Workflow.DelayAsync call in production ExecuteSleepAsync
    // (UniversalWorkflow.cs:607) through SleepTestActivities stubs.
    //
    // The time-skipping WorkflowEnvironment advances logical time instantly when
    // the workflow coroutine is suspended on a timer; Thread.Sleep and Task.Delay
    // are not intercepted and would hang.  Measuring logical elapsed time with
    // env.GetCurrentTimeAsync() before and after the run catches unit errors:
    //   - below 300s → FromMilliseconds used instead of FromSeconds
    //   - above 600s → FromMinutes used instead of FromSeconds
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SleepStep_ValidSeconds_ProductionTimerFires()
    {
        // Drives the actual Workflow.DelayAsync call in production ExecuteSleepAsync
        // (UniversalWorkflow.cs:607) through the time-skipping environment.
        //
        // Assertion strategy: inspect the workflow history after completion.
        // The single TimerStarted event in the history is produced by Workflow.DelayAsync;
        // its StartToFireTimeout must be exactly 300 seconds.
        //
        // This catches the FromMinutes(seconds) mutation (would write "18000s" to history)
        // and the FromMilliseconds(seconds) mutation (would write "0.3s").  Neither would
        // pass the exact TimeSpan.FromSeconds(300) equality check.
        //
        // Why not use env.GetCurrentTimeAsync() for elapsed?
        // Temporal's embedded test server registers a 10-year execution-timeout timer
        // for the workflow when no explicit ExecutionTimeout is set.  The auto-time-skip
        // fires that timer too, advancing logical time by 10 years — making "elapsed"
        // appear as ~315 000 000 s regardless of the sleep duration.
        var definition = new WorkflowDefinitionModel
        {
            Name      = "test-sleep",
            Namespace = "default",
            TaskQueue = "test",
            Root      = new SleepStep { Seconds = 300L }
        };

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"sleep-valid-{Guid.NewGuid():N}";
        var workflowId = $"sleep-valid-{Guid.NewGuid():N}";

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(new SleepTestActivities(definition)));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "test-sleep",
                Array.Empty<object?>(),
                new WorkflowOptions(id: workflowId, taskQueue: taskQueue));

            await handle.GetResultAsync();
        });

        // Fetch history outside ExecuteAsync so the auto-time-skip is already done.
        var handle = env.Client.GetWorkflowHandle(workflowId);
        var history = await handle.FetchHistoryAsync();

        // Only one TimerStarted event exists in the history; it is the one produced
        // by Workflow.DelayAsync — activity timeouts use different event types.
        var timerEvents = history.Events
            .Where(e => e.EventType == EventType.TimerStarted)
            .ToList();

        Assert.Single(timerEvents);

        var timerTimeout = timerEvents[0].TimerStartedEventAttributes.StartToFireTimeout.ToTimeSpan();
        Assert.Equal(TimeSpan.FromSeconds(300), timerTimeout);
    }

    // -----------------------------------------------------------------------
    // Validation — invalid Seconds values are rejected by the production
    // ExecuteSleepAsync inside UniversalWorkflow.  The test drives real
    // production code via stub activities, so it would catch a regression
    // in the production path even if the message or exception type changed.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(null,         "null")]
    [InlineData(0L,           "0")]
    [InlineData(-1L,          "-1")]
    [InlineData(2_592_001L,   "2592001")] // one above ceiling
    public async Task SleepStep_InvalidSeconds_ProductionWorkflowFails(long? seconds, string displayValue)
    {
        // Build a workflow definition whose only step is a sleep with an invalid seconds value.
        // SleepTestActivities stubs the LoadWorkflowDefinition activity so UniversalWorkflow
        // receives this definition without needing the orchestrator REST endpoint.
        var definition = new WorkflowDefinitionModel
        {
            Name      = "test-sleep",
            Namespace = "default",
            TaskQueue = "test",
            Root      = new SleepStep { Seconds = seconds }
        };

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"sleep-invalid-{Guid.NewGuid():N}";

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(new SleepTestActivities(definition)));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "test-sleep",
                Array.Empty<object?>(),
                new WorkflowOptions(
                    id: $"sleep-invalid-{Guid.NewGuid():N}",
                    taskQueue: taskQueue)
                {
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 }
                });

            var ex = await Assert.ThrowsAsync<WorkflowFailedException>(
                () => handle.GetResultAsync());

            // The production ExecuteSleepAsync must throw ApplicationFailureException(nonRetryable: true).
            // A plain InvalidOperationException would be wrapped with non_retryable=false, causing
            // indefinite Temporal task retries — the time-skipping environment would hang.
            var cause = Assert.IsType<ApplicationFailureException>(ex.InnerException);
            Assert.True(cause.NonRetryable,
                "ExecuteSleepAsync must throw ApplicationFailureException(nonRetryable: true). " +
                "A non-retryable=false exception causes indefinite task retries and wedges the workflow.");

            Assert.Contains("seconds", cause.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(displayValue, cause.Message);
        });
    }
}

// ---------------------------------------------------------------------------
// Test-only activity stubs
// ---------------------------------------------------------------------------

/// <summary>
/// Stub activities for the validation tests.  Returns a pre-baked WorkflowDefinitionModel
/// containing the step under test so UniversalWorkflow can run without the orchestrator.
/// </summary>
file sealed class SleepTestActivities(WorkflowDefinitionModel definition)
{
    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });
}
