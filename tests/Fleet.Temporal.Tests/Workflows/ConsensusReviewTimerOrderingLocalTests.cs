using System.Text;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Fleet.Temporal.Workflows.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Workflows;

/// <summary>
/// The end-to-end shape, against a REAL Temporal server with REAL timers.
///
/// Every other test here runs under <c>StartTimeSkippingAsync</c>, where the server jumps its
/// clock whenever the workflow is idle. That is exactly the wrong environment for this change:
/// the defect is an ordering between two wall-clock timers — the activity's own agent budget and
/// the <c>StartToCloseTimeout</c> Temporal enforces — and a server that skips time can fire the
/// second one while the activity is genuinely working, which is the failure being fixed.
///
/// So: <c>StartLocalAsync</c>, a 20-second budget, and roughly a minute of real wall clock. The
/// activity under test is the production <see cref="DelegateToAgentActivity"/>; only the two
/// edges that leave the process are substituted (RabbitMQ publish, the orchestrator HTTP client),
/// so the registry, the heartbeat loop, the cancellation wiring and the timeout path are all real.
/// </summary>
[Collection("consensus-local-timers")]
public class ConsensusReviewTimerOrderingLocalTests : IClassFixture<LocalTemporalFixture>
{
    private const string Reviewer = "reviewer-one";
    private const int BudgetSeconds = 20;

    private readonly LocalTemporalFixture _fixture;

    public ConsensusReviewTimerOrderingLocalTests(LocalTemporalFixture fixture) => _fixture = fixture;

    private static string ApprovedResponse() => string.Join("\n",
        "Detailed body.",
        "SUMMARY: nothing blocking",
        "EVIDENCE: none",
        "BLOCKER: none",
        "VERDICT: approved");

    private static ConsensusReviewInput Input() =>
        new("a change under review", "review it", [Reviewer], null, "synthesizer");

    [Fact]
    public async Task AReviewerThatWorksForMostOfItsBudget_CompletesOnTheFirstAttempt()
    {
        // 14 seconds of work inside a 20-second budget, inside a 20s + 2min container. Under the
        // old options this is the case that broke: a hard-coded 15-minute container over a
        // 90-minute budget killed the attempt while the reviewer was still typing.
        var h = LocalHarness.Build();
        var workflowId = $"consensus-local-ok-{Guid.NewGuid():N}";
        var reviewTaskId = $"{workflowId}/review-{Reviewer}";

        using var worker = h.BuildWorker(_fixture.Client, BudgetSeconds);

        var output = await worker.ExecuteAsync(async () =>
        {
            var answering = h.AnswerAfterAsync(
                reviewTaskId, TimeSpan.FromSeconds(14),
                new AgentTaskResult(ApprovedResponse(), "completed"));

            var result = await _fixture.Client.ExecuteWorkflowAsync(
                (ConsensusReviewWorkflow wf) => wf.RunAsync(Input()),
                new WorkflowOptions(workflowId, h.TaskQueue));

            await answering;
            return result;
        });

        Assert.Equal(ReviewVerdict.Approved, output.FinalVerdict);

        // One publish for this taskId means one attempt: a Temporal retry would republish, and
        // the activity's own 5-minute re-send cannot fire inside 20 seconds.
        Assert.Equal(1, h.PublishCountFor(reviewTaskId));

        // A healthy reviewer was never told to stop.
        Assert.Empty(h.CancelRequests);
    }

    [Fact]
    public async Task AReviewerThatNeverAnswers_FailsOnceWithMeasuredElapsedTime()
    {
        // The activity must lose its own race before Temporal loses patience, so the failure
        // carries an explanation and a measured number instead of an unexplained kill. And with
        // MaximumAttempts = 1 it happens exactly once — unbounded retry is what made a review
        // unfinishable rather than merely slow.
        var h = LocalHarness.Build();
        var workflowId = $"consensus-local-timeout-{Guid.NewGuid():N}";
        var reviewTaskId = $"{workflowId}/review-{Reviewer}";

        using var worker = h.BuildWorker(_fixture.Client, BudgetSeconds);

        var ex = await worker.ExecuteAsync(async () =>
            await Assert.ThrowsAsync<WorkflowFailedException>(() =>
                _fixture.Client.ExecuteWorkflowAsync(
                    (ConsensusReviewWorkflow wf) => wf.RunAsync(Input()),
                    new WorkflowOptions(workflowId, h.TaskQueue))));

        var activityFailure = Assert.IsType<ActivityFailureException>(ex.InnerException);
        var appFailure = Assert.IsType<ApplicationFailureException>(activityFailure.InnerException);

        Assert.Contains("did not respond", appFailure.Message);
        Assert.Contains("elapsed", appFailure.Message);
        Assert.Contains("budget 20.0s", appFailure.Message);

        Assert.Equal(1, h.PublishCountFor(reviewTaskId));
    }

    // Cancellation is NOT asserted here, deliberately. Temporal delivers an activity cancel in
    // the response to a heartbeat, and the wait loop heartbeats every 30 seconds — so inside a
    // 20-second budget the activity provably cannot observe one, and a test written here would be
    // asserting the heartbeat cadence rather than the cancellation path. It lives in
    // DelegateToAgentActivityTests instead, where the token can be signalled directly:
    // Cancellation_SendsATargetedCancelToTheOrchestrator.
    //
    // (Worth knowing operationally: a cancel issued against a delegate is noticed at the next
    // heartbeat, up to 30 seconds later. That is pre-existing behaviour, unchanged here.)

    [Fact]
    public async Task AFiveArgumentCaller_StillBindsToTheActivity()
    {
        // UWE's delegate step invokes "DelegateToAgent" by NAME with five positional payloads and
        // knows nothing about the budget parameter. Adding a sixth parameter breaks that kind of
        // caller at runtime rather than at compile time, so the binding is asserted against a real
        // worker rather than inferred from the method signature.
        var h = LocalHarness.Build();
        var workflowId = $"five-arg-{Guid.NewGuid():N}";
        var taskId = $"{workflowId}/five-arg";

        using var worker = h.BuildWorker(_fixture.Client, BudgetSeconds, extra => extra
            .AddWorkflow<FiveArgDelegateDouble>());

        var text = await worker.ExecuteAsync(async () =>
        {
            var answering = h.AnswerAfterAsync(
                taskId, TimeSpan.Zero, new AgentTaskResult("bound fine", "completed"));

            var result = await _fixture.Client.ExecuteWorkflowAsync(
                (FiveArgDelegateDouble wf) => wf.RunAsync(Reviewer),
                new WorkflowOptions(workflowId, h.TaskQueue));

            await answering;
            return result;
        });

        Assert.Equal("bound fine", text);
    }
}

/// <summary>
/// Starts one real Temporal server for the whole class. Starting it per test would triple a
/// startup that is already the slowest part of this file.
/// </summary>
public sealed class LocalTemporalFixture : IAsyncLifetime
{
    private WorkflowEnvironment _env = null!;

    public ITemporalClient Client => _env.Client;

    public async Task InitializeAsync() => _env = await WorkflowEnvironment.StartLocalAsync();

    public async Task DisposeAsync() => await _env.DisposeAsync();
}

/// <summary>
/// The production activity with only its two outbound edges substituted, plus the bookkeeping the
/// assertions read: what was published, and what was asked to cancel.
/// </summary>
internal sealed class LocalHarness
{
    private readonly List<string> _publishedTaskIds = [];
    private readonly Lock _gate = new();

    public required DelegateToAgentActivity Activity { get; init; }
    public required TaskCompletionRegistry Registry { get; init; }
    public required List<string> CancelRequests { get; init; }
    public string TaskQueue { get; } = $"consensus-local-{Guid.NewGuid():N}";

    public int PublishCountFor(string taskId)
    {
        lock (_gate) return _publishedTaskIds.Count(t => t == taskId);
    }

    public static LocalHarness Build()
    {
        var cancelRequests = new List<string>();
        var harnessRef = new LocalHarness[1];

        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var json = Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("TaskId", out var taskId))
                    harnessRef[0].RecordPublish(taskId.GetString() ?? "");
                return ValueTask.CompletedTask;
            });

        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));

        var connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(connection));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new RecordingHandler(cancelRequests)));

        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);

        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            // Deliberately NOT the budget under test: if the activity fell back to config the
            // waits below would be an hour, not twenty seconds.
            Options.Create(new TemporalBridgeOptions
            {
                AgentTimeoutSeconds = 3600,
                OrchestratorUrl = "http://orchestrator.invalid",
            }),
            registry,
            connectionFactory,
            httpClientFactory,
            NullLogger<DelegateToAgentActivity>.Instance);

        var harness = new LocalHarness
        {
            Activity = activity,
            Registry = registry,
            CancelRequests = cancelRequests,
        };
        harnessRef[0] = harness;
        return harness;
    }

    private void RecordPublish(string taskId)
    {
        lock (_gate) _publishedTaskIds.Add(taskId);
    }

    public TemporalWorker BuildWorker(
        ITemporalClient client, int budgetSeconds, Action<TemporalWorkerOptions>? configure = null)
    {
        var options = new TemporalWorkerOptions(TaskQueue)
            .AddAllActivities(Activity)
            .AddActivity(ActivityDefinition.Create(
                "LoadAgentBudgetSeconds", typeof(int), [], 0, _ => budgetSeconds))
            .AddWorkflow<ConsensusReviewWorkflow>();

        configure?.Invoke(options);
        return new TemporalWorker(client, options);
    }

    /// <summary>Waits until the activity has published for this taskId, then answers after a delay.</summary>
    public async Task AnswerAfterAsync(string taskId, TimeSpan delay, AgentTaskResult result)
    {
        await WaitForPublishAsync(taskId);
        if (delay > TimeSpan.Zero) await Task.Delay(delay);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Registry.TryComplete(taskId, result)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"taskId {taskId} was never registered by the activity");
    }

    public async Task WaitForPublishAsync(string taskId, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (PublishCountFor(taskId) > 0) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"no directive was published for taskId {taskId}");
    }

    private sealed class RecordingHandler(List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests) requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}

/// <summary>
/// Stands in for a UWE delegate step: binds "DelegateToAgent" by name with five positional
/// payloads and no budget, exactly as <c>UniversalWorkflow.ExecuteDelegateAsync</c> does.
/// </summary>
[Workflow("FiveArgDelegateDouble")]
public class FiveArgDelegateDouble
{
    [WorkflowRun]
    public async Task<string> RunAsync(string agent)
    {
        var result = await Workflow.ExecuteActivityAsync<AgentTaskResult>(
            "DelegateToAgent",
            [agent, "do the thing", $"{Workflow.Info.WorkflowId}/five-arg", true, 3],
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(60),
                HeartbeatTimeout = TimeSpan.FromMinutes(2),
                RetryPolicy = new() { MaximumAttempts = 1 },
            });

        return result.Text;
    }
}
