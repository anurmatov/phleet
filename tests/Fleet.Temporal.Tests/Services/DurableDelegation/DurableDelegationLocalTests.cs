using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Fleet.Temporal.Tests.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// Durable delegation against a real Temporal server, in real time: the real
/// <see cref="DelegateToAgentActivity"/>, the real <see cref="DurableDelegationService"/> loop, the
/// real <see cref="TemporalAsyncActivityGateway"/> and the relay listener's reply routing. Only the
/// broker and the orchestrator are substituted, because they leave the process.
///
/// <para>
/// A "bridge" here is what a bridge process is: a worker, a service over the state file, and an
/// in-memory registry. Killing one and building another over the same file is a restart. The
/// tests wait in seconds because a heartbeat timeout only exists on a server that keeps time.
/// </para>
/// </summary>
[Collection("durable-delegation-local")]
public sealed class DurableDelegationLocalTests : IClassFixture<LocalTemporalFixture>, IDisposable
{
    private const string Agent = "agent-a";

    private readonly LocalTemporalFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "durable-local-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRelay _relay = new();

    public DurableDelegationLocalTests(LocalTemporalFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a temp directory, or nothing was written */ }
    }

    private string StateFile => Path.Combine(_dir, "pending-delegations.json");

    /// <summary>
    /// The converter the bridge's workers and its client factory both use (camelCase, case
    /// insensitive), so an answer written by an asynchronous completion is encoded exactly as one
    /// returned by the activity would be.
    /// </summary>
    private static readonly DataConverter BridgeConverter = DataConverter.Default with
    {
        PayloadConverter = new DefaultPayloadConverter(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private async Task<ITemporalClient> BridgeClientAsync() => await TemporalClient.ConnectAsync(
        new TemporalClientConnectOptions(_fixture.Client.Connection.Options.TargetHost!)
        {
            Namespace = _fixture.Client.Options.Namespace,
            DataConverter = BridgeConverter,
        });

    private sealed class SingleClientFactory(ITemporalClient client) : ITemporalClientFactory
    {
        public Task<ITemporalClient> GetClientAsync(string @namespace) => Task.FromResult(client);
    }

    /// <summary>One bridge process: its worker, its durable service and its listener.</summary>
    private sealed class Bridge : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TemporalWorker _worker;
        private readonly Task _workerRun;

        public DurableDelegationService Durable { get; }
        public TaskCompletionRegistry Registry { get; }
        public TemporalRelayListener Listener { get; }
        public List<string> OrchestratorCancels { get; } = [];

        public Bridge(
            ITemporalClient client, string taskQueue, string stateFile, FakeRelay relay, bool durable,
            WorkflowDefinitionModel? definition = null, bool runService = true)
        {
            Durable = new DurableDelegationService(
                Options.Create(new DurableDelegationOptions { Enabled = durable, StateFile = stateFile }),
                new TemporalAsyncActivityGateway(new SingleClientFactory(client), NullLogger<TemporalAsyncActivityGateway>.Instance),
                relay,
                NullLogger<DurableDelegationService>.Instance)
            {
                TickInterval = TimeSpan.FromMilliseconds(300),
            };
            Registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
            Listener = new TemporalRelayListener(
                Options.Create(new RabbitMqOptions()), Registry, new SingleClientFactory(client),
                NullLogger<TemporalRelayListener>.Instance, Durable);

            // The in-memory path's two outbound edges, for the runs that have the switch off.
            var channel = Substitute.For<IChannel>();
            var connection = Substitute.For<IConnection>();
            connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
            var connectionFactory = Substitute.For<IConnectionFactory>();
            connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));
            var http = Substitute.For<IHttpClientFactory>();
            http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new CancelRecorder(OrchestratorCancels)));

            var activity = new DelegateToAgentActivity(
                Options.Create(new RabbitMqOptions()),
                Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = 600, OrchestratorUrl = "http://orchestrator.invalid" }),
                Registry, connectionFactory, http, NullLogger<DelegateToAgentActivity>.Instance, Durable);

            var options = new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddWorkflow<DelegationProbeWorkflow>()
                .AddAllActivities(activity);
            if (definition is not null) options.AddAllActivities(new DefinitionActivities(definition));

            _worker = new TemporalWorker(client, options);
            _workerRun = _worker.ExecuteAsync(_stop.Token);
            if (runService) Durable.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>Stops the worker and the service and forgets everything held in memory.</summary>
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await _workerRun; }
            catch (OperationCanceledException) { /* that is how a worker stops */ }
            await Durable.StopAsync(CancellationToken.None);
            Registry.CancelAll();
            _worker.Dispose();
            _stop.Dispose();
        }

        private sealed class CancelRecorder(List<string> requests) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (requests) requests.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }
        }
    }

    private sealed class DefinitionActivities(WorkflowDefinitionModel definition)
    {
        [Activity("LoadWorkflowDefinition")]
        public WorkflowDefinitionModel LoadDefinition(string _) => definition;

        [Activity("LoadWorkflowConfig")]
        public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });
    }

    private static WorkflowDefinitionModel OneDelegate(string name) => new()
    {
        Name = name,
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep
        {
            Steps =
            [
                new DelegateStep { Name = "work", Target = Agent, Instruction = "Do the work.", OutputVar = "answer" },
                new SetVariableStep { Vars = new() { ["_result"] = "{{vars.answer}}" } },
            ],
        },
    };

    private static RelayMessage Reply(string taskId, string text) =>
        new(0, Agent, text, DateTimeOffset.UtcNow, RelayMessageType.Response, TaskId: taskId);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what, int seconds = 30)
    {
        var deadline = Environment.TickCount64 + seconds * 1000;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        Assert.Fail($"timed out waiting until {what}");
    }

    private static Task WaitForAsync(Func<bool> condition, string what, int seconds = 30) =>
        WaitForAsync(() => Task.FromResult(condition()), what, seconds);

    private static async Task<bool> IsRunningAsync(WorkflowHandle handle) =>
        (await handle.DescribeAsync()).Status == WorkflowExecutionStatus.Running;

    private static IEnumerable<EventType> ActivityEvents(Temporalio.Common.WorkflowHistory history) =>
        history.Events.Select(e => e.EventType).Where(t => t.ToString().StartsWith("ActivityTask", StringComparison.Ordinal));

    // ── the restart ───────────────────────────────────────────────────────────

    /// <summary>
    /// The scenario the change exists for, on the real engine. A UWE run delegates; the bridge is
    /// killed while the agent is working; a new bridge starts over the same state file; the
    /// agent's reply arrives at the new bridge's listener; the workflow completes with it. One
    /// activity was scheduled and it completed: it was never failed, timed out or retried. The
    /// resulting history replays.
    /// </summary>
    [Fact]
    public async Task ADelegationInFlightAcrossABridgeRestart_CompletesWithTheAgentsReply()
    {
        var client = await BridgeClientAsync();
        var id = "durable-restart-" + Guid.NewGuid().ToString("N");
        var definition = OneDelegate("DurableRestartExample");
        var taskId = $"{id}/work";

        WorkflowHandle handle;
        await using (var first = new Bridge(client, id, StateFile, _relay, durable: true, definition))
        {
            handle = await client.StartWorkflowAsync(
                definition.Name,
                new object?[] { JsonSerializer.SerializeToElement(new { }) },
                new WorkflowOptions(id: id, taskQueue: id));

            await WaitForAsync(() => _relay.Published.Count == 1, "the directive was published");
            Assert.Equal((Agent, taskId, "Do the work.", (string?)null), _relay.Published[0]);
            Assert.Equal(0, first.Registry.PendingCount);
            Assert.Equal(taskId, Assert.Single((await first.Durable.SnapshotAsync()).Pending).TaskId);
        }

        // No bridge at all. The workflow is still waiting on its one activity.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(await IsRunningAsync(handle));

        await using (var second = new Bridge(client, id, StateFile, _relay, durable: true, definition))
        {
            var resumed = Assert.Single((await second.Durable.SnapshotAsync()).Pending);
            Assert.Equal(taskId, resumed.TaskId);
            Assert.Equal(id, resumed.WorkflowId);

            // A few passes of the new bridge's loop: the activity is being heartbeated from the file.
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.True(await IsRunningAsync(handle));

            Assert.True(await second.Listener.TryRouteReplyAsync(Reply(taskId, "[status: completed] the agent's answer")));

            await handle.GetResultAsync<object?>();
            Assert.Empty((await second.Durable.SnapshotAsync()).Pending);
        }

        var history = await handle.FetchHistoryAsync();
        Assert.Equal(
            [EventType.ActivityTaskScheduled, EventType.ActivityTaskStarted, EventType.ActivityTaskCompleted],
            DelegateEvents(history));
        // Encoded as the worker would have encoded the activity's own return value: camelCase.
        var completed = history.Events.Last(e => e.EventType == EventType.ActivityTaskCompleted);
        using var result = JsonDocument.Parse(
            completed.ActivityTaskCompletedEventAttributes.Result.Payloads_[0].Data.ToStringUtf8());
        Assert.Equal("the agent's answer", result.RootElement.GetProperty("text").GetString());
        Assert.Equal("completed", result.RootElement.GetProperty("status").GetString());
        Assert.Single(_relay.Published);
        Assert.Empty(_relay.Cancels);

        // The history a run in flight across the restart leaves behind replays: the workflow saw
        // an ordinary activity that completed.
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions { DataConverter = BridgeConverter }.AddWorkflow<UniversalWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

    /// <summary>The activity events of the one <c>DelegateToAgent</c> the history scheduled.</summary>
    private static EventType[] DelegateEvents(Temporalio.Common.WorkflowHistory history)
    {
        var scheduled = history.Events.Single(e =>
            e.ActivityTaskScheduledEventAttributes?.ActivityType?.Name == DelegateToAgentActivity.ActivityName);
        return history.Events
            .Where(e => e.EventId == scheduled.EventId
                || e.ActivityTaskStartedEventAttributes?.ScheduledEventId == scheduled.EventId
                || e.ActivityTaskCompletedEventAttributes?.ScheduledEventId == scheduled.EventId
                || e.ActivityTaskFailedEventAttributes?.ScheduledEventId == scheduled.EventId
                || e.ActivityTaskTimedOutEventAttributes?.ScheduledEventId == scheduled.EventId
                || e.ActivityTaskCanceledEventAttributes?.ScheduledEventId == scheduled.EventId)
            .Select(e => e.EventType)
            .ToArray();
    }

    /// <summary>
    /// The defect, with the switch off: the same restart fails the delegation. This is what
    /// "no bridge restart during consensus" has been protecting against.
    /// </summary>
    [Fact]
    public async Task WithTheSwitchOff_TheSameRestartFailsTheDelegation()
    {
        var client = await BridgeClientAsync();
        var id = "in-memory-restart-" + Guid.NewGuid().ToString("N");

        WorkflowHandle<DelegationProbeWorkflow, string> handle;
        await using (var first = new Bridge(client, id, StateFile, _relay, durable: false))
        {
            handle = await client.StartWorkflowAsync(
                (DelegationProbeWorkflow wf) => wf.RunAsync(new DelegationProbe(60)),
                new WorkflowOptions(id: id, taskQueue: id));
            await WaitForAsync(() => first.Registry.PendingCount == 1, "the activity registered in memory");
        }

        await using var second = new Bridge(client, id, StateFile, _relay, durable: false);
        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
        Assert.IsType<ActivityFailureException>(failure.InnerException);
        // And the agent's answer, when it comes, belongs to nothing.
        Assert.False(await second.Listener.TryRouteReplyAsync(Reply($"{id}/probe", "[status: completed] too late")));
        Assert.False(File.Exists(StateFile));
    }

    // ── heartbeats ────────────────────────────────────────────────────────────

    /// <summary>
    /// The passes really keep the activity alive: it outlives several of its own heartbeat
    /// timeouts with nothing running inside any worker.
    /// </summary>
    [Fact]
    public async Task ThePasses_KeepAnActivityAlivePastItsHeartbeatTimeout()
    {
        var client = await BridgeClientAsync();
        var id = "durable-heartbeat-" + Guid.NewGuid().ToString("N");
        await using var bridge = new Bridge(client, id, StateFile, _relay, durable: true);

        var handle = await client.StartWorkflowAsync(
            (DelegationProbeWorkflow wf) => wf.RunAsync(new DelegationProbe(3)),
            new WorkflowOptions(id: id, taskQueue: id));
        await WaitForAsync(() => _relay.Published.Count == 1, "the directive was published");

        await Task.Delay(TimeSpan.FromSeconds(8));
        Assert.True(await IsRunningAsync(handle));

        Assert.True(await bridge.Listener.TryRouteReplyAsync(Reply($"{id}/probe", "[status: completed] still here")));
        Assert.Equal("completed:still here", await handle.GetResultAsync());
    }

    /// <summary>
    /// The limit, and the decision for a record whose activity no longer exists. A bridge that is
    /// away for longer than the activity's heartbeat timeout loses the delegation: Temporal times
    /// it out. The next bridge's first pass is told the activity is gone, tells the agent to stop,
    /// and drops the record.
    /// </summary>
    [Fact]
    public async Task ABridgeAwayLongerThanTheHeartbeatTimeout_LosesTheDelegation_AndTheRecordIsDropped()
    {
        var client = await BridgeClientAsync();
        var id = "durable-outage-" + Guid.NewGuid().ToString("N");

        WorkflowHandle<DelegationProbeWorkflow, string> handle;
        await using (var first = new Bridge(client, id, StateFile, _relay, durable: true))
        {
            handle = await client.StartWorkflowAsync(
                (DelegationProbeWorkflow wf) => wf.RunAsync(new DelegationProbe(3)),
                new WorkflowOptions(id: id, taskQueue: id));
            await WaitForAsync(() => _relay.Published.Count == 1, "the directive was published");
        }

        // Nobody heartbeats. A worker is needed for the workflow to observe the timeout, so the
        // second bridge has one, but its durable service is not started yet.
        await using var second = new Bridge(client, id, StateFile, _relay, durable: true, runService: false);
        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
        var activityFailure = Assert.IsType<ActivityFailureException>(failure.InnerException);
        var timeout = Assert.IsType<TimeoutFailureException>(activityFailure.InnerException);
        Assert.Equal(TimeoutType.Heartbeat, timeout.TimeoutType);
        Assert.Single((await second.Durable.SnapshotAsync()).Pending);

        await second.Durable.TickAsync(default);

        Assert.Empty((await second.Durable.SnapshotAsync()).Pending);
        Assert.Equal((Agent, $"{id}/probe"), Assert.Single(_relay.Cancels));
        // The agent's late answer then belongs to nothing, as an orphan always did.
        Assert.False(await second.Listener.TryRouteReplyAsync(Reply($"{id}/probe", "[status: completed] too late")));
    }

    // ── cancel and budget ─────────────────────────────────────────────────────

    [Fact]
    public async Task AWorkflowCancel_ReachesTheAgent_AndTheActivityIsReportedCancelled()
    {
        var client = await BridgeClientAsync();
        var id = "durable-cancel-" + Guid.NewGuid().ToString("N");
        await using var bridge = new Bridge(client, id, StateFile, _relay, durable: true);

        var handle = await client.StartWorkflowAsync(
            (DelegationProbeWorkflow wf) => wf.RunAsync(new DelegationProbe(30, 0, true)),
            new WorkflowOptions(id: id, taskQueue: id));
        await WaitForAsync(() => _relay.Published.Count == 1, "the directive was published");

        await handle.CancelAsync();

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
        Assert.IsType<CanceledFailureException>(failure.InnerException);
        Assert.Equal((Agent, $"{id}/probe"), Assert.Single(_relay.Cancels));
        Assert.Empty((await bridge.Durable.SnapshotAsync()).Pending);
        Assert.Contains(EventType.ActivityTaskCanceled, ActivityEvents(await handle.FetchHistoryAsync()));
    }

    /// <summary>
    /// A budget that runs out fails the activity with the same failure the workflow has always
    /// seen: an application failure of type <c>TimeoutException</c> whose message carries the
    /// measured wait. Run once each way, the two differ only in the measured number.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABudgetThatRunsOut_FailsTheActivityTheSameWay_WhicheverPathWaited(bool durable)
    {
        var client = await BridgeClientAsync();
        var id = (durable ? "durable-budget-" : "in-memory-budget-") + Guid.NewGuid().ToString("N");
        await using var bridge = new Bridge(client, id, StateFile, _relay, durable);

        var handle = await client.StartWorkflowAsync(
            (DelegationProbeWorkflow wf) => wf.RunAsync(new DelegationProbe(60, 2)),
            new WorkflowOptions(id: id, taskQueue: id));

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
        var activityFailure = Assert.IsType<ActivityFailureException>(failure.InnerException);
        var application = Assert.IsType<ApplicationFailureException>(activityFailure.InnerException);
        Assert.Equal("TimeoutException", application.ErrorType);
        Assert.False(application.NonRetryable);
        Assert.Matches(
            $@"^Agent agent-a did not respond — elapsed 2\.\ds \(budget 2\.0s, taskId={id}/probe, workflowId={id}\)$",
            application.Message);

        if (durable)
        {
            Assert.Equal($"{id}/probe", Assert.Single(_relay.TimeoutReports).TaskId);
            Assert.Empty((await bridge.Durable.SnapshotAsync()).Pending);
        }
    }
}

public sealed record DelegationProbe(int HeartbeatSeconds, int BudgetSeconds = 0, bool WaitForCancel = false);

/// <summary>
/// Schedules one <c>DelegateToAgent</c> the way the engine does (by name, single attempt), with a
/// heartbeat timeout short enough to wait out in a test.
/// </summary>
[Workflow("DelegationProbe")]
public sealed class DelegationProbeWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(DelegationProbe probe)
    {
        var result = await Workflow.ExecuteActivityAsync<AgentTaskResult>(
            DelegateToAgentActivity.ActivityName,
            new object?[] { "agent-a", "Do the work.", $"{Workflow.Info.WorkflowId}/probe", true, 3, probe.BudgetSeconds },
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(5),
                HeartbeatTimeout = TimeSpan.FromSeconds(probe.HeartbeatSeconds),
                RetryPolicy = new() { MaximumAttempts = 1 },
                CancellationType = probe.WaitForCancel
                    ? ActivityCancellationType.WaitCancellationCompleted
                    : ActivityCancellationType.TryCancel,
            });
        return $"{result.Status}:{result.Text}";
    }
}
