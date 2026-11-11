using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services.Push;

namespace Fleet.Orchestrator.Tests.Push;

/// <summary>
/// Which changes in the fleet are worth a notification, and that each is announced once.
/// </summary>
public class PushEventDetectorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static WorkflowSummary Run(
        string id = "pr-42", string runId = "run-1", string type = "UwePrImplementationWorkflow",
        string status = "Running", string? phase = "merge-approval", string? gateRef = "gate-a",
        int? pr = 42, string? repo = "example/project") =>
        new(id, runId, type, "fleet", "fleet", status, DateTimeOffset.UtcNow.AddHours(-1), PrNumber: pr, Repo: repo, Phase: phase, GateRef: gateRef);

    private static AgentState Agent(string name, TimeSpan silentFor, string status = "idle", string? task = null, bool dbOnly = false) =>
        new() { AgentName = name, ReportedStatus = status, CurrentTask = task, LastSeen = DateTimeOffset.UtcNow - silentFor, IsDbOnly = dbOnly };

    private static (PushEventDetector Detector, OperatorStops Stops) NewDetector()
    {
        var stops = new OperatorStops();
        return (new PushEventDetector(Grace, stops), stops);
    }

    private static IReadOnlyList<PushMessage> Observe(
        PushEventDetector detector, WorkflowSummary[]? running = null, WorkflowSummary[]? failed = null, AgentState[]? agents = null,
        DateTimeOffset? now = null) =>
        detector.Observe(running ?? [], failed ?? [], agents ?? [], now ?? DateTimeOffset.UtcNow);

    // ── Start-up ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WhatIsAlreadyTrueAtStartUp_IsNotAnnounced()
    {
        var (detector, _) = NewDetector();

        // An orchestrator restart must not re-announce every gate that has waited since yesterday.
        var first = Observe(detector,
            running: [Run()],
            failed: [Run("pr-7", status: "Failed", phase: null)],
            agents: [Agent("builder", TimeSpan.FromHours(1))]);

        Assert.Empty(first);
        Assert.Empty(Observe(detector,
            running: [Run()],
            failed: [Run("pr-7", status: "Failed", phase: null)],
            agents: [Agent("builder", TimeSpan.FromHours(1))]));
    }

    // ── Gates ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARunReachingAGate_IsAnnouncedOnce()
    {
        var (detector, _) = NewDetector();
        Observe(detector, running: [Run(phase: "implementation", gateRef: null)]);

        var message = Assert.Single(Observe(detector, running: [Run()]));

        Assert.Equal("gate", message.Kind);
        Assert.Equal("Merge approval waiting", message.Title);
        Assert.Equal("example/project · PR #42", message.Body);
        Assert.Equal("merge-approval", message.Target["signal"]);
        Assert.Equal("run-1", message.Target["runId"]);
        Assert.Empty(Observe(detector, running: [Run()]));
    }

    [Fact]
    public void ARunThatLeavesAGateAndComesBack_IsAnnouncedAgain()
    {
        var (detector, _) = NewDetector();
        Observe(detector);
        Assert.Single(Observe(detector, running: [Run(gateRef: null)]));

        Observe(detector, running: [Run(phase: "implementation", gateRef: null)]);

        // Changes were requested, the work was redone, and it is waiting again: a new question.
        Assert.Single(Observe(detector, running: [Run(gateRef: null)]));
    }

    [Fact]
    public void ANewVisitToTheSameGate_IsAnnouncedEvenWithoutLeavingThePhase()
    {
        var (detector, _) = NewDetector();
        Observe(detector);
        Assert.Single(Observe(detector, running: [Run(gateRef: "gate-a")]));

        Assert.Single(Observe(detector, running: [Run(gateRef: "gate-b")]));
    }

    [Theory]
    [InlineData("implementation")]
    [InlineData(null)]
    [InlineData("")]
    public void ARunInAPhaseNoSignalNames_IsNotWaiting(string? phase)
    {
        var (detector, _) = NewDetector();
        Observe(detector);

        Assert.Empty(Observe(detector, running: [Run(phase: phase)]));
    }

    [Fact]
    public void ASignalAcceptedAtAnyTime_DoesNotMakeARunWait()
    {
        var (detector, _) = NewDetector();
        Observe(detector);

        // ExternalAdvisoryWorkflow registers a signal with no phase list.
        Assert.Empty(Observe(detector, running: [Run(type: "ExternalAdvisoryWorkflow", phase: "review")]));
    }

    // ── Failures ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Failed", "Workflow failed")]
    [InlineData("TimedOut", "Workflow timed out")]
    public void ARunThatFailed_IsAnnouncedOnce(string status, string title)
    {
        var (detector, _) = NewDetector();
        Observe(detector);
        var failed = Run("pr-7", status: status, phase: null, pr: 7);

        var message = Assert.Single(Observe(detector, failed: [failed]));

        Assert.Equal("run", message.Kind);
        Assert.Equal(title, message.Title);
        Assert.Equal("example/project · PR #7 · UwePrImplementationWorkflow", message.Body);
        Assert.Empty(Observe(detector, failed: [failed]));
    }

    [Theory]
    [InlineData("Canceled")]
    [InlineData("Terminated")]
    public void ARunSomeoneStopped_IsNotAnnounced(string status)
    {
        var (detector, _) = NewDetector();
        Observe(detector);

        // Whoever cancelled or terminated it already knows.
        Assert.Empty(Observe(detector, failed: [Run("pr-7", status: status, phase: null)]));
    }

    // ── Agents ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAgentThatWasAlive_AndIsSilentPastTheGrace_IsAnnouncedOnce()
    {
        var (detector, _) = NewDetector();
        Observe(detector, agents: [Agent("builder", TimeSpan.Zero)]);

        // Dead by the registry's reckoning, but not yet for long enough to be worth a notification.
        var silent = Agent("builder", TimeSpan.FromMinutes(2));
        Assert.Empty(Observe(detector, agents: [silent]));

        // The same silence, four minutes on: its last heartbeat has not moved, the clock has.
        var message = Assert.Single(Observe(detector, agents: [silent], now: DateTimeOffset.UtcNow.AddMinutes(4)));
        Assert.Equal("agent", message.Kind);
        Assert.Equal("Agent down", message.Title);
        Assert.Equal("builder", message.Body);
        Assert.Equal("builder", message.Target["agentName"]);

        Assert.Empty(Observe(detector, agents: [silent], now: DateTimeOffset.UtcNow.AddMinutes(30)));
    }

    [Fact]
    public void AnAgentThatRecoversAndGoesDownAgain_IsAnnouncedAgain()
    {
        var (detector, _) = NewDetector();
        Observe(detector, agents: [Agent("builder", TimeSpan.Zero)]);
        var first = Assert.Single(Observe(detector, agents: [Agent("builder", TimeSpan.FromMinutes(6))]));

        Observe(detector, agents: [Agent("builder", TimeSpan.Zero)]);
        var second = Assert.Single(Observe(detector, agents: [Agent("builder", TimeSpan.FromMinutes(7))]));

        // A second outage is its own notification on the phone, not a replacement of the first.
        Assert.NotEqual(first.CollapseId, second.CollapseId);
    }

    [Fact]
    public void AnAgentStoppedOnPurpose_IsNotAnnounced_UntilItHasRunAgain()
    {
        var (detector, stops) = NewDetector();
        Observe(detector, agents: [Agent("builder", TimeSpan.Zero)]);
        stops.MarkStopped("builder");

        Assert.Empty(Observe(detector, agents: [Agent("builder", TimeSpan.FromMinutes(6))]));
        Assert.Empty(Observe(detector, agents: [Agent("builder", TimeSpan.FromHours(2))]));

        // Started again: the next silence is not one anyone asked for.
        Observe(detector, agents: [Agent("builder", TimeSpan.Zero)]);
        Assert.False(stops.Contains("builder"));
        Assert.Single(Observe(detector, agents: [Agent("builder", TimeSpan.FromMinutes(6))]));
    }

    [Fact]
    public void AnAgentThisProcessNeverSawAlive_CannotBeSaidToHaveGoneDown()
    {
        var (detector, _) = NewDetector();
        Observe(detector);

        Assert.Empty(Observe(detector, agents: [Agent("builder", TimeSpan.FromHours(3))]));
    }

    [Fact]
    public void AnAgentThatWasNeverProvisioned_IsNotDown()
    {
        var (detector, _) = NewDetector();
        Observe(detector, agents: [Agent("spare", TimeSpan.Zero)]);

        Assert.Empty(Observe(detector, agents: [Agent("spare", TimeSpan.FromHours(3), status: "offline", dbOnly: true)]));
    }

    // ── Still current ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WhatWasAnnounced_IsCurrent_UntilItResolves()
    {
        var (detector, _) = NewDetector();
        var failed = Run("pr-7", status: "Failed", phase: null);
        Observe(detector, agents: [Agent("builder", TimeSpan.Zero)]);
        var announced = Observe(detector, running: [Run()], failed: [failed], agents: [Agent("builder", TimeSpan.FromMinutes(6))]);
        var gate = Assert.Single(announced, m => m.Kind == "gate");
        var run = Assert.Single(announced, m => m.Kind == "run");
        var agent = Assert.Single(announced, m => m.Kind == "agent");

        // Nothing new on the next pass, and all three are still true.
        Assert.Empty(Observe(detector, running: [Run()], failed: [failed], agents: [Agent("builder", TimeSpan.FromMinutes(7))]));
        Assert.All(announced, m => Assert.True(detector.IsCurrent(m.Key)));

        // The gate decided, the failure out of the window, the agent back: none is current.
        Observe(detector, running: [Run(phase: "implementation", gateRef: null)], agents: [Agent("builder", TimeSpan.Zero)]);
        Assert.False(detector.IsCurrent(gate.Key));
        Assert.False(detector.IsCurrent(run.Key));
        Assert.False(detector.IsCurrent(agent.Key));
    }

    // ── What a notification says ─────────────────────────────────────────────────────────────

    [Fact]
    public void NoNotification_CarriesTaskTextOrAnythingBeyondWhatIdentifiesTheThing()
    {
        var (detector, _) = NewDetector();
        const string task = "SECRET-LOOKING TASK TEXT that must stay off a lock screen";
        Observe(detector, agents: [Agent("builder", TimeSpan.Zero, status: "busy", task: task)]);

        var messages = Observe(detector,
            running: [Run()],
            failed: [Run("pr-7", status: "Failed", phase: null, pr: 7)],
            agents: [Agent("builder", TimeSpan.FromMinutes(6), status: "busy", task: task)]);

        Assert.Equal(["agent", "gate", "run"], messages.Select(m => m.Kind).Order());
        foreach (var message in messages)
        {
            var payload = Encoding.UTF8.GetString(message.ToPayload());
            Assert.DoesNotContain("TASK TEXT", payload);
            using var doc = JsonDocument.Parse(payload);
            Assert.Equal(message.Kind, doc.RootElement.GetProperty("phleet").GetProperty("kind").GetString());
        }
    }

    [Fact]
    public void ARunWithNoRepository_IsDescribedByItsIdentifier()
    {
        var bare = Run("advisory-1", pr: null, repo: null);

        Assert.Equal("advisory-1", PushMessages.Subject(bare));
        Assert.Equal("UwePrImplementationWorkflow", PushMessages.RunFailed(bare with { Status = "Failed" }, "k").Body);
    }
}
