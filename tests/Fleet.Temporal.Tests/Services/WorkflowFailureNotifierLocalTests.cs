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
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// The connected check for #488, against a REAL local Temporal server: synthetic runs close Failed
/// (one with a secret-shaped error type), explicitly failed, TimedOut, successfully with null, and
/// canceled. The production notifier lists them through Temporal visibility and starts the real
/// NotifyCtoWorkflow, whose real DelegateToAgentActivity publishes the directive. Only the broker
/// edge is substituted: it fails the first publish (an outage) and otherwise captures directives for a
/// test coordinator, which answers them. No real agent or human is notified.
/// </summary>
[Collection("workflow-failure-local")]
public class WorkflowFailureNotifierLocalTests : IClassFixture<LocalTemporalFixture>, IDisposable
{
    private const string Coordinator = "test-coordinator";
    private const string SecretType = "synthetic-secret-123";

    private readonly LocalTemporalFixture _fixture;
    private readonly string _dir = Directory.CreateTempSubdirectory("wf-failure-local-").FullName;

    public WorkflowFailureNotifierLocalTests(LocalTemporalFixture fixture) => _fixture = fixture;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task FailuresAreDeliveredOnceThroughAnOutageAndARestart_NonFailuresNever_NoSecretsLeak()
    {
        var queue = $"wf-failure-local-{Guid.NewGuid():N}";
        var capture = new PublishCapture { FailNext = 1 };
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

            using var answering = new CancellationTokenSource();
            var coordinator = AnswerEveryDirectiveAsync(capture, registry, answering.Token);

            var notifier = NewNotifier(factory, source, ns, queue);
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (notifier.Snapshot().Delivered < 3 && DateTime.UtcNow < deadline)
            {
                await notifier.RunCycleAsync(CancellationToken.None);
                await Task.Delay(300);
            }
            var status = notifier.Snapshot();
            Assert.Equal(3, status.Delivered);
            Assert.True(status.Durable);
            Assert.Empty(status.Pending);
            Assert.Empty(status.InFlight);
            Assert.Empty(status.Parked);

            // The outage: one attempt's run closed Failed (not swallowed), and that failure was retried.
            var failedAttempt = Assert.Single(capture.FailedTaskIds).Split('/')[0];
            var description = await _fixture.Client.GetWorkflowHandle(failedAttempt).DescribeAsync();
            Assert.Equal(WorkflowExecutionStatus.Failed, description.Status);
            var retried = failedAttempt[..^1] + "2";
            Assert.Equal(WorkflowExecutionStatus.Completed, (await _fixture.Client.GetWorkflowHandle(retried).DescribeAsync()).Status);

            var directives = capture.Delivered.Where(d => d.Text.Contains(tag)).ToList();
            Assert.Equal(3, directives.Count);
            Assert.All(directives, d => Assert.Equal(Coordinator, d.RoutingKey));
            Assert.All(capture.All, d => Assert.DoesNotContain(SecretType, d.Text));
            Assert.All(capture.All, d => Assert.DoesNotContain("hunter2", d.Text));
            Assert.Contains("cause: explicit_fail (explicit business failure)", For(directives, explicitFail).Text);
            Assert.Contains("cause: application_error\n", For(directives, plainFail).Text);
            Assert.Contains("status: TimedOut", For(directives, timedOut).Text);
            Assert.DoesNotContain(capture.All, d => d.Text.Contains(success.Id) || d.Text.Contains(canceled.Id));
            Assert.DoesNotContain(SecretType, File.ReadAllText(StatePath));

            // A restart reloads the state file: nothing is notified again.
            var published = capture.All.Count;
            var restarted = NewNotifier(factory, source, ns, queue);
            await restarted.RunCycleAsync(CancellationToken.None);
            await restarted.RunCycleAsync(CancellationToken.None);
            Assert.Equal(published, capture.All.Count);
            Assert.Equal(3, restarted.Snapshot().Delivered);

            answering.Cancel();
            await coordinator;
        });
    }

    private string StatePath => Path.Combine(_dir, "workflow-failures.json");

    private static (string RoutingKey, string Text) For(List<(string RoutingKey, string Text)> directives, WorkflowHandle run) =>
        Assert.Single(directives, d => d.Text.Contains($"workflowId: {run.Id}\n") && d.Text.Contains($"runId: {run.ResultRunId}"));

    private WorkflowFailureNotifier NewNotifier(ITemporalClientFactory factory, IWorkflowFailureSource source, string ns, string queue)
    {
        var options = Options.Create(new WorkflowFailureNotifierOptions
        {
            Enabled = true,
            CoordinatorAgent = Coordinator,
            NotificationNamespace = ns,
            NotificationTaskQueue = queue,
            StateFile = StatePath,
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

    /// <summary>The test coordinator: acknowledges each delivered directive the way an agent's reply would.</summary>
    private static async Task AnswerEveryDirectiveAsync(PublishCapture capture, TaskCompletionRegistry registry, CancellationToken ct)
    {
        var answered = new HashSet<string>();
        while (!ct.IsCancellationRequested)
        {
            foreach (var taskId in capture.DeliveredTaskIds.Where(t => !answered.Contains(t)))
                if (registry.TryComplete(taskId, new AgentTaskResult("noted", "completed")))
                    answered.Add(taskId);
            await Task.Delay(50, CancellationToken.None);
        }
    }

    private sealed class FixedClientFactory(ITemporalClient client) : ITemporalClientFactory
    {
        public Task<ITemporalClient> GetClientAsync(string @namespace) => Task.FromResult(client);
    }

    /// <summary>The broker edge: fails the first <see cref="FailNext"/> publishes, captures the rest.</summary>
    private sealed class PublishCapture
    {
        private readonly ConcurrentQueue<(string RoutingKey, string Text, string TaskId, bool Failed)> _published = new();
        public int FailNext;

        public List<(string RoutingKey, string Text)> All => [.. _published.Select(p => (p.RoutingKey, p.Text))];
        public List<(string RoutingKey, string Text)> Delivered => [.. _published.Where(p => !p.Failed).Select(p => (p.RoutingKey, p.Text))];
        public IEnumerable<string> DeliveredTaskIds => _published.Where(p => !p.Failed).Select(p => p.TaskId).ToArray();
        public List<string> FailedTaskIds => [.. _published.Where(p => p.Failed).Select(p => p.TaskId)];

        public IConnectionFactory Factory()
        {
            var channel = Substitute.For<IChannel>();
            channel.BasicPublishAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                    Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span));
                    var failed = Interlocked.Decrement(ref FailNext) >= 0;
                    _published.Enqueue((call.ArgAt<string>(1), doc.RootElement.GetProperty("Text").GetString() ?? "",
                        doc.RootElement.GetProperty("TaskId").GetString() ?? "", failed));
                    if (failed) throw new IOException("broker unavailable");
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

/// <summary>Closes the way its <c>mode</c> says, carrying a fake secret in every failure.</summary>
[Workflow]
public class SyntheticOutcomeWorkflow
{
    [WorkflowRun]
    public async Task<string?> RunAsync(string mode)
    {
        switch (mode)
        {
            case "explicit":
                throw new ApplicationFailureException("validation failed password=hunter2", errorType: "ExplicitFail", nonRetryable: true);
            case "error":
                throw new ApplicationFailureException("upstream said password=hunter2", errorType: "synthetic-secret-123", nonRetryable: true);
            case "success":
                return null;
            default:
                await Workflow.WaitConditionAsync(() => false);
                return null;
        }
    }
}
