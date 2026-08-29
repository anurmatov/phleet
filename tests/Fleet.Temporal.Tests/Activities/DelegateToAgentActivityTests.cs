using System.Text;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
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
    }

    private static Harness Build(int agentTimeoutSeconds = 30)
    {
        var publishedInstructions = new List<string>();
        var publishedTaskIds = new List<string>();

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
                return ValueTask.CompletedTask;
            });

        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));

        var connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(connection));

        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);

        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = agentTimeoutSeconds }),
            registry,
            connectionFactory,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<DelegateToAgentActivity>.Instance);

        return new Harness
        {
            Activity = activity,
            Registry = registry,
            PublishedInstructions = publishedInstructions,
            PublishedTaskIds = publishedTaskIds,
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

    // ── The consensus change did not alter this surface ───────────────────────

    [Fact]
    public void ActivitySignature_IsUnchangedByTheConsensusChange()
    {
        // Constraint #7 of the issue: parsing stays inside ConsensusReviewWorkflow so unrelated
        // callers are structurally unaffected. A signature change here would break every one of
        // them silently at runtime rather than at compile time, since workflows bind activities
        // by name.
        var method = typeof(DelegateToAgentActivity).GetMethod(nameof(DelegateToAgentActivity.DelegateToAgentAsync))!;
        var parameters = method.GetParameters();

        Assert.Equal(5, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);   // agentName
        Assert.Equal(typeof(string), parameters[1].ParameterType);   // instruction
        Assert.Equal(typeof(string), parameters[2].ParameterType);   // taskId
        Assert.Equal(typeof(bool),   parameters[3].ParameterType);   // retryOnIncomplete
        Assert.Equal(typeof(int),    parameters[4].ParameterType);   // maxIncompleteRetries
        Assert.Equal(typeof(Task<AgentTaskResult>), method.ReturnType);

        // AgentTaskResult's shape is likewise untouched.
        var resultProps = typeof(AgentTaskResult).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("Text", resultProps);
        Assert.Contains("Status", resultProps);
    }
}
