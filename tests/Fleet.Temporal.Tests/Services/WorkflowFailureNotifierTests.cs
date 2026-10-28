using Fleet.Temporal.Configuration;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// #488 focused behaviour of <see cref="WorkflowFailureNotifier"/>: its two Temporal edges are faked
/// (what is listed; starting a notification run and reading its outcome), its state file is real. The
/// connected path against a real server is <see cref="WorkflowFailureNotifierLocalTests"/>.
/// </summary>
public sealed class WorkflowFailureNotifierTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("wf-failure-state-").FullName;
    private string StatePath => Path.Combine(_dir, "workflow-failures.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ClosedRun Run(string id, string status = "Failed", string ns = "fleet", double ageHours = 0.1) =>
        new(ns, id, $"run-{id}", "SomeWorkflow", status, Now.AddHours(-ageHours));

    private WorkflowFailureNotifier Notifier(
        FakeSource source, FakeSender sender, string coordinator = "coord", FixedCto? cto = null,
        int maxAttempts = 5, bool enabled = true, DateTime? now = null, params string[] namespaces) =>
        new(Options.Create(new WorkflowFailureNotifierOptions
            {
                Enabled = enabled,
                CoordinatorAgent = coordinator,
                MaxDeliveryAttempts = maxAttempts,
                StateFile = StatePath,
            }),
            Options.Create(new TemporalBridgeOptions { Namespaces = namespaces.Length > 0 ? [.. namespaces] : ["fleet"] }),
            cto ?? new FixedCto(""), source, sender, NullLogger<WorkflowFailureNotifier>.Instance, new FixedTime(now ?? Now));

    private static async Task Cycles(WorkflowFailureNotifier notifier, int count)
    {
        for (var i = 0; i < count; i++) await notifier.RunCycleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedTimedOutAndExplicitBusinessFailure_ReachTheCoordinatorWithTheirIdentity()
    {
        var source = new FakeSource();
        source.Add(Run("plain"), WorkflowFailureClassifier.ApplicationError);
        source.Add(Run("slow", "TimedOut"), WorkflowFailureClassifier.WorkflowTimeout);
        source.Add(Run("business"), WorkflowFailureClassifier.ExplicitFail);
        var sender = new FakeSender();

        await Notifier(source, sender).RunCycleAsync(CancellationToken.None);

        Assert.Equal(3, sender.Started.Count);
        Assert.All(sender.Started, d => Assert.Equal("coord", d.Coordinator));
        foreach (var id in new[] { "plain", "slow", "business" })
        {
            var notice = Assert.Single(sender.Started, d => d.Id == $"workflow-failure-fleet-run-{id}-1");
            Assert.Contains($"workflowId: {id}", notice.Text);
            Assert.Contains($"runId: run-{id}", notice.Text);
        }
        Assert.Contains("status: TimedOut", sender.Started.Single(d => d.Id.Contains("run-slow")).Text);
        Assert.Contains("cause: explicit_fail (explicit business failure)", sender.Started.Single(d => d.Id.Contains("run-business")).Text);
    }

    [Fact]
    public async Task WithoutAnOverride_TheConfiguredCtoIsTheRecipient()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();

        await Notifier(source, sender, coordinator: "", cto: new FixedCto("cto-agent")).RunCycleAsync(CancellationToken.None);

        Assert.Equal("cto-agent", Assert.Single(sender.Started).Coordinator);
    }

    [Fact]
    public async Task AStartedNotificationIsNotDelivery_AFailedRunIsRetriedUnderTheNextAttemptId()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();
        var notifier = Notifier(source, sender);

        await notifier.RunCycleAsync(CancellationToken.None);
        Assert.Equal("in_flight", Assert.Single(notifier.Snapshot().InFlight).State);
        Assert.Equal(0, notifier.Snapshot().Delivered);

        // The notification run closed without delivering (broker or agent failure).
        sender.Outcomes["workflow-failure-fleet-run-a-1"] = NotificationOutcome.Failed;
        await notifier.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, sender.Started.Count);
        Assert.Equal("workflow-failure-fleet-run-a-2", sender.Started[1].Id);
        var retried = Assert.Single(notifier.Snapshot().InFlight);
        Assert.Equal((1, WorkflowFailureClassifier.DeliveryFailed), (retried.Attempts, retried.LastError));

        sender.Outcomes["workflow-failure-fleet-run-a-2"] = NotificationOutcome.Delivered;
        await notifier.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, notifier.Snapshot().Delivered);
        Assert.Empty(notifier.Snapshot().InFlight);

        await Cycles(notifier, 2);
        Assert.Equal(2, sender.Started.Count);
    }

    [Fact]
    public async Task AHandOverOutage_KeepsTheFailurePendingAndVisible_ThenDeliversIt()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender { FailStarts = 2 };
        var notifier = Notifier(source, sender);

        await notifier.RunCycleAsync(CancellationToken.None);
        var pending = Assert.Single(notifier.Snapshot().Pending);
        Assert.Equal(("a", 1, WorkflowFailureClassifier.HandoverFailed), (pending.WorkflowId, pending.Attempts, pending.LastError));

        await Cycles(notifier, 2);
        Assert.Equal("workflow-failure-fleet-run-a-3", Assert.Single(sender.Started).Id);
        sender.Outcomes["workflow-failure-fleet-run-a-3"] = NotificationOutcome.Delivered;
        await notifier.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, notifier.Snapshot().Delivered);
    }

    [Fact]
    public async Task AttemptsAreBounded_AndAParkedFailureStaysVisibleAcrossARestart()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender { FailStarts = int.MaxValue };

        await Cycles(Notifier(source, sender, maxAttempts: 2), 4);
        var restarted = Notifier(source, sender, maxAttempts: 2);
        await Cycles(restarted, 2);

        Assert.Equal(2, sender.StartCalls);
        var parked = Assert.Single(restarted.Snapshot().Parked);
        Assert.Equal(("a", 2, WorkflowFailureClassifier.HandoverFailed), (parked.WorkflowId, parked.Attempts, parked.LastError));
    }

    [Fact]
    public async Task AnUndeliveredFailureOlderThanTheLookback_SurvivesARestartFromTheStateFile()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();
        var cto = new FixedCto("");
        await Notifier(source, sender, coordinator: "", cto: cto).RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, sender.StartCalls);

        // A day later the run is far outside the lookback and no longer listed; the bridge restarts.
        var empty = new FakeSource();
        cto.Value = "cto-agent";
        var restarted = Notifier(empty, sender, coordinator: "", cto: cto, now: Now.AddDays(1));
        await restarted.RunCycleAsync(CancellationToken.None);

        Assert.Equal("workflow-failure-fleet-run-a-1", Assert.Single(sender.Started).Id);
        Assert.Equal("in_flight", Assert.Single(restarted.Snapshot().InFlight).State);
    }

    [Fact]
    public async Task ADeliveredFailure_IsNotNotifiedAgainAfterARestart()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();
        var first = Notifier(source, sender);
        await first.RunCycleAsync(CancellationToken.None);
        sender.Outcomes["workflow-failure-fleet-run-a-1"] = NotificationOutcome.Delivered;
        await first.RunCycleAsync(CancellationToken.None);

        var restarted = Notifier(source, sender);
        await Cycles(restarted, 2);

        Assert.Single(sender.Started);
        Assert.Equal(1, restarted.Snapshot().Delivered);
    }

    [Fact]
    public async Task ALostStateFile_IsRebuiltFromTheAttemptIdsWithoutANewNotification()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();
        var first = Notifier(source, sender);
        await first.RunCycleAsync(CancellationToken.None);
        sender.Outcomes["workflow-failure-fleet-run-a-1"] = NotificationOutcome.Delivered;
        File.Delete(StatePath);

        var restarted = Notifier(source, sender);
        await Cycles(restarted, 2);

        Assert.Single(sender.Started);
        Assert.Equal(1, sender.Duplicates);
        Assert.Equal(1, restarted.Snapshot().Delivered);
    }

    [Fact]
    public async Task AnUnreadableStateFile_StartsEmptyAndKeepsWorking()
    {
        File.WriteAllText(StatePath, "{ not json");
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();

        var notifier = Notifier(source, sender);
        await notifier.RunCycleAsync(CancellationToken.None);

        Assert.Single(sender.Started);
        Assert.True(notifier.Snapshot().Durable);
    }

    [Fact]
    public async Task WithNoCoordinator_FailuresAreHeldWithoutGuessingARecipient()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();
        var cto = new FixedCto("");
        var notifier = Notifier(source, sender, coordinator: "", cto: cto);

        await notifier.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, sender.StartCalls);
        Assert.False(notifier.Snapshot().CoordinatorConfigured);
        Assert.Equal(0, Assert.Single(notifier.Snapshot().Pending).Attempts);

        cto.Value = "cto-agent";
        await notifier.RunCycleAsync(CancellationToken.None);
        Assert.Equal("cto-agent", Assert.Single(sender.Started).Coordinator);
    }

    [Fact]
    public async Task OwnNotificationRuns_AreNeverNotifiedAbout()
    {
        var source = new FakeSource();
        source.Add(Run("workflow-failure-fleet-run-x-1"), WorkflowFailureClassifier.DeliveryFailed);
        var sender = new FakeSender();

        await Notifier(source, sender).RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, sender.StartCalls);
    }

    [Fact]
    public async Task OneUnreachableNamespace_DoesNotStopTheOthers()
    {
        var source = new FakeSource { BrokenNamespace = "mml" };
        source.Add(Run("a", ns: "fleet"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();

        await Notifier(source, sender, namespaces: ["mml", "fleet"]).RunCycleAsync(CancellationToken.None);

        Assert.Equal("workflow-failure-fleet-run-a-1", Assert.Single(sender.Started).Id);
    }

    [Fact]
    public async Task WhenDisabled_NothingIsListedOrSent()
    {
        var source = new FakeSource();
        source.Add(Run("a"), WorkflowFailureClassifier.Unknown);
        var sender = new FakeSender();
        var notifier = Notifier(source, sender, enabled: false);

        await notifier.StartAsync(CancellationToken.None);
        await notifier.StopAsync(CancellationToken.None);

        Assert.Equal(0, source.ListCalls);
        Assert.Equal(0, sender.StartCalls);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task CausesAreAClosedSet_CallerErrorTypeTextNeverReachesANoticeOrTheStateFile()
    {
        const string secret = "synthetic-secret-123";
        Assert.Equal(WorkflowFailureClassifier.ApplicationError,
            WorkflowFailureClassifier.Category(new ApplicationFailureException("x", errorType: secret)));
        Assert.Equal(WorkflowFailureClassifier.ExplicitFail,
            WorkflowFailureClassifier.Category(new ApplicationFailureException("x " + secret, errorType: "ExplicitFail")));
        Assert.Equal(WorkflowFailureClassifier.RefusedSignal,
            WorkflowFailureClassifier.Category(new ApplicationFailureException("x", errorType: "ReservedSignalName")));
        Assert.Equal(WorkflowFailureClassifier.Unknown, WorkflowFailureClassifier.Category(null));
        Assert.Equal(WorkflowFailureClassifier.Unknown, WorkflowFailureClassifier.Known(secret));

        // Even a source or an edited state file that returns free text is reduced to the closed set.
        var source = new FakeSource();
        source.Add(Run("a"), secret);
        var sender = new FakeSender();
        await Notifier(source, sender).RunCycleAsync(CancellationToken.None);
        Assert.Contains("cause: unknown", Assert.Single(sender.Started).Text);
        Assert.DoesNotContain(secret, File.ReadAllText(StatePath));

        File.WriteAllText(StatePath, File.ReadAllText(StatePath).Replace("\"unknown\"", $"\"{secret}\""));
        var restarted = Notifier(source, sender);
        Assert.DoesNotContain(secret, restarted.Snapshot().InFlight.Single().ToString());
        Assert.DoesNotContain(secret, WorkflowFailureNotifier.ComposeNotice(Run("a"), secret));
    }

    [Fact]
    public void TheOptInKeyAndStateVolume_AreOnEveryInstallSurface()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        string Read(string path) => File.ReadAllText(Path.Combine(root!.FullName, path));

        Assert.Contains("FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED=false", Read(".env.example"));
        var compose = Read("docker-compose.example.yml");
        Assert.Contains("WorkflowFailureNotifier__Enabled=${FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED:-false}", compose);
        Assert.Contains("${FLEET_BASE_DIR}/temporal-bridge:/var/lib/fleet-temporal-bridge", compose);
        Assert.Contains("FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED=false; do", Read("setup.sh"));
        Assert.Contains("add_missing_env_var FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED false", Read("upgrade.sh"));
        Assert.Contains("\"Enabled\": false", Read("src/Fleet.Temporal/appsettings.json"));
        Assert.Equal("/var/lib/fleet-temporal-bridge/workflow-failures.json", new WorkflowFailureNotifierOptions().StateFile);
    }

    private sealed class FakeSource : IWorkflowFailureSource
    {
        private readonly List<ClosedRun> _runs = [];
        private readonly Dictionary<string, string> _causes = new();
        public string? BrokenNamespace { get; init; }
        public int ListCalls { get; private set; }

        public void Add(ClosedRun run, string cause)
        {
            _runs.Add(run);
            _causes[run.Identity] = cause;
        }

        public Task<IReadOnlyList<ClosedRun>> ListFailedAsync(string @namespace, DateTime sinceUtc, CancellationToken ct)
        {
            ListCalls++;
            if (@namespace == BrokenNamespace) throw new TimeoutException("unreachable");
            return Task.FromResult<IReadOnlyList<ClosedRun>>(
                _runs.Where(r => r.Namespace == @namespace && r.CloseTime >= sinceUtc).ToList());
        }

        public Task<string> CauseAsync(ClosedRun run, CancellationToken ct) => Task.FromResult(_causes[run.Identity]);
    }

    /// <summary>Stands in for Temporal: started ids persist, and each run's outcome is set by the test.</summary>
    private sealed class FakeSender : IFailureNotificationSender
    {
        private readonly HashSet<string> _ids = [];
        public List<(string Id, string Coordinator, string Text)> Started { get; } = [];
        public Dictionary<string, NotificationOutcome> Outcomes { get; } = new();
        public int FailStarts { get; set; }
        public int StartCalls { get; private set; }
        public int Duplicates { get; private set; }

        public Task<bool> StartAsync(string notificationId, string coordinator, string text, CancellationToken ct)
        {
            StartCalls++;
            if (FailStarts > 0)
            {
                FailStarts--;
                throw new InvalidOperationException("hand-over path down");
            }
            if (!_ids.Add(notificationId))
            {
                Duplicates++;
                return Task.FromResult(false);
            }
            Started.Add((notificationId, coordinator, text));
            return Task.FromResult(true);
        }

        public Task<NotificationOutcome> OutcomeAsync(string notificationId, CancellationToken ct) =>
            Task.FromResult(Outcomes.GetValueOrDefault(notificationId, NotificationOutcome.Running));
    }

    private sealed class FixedCto(string value) : CtoAgentConfigService
    {
        public string Value { get; set; } = value;
        public override string GetCtoAgent() => Value;
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}

/// <summary>
/// #488 change to NotifyCtoWorkflow: delivery-failure reporting is opt-in. The input shape notify_cto
/// sends (no flag) still swallows a failed delegation; the notifier's flag closes the run Failed.
/// </summary>
public class NotifyCtoDeliveryReportingTests
{
    private static Temporalio.Worker.TemporalWorker Worker(Temporalio.Client.ITemporalClient client, string queue) =>
        new(client, new Temporalio.Worker.TemporalWorkerOptions(queue)
            .AddWorkflow<Fleet.Temporal.Workflows.Fleet.NotifyCtoWorkflow>()
            .AddActivity(Temporalio.Activities.ActivityDefinition.Create(
                Fleet.Temporal.Activities.DelegateToAgentActivity.ActivityName,
                typeof(Fleet.Temporal.Models.AgentTaskResult),
                [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int), typeof(int), typeof(string)],
                3,
                _ => throw new IOException("broker unavailable"))));

    [Fact]
    public async Task TheNotifyCtoInputShape_StillSwallowsAFailedDelegation()
    {
        await using var env = await Temporalio.Testing.WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = $"notify-cto-{Guid.NewGuid():N}";
        using var worker = Worker(env.Client, queue);

        await worker.ExecuteAsync(async () =>
        {
            // Exactly what TemporalWorkflowDispatcher sends for notify_cto.
            var handle = await env.Client.StartWorkflowAsync(
                "NotifyCtoWorkflow", [new { TargetAgent = "agent-a", TaskDescription = "heads up" }],
                new Temporalio.Client.WorkflowOptions($"notify-cto-{Guid.NewGuid():N}", queue));
            await handle.GetResultAsync();
            Assert.Equal(Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Completed, (await handle.DescribeAsync()).Status);
        });
    }

    [Fact]
    public async Task WithDeliveryReporting_AFailedDelegationClosesTheRunFailedWithAFixedType()
    {
        await using var env = await Temporalio.Testing.WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = $"notify-cto-{Guid.NewGuid():N}";
        using var worker = Worker(env.Client, queue);

        await worker.ExecuteAsync(async () =>
        {
            var ex = await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(() =>
                env.Client.ExecuteWorkflowAsync(
                    (Fleet.Temporal.Workflows.Fleet.NotifyCtoWorkflow wf) =>
                        wf.RunAsync(new Fleet.Temporal.Workflows.Fleet.NotifyCtoWorkflowInput("agent-a", "heads up", ReportDeliveryFailure: true)),
                    new Temporalio.Client.WorkflowOptions($"notify-cto-{Guid.NewGuid():N}", queue)));
            var failure = Assert.IsType<ApplicationFailureException>(ex.InnerException);
            Assert.Equal("NotificationDeliveryFailed", failure.ErrorType);
            Assert.DoesNotContain("broker", failure.Message);
        });
    }
}
