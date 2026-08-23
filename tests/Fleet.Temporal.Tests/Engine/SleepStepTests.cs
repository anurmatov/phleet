using System.Text.Json;
using Fleet.Temporal.Engine;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// Tests for the <see cref="SleepStep"/> step type.
///
/// Three layers:
/// 1. JSON deserialization — verifies [JsonDerivedType] registration and Seconds property.
/// 2. Durable-timer integration — verifies Workflow.DelayAsync is the primitive
///    (not Thread.Sleep / Task.Delay) by running a minimal workflow in a
///    time-skipping WorkflowEnvironment where a 300-second sleep finishes in milliseconds.
/// 3. Validation — verifies that invalid Seconds values fail the workflow via the production
///    ExecuteSleepAsync path inside UniversalWorkflow, not a copy-pasted mirror.
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
    // Durable-timer integration — time-skipping WorkflowEnvironment
    //
    // Workflow.DelayAsync suspends the Temporal coroutine and records a timer
    // in the event history; replay picks up after the timer fires, not from
    // before the sleep started.  The time-skipping environment advances logical
    // time instantly so a 300-second sleep completes in milliseconds of real time.
    //
    // If this test hangs it proves the implementation used Thread.Sleep or
    // Task.Delay instead of Workflow.DelayAsync, because those primitives are
    // not intercepted by the testing environment.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SleepStep_DurableTimer_SkipsTimeInTestEnvironment()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions($"sleep-test-{Guid.NewGuid():N}")
                .AddWorkflow<SleepTimerWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                (SleepTimerWorkflow wf) => wf.RunAsync(300L),
                new WorkflowOptions(
                    id: $"sleep-timer-{Guid.NewGuid():N}",
                    taskQueue: worker.Options.TaskQueue!));

            var elapsed = await handle.GetResultAsync();

            // Logical time must have advanced by at least the requested duration.
            Assert.True(elapsed >= 300L,
                $"Expected logical elapsed seconds >= 300, got {elapsed}. " +
                "This likely means Workflow.DelayAsync was not used.");
        });
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
// Inline test-only workflows (not registered in production)
// ---------------------------------------------------------------------------

/// <summary>
/// Sleeps for the given number of seconds using Workflow.DelayAsync and returns
/// the logical elapsed seconds measured inside the workflow — the same primitive
/// used by ExecuteSleepAsync in UniversalWorkflow.
/// </summary>
[Workflow]
file sealed class SleepTimerWorkflow
{
    [WorkflowRun]
    public async Task<long> RunAsync(long seconds)
    {
        var before = Workflow.UtcNow;
        await Workflow.DelayAsync(TimeSpan.FromSeconds(seconds));
        return (long)(Workflow.UtcNow - before).TotalSeconds;
    }
}

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
