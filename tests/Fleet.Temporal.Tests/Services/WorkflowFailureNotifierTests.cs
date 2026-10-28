using Fleet.Temporal.Configuration;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// #488 focused behaviour of <see cref="WorkflowFailureNotifier"/> with its two Temporal edges faked:
/// what is listed, and whether the NotifyCtoWorkflow start succeeds. The connected path against a real
/// server is <see cref="WorkflowFailureNotifierLocalTests"/>.
/// </summary>
public class WorkflowFailureNotifierTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static ClosedRun Run(string id, string status = "Failed", string ns = "fleet") =>
        new(ns, id, $"run-{id}", "SomeWorkflow", status, Now.AddMinutes(-5));

    private static WorkflowFailureNotifier Notifier(
        FakeSource source, FakeSender sender, string coordinator = "coord", string cto = "",
        int maxAttempts = 5, bool enabled = true, params string[] namespaces)
    {
        var options = new WorkflowFailureNotifierOptions
        {
            Enabled = enabled,
            CoordinatorAgent = coordinator,
            MaxDeliveryAttempts = maxAttempts,
        };
        var bridge = new TemporalBridgeOptions { Namespaces = namespaces.Length > 0 ? [.. namespaces] : ["fleet"] };
        return new WorkflowFailureNotifier(
            Options.Create(options), Options.Create(bridge), new FixedCto(cto), source, sender,
            NullLogger<WorkflowFailureNotifier>.Instance, new FixedTime(Now));
    }

    [Fact]
    public async Task FailedTimedOutAndExplicitBusinessFailure_ReachTheCoordinatorWithTheirIdentity()
    {
        var source = new FakeSource();
        source.Add(Run("plain"), "application_error:InvalidOperation");
        source.Add(Run("slow", "TimedOut"), "timeout:workflow_execution");
        source.Add(Run("business"), WorkflowFailureClassifier.ExplicitFailCategory);
        var sender = new FakeSender();

        await Notifier(source, sender).RunCycleAsync(CancellationToken.None);

        Assert.Equal(3, sender.Delivered.Count);
        Assert.All(sender.Delivered, d => Assert.Equal("coord", d.Coordinator));
        foreach (var id in new[] { "plain", "slow", "business" })
        {
            var notice = Assert.Single(sender.Delivered, d => d.Id == $"workflow-failure-fleet-run-{id}");
            Assert.Contains($"workflowId: {id}", notice.Text);
            Assert.Contains($"runId: run-{id}", notice.Text);
            Assert.Contains("namespace: fleet", notice.Text);
        }
        Assert.Contains("status: TimedOut", sender.Delivered.Single(d => d.Id.EndsWith("run-slow")).Text);
        Assert.Contains("(explicit business failure)", sender.Delivered.Single(d => d.Id.EndsWith("run-business")).Text);
    }

    [Fact]
    public async Task WithoutAnOverride_TheConfiguredCtoIsTheRecipient()
    {
        var source = new FakeSource();
        source.Add(Run("a"), "unknown");
        var sender = new FakeSender();

        await Notifier(source, sender, coordinator: "", cto: "cto-agent").RunCycleAsync(CancellationToken.None);

        Assert.Equal("cto-agent", Assert.Single(sender.Delivered).Coordinator);
    }

    [Fact]
    public async Task RepeatPollsAndARestart_DoNotNotifyTheSameFailureAgain()
    {
        var source = new FakeSource();
        source.Add(Run("a"), "unknown");
        var sender = new FakeSender();

        var first = Notifier(source, sender);
        await first.RunCycleAsync(CancellationToken.None);
        await first.RunCycleAsync(CancellationToken.None);

        // A restart loses memory; the persisted notification id (duplicate rejection) is what stops it.
        var restarted = Notifier(source, sender);
        await restarted.RunCycleAsync(CancellationToken.None);

        Assert.Single(sender.Delivered);
        Assert.Equal(2, sender.Attempts);   // the repeat poll did not even try; the restart hit the duplicate
        Assert.Equal(1, sender.Duplicates);
        Assert.Empty(restarted.Snapshot().Pending);
        Assert.Equal(0, restarted.Snapshot().Sent);
    }

    [Fact]
    public async Task ADeliveryOutage_KeepsTheFailurePendingAndVisibleThenDeliversItUnderTheSameId()
    {
        var source = new FakeSource();
        source.Add(Run("a"), "unknown");
        var sender = new FakeSender { FailNext = 2 };
        var notifier = Notifier(source, sender);

        await notifier.RunCycleAsync(CancellationToken.None);
        var pending = Assert.Single(notifier.Snapshot().Pending);
        Assert.Equal(("a", "run-a", 1, "InvalidOperationException"), (pending.WorkflowId, pending.RunId, pending.Attempts, pending.LastError));
        Assert.Empty(sender.Delivered);

        await notifier.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, Assert.Single(notifier.Snapshot().Pending).Attempts);

        await notifier.RunCycleAsync(CancellationToken.None);
        Assert.Equal("workflow-failure-fleet-run-a", Assert.Single(sender.Delivered).Id);
        Assert.Empty(notifier.Snapshot().Pending);
        Assert.Equal(1, notifier.Snapshot().Sent);
    }

    [Fact]
    public async Task AttemptsAreBounded_AndAnUndeliveredFailureIsParkedVisibly()
    {
        var source = new FakeSource();
        source.Add(Run("a"), "unknown");
        var sender = new FakeSender { FailNext = int.MaxValue };
        var notifier = Notifier(source, sender, maxAttempts: 2);

        for (var i = 0; i < 4; i++) await notifier.RunCycleAsync(CancellationToken.None);

        Assert.Equal(2, sender.Attempts);
        var parked = Assert.Single(notifier.Snapshot().Parked);
        Assert.Equal(("a", 2), (parked.WorkflowId, parked.Attempts));
        Assert.Empty(notifier.Snapshot().Pending);
    }

    [Fact]
    public async Task WithNoCoordinator_FailuresAreHeldWithoutGuessingARecipient()
    {
        var source = new FakeSource();
        source.Add(Run("a"), "unknown");
        var sender = new FakeSender();
        var cto = new FixedCto("");
        var options = new WorkflowFailureNotifierOptions { Enabled = true };
        var notifier = new WorkflowFailureNotifier(
            Options.Create(options), Options.Create(new TemporalBridgeOptions { Namespaces = ["fleet"] }), cto,
            source, sender, NullLogger<WorkflowFailureNotifier>.Instance, new FixedTime(Now));

        await notifier.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, sender.Attempts);
        var status = notifier.Snapshot();
        Assert.False(status.CoordinatorConfigured);
        Assert.Equal(0, Assert.Single(status.Pending).Attempts);

        cto.Value = "cto-agent";
        await notifier.RunCycleAsync(CancellationToken.None);
        Assert.Equal("cto-agent", Assert.Single(sender.Delivered).Coordinator);
    }

    [Fact]
    public async Task OwnNotificationRuns_AreNeverNotifiedAbout()
    {
        var source = new FakeSource();
        source.Add(Run("workflow-failure-fleet-run-x"), "unknown");
        var sender = new FakeSender();

        await Notifier(source, sender).RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, sender.Attempts);
    }

    [Fact]
    public async Task OneUnreachableNamespace_DoesNotStopTheOthers()
    {
        var source = new FakeSource { BrokenNamespace = "mml" };
        source.Add(Run("a", ns: "fleet"), "unknown");
        var sender = new FakeSender();

        await Notifier(source, sender, namespaces: ["mml", "fleet"]).RunCycleAsync(CancellationToken.None);

        Assert.Equal("workflow-failure-fleet-run-a", Assert.Single(sender.Delivered).Id);
    }

    [Fact]
    public async Task WhenDisabled_NothingIsListedOrSent()
    {
        var source = new FakeSource();
        source.Add(Run("a"), "unknown");
        var sender = new FakeSender();
        var notifier = Notifier(source, sender, enabled: false);

        await notifier.StartAsync(CancellationToken.None);
        await notifier.StopAsync(CancellationToken.None);

        Assert.Equal(0, source.ListCalls);
        Assert.Equal(0, sender.Attempts);
    }

    [Fact]
    public void TheCauseIsBuiltFromTypeNames_NeverFromFailureText()
    {
        var secret = "password=hunter2 sk-live-SECRET";

        Assert.Equal(WorkflowFailureClassifier.ExplicitFailCategory,
            WorkflowFailureClassifier.Category(new ApplicationFailureException(secret, errorType: "ExplicitFail")));
        var weird = WorkflowFailureClassifier.Category(
            new ApplicationFailureException(secret, errorType: "Bad Type <script>alert(1)</script> " + new string('x', 200)));
        Assert.DoesNotContain("hunter2", weird);
        Assert.DoesNotContain("<", weird);
        Assert.DoesNotContain(" ", weird);
        Assert.True(weird.Length <= "application_error:".Length + 80);
        Assert.Equal("application_error:unspecified",
            WorkflowFailureClassifier.Category(new ApplicationFailureException(secret)));
        Assert.Equal("unknown", WorkflowFailureClassifier.Category(null));

        var notice = WorkflowFailureNotifier.ComposeNotice(Run("a"), weird);
        Assert.DoesNotContain("hunter2", notice);
        Assert.DoesNotContain("SECRET", notice);
    }

    [Fact]
    public void TheOptInKey_IsOffByDefaultOnEveryInstallSurface()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        string Read(string path) => File.ReadAllText(Path.Combine(root!.FullName, path));

        Assert.Contains("FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED=false", Read(".env.example"));
        Assert.Contains("WorkflowFailureNotifier__Enabled=${FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED:-false}", Read("docker-compose.example.yml"));
        Assert.Contains("FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED=false; do", Read("setup.sh"));
        Assert.Contains("add_missing_env_var FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED false", Read("upgrade.sh"));
        Assert.Contains("\"Enabled\": false", Read("src/Fleet.Temporal/appsettings.json"));
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

    /// <summary>Stands in for Temporal: started ids persist, so a second start of one is a duplicate.</summary>
    private sealed class FakeSender : IFailureNotificationSender
    {
        private readonly HashSet<string> _started = [];
        public List<(string Id, string Coordinator, string Text)> Delivered { get; } = [];
        public int FailNext { get; set; }
        public int Attempts { get; private set; }
        public int Duplicates { get; private set; }

        public Task<bool> SendAsync(string notificationId, string coordinator, string text, CancellationToken ct)
        {
            Attempts++;
            if (FailNext > 0)
            {
                FailNext--;
                throw new InvalidOperationException("delivery path down");
            }
            if (!_started.Add(notificationId))
            {
                Duplicates++;
                return Task.FromResult(false);
            }
            Delivered.Add((notificationId, coordinator, text));
            return Task.FromResult(true);
        }
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
