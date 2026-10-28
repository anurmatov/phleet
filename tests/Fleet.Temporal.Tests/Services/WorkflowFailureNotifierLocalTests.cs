using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Fleet.Temporal.Tests.Workflows;
using Fleet.Temporal.Workflows.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// The one connected check for #488, against a REAL local Temporal server: synthetic runs close
/// Failed, TimedOut, explicitly failed, successfully with null, and canceled. The production
/// notifier lists them through Temporal visibility and starts the real NotifyCtoWorkflow, whose real
/// DelegateToAgentActivity publishes the directive. Only the broker edge is captured, addressed to a
/// test coordinator, so no real agent or human is notified.
/// </summary>
[Collection("workflow-failure-local")]
public class WorkflowFailureNotifierLocalTests : IClassFixture<LocalTemporalFixture>
{
    private const string Coordinator = "test-coordinator";
    private const string Secret = "sk-live-SECRET-hunter2";

    private readonly LocalTemporalFixture _fixture;

    public WorkflowFailureNotifierLocalTests(LocalTemporalFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FailedRunsReachTheTestCoordinatorOnce_AndNonFailuresNever()
    {
        var queue = $"wf-failure-local-{Guid.NewGuid():N}";
        var capture = new PublishCapture();
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = 60, OrchestratorUrl = "http://orchestrator.invalid" }),
            registry, capture.Factory(), Substitute.For<IHttpClientFactory>(),
            NullLogger<DelegateToAgentActivity>.Instance);

        using var worker = new TemporalWorker(_fixture.Client, new TemporalWorkerOptions(queue)
            .AddAllActivities(activity)
            .AddWorkflow<NotifyCtoWorkflow>()
            .AddWorkflow<SyntheticOutcomeWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var explicitFail = await CloseAsync($"synthetic-explicit-{tag}", "explicit", queue);
            var plainFail = await CloseAsync($"synthetic-error-{tag}", "error", queue);
            var timedOut = await CloseAsync($"synthetic-timeout-{tag}", "hold", queue, TimeSpan.FromSeconds(2));
            var success = await CloseAsync($"synthetic-success-{tag}", "success", queue);
            var canceled = await CloseAsync($"synthetic-hold-{tag}", "hold", queue, cancel: true);

            var ns = _fixture.Client.Options.Namespace;
            var factory = new FixedClientFactory(_fixture.Client);
            var source = new TemporalWorkflowFailureSource(factory);
            await WaitUntilListedAsync(source, ns, 3, tag);

            var notifier = NewNotifier(factory, source, ns, queue);
            var answering = AnswerEveryPublishAsync(capture, registry, expected: 3);
            await notifier.RunCycleAsync(CancellationToken.None);
            await answering;

            var directives = capture.Directives.Where(d => d.Text.Contains(tag)).ToList();
            Assert.Equal(3, directives.Count);
            Assert.All(directives, d => Assert.Equal(Coordinator, d.RoutingKey));
            Assert.All(directives, d => Assert.DoesNotContain("hunter2", d.Text));
            Assert.All(directives, d => Assert.Contains("[fleet-wf:NotifyCtoWorkflow:workflow-failure-", d.Text));

            Assert.Contains("cause: explicit_fail (explicit business failure)", Single(directives, explicitFail).Text);
            Assert.Contains("status: Failed", Single(directives, plainFail).Text);
            Assert.Contains("cause: application_error:unspecified", Single(directives, plainFail).Text);
            Assert.Contains("status: TimedOut", Single(directives, timedOut).Text);
            Assert.DoesNotContain(directives, d => d.Text.Contains(success.Id) || d.Text.Contains(canceled.Id));

            // A repeat poll and a restarted notifier both meet the persisted notification ids.
            await notifier.RunCycleAsync(CancellationToken.None);
            await NewNotifier(factory, source, ns, queue).RunCycleAsync(CancellationToken.None);
            Assert.Equal(3, capture.Directives.Count(d => d.Text.Contains(tag)));
            Assert.Empty(notifier.Snapshot().Pending);
            Assert.Equal(3, notifier.Snapshot().Sent);
        });
    }

    private static (string Id, string RunId, string RoutingKey, string Text) Single(
        List<(string RoutingKey, string Text)> directives, WorkflowHandle run)
    {
        var d = Assert.Single(directives, d => d.Text.Contains($"workflowId: {run.Id}\n") && d.Text.Contains($"runId: {run.ResultRunId}"));
        return (run.Id, run.ResultRunId!, d.RoutingKey, d.Text);
    }

    private static WorkflowFailureNotifier NewNotifier(ITemporalClientFactory factory, IWorkflowFailureSource source, string ns, string queue)
    {
        var options = Options.Create(new WorkflowFailureNotifierOptions
        {
            Enabled = true,
            CoordinatorAgent = Coordinator,
            NotificationNamespace = ns,
            NotificationTaskQueue = queue,
        });
        return new WorkflowFailureNotifier(
            options, Options.Create(new TemporalBridgeOptions { Namespaces = [ns] }), new CtoAgentConfigService(),
            source, new TemporalFailureNotificationSender(factory, options), NullLogger<WorkflowFailureNotifier>.Instance);
    }

    private async Task<WorkflowHandle> CloseAsync(string id, string mode, string queue, TimeSpan? timeout = null, bool cancel = false)
    {
        var handle = await _fixture.Client.StartWorkflowAsync(
            (SyntheticOutcomeWorkflow wf) => wf.RunAsync(mode),
            new WorkflowOptions(id, queue) { ExecutionTimeout = timeout });
        if (cancel)
        {
            await Task.Delay(500);
            await handle.CancelAsync();
        }
        try { await handle.GetResultAsync(); } catch (WorkflowFailedException) { }
        return handle;
    }

    private static async Task WaitUntilListedAsync(IWorkflowFailureSource source, string ns, int count, string tag)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var runs = await source.ListFailedAsync(ns, DateTime.UtcNow.AddHours(-1), CancellationToken.None);
            if (runs.Count(r => r.WorkflowId.Contains(tag)) >= count) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("failed runs never appeared in visibility");
    }

    /// <summary>The test coordinator: acknowledges each directive the way an agent's reply would.</summary>
    private static async Task AnswerEveryPublishAsync(PublishCapture capture, TaskCompletionRegistry registry, int expected)
    {
        var answered = new HashSet<string>();
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (answered.Count < expected && DateTime.UtcNow < deadline)
        {
            foreach (var taskId in capture.TaskIds.Where(t => !answered.Contains(t)))
                if (registry.TryComplete(taskId, new AgentTaskResult("noted", "completed")))
                    answered.Add(taskId);
            await Task.Delay(50);
        }
        Assert.Equal(expected, answered.Count);
    }

    private sealed class FixedClientFactory(ITemporalClient client) : ITemporalClientFactory
    {
        public Task<ITemporalClient> GetClientAsync(string @namespace) => Task.FromResult(client);
    }

    private sealed class PublishCapture
    {
        public ConcurrentQueue<(string RoutingKey, string Text)> Published { get; } = new();
        public ConcurrentQueue<string> Ids { get; } = new();
        public List<(string RoutingKey, string Text)> Directives => [.. Published];
        public IEnumerable<string> TaskIds => Ids.ToArray();

        public IConnectionFactory Factory()
        {
            var channel = Substitute.For<IChannel>();
            channel.BasicPublishAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                    Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span));
                    Published.Enqueue((call.ArgAt<string>(1), doc.RootElement.GetProperty("Text").GetString() ?? ""));
                    Ids.Enqueue(doc.RootElement.GetProperty("TaskId").GetString() ?? "");
                    return ValueTask.CompletedTask;
                });
            var connection = Substitute.For<IConnection>();
            connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(channel));
            var factory = Substitute.For<IConnectionFactory>();
            factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));
            return factory;
        }
    }
}

/// <summary>Closes the way its <c>mode</c> says, carrying a fake secret in every failure message.</summary>
[Workflow]
public class SyntheticOutcomeWorkflow
{
    [WorkflowRun]
    public async Task<string?> RunAsync(string mode)
    {
        switch (mode)
        {
            case "explicit":
                throw new ApplicationFailureException("validation failed sk-live-SECRET-hunter2", errorType: "ExplicitFail", nonRetryable: true);
            case "error":
                throw new ApplicationFailureException("upstream said password=sk-live-SECRET-hunter2", nonRetryable: true);
            case "success":
                return null;
            default:
                await Workflow.WaitConditionAsync(() => false);
                return null;
        }
    }
}
