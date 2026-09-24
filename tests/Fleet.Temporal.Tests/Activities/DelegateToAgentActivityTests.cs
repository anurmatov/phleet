using System.Text;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests.Activities;

/// <summary>
/// Cancellation, timeout, heartbeat and incomplete-response retry, exercised through the REAL
/// <see cref="DelegateToAgentActivity"/> rather than the workflow-level stub.
///
/// The consensus change deliberately touches none of this — the whole point of extending the
/// marker-line convention was that the transport stays untouched. That claim is worth an
/// assertion rather than a promise, and this file is the assertion: it drives the actual
/// activity through <see cref="ActivityEnvironment"/>, so the heartbeat calls, the cancellation
/// token wiring and the continuation-prompt loop are the production ones.
///
/// Only the two outbound edges are substituted — the RabbitMQ connection and the HTTP client —
/// because those leave the process. Everything between them is real, including
/// <see cref="TaskCompletionRegistry"/>, which is how a response is delivered back.
/// </summary>
public class DelegateToAgentActivityTests
{
    private const string Agent = "reviewer-one";

    private sealed class Harness
    {
        public required DelegateToAgentActivity Activity { get; init; }
        public required TaskCompletionRegistry Registry { get; init; }
        public required List<string> PublishedInstructions { get; init; }
        public required List<string> PublishedTaskIds { get; init; }
        public required List<string?> PublishedRepos { get; init; }
        public required List<string> OrchestratorRequests { get; init; }
    }

    /// <summary>Records the orchestrator calls the activity makes instead of sending them.</summary>
    private sealed class RecordingHandler(List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests) requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static Harness Build(int agentTimeoutSeconds = 30, string orchestratorUrl = "")
    {
        var publishedInstructions = new List<string>();
        var publishedTaskIds = new List<string>();
        var publishedRepos = new List<string?>();

        // A RabbitMQ channel that records what was published instead of sending it.
        var channel = Substitute.For<IChannel>();
        // The production call is the GENERIC overload with BasicProperties; substituting the
        // non-generic one silently intercepts nothing and the capture list stays empty.
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var json = Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                // RelayMessage carries the instruction in Text, and the correlation in TaskId.
                if (root.TryGetProperty("Text", out var text))
                    publishedInstructions.Add(text.GetString() ?? "");
                if (root.TryGetProperty("TaskId", out var taskId))
                    publishedTaskIds.Add(taskId.GetString() ?? "");
                // #347: the repo is a structured field, null when the delegation has none.
                publishedRepos.Add(root.TryGetProperty("Repo", out var repo) ? repo.GetString() : null);
                return ValueTask.CompletedTask;
            });

        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));

        var connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(connection));

        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);

        var orchestratorRequests = new List<string>();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new RecordingHandler(orchestratorRequests)));

        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions
            {
                AgentTimeoutSeconds = agentTimeoutSeconds,
                OrchestratorUrl = orchestratorUrl,
            }),
            registry,
            connectionFactory,
            httpClientFactory,
            NullLogger<DelegateToAgentActivity>.Instance);

        return new Harness
        {
            Activity = activity,
            Registry = registry,
            PublishedInstructions = publishedInstructions,
            PublishedTaskIds = publishedTaskIds,
            PublishedRepos = publishedRepos,
            OrchestratorRequests = orchestratorRequests,
        };
    }

    /// <summary>
    /// Completes a registered task once the activity has actually registered it. Polling rather
    /// than a fixed delay so the test does not depend on scheduler timing.
    /// </summary>
    private static async Task CompleteWhenRegisteredAsync(
        Harness h, string taskId, AgentTaskResult result, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (h.Registry.TryComplete(taskId, result)) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"taskId {taskId} was never registered by the activity");
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompletedResponse_IsReturnedVerbatim()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-1"));

        await CompleteWhenRegisteredAsync(h, "wf/task-1", new AgentTaskResult("all done", "completed"));
        var result = await run;

        Assert.Equal("all done", result.Text);
        Assert.True(result.IsCompleted);
        // The activity wraps the instruction with the [fleet-wf:Type:ID] provenance tag and the
        // memory-search line, so assert containment rather than equality.
        Assert.Contains("do the thing", Assert.Single(h.PublishedInstructions));
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_IsEmittedWhileWaitingForTheAgent()
    {
        // Temporal kills an activity that stops heartbeating past its HeartbeatTimeout, so a
        // silent regression here would surface as agents timing out mid-review.
        // The agent timeout must outlast the 31s wait below. At the default 30s the activity
        // now times out first (correctly, since #251 was fixed) and cancels the registry entry —
        // this test only passed before because the timeout did not fire promptly.
        var h = Build(agentTimeoutSeconds: 120);
        var heartbeats = new List<object?[]>();
        var env = new ActivityEnvironment { Heartbeater = details => heartbeats.Add(details) };

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-hb"));

        // The wait loop heartbeats on a 30s cadence, so this has to outlast one tick. Slow by
        // nature: the interval is the production value and the test asserts the real call.
        await Task.Delay(TimeSpan.FromSeconds(31));
        await CompleteWhenRegisteredAsync(h, "wf/task-hb", new AgentTaskResult("done", "completed"));
        await run;

        Assert.NotEmpty(heartbeats);
        Assert.Contains(heartbeats,
            d => d.Length > 0 && (d[0]?.ToString() ?? "").Contains("wf/task-hb"));
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        var h = Build();
        using var cts = new CancellationTokenSource();
        var env = new ActivityEnvironment { CancellationTokenSource = cts };

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-cancel"));

        // Wait until the activity is genuinely waiting, then cancel.
        var deadline = Environment.TickCount64 + 5000;
        while (h.PublishedInstructions.Count == 0 && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        Assert.NotEmpty(h.PublishedInstructions);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Cancellation_SendsATargetedCancelToTheOrchestrator()
    {
        // The cancellation path is not what broke in #321 and must not be removed — cancelling a
        // genuinely hung agent is correct, and the fix only stops it firing on a healthy one. So
        // assert it still reaches the orchestrator, for THIS task rather than the whole agent.
        var h = Build(orchestratorUrl: "http://orchestrator.invalid");
        using var cts = new CancellationTokenSource();
        var env = new ActivityEnvironment { CancellationTokenSource = cts };

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-cancel-http"));

        var deadline = Environment.TickCount64 + 5000;
        while (h.PublishedInstructions.Count == 0 && Environment.TickCount64 < deadline)
            await Task.Delay(10);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var cancel = Assert.Single(h.OrchestratorRequests);
        Assert.Contains($"/api/agents/{Agent}/cancel/", cancel);
        Assert.Contains(Uri.EscapeDataString("wf/task-cancel-http"), cancel);

        // Targeted, not the blunt broadcast — the RabbitMQ fallback would have published a
        // "/cancel all" directive, which stops every other task on that agent too.
        Assert.DoesNotContain(h.PublishedInstructions, i => i.Contains("/cancel all"));
    }

    // ── Timeout (issue #251) ──────────────────────────────────────────────────

    [Fact]
    public async Task NoResponseWithinTheAgentTimeout_ThrowsPromptly()
    {
        // Regression test for #251. Before the fix this HUNG rather than failed: once the timeout
        // cancelled the token, Task.Delay returned an already-cancelled task each iteration and
        // Task.WhenAny does not throw on one, so the loop spun with no delay until the 5-minute
        // re-publish branch finally threw.
        //
        // The bound is the assertion. A test that only checked "TimeoutException eventually" would
        // have passed against the broken code too — it was five minutes of hot loop, not a hang.
        var h = Build(agentTimeoutSeconds: 1);
        var env = new ActivityEnvironment();
        var started = Environment.TickCount64;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            env.RunAsync(() =>
                h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-timeout")));

        var elapsedMs = Environment.TickCount64 - started;
        Assert.True(elapsedMs < 15_000,
            $"timeout should surface promptly after the 1s agent timeout, took {elapsedMs}ms");

        Assert.Contains(Agent, ex.Message);
        Assert.Contains("wf/task-timeout", ex.Message);
    }

    // ── Timer ordering guard (issue #321) ─────────────────────────────────────

    /// <summary>
    /// An environment whose Info reports a specific StartToCloseTimeout — the value the SDK says
    /// the activity was actually scheduled with, which is what the guard reads.
    /// </summary>
    private static ActivityEnvironment EnvWithStartToClose(TimeSpan? startToClose) =>
        new() { Info = ActivityEnvironment.DefaultInfo with { StartToCloseTimeout = startToClose } };

    [Fact]
    public async Task AnExplicitBudgetLongerThanTheScheduledContainer_FailsNonRetryably()
    {
        // The inversion that killed working reviewers: the activity is told it has 2s of budget
        // while Temporal will kill it after 1s. Failing at START is the point — it refuses before
        // publishing anything, and non-retryably so a misconfigured caller cannot loop.
        var h = Build();
        var env = EnvWithStartToClose(TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            env.RunAsync(() => h.Activity.DelegateToAgentAsync(
                Agent, "do the thing", "wf/task-misordered",
                retryOnIncomplete: true, maxIncompleteRetries: 3, agentBudgetSeconds: 2)));

        Assert.True(ex.NonRetryable);
        Assert.Equal("MisorderedAgentTimeouts", ex.ErrorType);

        // Nothing was published and nothing was registered — the guard runs before any work.
        Assert.Empty(h.PublishedInstructions);
        Assert.Equal(0, h.Registry.PendingCount);
    }

    [Fact]
    public async Task AnExplicitBudgetInsideTheScheduledContainer_Proceeds()
    {
        // Control for the test above. Without this, "the guard throws" would be satisfied by a
        // guard that throws unconditionally.
        var h = Build();
        var env = EnvWithStartToClose(TimeSpan.FromMinutes(3));

        var run = env.RunAsync(() => h.Activity.DelegateToAgentAsync(
            Agent, "do the thing", "wf/task-ordered",
            retryOnIncomplete: true, maxIncompleteRetries: 3, agentBudgetSeconds: 30));

        await CompleteWhenRegisteredAsync(h, "wf/task-ordered", new AgentTaskResult("all done", "completed"));

        Assert.Equal("all done", (await run).Text);
    }

    [Fact]
    public async Task NoExplicitBudget_IsNotGuarded()
    {
        // UWE delegate steps size StartToClose from their own per-step timeoutMinutes and pass no
        // budget, so they routinely run inside a container shorter than the deployment-wide
        // TemporalBridge:AgentTimeoutSeconds. Guarding them would fail every one of those steps
        // against a number they never asked for.
        var h = Build(agentTimeoutSeconds: 3600);
        var env = EnvWithStartToClose(TimeSpan.FromSeconds(1));

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-unguarded"));

        await CompleteWhenRegisteredAsync(h, "wf/task-unguarded", new AgentTaskResult("all done", "completed"));

        Assert.Equal("all done", (await run).Text);
    }

    [Fact]
    public async Task AnExplicitBudget_OverridesTheConfiguredDefault_AndIsWhatActuallyFires()
    {
        // Proves the explicit argument is the budget that runs, not a decoration alongside config:
        // config says an hour, the caller says 2s, and the wait ends in ~2s.
        var h = Build(agentTimeoutSeconds: 3600);
        var env = EnvWithStartToClose(TimeSpan.FromMinutes(3));
        var started = Environment.TickCount64;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            env.RunAsync(() => h.Activity.DelegateToAgentAsync(
                Agent, "do the thing", "wf/task-budget",
                retryOnIncomplete: true, maxIncompleteRetries: 3, agentBudgetSeconds: 2)));

        var elapsedMs = Environment.TickCount64 - started;
        Assert.True(elapsedMs < 20_000,
            $"the 2s explicit budget should fire, not the 3600s config value; took {elapsedMs}ms");
    }

    // ── The timeout message reports a measurement, not a constant ──────────────

    [Fact]
    public async Task TheTimeoutMessage_CarriesMeasuredElapsedTime_NotAConfiguredConstant()
    {
        // The old message printed (int)timeout.TotalMinutes — "did not respond within 90 minutes"
        // — which reads as a measurement and is not one. On the run behind #321 it appeared about
        // thirty seconds into an attempt and sent the investigation the wrong way.
        var h = Build(agentTimeoutSeconds: 5400);   // the deployed host's 90 minutes
        var env = EnvWithStartToClose(TimeSpan.FromMinutes(3));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            env.RunAsync(() => h.Activity.DelegateToAgentAsync(
                Agent, "do the thing", "wf/task-elapsed",
                retryOnIncomplete: true, maxIncompleteRetries: 3, agentBudgetSeconds: 2)));

        // Exactly one TimeoutException, and its elapsed figure is the ~2s actually waited.
        var elapsed = System.Text.RegularExpressions.Regex.Match(ex.Message, @"elapsed (\d+(?:\.\d+)?)s");
        Assert.True(elapsed.Success, $"expected a measured 'elapsed Ns' in: {ex.Message}");
        var elapsedSeconds = double.Parse(elapsed.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(elapsedSeconds, 1.0, 10.0);

        // The budget is present but labelled as a budget, so the two can never be confused...
        Assert.Contains("budget 2.0s", ex.Message);
        // ...and the configured 90 minutes appears nowhere.
        Assert.DoesNotContain("90", ex.Message);
        Assert.Contains(Agent, ex.Message);
        Assert.Contains("wf/task-elapsed", ex.Message);
    }

    [Fact]
    public async Task AForeignCancellationOfThePendingTask_IsNotReportedAsATimeout()
    {
        // The other half of the misleading message. When something other than this activity
        // cancels the registration — registry shutdown, or a stale attempt before
        // compare-and-remove landed — the wait ends immediately. Calling that a timeout claimed
        // the agent had had its full budget when it had had seconds.
        var h = Build(agentTimeoutSeconds: 3600);
        var env = EnvWithStartToClose(TimeSpan.FromMinutes(70));

        var run = env.RunAsync(() => h.Activity.DelegateToAgentAsync(
            Agent, "do the thing", "wf/task-foreign",
            retryOnIncomplete: true, maxIncompleteRetries: 3, agentBudgetSeconds: 3600));

        var deadline = Environment.TickCount64 + 5000;
        while (h.Registry.PendingCount == 0 && Environment.TickCount64 < deadline)
            await Task.Delay(10);

        h.Registry.CancelAll();   // e.g. the relay listener shutting down

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() => run);
        Assert.Equal("DelegationAborted", ex.ErrorType);
        Assert.Contains("not a timeout", ex.Message);
    }

    // ── Incomplete-response retry ─────────────────────────────────────────────

    [Fact]
    public async Task IncompleteResponse_IsContinued_AndTextIsAccumulated()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "write a long thing", "wf/task-inc"));

        await CompleteWhenRegisteredAsync(h, "wf/task-inc", new AgentTaskResult("part one", "incomplete"));
        await CompleteWhenRegisteredAsync(h, "wf/task-inc/incomplete-retry-1",
            new AgentTaskResult("part two", "completed"));

        var result = await run;

        // The final Text is the concatenation of every partial response.
        Assert.Equal("part one\npart two", result.Text);
        Assert.True(result.IsCompleted);

        // The continuation carries the prior partial text, which is what lets a reviewer reuse
        // an EVIDENCE: URL it already posted instead of posting a second comment.
        Assert.Equal(2, h.PublishedInstructions.Count);
        Assert.Contains("continue where you left off", h.PublishedInstructions[1]);
        Assert.Contains("part one", h.PublishedInstructions[1]);

        // A continuation uses a NEW task id — transport re-publication reuses the original, so
        // these two dedup differently and the distinction is load-bearing.
        Assert.Equal("wf/task-inc", h.PublishedTaskIds[0]);
        Assert.Equal("wf/task-inc/incomplete-retry-1", h.PublishedTaskIds[1]);
    }

    [Fact]
    public async Task IncompleteResponse_StopsAfterMaxRetries_AndReturnsWhatItHas()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(
                Agent, "write a long thing", "wf/task-max", retryOnIncomplete: true, maxIncompleteRetries: 2));

        await CompleteWhenRegisteredAsync(h, "wf/task-max", new AgentTaskResult("a", "incomplete"));
        await CompleteWhenRegisteredAsync(h, "wf/task-max/incomplete-retry-1", new AgentTaskResult("b", "incomplete"));
        await CompleteWhenRegisteredAsync(h, "wf/task-max/incomplete-retry-2", new AgentTaskResult("c", "incomplete"));

        var result = await run;

        // Bounded: three publishes total, not an unbounded continuation loop.
        Assert.Equal(3, h.PublishedInstructions.Count);
        Assert.Equal("a\nb\nc", result.Text);
    }

    [Fact]
    public async Task RetryOnIncompleteFalse_ReturnsTheIncompleteResponseUnchanged()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(
                Agent, "do the thing", "wf/task-noretry", retryOnIncomplete: false));

        await CompleteWhenRegisteredAsync(h, "wf/task-noretry", new AgentTaskResult("partial", "incomplete"));
        var result = await run;

        Assert.Equal("partial", result.Text);
        Assert.True(result.IsIncomplete);
        Assert.Single(h.PublishedInstructions);
    }

    // ── The activity surface existing callers bind to ─────────────────────────

    [Fact]
    public void ActivitySignature_AppendsTheBudgetAndRepoAsOptionalTrailingParameters()
    {
        // Workflows bind activities BY NAME, so a signature change breaks them at runtime rather
        // than at compile time. The budget and the repo (#347) therefore go last and optional:
        // UWE's delegate step invokes "DelegateToAgent" with five positional payloads and must
        // keep binding.
        var method = typeof(DelegateToAgentActivity).GetMethod(nameof(DelegateToAgentActivity.DelegateToAgentAsync))!;
        var parameters = method.GetParameters();

        Assert.Equal(7, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);   // agentName
        Assert.Equal(typeof(string), parameters[1].ParameterType);   // instruction
        Assert.Equal(typeof(string), parameters[2].ParameterType);   // taskId
        Assert.Equal(typeof(bool),   parameters[3].ParameterType);   // retryOnIncomplete
        Assert.Equal(typeof(int),    parameters[4].ParameterType);   // maxIncompleteRetries
        Assert.Equal(typeof(int),    parameters[5].ParameterType);   // agentBudgetSeconds
        Assert.Equal(typeof(string), parameters[6].ParameterType);   // repo
        Assert.Equal(typeof(Task<AgentTaskResult>), method.ReturnType);

        // The first three are mandatory and the rest carry defaults — this is what the Temporal
        // SDK turns into RequiredParameterCount, and it is why a five-payload caller still binds.
        Assert.Equal([false, false, false, true, true, true, true],
            parameters.Select(p => p.HasDefaultValue).ToArray());
        Assert.Equal(0, parameters[5].DefaultValue);   // 0 = "no explicit budget", not "no budget"
        Assert.Null(parameters[6].DefaultValue);       // no repo signal

        // AgentTaskResult's shape is untouched.
        var resultProps = typeof(AgentTaskResult).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("Text", resultProps);
        Assert.Contains("Status", resultProps);
    }

    [Fact]
    public void ActivityDefinition_StillAcceptsAFivePayloadCaller()
    {
        // The property the reflection assertions above only imply. The SDK derives
        // RequiredParameterCount from the parameter defaults, and it is that number the worker
        // checks an inbound payload count against — so this is the thing that actually decides
        // whether UWE's existing five-argument call site keeps working.
        var method = typeof(DelegateToAgentActivity).GetMethod(nameof(DelegateToAgentActivity.DelegateToAgentAsync))!;
        var definition = Temporalio.Activities.ActivityDefinition.Create(method, _ => null);

        Assert.Equal(7, definition.ParameterTypes.Count);
        Assert.Equal(3, definition.RequiredParameterCount);

        // Workflows that schedule by name use the constant; it must be the name the worker registers.
        Assert.Equal(DelegateToAgentActivity.ActivityName, definition.Name);
    }

    // ── Repo signal (#347) ────────────────────────────────────────────────────

    [Fact]
    public async Task ARepo_IsCarriedOnTheDirective_AndOnEveryContinuation()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "write a long thing", "wf/task-repo", repo: "org/app"));

        await CompleteWhenRegisteredAsync(h, "wf/task-repo", new AgentTaskResult("part one", "incomplete"));
        await CompleteWhenRegisteredAsync(h, "wf/task-repo/incomplete-retry-1",
            new AgentTaskResult("part two", "completed"));
        await run;

        // The continuation is the same delegation, so it routes the same way.
        Assert.Equal(["org/app", "org/app"], h.PublishedRepos);

        // Structured field only — the instruction text is not a carrier.
        Assert.DoesNotContain(h.PublishedInstructions, i => i.Contains("org/app"));
    }

    [Fact]
    public async Task NoRepo_PublishesANullRepo()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-norepo"));

        await CompleteWhenRegisteredAsync(h, "wf/task-norepo", new AgentTaskResult("done", "completed"));
        await run;

        Assert.Equal([null], h.PublishedRepos);
    }

    [Theory]
    [InlineData("not a repo")]
    [InlineData("org/app/extra")]
    [InlineData("org")]
    [InlineData("/app")]
    [InlineData("org/app?x=1")]
    [InlineData("   ")]
    public async Task AMalformedOrBlankRepo_IsDropped_AndTheDelegationStillGoesOut(string repo)
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-badrepo", repo: repo));

        await CompleteWhenRegisteredAsync(h, "wf/task-badrepo", new AgentTaskResult("done", "completed"));
        var result = await run;

        Assert.True(result.IsCompleted);
        Assert.Equal([null], h.PublishedRepos);
    }

    [Fact]
    public async Task ARepoWithSurroundingWhitespace_IsTrimmed()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-trim", repo: " org/app\n"));

        await CompleteWhenRegisteredAsync(h, "wf/task-trim", new AgentTaskResult("done", "completed"));
        await run;

        Assert.Equal(["org/app"], h.PublishedRepos);
    }
}
