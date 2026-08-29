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
/// Regression coverage for the agent-timeout path (issue #251).
///
/// The activity is driven for real through <see cref="ActivityEnvironment"/>; only the two
/// outbound edges — RabbitMQ and HTTP — are substituted, because those leave the process.
/// <see cref="TaskCompletionRegistry"/> and the wait loop are production code, which is the point:
/// the defect lived in the loop's interaction with a cancelled token, and a test that stubbed the
/// loop would not have seen it.
/// </summary>
public class DelegateToAgentActivityTimeoutTests
{
    private const string Agent = "reviewer-one";

    private sealed record Harness(
        DelegateToAgentActivity Activity,
        TaskCompletionRegistry Registry,
        List<string> Published);

    private static Harness Build(int agentTimeoutSeconds)
    {
        var published = new List<string>();

        var channel = Substitute.For<IChannel>();
        // Note the GENERIC overload with BasicProperties — substituting the non-generic one
        // silently intercepts nothing and the capture list stays quietly empty.
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var json = Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Text", out var text))
                    published.Add(text.GetString() ?? "");
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

        return new Harness(activity, registry, published);
    }

    [Fact]
    public async Task AgentThatNeverResponds_TimesOutPromptly()
    {
        // THE BOUND IS THE ASSERTION, not the exception type.
        //
        // Before the fix this test did not fail — it HUNG. Once the agent timeout cancelled
        // timeoutCts, Task.Delay(30s, cancelledToken) returned an already-cancelled task on every
        // iteration, and Task.WhenAny does not throw on a cancelled task, so the loop heartbeated
        // and went round again with no delay. It escaped only when the 5-minute re-publish branch
        // was finally reached by wall clock and threw.
        //
        // So a TimeoutException DID eventually arrive, and a test asserting only
        // Assert.ThrowsAsync<TimeoutException> would have passed against the broken code after
        // five minutes of a hot loop. Bounding the elapsed time is what makes this catch it.
        var h = Build(agentTimeoutSeconds: 1);
        var env = new ActivityEnvironment();
        var started = Environment.TickCount64;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            env.RunAsync(() =>
                h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-timeout")));

        var elapsedMs = Environment.TickCount64 - started;
        Assert.True(elapsedMs < 15_000,
            $"timeout should surface shortly after the 1s agent timeout, took {elapsedMs}ms");

        Assert.Contains(Agent, ex.Message);
        Assert.Contains("wf/task-timeout", ex.Message);

        // The directive was published exactly once — the spin did not also cause repeated
        // re-publication before the timeout surfaced.
        Assert.Single(h.Published);
    }

    [Fact]
    public async Task ActivityCancellation_IsStillReportedAsCancellation_NotAsATimeout()
    {
        // The fix throws on a token that is linked to BOTH the timeout and the activity's own
        // cancellation. The two must stay distinguishable: Temporal treats a cancelled activity
        // differently from a failed one, and collapsing cancellation into TimeoutException would
        // also trigger the timeout-notification path for a workflow the caller deliberately stopped.
        var h = Build(agentTimeoutSeconds: 300);
        using var cts = new CancellationTokenSource();
        var env = new ActivityEnvironment { CancellationTokenSource = cts };

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-cancel"));

        var deadline = Environment.TickCount64 + 5000;
        while (h.Published.Count == 0 && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        Assert.NotEmpty(h.Published);

        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task ResponseBeforeTheTimeout_IsUnaffected()
    {
        // Guards against over-correcting: the new throw must not fire on the ordinary path where
        // the agent answers within its window.
        var h = Build(agentTimeoutSeconds: 60);
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() =>
            h.Activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-ok"));

        var deadline = Environment.TickCount64 + 5000;
        while (!h.Registry.TryComplete("wf/task-ok", new AgentTaskResult("all done", "completed"))
               && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        var result = await run;

        Assert.Equal("all done", result.Text);
        Assert.True(result.IsCompleted);
    }
}
